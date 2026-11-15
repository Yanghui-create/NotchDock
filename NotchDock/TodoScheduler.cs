using System.Windows.Threading;

namespace NotchDock;

/// <summary>待办提醒调度：每 10 秒扫描一次「截止时间 - 提前量」。</summary>
public sealed class TodoScheduler
{
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollectionWrapper _items;

    public event Action<TodoItem>? Fired;

    public TodoScheduler(IEnumerable<TodoItem> items)
    {
        _items = new ObservableCollectionWrapper(items);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => Sweep();
    }

    public void Start() { _timer.Start(); Sweep(); }
    public void Stop() => _timer.Stop();

    /// <summary>数据源变化后调用，保证调度器看到最新列表</summary>
    public void Refresh(IEnumerable<TodoItem> items) => _items.Reset(items);

    public void Sweep()
    {
        var now = DateTime.Now;
        foreach (var t in _items.Snapshot())
        {
            if (t.Done || t.Fired) continue;
            if (now < t.FireAt) continue;

            t.Fired = true;
            try { Fired?.Invoke(t); } catch (Exception ex) { Log.Ex("触发提醒异常", ex); }
        }
    }

    /// <summary>把一条待办重新排程（稍后提醒）</summary>
    public static void Snooze(TodoItem item, int minutes)
    {
        item.Due = DateTime.Now.AddMinutes(minutes + item.BeforeMinutes);
        item.Fired = false;
        item.Touch();
    }

    private sealed class ObservableCollectionWrapper
    {
        private List<TodoItem> _items;
        public ObservableCollectionWrapper(IEnumerable<TodoItem> items) => _items = items.ToList();
        public void Reset(IEnumerable<TodoItem> items) => _items = items.ToList();
        public List<TodoItem> Snapshot() => _items.ToList();
    }
}
