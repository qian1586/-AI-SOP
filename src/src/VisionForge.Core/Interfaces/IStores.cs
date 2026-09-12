using VisionForge.Core.Models;

namespace VisionForge.Core.Interfaces;

/// <summary>
/// 配方仓储。底层可以是 JSON 文件、SQLite、甚至共享服务器目录 —— 接口不变。
/// </summary>
public interface IRecipeRepository
{
    Task<IReadOnlyList<Recipe>> LoadAllAsync(CancellationToken ct = default);

    Task<Recipe?> GetAsync(string recipeId, CancellationToken ct = default);

    /// <summary>新增或更新（按 Id 判断）。</summary>
    Task SaveAsync(Recipe recipe, CancellationToken ct = default);

    Task<bool> DeleteAsync(string recipeId, CancellationToken ct = default);

    /// <summary>配方文件所在目录，界面上显示给用户看，方便手工备份。</summary>
    string StoragePath { get; }
}

/// <summary>
/// 检测历史仓储。
///
/// 文章里的要求："每次检测结果都记录到历史库，包含检测方案、批次号、产品型号、
/// 判定结果、测量数据，支持按条件筛选和导出，配合 NG 图存储，后续质量追溯有据可查。"
/// </summary>
public interface IInspectionHistoryStore
{
    /// <summary>写入一条记录，返回自增 Id。</summary>
    Task<long> SaveAsync(InspectionRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<InspectionRecord>> QueryAsync(HistoryQuery query, CancellationToken ct = default);

    Task<int> CountAsync(HistoryQuery? query = null, CancellationToken ct = default);

    /// <summary>导出 CSV。编码必须是带 BOM 的 UTF-8，否则 Excel 打开中文是乱码。</summary>
    Task ExportCsvAsync(HistoryQuery query, string filePath, CancellationToken ct = default);

    /// <summary>按维度统计合格率 —— 界面上的汇总卡片、Pareto 图靠它。</summary>
    Task<IReadOnlyList<StatisticsItem>> StatisticsAsync(
        DateTime from,
        DateTime to,
        StatisticsDimension dimension,
        CancellationToken ct = default);

    /// <summary>NG 图片存放目录。</summary>
    string NgImageDirectory { get; }
}

/// <summary>统计维度。</summary>
public enum StatisticsDimension
{
    ByProductModel = 0,
    ByRecipe = 1,
    ByBatch = 2,
    ByDay = 3,
    ByOperator = 4,
}

/// <summary>一条统计结果。</summary>
public sealed class StatisticsItem
{
    public string Key { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Pass { get; set; }
    public int Fail { get; set; }
    public double AverageElapsedMs { get; set; }

    public double PassRate => Total == 0 ? 0 : (double)Pass / Total * 100;
}
