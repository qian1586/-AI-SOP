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
    private readonly IInspectionHistoryStore _history;
    private int _queryVerdictIndex;
    private int _queryTotal;
    private int _queryPass;
    private int _queryFail;
    private string _queryStatus = "设置条件后点「查询」";

    /// <summary>界面最近 N 条历史记录，避免一次加载全表。</summary>
    private const int HistoryDisplayLimit = 100;

    public DateTime? QueryFrom
    {
        get => _queryFrom;
        set => SetProperty(ref _queryFrom, value);
    }

    public DateTime? QueryTo
    {
        get => _queryTo;
        set => SetProperty(ref _queryTo, value);
    }

    public string QueryBatch
    {
        get => _queryBatch;
        set => SetProperty(ref _queryBatch, value);
    }

    public string QueryModel
    {
        get => _queryModel;
        set => SetProperty(ref _queryModel, value);
    }

    /// <summary>结论筛选：0=全部 1=OK 2=NG。</summary>
    public int QueryVerdictIndex
    {
        get => _queryVerdictIndex;
        set => SetProperty(ref _queryVerdictIndex, value);
    }

    public int QueryTotal
    {
        get => _queryTotal;
        private set => SetProperty(ref _queryTotal, value);
    }

    public int QueryPass
    {
        get => _queryPass;
        private set
        {
            if (SetProperty(ref _queryPass, value)) OnPropertyChanged(nameof(QueryPassRate));
        }
    }

    public int QueryFail
    {
        get => _queryFail;
        private set => SetProperty(ref _queryFail, value);
    }

    public string QueryPassRate =>
        QueryTotal == 0 ? "—" : $"{(double)QueryPass / QueryTotal:P1}";

    public string QueryStatus
    {
        get => _queryStatus;
        private set => SetProperty(ref _queryStatus, value);
    }

    // ==================================================================
    // 历史查询页
    // ==================================================================
    private async Task QueryHistoryAsync()
    {
        try
        {
            var query = new HistoryQuery
            {
                From = _queryFrom,
                // To 是"日期"语义，补到当天 23:59:59，否则查不到当天下午的记录
                To = _queryTo?.Date.AddDays(1).AddTicks(-1),
                BatchNo = string.IsNullOrWhiteSpace(_queryBatch) ? null : _queryBatch.Trim(),
                ProductModel = string.IsNullOrWhiteSpace(_queryModel) ? null : _queryModel.Trim(),
                Verdict = _queryVerdictIndex switch
                {
                    1 => InspectionVerdict.Pass,
                    2 => InspectionVerdict.Fail,
                    _ => (InspectionVerdict?)null,
                },
                Limit = 2000,
            };

            var list = await _history.QueryAsync(query);

            QueryResults.Clear();
            foreach (var record in list) QueryResults.Add(record);

            QueryTotal = list.Count;
            QueryPass = list.Count(r => r.Verdict == InspectionVerdict.Pass);
            QueryFail = list.Count(r => r.Verdict == InspectionVerdict.Fail);
            QueryStatus = $"共 {QueryTotal} 条 · OK {QueryPass} · NG {QueryFail} · 合格率 {QueryPassRate}";
            StatusMessage = "历史查询完成：" + QueryStatus;
            _log.Info("历史查询：" + QueryStatus);
        }
        catch (Exception ex)
        {
            QueryStatus = "查询失败：" + ex.Message;
            _log.Error("历史查询失败", ex);
        }
    }

    private async Task ExportHistoryAsync()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"检测历史_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        await _history.ExportCsvAsync(new HistoryQuery { Limit = int.MaxValue }, path);
        StatusMessage = "历史已导出：" + path;
        _log.Info("导出历史到 " + path);
    }

    /// <summary>
    /// 把刚生成的记录插到表格顶部，溢出时从尾部截断。
    /// 表格只能展示最近 <see cref="HistoryDisplayLimit"/> 条，否则滚动条体验会崩。
    /// </summary>
    private void AppendHistoryRecord(InspectionRecord record)
    {
        HistoryRecords.Insert(0, record);
        while (HistoryRecords.Count > HistoryDisplayLimit)
            HistoryRecords.RemoveAt(HistoryRecords.Count - 1);
    }
}
