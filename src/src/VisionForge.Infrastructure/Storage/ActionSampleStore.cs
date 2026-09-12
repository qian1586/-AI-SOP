using System.Text.Json;
using VisionForge.Core.Services;

namespace VisionForge.Infrastructure.Storage;

/// <summary>
/// 教示样本的落盘：一个 JSON（含特征向量）+ 一堆抓帧图。
///
/// <para>为什么要落盘：教示是现场工程师花时间"教"出来的资产 ——
/// 采了几十条 OK/NG 样本，重启软件就没了，没人会愿意再教一遍。
/// 所以样本必须像配方一样存下来，可备份、可跨机器拷。</para>
/// </summary>
public static class ActionSampleStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static List<ActionSample> Load(string path)
    {
        if (!File.Exists(path)) return new List<ActionSample>();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<ActionSample>>(json, Options) ?? new List<ActionSample>();
        }
        catch
        {
            // 样本文件坏了不能让程序起不来：备份后从空样本开始
            try { File.Move(path, path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true); }
            catch { }
            return new List<ActionSample>();
        }
    }

    public static void Save(IEnumerable<ActionSample> samples, string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(samples.ToList(), Options));
        File.Move(tmp, path, overwrite: true);
    }
}
