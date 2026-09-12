using System.Text.Json;
using System.Text.Json.Serialization;
using VisionForge.Core.Models;

namespace VisionForge.Infrastructure.Storage;

/// <summary>
/// SOP 定义的读写。
///
/// SOP 和检测配方分开存：
///   recipes/*.json  —— 每个检测点怎么做（ROI、模板、阈值）
///   sop.json        —— 一个产品的步骤顺序，每步引用一个配方 Id
///
/// 分开的好处：换产品只改 sop.json；调检测参数只改对应配方。
/// 两件事由不同角色负责（工艺 vs 视觉工程师），分开才不会互相踩。
/// </summary>
public static class SopDefinitionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public static SopDefinition Load(string path)
    {
        if (!File.Exists(path))
        {
            // 首次运行：生成一份示例 SOP，让界面有东西可看，而不是一片空白
            var sample = SopDefinition.CreateSample();
            Save(sample, path);
            return sample;
        }

        try
        {
            var json = File.ReadAllText(path);
            var sop = JsonSerializer.Deserialize<SopDefinition>(json, Options);
            return sop ?? SopDefinition.CreateSample();
        }
        catch (Exception ex)
        {
            // 损坏时不让程序起不来，备份后回退到示例
            var backup = path + $".bad-{DateTime.Now:yyyyMMddHHmmss}";
            try { File.Move(path, backup, overwrite: true); } catch { }
            System.Diagnostics.Debug.WriteLine($"[SopStore] 解析失败，已备份到 {backup}：{ex.Message}");
            return SopDefinition.CreateSample();
        }
    }

    public static void Save(SopDefinition sop, string path)
    {
        sop.UpdatedAt = DateTime.Now;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(sop, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
