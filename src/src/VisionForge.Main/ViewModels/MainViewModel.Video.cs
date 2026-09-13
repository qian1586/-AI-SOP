using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using VisionForge.Main.Imaging;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 录像当相机：导入一段 mp4/avi，像真相机一样出帧，整套流程都能先在录像上跑通。
///
/// <para>参考界面里最实用的一点就是这个 —— 现场没相机、人也不在工位时，
/// 拿一段录像就能把框 ROI、教 OK/NG、看计时、留证据、看趋势图全过一遍。</para>
/// </summary>
public sealed partial class MainViewModel
{
    private VideoFileCamera? _videoCamera;
    private bool _isVideoSource;
    private string _videoNameText = "—";
    private string _videoTimeText = "00:00 / 00:00";
    private double _videoProgress;

    /// <summary>导入一段录像当相机。</summary>
    public ICommand ImportVideoCommand { get; }

    /// <summary>播放 / 暂停录像。</summary>
    public ICommand ToggleVideoPlayCommand { get; }

    /// <summary>从头再播一遍（换一件产品时用）。</summary>
    public ICommand RestartVideoCommand { get; }

    /// <summary>打开日志目录（现场取证、发给供应商排查都用它）。</summary>
    public ICommand OpenLogFolderCommand { get; }

    /// <summary>当前画面是不是来自录像（决定要不要显示录像工具条）。</summary>
    public bool IsVideoSource
    {
        get => _isVideoSource;
        private set
        {
            if (!SetProperty(ref _isVideoSource, value)) return;
            OnPropertyChanged(nameof(VideoPlayButtonText));
        }
    }

    public string VideoPlayButtonText =>
        _videoCamera?.IsPlaying == true ? "⏸ 暂停录像" : "▶ 播放录像";

    public string VideoNameText
    {
        get => _videoNameText;
        private set => SetProperty(ref _videoNameText, value);
    }

    /// <summary>"00:25 / 01:33"。</summary>
    public string VideoTimeText
    {
        get => _videoTimeText;
        private set => SetProperty(ref _videoTimeText, value);
    }

    /// <summary>播放进度 0~1（进度条用）。</summary>
    public double VideoProgress
    {
        get => _videoProgress;
        private set => SetProperty(ref _videoProgress, value);
    }

    private async Task ImportVideoAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选一段录像当相机（工位实拍视频）",
            Filter = "视频文件|*.mp4;*.avi;*.mov;*.wmv;*.mkv;*.m4v|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "已取消导入录像";
            return;
        }

        IsBusy = true;
        try
        {
            // 先把原来的相机（或上一段录像）停干净，避免两路帧同时进来
            _cameraWatchdog?.Stop();
            if (_camera is not null)
            {
                _camera.FrameArrived -= OnFrameArrived;
                _camera.Dispose();
                _camera = null;
            }

            _videoCamera = null;
            _sceneCamera = null;
            _simulator = null;
            IsVideoSource = false;
            PreviewImage = FrameConverter.CreatePlaceholder(text: "正在打开录像…");

            var video = new VideoFileCamera(dialog.FileName);
            if (!await video.ConnectAsync())
            {
                video.Dispose();
                PreviewImage = FrameConverter.CreatePlaceholder(text: "录像打不开");
                StatusMessage = "录像打开失败：换一段文件试试（推荐 mp4 / h264 编码）";
                _log.Warn("导入录像失败：" + dialog.FileName);
                return;
            }

            _camera = video;
            _videoCamera = video;
            IsVideoSource = true;
            VideoNameText = Path.GetFileName(dialog.FileName);

            _camera.FrameArrived += OnFrameArrived;
            await video.StartGrabbingAsync();

            // 导入后**先暂停在第 1 帧**。
            //
            // 现场反馈"导入后不能暂停视频"—— 真正的问题是：原来一导入就自己播起来，
            // 画面一直在动，人根本没法从容地画框、教 OK/NG；31 秒一过又停在结尾。
            // 所以默认停住，等现场说"好了"再点播放（点「开始识别」也会自动播）。
            video.Pause();

            RefreshVideoProgress();
            StatusMessage = $"已把录像当相机：{VideoNameText}（{video.DurationSeconds:F0}s）—— " +
                            "已暂停在第 1 帧：先画框、教 OK/NG；好了点「▶ 播放录像」跑流程";
            _log.Info($"录像已作为相机打开：{dialog.FileName}（{video.DurationSeconds:F0}s）");
        }
        catch (Exception ex)
        {
            StatusMessage = "导入录像失败：" + ex.Message;
            _log.Error("导入录像失败", ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(VideoPlayButtonText));
        }
    }

    private void ToggleVideoPlay()
    {
        var video = _videoCamera;
        if (video is null) return;

        if (video.IsPlaying) video.Pause();
        else video.Play();

        OnPropertyChanged(nameof(VideoPlayButtonText));
    }

    private void RestartVideo()
    {
        var video = _videoCamera;
        if (video is null) return;

        video.Restart();
        RefreshVideoProgress();
        OnPropertyChanged(nameof(VideoPlayButtonText));
        StatusMessage = "录像已从头播放";
    }

    /// <summary>由 200ms 定时器调用：刷新进度条与时间文案。</summary>
    private void RefreshVideoProgress()
    {
        var video = _videoCamera;
        if (video is null) return;

        double pos = Math.Max(0, video.PositionSeconds);
        double dur = Math.Max(0, video.DurationSeconds);

        VideoTimeText = $"{FormatSeconds(pos)} / {FormatSeconds(dur)}";
        VideoProgress = dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0;
    }

    private static string FormatSeconds(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes:D2}:{span.Seconds:D2}";
    }

    /// <summary>关掉录像源（切回真相机 / 断开时调用），把状态一起清干净。</summary>
    private void ClearVideoSource()
    {
        if (_videoCamera is not null && ReferenceEquals(_camera, _videoCamera))
        {
            _videoCamera.FrameArrived -= OnFrameArrived;
            _camera = null;
        }

        _videoCamera?.Dispose();
        _videoCamera = null;

        if (!IsVideoSource) return;

        IsVideoSource = false;
        VideoNameText = "—";
        VideoTimeText = "00:00 / 00:00";
        VideoProgress = 0;
    }

    /// <summary>打开日志目录（现场把日志发给供应商排查时最省事的入口）。</summary>
    private void OpenLogFolder()
    {
        try
        {
            string dir = _settings.Current.LogDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            StatusMessage = "已打开日志目录：" + dir;
        }
        catch (Exception ex)
        {
            StatusMessage = "打开日志目录失败：" + ex.Message;
        }
    }
}
