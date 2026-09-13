namespace VisionForge.Core.Services;

/// <summary>手部动作跟踪的参数。</summary>
public sealed class HandActionOptions
{
    /// <summary>手部整体置信度低于它就不采信（模型自己都不确定，别拿它判工序）。</summary>
    public double MinScore { get; set; } = 0.5;

    /// <summary>在同一个框里连续待够多久，才算"真的在那儿做了个动作"。</summary>
    public int DwellMs { get; set; } = 300;

    /// <summary>
    /// 短暂丢失的宽限：手被零件/工具挡住、模型掉一两帧时，
    /// 只要在这个时间内回到同一个框，仍然算同一次停留（不重新计时）。
    /// </summary>
    public int LeaveGraceMs { get; set; } = 400;
}

/// <summary>跟踪器吐出来的事件。</summary>
public sealed class HandActionEvent
{
    /// <summary>Enter（进框）/ Dwell（停留够时间＝做了动作）/ Leave（离开）。</summary>
    public string Kind { get; init; } = string.Empty;

    public int RoiIndex { get; init; }

    public double DwellMs { get; init; }

    public DateTime Timestamp { get; init; }

    public string Describe(int displayIndex) => Kind switch
    {
        "Enter" => $"手进入第 {displayIndex} 个框",
        "Dwell" => $"手在第 {displayIndex} 个框停留 {DwellMs:F0}ms（判定：这一步有动作）",
        "Leave" => $"手离开第 {displayIndex} 个框（共停留 {DwellMs:F0}ms）",
        _ => Kind,
    };
}

/// <summary>
/// 手部动作跟踪 —— 把"每帧手在哪个框"变成"手去了哪儿、待了多久、按什么顺序"。
///
/// <para><b>它解决什么问题：</b>光有零件判定，只知道"东西有没有装对"；
/// 加上手的轨迹，才能回答"人到底动没动手、先做的哪一步"。
/// 文章里 3.2（手部位姿判断操作区域、跟踪轨迹判断动作方向）和 3.5（顺序校验）就是这条。</para>
///
/// <para><b>为什么做成纯逻辑类：</b>它不碰界面、不碰相机，输入就是"第几帧、手在哪个框、置信度"。
/// 这样自检可以用人工构造的轨迹把"停留判定、宽限、顺序"全部测一遍 ——
/// 这些时序逻辑靠现场试是试不全的。</para>
///
/// <para><b>为什么要有"宽限"：</b>手部模型会掉帧（手被挡、快速移动），
/// 一掉帧就判定"离开"会让停留时间永远攒不够。宽限期内回到同一个框就继续计时。</para>
/// </summary>
public sealed class HandActionTracker
{
    private readonly HandActionOptions _options;
    private readonly List<(int RoiIndex, DateTime At)> _path = new();

    private int _currentRoi = -1;
    private DateTime _enteredAt;
    private DateTime _lastSeenAt;
    private bool _dwellFired;
    private bool _inside;

    public HandActionTracker(HandActionOptions? options = null)
        => _options = options ?? new HandActionOptions();

    /// <summary>当前手在哪个框（-1 = 不在任何框里）。</summary>
    public int CurrentRoi => _currentRoi;

    /// <summary>本次停留已经持续多久（毫秒）。没在框里就是 0。</summary>
    public double CurrentDwellMs { get; private set; }

    /// <summary>手走过的路径（只在"停留够时间"时才记一笔，避免手划过就进路径）。</summary>
    public IReadOnlyList<(int RoiIndex, DateTime At)> Path => _path;

    /// <summary>路径的中文描述，例如 "②→③→①"。</summary>
    public string PathText => _path.Count == 0
        ? "（还没有动作）"
        : string.Join("→", _path.Select(p => "第" + p.RoiIndex + "框"));

    /// <summary>
    /// 喂一帧观测。
    /// </summary>
    /// <param name="time">这一帧的时刻。</param>
    /// <param name="roiIndex">手的重心落在第几个框（1 开始）；不在任何框里传 -1。</param>
    /// <param name="score">手部整体置信度。</param>
    /// <returns>这一帧产生的事件（可能是 0 个、也可能有多个）。</returns>
    public IReadOnlyList<HandActionEvent> Observe(DateTime time, int roiIndex, double score)
    {
        var events = new List<HandActionEvent>(2);

        // 置信度不够：当作"这一帧没看见手"，但不清状态 —— 交给宽限期处理
        bool seen = score >= _options.MinScore && roiIndex > 0;
        int observed = seen ? roiIndex : -1;

        if (!seen)
        {
            if (_inside && (time - _lastSeenAt).TotalMilliseconds > _options.LeaveGraceMs)
            {
                events.Add(new HandActionEvent
                {
                    Kind = "Leave", RoiIndex = _currentRoi,
                    DwellMs = (time - _enteredAt).TotalMilliseconds, Timestamp = time,
                });
                Reset();
            }
            return events;
        }

        // 手在同一个框里：继续累计停留
        if (_inside && observed == _currentRoi)
        {
            _lastSeenAt = time;
            CurrentDwellMs = (time - _enteredAt).TotalMilliseconds;

            if (!_dwellFired && CurrentDwellMs >= _options.DwellMs)
            {
                _dwellFired = true;
                _path.Add((_currentRoi, time));

                events.Add(new HandActionEvent
                {
                    Kind = "Dwell", RoiIndex = _currentRoi,
                    DwellMs = CurrentDwellMs, Timestamp = time,
                });
            }

            return events;
        }

        // 走到这里说明：换了一个框，或者刚从框外进来
        // （"掉帧后回到同一个框"在上面那个分支就处理掉了 —— 那正是宽限期的作用）
        if (_inside)
        {
            events.Add(new HandActionEvent
            {
                Kind = "Leave", RoiIndex = _currentRoi,
                DwellMs = (time - _enteredAt).TotalMilliseconds, Timestamp = time,
            });
        }

        _currentRoi = observed;
        _enteredAt = time;
        _lastSeenAt = time;
        _inside = true;
        _dwellFired = false;
        CurrentDwellMs = 0;

        events.Add(new HandActionEvent
        {
            Kind = "Enter", RoiIndex = observed, DwellMs = 0, Timestamp = time,
        });

        return events;
    }

    /// <summary>清空（换一件产品时调）。</summary>
    public void Reset()
    {
        _currentRoi = -1;
        _inside = false;
        _dwellFired = false;
        CurrentDwellMs = 0;
    }

    /// <summary>连路径一起清掉（换配方/重新开始一件）。</summary>
    public void ResetAll()
    {
        Reset();
        _path.Clear();
    }
}
