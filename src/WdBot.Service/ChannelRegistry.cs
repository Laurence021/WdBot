namespace WdBot.Service;

/// <summary>
/// 記錄每個公頻（大廳）由哪個機器人佔用，確保一個廳只有一個機器人。
/// 只在單一服務程序內有效：同時執行多個機器人服務實例時，彼此看不到對方的機器人。
/// </summary>
public sealed class ChannelRegistry
{
    /// <summary>保護 _holders。</summary>
    private readonly object _gate = new();

    /// <summary>（環境, 公頻 ID）→ 佔用的機器人 ID。環境名稱一律轉成小寫。</summary>
    private readonly Dictionary<(string Env, long ChannelId), string> _holders = new();

    /// <summary>
    /// 嘗試佔用公頻。成功時同時釋放這個機器人先前佔的其他公頻。
    /// 失敗時 holderBotId 為目前佔用者。
    /// </summary>
    /// <param name="env">環境名稱（例如 prod、test）；不同環境的公頻分開登記。</param>
    /// <param name="channelId">要佔用的公頻 ID。</param>
    /// <param name="botId">要佔用公頻的機器人 ID。</param>
    /// <param name="holderBotId">輸出：目前佔用這個公頻的機器人 ID；成功時為 botId。</param>
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
    /// <param name="botId">機器人 ID。</param>
    public void Release(string botId)
    {
        lock (_gate)
        {
            RemoveLocked(botId);
        }
    }

    /// <summary>移除這個機器人佔用的所有公頻。呼叫前必須持有 _gate。</summary>
    /// <param name="botId">機器人 ID。</param>
    private void RemoveLocked(string botId)
    {
        foreach (var key in _holders.Where(kv => kv.Value == botId).Select(kv => kv.Key).ToList())
        {
            _holders.Remove(key);
        }
    }
}
