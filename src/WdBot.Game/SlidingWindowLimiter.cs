namespace WdBot.Game;

/// <summary>滑動視窗限流：window 內最多 limit 次，超過就等到最舊的一次過期。</summary>
internal sealed class SlidingWindowLimiter
{
    /// <summary>視窗內允許的次數。</summary>
    private readonly int _limit;

    /// <summary>視窗長度（毫秒）。</summary>
    private readonly long _windowMs;

    /// <summary>視窗內每次呼叫的時間（Environment.TickCount64），最舊的在前。</summary>
    private readonly Queue<long> _stamps = new();

    /// <summary>讓呼叫端依序取得名額。</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>建立限流器：window 內最多 limit 次。</summary>
    /// <param name="limit">時間視窗內最多幾次呼叫。</param>
    /// <param name="window">時間視窗的長度。</param>
    public SlidingWindowLimiter(int limit, TimeSpan window)
    {
        _limit = limit;
        _windowMs = (long)window.TotalMilliseconds;
    }

    /// <summary>取得一次呼叫的名額；視窗內已滿時等到最舊的一次過期。</summary>
    /// <param name="ct">用來取消等待。</param>
    public async Task WaitAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = Environment.TickCount64;
                while (_stamps.Count > 0 && now - _stamps.Peek() >= _windowMs)
                {
                    _stamps.Dequeue();
                }

                if (_stamps.Count < _limit)
                {
                    _stamps.Enqueue(now);
                    return;
                }

                var waitMs = _stamps.Peek() + _windowMs - now;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(waitMs, 1)), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
