namespace WdBot.Game;

/// <summary>記住最近看過的 ID（例如 message_id），用來去重。超過容量時淘汰最舊的。</summary>
public sealed class RecentIdCache
{
    /// <summary>最多記住幾個 ID。</summary>
    private readonly int _capacity;

    /// <summary>已看過的 ID，用來快速查詢。</summary>
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);

    /// <summary>ID 加入的先後順序，用來淘汰最舊的。</summary>
    private readonly Queue<string> _order = new();

    /// <summary>保護 _set 與 _order。</summary>
    private readonly object _gate = new();

    /// <summary>建立快取，capacity 為最多記住的 ID 數量。</summary>
    /// <param name="capacity">最多記住幾個 ID，超過時淘汰最舊的。</param>
    public RecentIdCache(int capacity = 2000)
    {
        _capacity = capacity;
    }

    /// <summary>第一次看到時回傳 true；看過則回傳 false。</summary>
    /// <param name="id">要檢查的 ID，例如 message_id。</param>
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
