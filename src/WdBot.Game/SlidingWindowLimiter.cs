namespace WdBot.Game;

/// <summary>滑動視窗限流：window 內最多 limit 次，超過就等到最舊的一次過期。</summary>
internal sealed class SlidingWindowLimiter
{
    private readonly int _limit;
    private readonly long _windowMs;
    private readonly Queue<long> _stamps = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SlidingWindowLimiter(int limit, TimeSpan window)
    {
        _limit = limit;
        _windowMs = (long)window.TotalMilliseconds;
    }

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
