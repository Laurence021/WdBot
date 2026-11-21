namespace WdBot.Game;

/// <summary>記住最近看過的 ID（例如 message_id），用來去重。超過容量時淘汰最舊的。</summary>
public sealed class RecentIdCache
{
    private readonly int _capacity;
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    public RecentIdCache(int capacity = 2000)
    {
        _capacity = capacity;
    }

    /// <summary>第一次看到時回傳 true；看過則回傳 false。</summary>
    public bool TryAdd(string id)
    {
        lock (_gate)
        {
            if (!_set.Add(id))
            {
                return false;
            }

            _order.Enqueue(id);
            while (_order.Count > _capacity)
            {
                _set.Remove(_order.Dequeue());
            }

            return true;
        }
    }
}
