namespace WdBot.Service;

/// <summary>
/// 記錄每個公頻（大廳）由哪個機器人佔用，確保一個廳只有一個機器人。
/// 只在單一服務程序內有效：同時執行多個機器人服務實例時，彼此看不到對方的機器人。
/// </summary>
public sealed class ChannelRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Env, long ChannelId), string> _holders = new();

    /// <summary>
    /// 嘗試佔用公頻。成功時同時釋放這個機器人先前佔的其他公頻。
    /// 失敗時 holderBotId 為目前佔用者。
    /// </summary>
    public bool TryClaim(string env, long channelId, string botId, out string holderBotId)
    {
        var key = (env.ToLowerInvariant(), channelId);
        lock (_gate)
        {
            if (_holders.TryGetValue(key, out var holder) && holder != botId)
            {
                holderBotId = holder;
                return false;
            }

            RemoveLocked(botId);
            _holders[key] = botId;
            holderBotId = botId;
            return true;
        }
    }

    /// <summary>釋放這個機器人佔用的公頻（斷線或停止時呼叫）。</summary>
    public void Release(string botId)
    {
        lock (_gate)
        {
            RemoveLocked(botId);
        }
    }

    private void RemoveLocked(string botId)
    {
        foreach (var key in _holders.Where(kv => kv.Value == botId).Select(kv => kv.Key).ToList())
        {
            _holders.Remove(key);
        }
    }
}
