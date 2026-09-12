using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionForge.Common.Events;
using VisionForge.Common.Mvvm;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;
using VisionForge.Core.Services;
using VisionForge.Hardware.Camera;
using VisionForge.Hardware.Simulation;
using VisionForge.Hardware.Vision;
using VisionForge.Infrastructure.Config;
using VisionForge.Infrastructure.Storage;
using VisionForge.Main.Imaging;

namespace VisionForge.Main.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IAlarmDevice _alarm;

    /// <summary>相机看门狗：定时检查相机是否掉线，掉线就自动重连。</summary>
    private DispatcherTimer? _cameraWatchdog;
    private int _cameraReconnectAttempts;

    /// <summary>相机掉线最多连续重连几次。超过就停手告警，交给运维查线缆/供电/网段。</summary>
    private const int MaxCameraReconnectAttempts = 3;

    private void RaiseAlarmProps()
    {
        OnPropertyChanged(nameof(AlarmStatusText));
        OnPropertyChanged(nameof(IsBuzzerSounding));
        OnPropertyChanged(nameof(AlarmSignalKey));
        (SilenceBuzzerCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 相机看门狗：连接状态下发现相机掉线就自动重连（最多连续 3 次）。
    ///
    /// <para>现场最常见的故障就是网线松动、相机意外重启。靠人发现往往要等
    /// 下一件产品检测失败；自动重连能把这段停机时间省掉。连续失败就停手并告警，
    /// 让运维去查线缆/供电/网段 —— 无限重连只会刷满日志，对现场没有任何帮助。</para>
    /// </summary>
    private async void OnCameraWatchdogTick(object? sender, EventArgs e)
    {
        // 录像是本地文件，不存在"掉线重连"这回事 —— 看门狗对录像源直接跳过，
        // 否则它会把录像关掉、再去连真相机（现场会看到画面莫名切走）。
        if (IsVideoSource) return;

        var camera = _camera;
        if (camera is null)
        {
            _cameraWatchdog?.Stop();
            return;
        }

        if (camera.IsConnected)
        {
            _cameraReconnectAttempts = 0;
            return;
        }

        try
        {
            if (_cameraReconnectAttempts >= MaxCameraReconnectAttempts)
            {
                _cameraWatchdog?.Stop();
                StatusMessage = $"相机掉线：连续 {MaxCameraReconnectAttempts} 次自动重连失败，" +
                                "已停止重连，请检查网线 / 供电 / 网段";
                _log.Error(StatusMessage);
                await _alarm.SetSignalAsync(AlarmSignal.Fault);   // 设备故障亮红灯，等人来查
                RaiseAlarmProps();
                return;
            }

            _cameraReconnectAttempts++;
            _log.Warn($"检测到相机掉线，第 {_cameraReconnectAttempts} 次自动重连…");

            if (await camera.ConnectAsync())
            {
                // 防重复订阅：重连后重新挂钩取帧回调
                camera.FrameArrived -= OnFrameArrived;
                camera.FrameArrived += OnFrameArrived;
                await camera.StartGrabbingAsync();

                _cameraReconnectAttempts = 0;
                StatusMessage = "相机已自动重连，采集恢复正常";
                _log.Info("相机自动重连成功");

                OnPropertyChanged(nameof(CameraStatus));
                OnPropertyChanged(nameof(SystemRunning));
                OnPropertyChanged(nameof(SystemStatusText));
                await UpdateAlarmFromSopAsync();              // 恢复成按流程状态亮灯
            }
        }
        catch (Exception ex)
        {
            _log.Error("相机自动重连异常", ex);
        }
    }

    // ==================================================================
    // 报警
    // ==================================================================
    private void OnSopAlarm(object? sender, AlarmEventArgs e)
    {
        if (e.IsBlocking)
        {
            _log.Warn($"【锁线】{e.Step.Name} NG：{e.Result.JudgementReason}");
            // 界面弹窗 + 声音由 View 订阅此事件处理，ViewModel 不直接弹
            Alarm?.Invoke(this, e);
        }
        else
        {
            _log.Warn($"{e.Step.Name} 异常（未锁线）：{e.Result.JudgementReason}");
        }
    }

    /// <summary>锁线报警事件，View 层订阅后弹窗 + 响警。</summary>
    public event EventHandler<AlarmEventArgs>? Alarm;

    // ==================================================================
    // 声光报警
    // ==================================================================
    private DispatcherTimer? _buzzerTimer;

    /// <summary>
    /// 按 SOP 状态机的当前局面映射到灯色。
    ///
    /// <b>这是整个系统里唯一决定「产线该不该停」的地方。</b>
    /// 映射规则很克制 —— 只有真的锁线才给红灯，因为红灯的含义是"全线停"：
    ///   流程锁死          → 🔴 红灯 + 蜂鸣器（必须人工介入）
    ///   有步骤失败但未锁线 → 🟡 黄灯（警示，不影响流转）
    ///   其余（进行中/已完成/就绪） → 🟢 绿灯
    ///
    /// 把"产品不合格"和"产线要停"分开，是这套东西能不能在现场活下来的关键：
    /// 如果每个 NG 都亮红灯停线，第二天就有人把灯的线剪了。
    /// </summary>
    private async Task UpdateAlarmFromSopAsync()
    {
        AlarmSignal signal;
        if (Sop.IsLocked) signal = AlarmSignal.Fault;
        else if (Sop.Steps.Any(s => s.IsFailed)) signal = AlarmSignal.Warning;
        else signal = AlarmSignal.Ok;

        await ApplyAlarmAsync(signal);
    }

    /// <summary>
    /// 驱动声光报警。
    ///
    /// <b>灯和蜂鸣器分开处理，这是有意的：</b>
    /// 灯保持常亮（红灯一直红着，提醒"这里还没处理完"），
    /// 蜂鸣器响一段时间自动停（否则工人会用胶带把它贴死，
    /// 更糟的是整个车间对报警脱敏，以后真出事没人管）。
    /// 消音不等于问题解决 —— 红灯还在，流程还锁着。
    /// </summary>
    private async Task ApplyAlarmAsync(AlarmSignal signal)
    {
        try
        {
            if (!_alarm.IsConnected)
                await _alarm.ConnectAsync();

            await _alarm.SetSignalAsync(signal);

            bool needBuzzer = signal == AlarmSignal.Fault && _settings.Current.Alarm.BuzzerEnabled;
            await _alarm.SetBuzzerAsync(needBuzzer);

            if (needBuzzer) StartBuzzerAutoOff();
            else StopBuzzerAutoOff();
        }
        catch (Exception ex)
        {
            // 报警装置出问题不能影响检测主流程 —— 这是本系统的优先级约定
            _log.Warn("声光报警驱动失败：" + ex.Message);
        }
        finally
        {
            RaiseAlarmProps();
        }
    }

    private void StartBuzzerAutoOff()
    {
        int seconds = _settings.Current.Alarm.BuzzerAutoOffSeconds;
        if (seconds <= 0) return;

        StopBuzzerAutoOff();
        _buzzerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _buzzerTimer.Tick += async (_, _) =>
        {
            StopBuzzerAutoOff();
            await _alarm.SetBuzzerAsync(false);
            _log.Info($"蜂鸣器 {seconds}s 后自动消音（红灯保持，等待人工确认）");
            RaiseAlarmProps();
        };
        _buzzerTimer.Start();
    }

    private void StopBuzzerAutoOff()
    {
        _buzzerTimer?.Stop();
        _buzzerTimer = null;
    }

    /// <summary>人工消音。只停声音，红灯继续亮、流程继续锁。</summary>
    private void SilenceBuzzer()
    {
        StopBuzzerAutoOff();
        _ = _alarm.SetBuzzerAsync(false);
        _log.Info("操作员人工消音（报警状态未解除）");
        RaiseAlarmProps();
    }
}
