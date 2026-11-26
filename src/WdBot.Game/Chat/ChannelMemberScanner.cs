using Microsoft.Extensions.Logging;

namespace WdBot.Game.Chat;

/// <summary>透過聊天 WS opcode 9 翻頁取得目前所在公頻的完整線上名單（每頁 100 人）。</summary>
public sealed class ChannelMemberScanner
{
    /// <summary>最多翻幾頁（每頁 100 人），避免伺服器異常時無限翻頁。</summary>
    private const int MaxPages = 200;

    /// <summary>每一頁最多嘗試幾次。</summary>
    private const int MaxAttemptsPerPage = 2;

    /// <summary>翻頁間隔，避免超過聊天 WS 每秒 50 則指令的限制。</summary>
    private static readonly TimeSpan PageInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>等待每一頁回應的逾時時間。</summary>
    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(10);

    /// <summary>記錄翻頁結果與重試。</summary>
    private readonly ILogger _logger;

    /// <summary>建立掃描器。</summary>
    /// <param name="logger">記錄翻頁結果與重試。</param>
    public ChannelMemberScanner(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 從第一頁翻到 next_cursor 為空字串。依 player_id 去重（翻頁期間有人進出時，同一人可能出現在兩頁）。
    /// firstPage 不為 null 時（遊戲方主動推送的第一頁），直接用它當第一頁，只向伺服器要之後的頁面。
    /// 失敗時拋出例外。
    /// </summary>
    /// <param name="client">目前的聊天 WS 連線，用來送出 opcode 9。</param>
    /// <param name="firstPage">遊戲方主動推送的第一頁；null 時從第一頁開始向伺服器要。</param>
    /// <param name="ct">用來取消翻頁。</param>
    public async Task<ChannelSnapshot> ScanAsync(ChatWsClient client, ChannelMembersPage? firstPage, CancellationToken ct)
    {
        var players = new Dictionary<long, ChannelMember>();
        ChannelMembersPage? last = null;
        var cursor = "";

        for (var page = 1; page <= MaxPages; page++)
        {
            if (page == 1 && firstPage is not null)
            {
                last = firstPage;
            }
            else
            {
                if (page > 1)
                {
                    await Task.Delay(PageInterval, ct).ConfigureAwait(false);
                }

                last = await FetchPageAsync(client, cursor, page, ct).ConfigureAwait(false);
            }

            foreach (var member in last.PlayerList)
            {
                if (member.PlayerId != 0)
                {
                    players[member.PlayerId] = member;
                }
            }

            if (string.IsNullOrEmpty(last.NextCursor))
            {
                _logger.LogInformation("公頻 {ChannelId}({ChannelName}) 名單取得完成：{Pages} 頁、{Count} 位玩家",
                    last.ChannelId, last.ChannelName, page, players.Count);
                return new ChannelSnapshot(last.ChannelId, last.ChannelName, last.OnlineCount, last.TotalCount,
                    players.Values.ToList());
            }

            cursor = last.NextCursor;
        }

        _logger.LogWarning("公頻名單超過 {MaxPages} 頁，停止翻頁，只回報已取得的 {Count} 位玩家", MaxPages, players.Count);
        return new ChannelSnapshot(last!.ChannelId, last.ChannelName, last.OnlineCount, last.TotalCount, players.Values.ToList());
    }

    /// <summary>送出 opcode 9 取得一頁並等待回包；失敗或逾時會重試一次，仍失敗就拋出例外。</summary>
    /// <param name="client">目前的聊天 WS 連線。</param>
    /// <param name="cursor">上一頁回傳的 next_cursor；第一頁為空字串。</param>
    /// <param name="page">第幾頁（從 1 開始），用於錯誤訊息。</param>
    /// <param name="ct">用來取消作業。</param>
    private async Task<ChannelMembersPage> FetchPageAsync(ChatWsClient client, string cursor, int page, CancellationToken ct)
    {
        // 第一頁 data 送空字串，之後帶 {"cursor": "..."}
        object? data = cursor.Length == 0 ? null : new ChannelMembersRequest(cursor);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var packet = await client.SendAsync(ChatOpcodes.ChannelMembers, data, awaitReply: true, PageTimeout, ct)
                    .ConfigureAwait(false);

                if (packet is { IsError: false } && packet.GetData<ChannelMembersPage>() is { } result)
                {
                    return result;
                }

                var reason = packet is null ? "沒有回應" : $"{packet.ErrCode} {packet.ErrMsg}";
                if (attempt >= MaxAttemptsPerPage)
                {
                    throw new InvalidOperationException($"取得公頻名單第 {page} 頁失敗：{reason}");
                }

                _logger.LogWarning("取得公頻名單第 {Page} 頁失敗：{Reason}，重試", page, reason);
            }
            catch (ChatTimeoutException) when (attempt < MaxAttemptsPerPage)
            {
                _logger.LogWarning("取得公頻名單第 {Page} 頁逾時，重試", page);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }
}
