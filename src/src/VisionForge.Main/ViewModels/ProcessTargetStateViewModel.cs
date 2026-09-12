using VisionForge.Common.Mvvm;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 过程层目标（料盒 / 作业位）在界面上的状态块。
///
/// <para>三态与竞品界面一致：<b>绿 = 已覆盖</b>（手到过且停留达标）、
/// <b>橙 = 当前等待</b>、<b>灰 = 还没做</b>。现场一眼就能看出卡在哪。</para>
/// </summary>
public sealed class ProcessTargetStateViewModel : ObservableObject
{
    private string _stateKey = "pending";

    public ProcessTargetStateViewModel(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>ok / active / pending，配色交给 XAML 的转换器。</summary>
    public string StateKey
    {
        get => _stateKey;
        private set
        {
            if (SetProperty(ref _stateKey, value))
                OnPropertyChanged(nameof(StateText));
        }
    }

    public string StateText => StateKey switch
    {
        "ok" => "已到位",
        "active" => "等待中",
        _ => "未执行",
    };

    public string Display => Id + " " + Name;

    internal void SetStateKey(string key)
    {
        if (!string.Equals(_stateKey, key, StringComparison.Ordinal))
            StateKey = key;
    }
}
