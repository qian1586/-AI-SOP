using System.Collections.ObjectModel;
using VisionForge.Common.Mvvm;
using VisionForge.Infrastructure.Config;

namespace VisionForge.Main.ViewModels;

/// <summary>「时间设置」窗口里的一道工序。</summary>
public sealed class TimingStandardItem : ObservableObject
{
    private double _standardSec;

    public TimingStandardItem(int stepSeq, string stepName, double standardSec)
    {
        StepSeq = stepSeq;
        StepName = string.IsNullOrWhiteSpace(stepName) ? $"第 {stepSeq} 步" : stepName;
        _standardSec = standardSec;
    }

    public int StepSeq { get; }

    public string StepName { get; }

    public string Label => $"第 {StepSeq} 步 · {StepName}";

    /// <summary>标准工时（秒）。0 = 不设，不参与快慢判定。</summary>
    public double StandardSec
    {
        get => _standardSec;
        set => SetProperty(ref _standardSec, value);
    }
}

/// <summary>
/// 「时间设置」窗口的数据：开关 + 标准工时 + 保存位置。
///
/// <para>标准工时是"先快慢判定"的基准。现场通常这样定：让熟练工连做 5 件，
/// 取平均（或取第 80 百分位）填进去，之后新员工上手就能立刻看出差距。</para>
/// </summary>
public sealed class TimingSettingsModel : ObservableObject
{
    private bool _enabled;
    private bool _recordNg;
    private double _slowAlertPercent;
    private string _errorText = string.Empty;

    public TimingSettingsModel(TimingOptions options, IEnumerable<TimingStandardItem> items, string dataFolder)
    {
        _enabled = options.Enabled;
        _recordNg = options.RecordNg;
        _slowAlertPercent = options.SlowAlertPercent;
        DataFolder = dataFolder;
        Items = new ObservableCollection<TimingStandardItem>(items);
    }

    public ObservableCollection<TimingStandardItem> Items { get; }

    /// <summary>计时数据保存在哪（给现场一个明确的落点，方便备份）。</summary>
    public string DataFolder { get; }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public bool RecordNg
    {
        get => _recordNg;
        set => SetProperty(ref _recordNg, value);
    }

    /// <summary>慢多少算"明显慢"（%）。</summary>
    public double SlowAlertPercent
    {
        get => _slowAlertPercent;
        set => SetProperty(ref _slowAlertPercent, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public bool Validate()
    {
        foreach (var item in Items)
        {
            if (item.StandardSec < 0)
            {
                ErrorText = $"第 {item.StepSeq} 步的标准工时为负数";
                return false;
            }
        }

        if (SlowAlertPercent <= 0 || SlowAlertPercent > 500)
        {
            ErrorText = "慢的报警阈值应在 1% ~ 500% 之间";
            return false;
        }

        ErrorText = string.Empty;
        return true;
    }
}
