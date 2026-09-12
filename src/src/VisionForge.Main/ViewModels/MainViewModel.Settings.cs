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
    private readonly AppSettingsProvider _settings;

    // ---- 过程层（方案 v9：手部关键点 + 规则引擎 + 实时拦截）----
    private ProcessRuleConfig? _processConfig;

    /// <summary>过程层规则（可在设置页调 K、置信度阈值、顺序开关）。</summary>
    public ProcessRuleConfig? ProcessConfig => _processConfig;

    /// <summary>
    /// 加载过程层规则。文件不存在时写一份示例规则 ——
    /// 和配方、SOP 一样，开箱就有东西可跑，而不是让用户先去猜配置格式。
    /// </summary>
    private void LoadProcessConfig()
    {
        string dataRoot = _settings.Current.DataRoot;
        string rulePath = Path.Combine(dataRoot, "process-rule.json");

        try
        {
            _processConfig = File.Exists(rulePath)
                ? ProcessConfigStore.Load(rulePath)
                : DemoDataFactory.CreateDemoProcessConfig();

            if (!File.Exists(rulePath)) ProcessConfigStore.SaveJson(_processConfig, rulePath);

            BuildProcessTargetStates();
            ProcessStatusText = "规则已加载：" + ProcessRuleSummary;
            _log.Info("过程层规则已加载：" + ProcessRuleSummary);
        }
        catch (Exception ex)
        {
            // 配置错误必须"响亮地失败"：一条 CONFIG_INVALID，
            // 而不是运行期什么都不动、现场一脸茫然
            _processConfig = null;
            ProcessStatusText = "规则加载失败（CONFIG_INVALID）：" + ex.Message;
            ProcessViolations.Insert(0, new ProcessViolation
            {
                Code = ViolationCode.ConfigInvalid,
                Message = ex.Message,
            });
            _log.Error("过程层规则加载失败", ex);
        }

        OnPropertyChanged(nameof(ProcessRuleSummary));
        (ToggleProcessMonitorCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportProcessRuleCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void BuildProcessTargetStates()
    {
        ProcessTargetStates.Clear();
        if (_processConfig is null) return;

        foreach (var target in _processConfig.Targets)
            ProcessTargetStates.Add(new ProcessTargetStateViewModel(target.Id, target.Name));
    }

    // ==================================================================
    // 系统设置页
    // ==================================================================
    /// <summary>
    /// 界面缩放倍数（1.0 = 100%）。
    ///
    /// <para>界面上的滑条已按现场要求撤掉（默认 100%，怕被误拖大导致界面超出屏幕），
    /// 但窗口整体的等比缩放仍然由它驱动 —— 所以这个属性必须留着，
    /// 改 config\appsettings.json 就能生效。上下限 0.8~1.6。</para>
    /// </summary>
    public double UiScale
    {
        get => _settings.Current.UiScale;
        set
        {
            double clamped = Math.Clamp(value, 0.8, 1.6);
            if (Math.Abs(_settings.Current.UiScale - clamped) < 0.001) return;

            _settings.Current.UiScale = clamped;
            _settings.Save();

            OnPropertyChanged(nameof(UiScale));
        }
    }

    private void SaveSettings()
    {
        _settings.Save(force: true);

        OnPropertyChanged(nameof(CurrentSettings));
        OnPropertyChanged(nameof(CameraStatus));
        OnPropertyChanged(nameof(PlcStatus));

        // 工位联网 / MES 的配置改完要立即按新配置重启（否则要关掉软件才生效，
        // 现场会以为"填了没用"）
        StartNetwork();

        StatusMessage = "设置已保存：" + _settings.FilePath;
        _log.Info("系统设置已保存");
    }

    private void ReloadSettings()
    {
        _settings.Reload();

        OnPropertyChanged(nameof(CurrentSettings));
        OnPropertyChanged(nameof(CameraStatus));
        OnPropertyChanged(nameof(PlcStatus));

        StatusMessage = "配置已重新载入（相机 / PLC 需重新连接才生效）";
        _log.Info("系统设置已重新载入");
    }

    private void SaveProcessRule()
    {
        var config = _processConfig;
        if (config is null) return;

        var errors = config.Validate();
        if (errors.Count > 0)
        {
            var violation = new ProcessViolation
            {
                Code = ViolationCode.ConfigInvalid,
                Message = "规则未保存：" + string.Join("；", errors),
            };
            ProcessViolations.Insert(0, violation);
            ProcessStatusText = violation.Message;
            _log.Warn(violation.Message);
            return;
        }

        try
        {
            string jsonPath = Path.Combine(_settings.Current.DataRoot, "process-rule.json");
            ProcessConfigStore.SaveJson(config, jsonPath);

            File.WriteAllText(
                Path.Combine(_settings.Current.DataRoot, "process-rule.yaml"),
                ProcessConfigStore.ToYaml(config),
                new System.Text.UTF8Encoding(true));

            ProcessStatusText = "规则已保存（运行中的引擎会立刻用新参数）";
            OnPropertyChanged(nameof(ProcessRuleSummary));
            OnPropertyChanged(nameof(ProcessConfig));
            StatusMessage = "过程层规则已保存";
            _log.Info("过程层规则已保存：" + ProcessRuleSummary);
        }
        catch (Exception ex)
        {
            ProcessStatusText = "规则保存失败：" + ex.Message;
            _log.Error("过程层规则保存失败", ex);
        }
    }
}
