namespace VisionForge.Infrastructure.Config;

/// <summary>
/// 配置的持有者。
///
/// 为什么要包一层，而不让各处直接调 AppSettingsStore：
///   1. 全程序只有一份配置实例，避免"A 处改了 B 处不知道"
///   2. 保存动作集中在一处，方便加节流（界面频繁改动时不要每次都写盘）
///   3. 将来要加密配置、或者从服务器下发配置，只改这一个类
/// </summary>
public sealed class AppSettingsProvider
{
    private readonly string _path;
    private DateTime _lastSave = DateTime.MinValue;

    public AppSettingsProvider(string path)
    {
        _path = path;
        Current = AppSettingsStore.Load(path);
    }

    /// <summary>当前配置。改完记得调 <see cref="Save"/>。</summary>
    public AppSettings Current { get; private set; }

    public string FilePath => _path;

    /// <summary>
    /// 保存。
    /// 做了 200ms 节流 —— 界面上拖滑块改参数时可能一秒触发几十次，
    /// 每次都写盘既慢又费 SSD 寿命。
    /// </summary>
    public void Save(bool force = false)
    {
        if (!force && (DateTime.Now - _lastSave).TotalMilliseconds < 200) return;

        try
        {
            AppSettingsStore.Save(Current, _path);
            _lastSave = DateTime.Now;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] 保存失败: {ex.Message}");
        }
    }

    /// <summary>重新从磁盘加载（界面上"重新载入配置"用）。</summary>
    public void Reload()
    {
        Current = AppSettingsStore.Load(_path);
    }
}
