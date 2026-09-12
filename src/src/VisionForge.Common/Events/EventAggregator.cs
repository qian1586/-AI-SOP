namespace VisionForge.Common.Events;

/// <summary>
/// 极简事件聚合器 —— ViewModel 之间通信的解耦手段。
///
/// 为什么需要它：
///   主界面 ViewModel 想通知历史页面"来了一条新检测记录"，
///   如果直接持有对方的引用，两个 ViewModel 就耦合死了。
///   走聚合器则双方都只依赖事件类型，互不认识。
///
/// 用法：
///   <c>_events.Subscribe&lt;InspectionCompletedEvent&gt;(e =&gt; Refresh());</c>
///   <c>_events.Publish(new InspectionCompletedEvent(result));</c>
/// </summary>
public class EventAggregator
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
    private readonly object _gate = new();

    public void Subscribe<TEvent>(Action<TEvent> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                list = new List<Delegate>();
                _handlers[typeof(TEvent)] = list;
            }
            list.Add(handler);
        }
    }

    public void Unsubscribe<TEvent>(Action<TEvent> handler)
    {
        lock (_gate)
        {
            if (_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                list.Remove(handler);
                if (list.Count == 0) _handlers.Remove(typeof(TEvent));
            }
        }
    }

    public void Publish<TEvent>(TEvent evt)
    {
        Delegate[] snapshot;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list) || list.Count == 0)
                return;
            // 快照，避免回调里再订阅/退订导致集合被修改
            snapshot = list.ToArray();
        }

        foreach (var d in snapshot)
        {
            // 单个订阅者出错不影响其他订阅者 —— 工控软件里这点很重要
            try
            {
                ((Action<TEvent>)d).Invoke(evt);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EventAggregator] 订阅者抛出异常: {ex}");
            }
        }
    }

    public void Clear()
    {
        lock (_gate) _handlers.Clear();
    }
}
