using System.Text.Json;

namespace WdBot.Game.Chat;

/// <summary>聊天 WS 的指令代碼（opcode）。</summary>
public static class ChatOpcodes
{
    /// <summary>連線成功（伺服器推送）。</summary>
    public const int Connected = 1;

    /// <summary>私訊：送出私訊，或收到私訊推送。</summary>
    public const int PrivateMessage = 2;

    /// <summary>公頻訊息推送。</summary>
    public const int ChannelMessage = 3;

    /// <summary>私訊已讀：送出已讀，或收到已讀推送。</summary>
    public const int ReadReceipt = 6;

    /// <summary>已加入公頻（連線後伺服器推送）。</summary>
    public const int ChannelJoined = 7;

    /// <summary>被踢下線，err_code 說明原因（見 <see cref="ChatErrorCodes"/> 的 Kick 開頭常數）。</summary>
    public const int Kicked = 8;

    /// <summary>公頻成員列表：送出請求取得一頁，或收到伺服器推送的第一頁。</summary>
    public const int ChannelMembers = 9;

    /// <summary>公頻成員異動推送，伺服器每 3 秒彙整一次。</summary>
    public const int ChannelMembersChanged = 17;
}

/// <summary>聊天 WS 回傳的錯誤碼。</summary>
public static class ChatErrorCodes
{
    /// <summary>指令太頻繁（每條連線每秒最多 50 則）。</summary>
    public const int TooFrequent = 2044;

    /// <summary>訊息發送間隔太快（私訊每 2 秒一則）。</summary>
    public const int SendTooFast = 5007;

    /// <summary>等級不足，無法發言（需要等級 1 以上）。</summary>
    public const int LevelTooLow = 5008;

    /// <summary>後台已關閉此帳號的聊天權限。</summary>
    public const int ChatDisabled = 5011;

    /// <summary>訊息超過長度上限（200 字）。</summary>
    public const int TextTooLong = 11007;

    /// <summary>私訊發送失敗。</summary>
    public const int SendPrivateFailed = 11012;

    /// <summary>訊息內容為空。</summary>
    public const int EmptyText = 11026;

    // opcode 8 的 err_code

    /// <summary>同一帳號在其他地方連線，舊連線被踢下線。</summary>
    public const int KickDuplicateLogin = 7000;

    /// <summary>被後台強制下線。</summary>
    public const int KickForced = 7001;

    /// <summary>密碼已變更。</summary>
    public const int KickPasswordChanged = 7002;

    /// <summary>帳號申請刪除中。</summary>
    public const int KickPendingDeletion = 7003;

    /// <summary>帳號已停權。</summary>
    public const int KickSuspended = 7004;

    /// <summary>聊天室維護中。</summary>
    public const int KickMaintenance = 7005;

    /// <summary>帳號已停用。</summary>
    public const int KickDisabled = 7006;
}

/// <summary>上行封包。data 必須是 JSON 編碼後的字串，沒有參數也要送空字串。</summary>
public sealed class ChatUpPacket
{
    /// <summary>指令代碼，見 <see cref="ChatOpcodes"/>。</summary>
    public int Opcode { get; set; }

    /// <summary>指令參數，JSON 編碼後的字串。</summary>
    public string Data { get; set; } = "";

    /// <summary>自訂的追蹤 ID，伺服器回給發送者時原樣帶回，用來對應回包。</summary>
    public string? TraceId { get; set; }
}

/// <summary>下行封包。data 是 JSON 編碼後的字串，用 <see cref="GetData{T}"/> 解開。</summary>
public sealed class ChatDownPacket
{
    /// <summary>是否成功；失敗時伺服器不帶。</summary>
    public bool Success { get; set; }

    /// <summary>指令代碼，見 <see cref="ChatOpcodes"/>。</summary>
    public int Opcode { get; set; }

    /// <summary>內容，JSON 編碼後的字串；沒有內容時不帶。</summary>
    public JsonElement? Data { get; set; }

    /// <summary>錯誤碼；成功時為 0。</summary>
    public int ErrCode { get; set; }

    /// <summary>錯誤訊息。</summary>
    public string? ErrMsg { get; set; }

    /// <summary>追蹤 ID；只有發送者本人收到的回包才會帶。</summary>
    public string? TraceId { get; set; }

    /// <summary>伺服器時間。</summary>
    public DateTimeOffset? ServerTime { get; set; }

    /// <summary>伺服器名稱。</summary>
    public string? ServerName { get; set; }

    /// <summary>是否帶有錯誤碼。</summary>
    public bool IsError => ErrCode != 0;

    /// <summary>data 的原始內容（字串時解開一層，方便閱讀），記錄 log 用。</summary>
    public string? RawDataText => Data switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } d => d.GetString(),
        { } d => d.GetRawText(),
    };

    /// <summary>把 data 解析成指定類別；沒有內容時回傳 default。</summary>
    /// <typeparam name="T">data 要解析成的類別。</typeparam>
    public T? GetData<T>()
    {
        if (Data is not { } d)
        {
            return default;
        }

        switch (d.ValueKind)
        {
            case JsonValueKind.String:
                var json = d.GetString();
                return string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, GameJson.Options);
            case JsonValueKind.Object or JsonValueKind.Array:
                // 規格說 data 一定是字串，保險起見也接受直接給物件
                return d.Deserialize<T>(GameJson.Options);
            default:
                return default;
        }
    }
}

/// <summary>opcode 2 送出的 data（編碼前）。發送者暱稱、頭像、對話框要自己帶，否則對方看到空白。</summary>
public sealed class SendPrivateMessageData
{
    /// <summary>收訊玩家的 player_id。</summary>
    public long ReceivePlayerId { get; set; }

    /// <summary>訊息類型，1 為文字。</summary>
    public int MessageType { get; set; } = 1;

    /// <summary>訊息內容，最多 200 字。</summary>
    public string Text { get; set; } = "";

    /// <summary>發送者暱稱。</summary>
    public string? PlayerName { get; set; }

    /// <summary>發送者頭像 ID。</summary>
    public long? AvatarId { get; set; }

    /// <summary>發送者頭像框 ID。</summary>
    public long? AvatarOuterId { get; set; }

    /// <summary>發送者自訂頭像網址。</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>發送者的對話框 ID。</summary>
    public long? ChatBoxFrameId { get; set; }

    /// <summary>要回覆（引用）的訊息 ID。</summary>
    public string? ReplyMessageId { get; set; }
}

/// <summary>opcode 6 已讀。</summary>
/// <param name="ChatId">私訊聊天室 ID。</param>
/// <param name="MessageId">已讀到的訊息 ID。</param>
public sealed record ReadReceiptData(string ChatId, string MessageId);

/// <summary>發訊息時帶入的發送者外觀。</summary>
/// <param name="PlayerName">暱稱。</param>
/// <param name="AvatarId">頭像 ID。</param>
/// <param name="AvatarOuterId">頭像框 ID。</param>
/// <param name="AvatarUrl">自訂頭像網址；沒有時為 null。</param>
/// <param name="ChatBoxFrameId">對話框 ID。</param>
public sealed record SenderProfile(string PlayerName, long AvatarId, long AvatarOuterId, string? AvatarUrl, long ChatBoxFrameId);

/// <summary>opcode 2 推送的私訊物件（data 解開後）。</summary>
public sealed class PrivateMessage
{
    /// <summary>私訊聊天室的雙方 player_id。</summary>
    public List<long> ChatPlayerIds { get; set; } = [];

    /// <summary>私訊聊天室 ID。</summary>
    public string ChatId { get; set; } = "";

    /// <summary>訊息 ID。</summary>
    public string MessageId { get; set; } = "";

    /// <summary>發送者的 player_id。</summary>
    public long PlayerId { get; set; }

    /// <summary>發送者暱稱。</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>發送者頭像 ID。</summary>
    public long AvatarId { get; set; }

    /// <summary>發送者頭像框 ID。</summary>
    public long AvatarOuterId { get; set; }

    /// <summary>發送者自訂頭像網址。</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>訊息內容。</summary>
    public string Text { get; set; } = "";

    /// <summary>1 文字、2 圖片、3 貼圖、4 贈禮、5 大聲公、6 大獎時刻、7 系統訊息。</summary>
    public int MessageType { get; set; }

    /// <summary>訊息建立時間。</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>這則訊息引用的原訊息；不是回覆時為 null。</summary>
    public ReplyMessageInfo? ReplyMessage { get; set; }
}

/// <summary>私訊引用的原訊息。</summary>
public sealed class ReplyMessageInfo
{
    /// <summary>原訊息 ID。</summary>
    public string? MessageId { get; set; }

    /// <summary>原訊息發送者的 player_id。</summary>
    public long PlayerId { get; set; }

    /// <summary>原訊息發送者的暱稱。</summary>
    public string? PlayerName { get; set; }

    /// <summary>原訊息類型。</summary>
    public int MessageType { get; set; }

    /// <summary>原訊息內容。</summary>
    public string? Text { get; set; }

    /// <summary>原訊息建立時間。</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>原訊息已不存在時為 true，其他欄位都不帶。</summary>
    public bool IsNotFound { get; set; }
}

/// <summary>opcode 3 推送的公頻訊息（data 解開後）。</summary>
public sealed class ChannelMessage
{
    /// <summary>公頻 ID。</summary>
    public long ChannelId { get; set; }

    /// <summary>訊息 ID。</summary>
    public string MessageId { get; set; } = "";

    /// <summary>發送者的 player_id。</summary>
    public long PlayerId { get; set; }

    /// <summary>發送者暱稱。</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>訊息內容。</summary>
    public string Text { get; set; } = "";

    /// <summary>訊息類型。</summary>
    public int MessageType { get; set; }

    /// <summary>訊息建立時間。</summary>
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>公頻成員（opcode 9 的 player_list、opcode 17 的 joined）。</summary>
public sealed class ChannelMember
{
    /// <summary>玩家 ID。</summary>
    public long PlayerId { get; set; }

    /// <summary>暱稱。</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>等級。</summary>
    public int Level { get; set; }

    /// <summary>自訂頭像網址；沒有時為空字串或 null。</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>頭像 ID。</summary>
    public long AvatarId { get; set; }

    /// <summary>頭像框 ID。</summary>
    public long AvatarOuterId { get; set; }
}

/// <summary>opcode 9 上行：第一頁送空字串，下一頁帶 cursor。</summary>
/// <param name="Cursor">上一頁推送的 next_cursor。</param>
public sealed record ChannelMembersRequest(string Cursor);

/// <summary>opcode 9 推送：公頻成員一頁（每頁 100 人）。</summary>
public sealed class ChannelMembersPage
{
    /// <summary>公頻 ID。</summary>
    public long ChannelId { get; set; }

    /// <summary>公頻名稱。</summary>
    public string? ChannelName { get; set; }

    /// <summary>在線人數。</summary>
    public int OnlineCount { get; set; }

    /// <summary>頻道總人數。</summary>
    public int TotalCount { get; set; }

    /// <summary>這一頁的在線玩家。</summary>
    public List<ChannelMember> PlayerList { get; set; } = [];

    /// <summary>空字串表示最後一頁。</summary>
    public string? NextCursor { get; set; }
}

/// <summary>opcode 17 推送：公頻成員異動，每 3 秒彙整一次。</summary>
public sealed class ChannelMembersChangedData
{
    /// <summary>公頻 ID。</summary>
    public long ChannelId { get; set; }

    /// <summary>異動後的在線人數。</summary>
    public int OnlineCount { get; set; }

    /// <summary>異動後的頻道總人數。</summary>
    public int TotalCount { get; set; }

    /// <summary>進入公頻的玩家。</summary>
    public List<ChannelMember> Joined { get; set; } = [];

    /// <summary>離開公頻的玩家 ID。</summary>
    public List<long> Left { get; set; } = [];
}

/// <summary>翻完所有分頁後的公頻名單。</summary>
/// <param name="ChannelId">公頻 ID。</param>
/// <param name="ChannelName">公頻名稱。</param>
/// <param name="OnlineCount">伺服器回報的在線人數。</param>
/// <param name="TotalCount">伺服器回報的頻道總人數。</param>
/// <param name="Players">在線玩家（依 player_id 去重）。</param>
public sealed record ChannelSnapshot(
    long ChannelId, string? ChannelName, int OnlineCount, int TotalCount, IReadOnlyList<ChannelMember> Players);

/// <summary>opcode 7 已加入公頻。</summary>
public sealed class ChannelJoinedData
{
    /// <summary>公頻 ID。</summary>
    public long ChannelId { get; set; }

    /// <summary>公頻名稱。</summary>
    public string? ChannelName { get; set; }

    /// <summary>在線人數。</summary>
    public int OnlineCount { get; set; }

    /// <summary>頻道總人數。</summary>
    public int TotalCount { get; set; }
}
