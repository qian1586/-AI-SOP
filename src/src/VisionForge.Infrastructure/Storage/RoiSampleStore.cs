using System.Text.Json;
using VisionForge.Core.Services;

namespace VisionForge.Infrastructure.Storage;

/// <summary>
/// "按框学习"样本的落盘：一个 JSON（含每个框的特征向量）+ 一堆抓帧图。
///
/// <para>和整幅画面的教示样本一样，这些是现场花时间教出来的资产，
/// 必须能存、能备份、能换机器拷过去。</para>
/// </summary>
public static class RoiSampleStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static List<RoiSample> Load(string path)
    {
        if (!File.Exists(path)) return new List<RoiSample>();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<RoiSample>>(json, Options) ?? new List<RoiSample>();
        }
        catch
        {
            // 文件坏了不能让程序起不来：备份后从空样本开始（和教示样本同样的处理）
            try { File.Move(path, path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true); }
            catch { }
            return new List<RoiSample>();
        }
    }

    public static void Save(IEnumerable<RoiSample> samples, string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(samples.ToList(), Options));
        File.Move(tmp, path, overwrite: true);
    }
}
