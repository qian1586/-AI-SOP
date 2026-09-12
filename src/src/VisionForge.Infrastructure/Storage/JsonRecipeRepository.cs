using System.Text.Json;
using System.Text.Json.Serialization;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Infrastructure.Storage;

/// <summary>
/// 配方仓储（JSON 文件实现）。
///
/// 一个配方一个文件，而不是全塞进一个大 JSON。理由：
///   · 现场工程师能直接打开某个配方看/改，不用在一堆配方里翻
///   · 备份和分享单个配方只需拷一个文件
///   · 一个文件损坏不会连累其他配方
///   · 可以纳入 Git 做版本管理（配方变更其实挺需要追溯的）
/// </summary>
public sealed class JsonRecipeRepository : IRecipeRepository
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public JsonRecipeRepository(string directory)
    {
        StoragePath = directory;
        Directory.CreateDirectory(directory);
    }

    public string StoragePath { get; }

    public async Task<IReadOnlyList<Recipe>> LoadAllAsync(CancellationToken ct = default)
    {
        var result = new List<Recipe>();
        if (!Directory.Exists(StoragePath)) return result;

        foreach (var file in Directory.EnumerateFiles(StoragePath, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // 库代码一律 ConfigureAwait(false)：不把续体绑回调用方的
                // SynchronizationContext。否则调用方一旦在 UI 线程上同步等待，
                // 就会死锁（本项目早期版本就踩过这个坑）。
                var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var recipe = JsonSerializer.Deserialize<Recipe>(json, Options);
                if (recipe is not null) result.Add(recipe);
            }
            catch (Exception ex)
            {
                // 单个配方坏了不能影响整批加载 —— 现场常见的场景是有人手工改坏了一个文件
                System.Diagnostics.Debug.WriteLine($"[RecipeRepo] 解析 {file} 失败: {ex.Message}");
            }
        }

        return result.OrderByDescending(r => r.UpdatedAt).ToList();
    }

    public async Task<Recipe?> GetAsync(string recipeId, CancellationToken ct = default)
    {
        var path = PathFor(recipeId);
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Recipe>(json, Options);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(Recipe recipe, CancellationToken ct = default)
    {
        recipe.UpdatedAt = DateTime.Now;

        var path = PathFor(recipe.Id);
        var json = JsonSerializer.Serialize(recipe, Options);

        // 先写临时文件再替换，避免写一半断电把配方弄坏
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    public Task<bool> DeleteAsync(string recipeId, CancellationToken ct = default)
    {
        var path = PathFor(recipeId);
        if (!File.Exists(path)) return Task.FromResult(false);

        try
        {
            // 不直接删，先改名 —— 配方误删的代价太高，留个后悔的机会
            var trash = Path.Combine(StoragePath, "_deleted");
            Directory.CreateDirectory(trash);
            File.Move(path, Path.Combine(trash, $"{Path.GetFileName(path)}.{DateTime.Now:yyyyMMddHHmmss}"),
                      overwrite: true);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private string PathFor(string recipeId) =>
        Path.Combine(StoragePath, $"{Sanitize(recipeId)}.json");

    private static string Sanitize(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "unnamed";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
