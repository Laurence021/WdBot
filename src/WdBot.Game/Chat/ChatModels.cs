using System.Text.Json;

namespace WdBot.Game.Chat;

public static class ChatOpcodes
{
    public const int Connected = 1;
    public const int PrivateMessage = 2;
    public const int ChannelMessage = 3;
    public const int ReadReceipt = 6;
    public const int ChannelJoined = 7;
    public const int Kicked = 8;
    public const int ChannelMembers = 9;
    public const int ChannelMembersChanged = 17;
}

public static class ChatErrorCodes
{
    public const int TooFrequent = 2044;
    public const int SendTooFast = 5007;
    public const int LevelTooLow = 5008;
    public const int ChatDisabled = 5011;
    public const int TextTooLong = 11007;
    public const int SendPrivateFailed = 11012;
    public const int EmptyText = 11026;

    // opcode 8 的 err_code
    public const int KickDuplicateLogin = 7000;
    public const int KickForced = 7001;
    public const int KickPasswordChanged = 7002;
    public const int KickPendingDeletion = 7003;
    public const int KickSuspended = 7004;
    public const int KickMaintenance = 7005;
    public const int KickDisabled = 7006;
}

/// <summary>上行封包。data 必須是 JSON 編碼後的字串，沒有參數也要送空字串。</summary>
public sealed class ChatUpPacket
{
    public int Opcode { get; set; }
    public string Data { get; set; } = "";
    public string? TraceId { get; set; }
}

/// <summary>下行封包。data 是 JSON 編碼後的字串，用 <see cref="GetData{T}"/> 解開。</summary>
public sealed class ChatDownPacket
{
    public bool Success { get; set; }
    public int Opcode { get; set; }
    public JsonElement? Data { get; set; }
    public int ErrCode { get; set; }
    public string? ErrMsg { get; set; }
    public string? TraceId { get; set; }
    public DateTimeOffset? ServerTime { get; set; }
    public string? ServerName { get; set; }

    public bool IsError => ErrCode != 0;

    /// <summary>data 的原始內容（字串時解開一層，方便閱讀），記錄 log 用。</summary>
    public string? RawDataText => Data switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } d => d.GetString(),
        { } d => d.GetRawText(),
    };

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
    public long ReceivePlayerId { get; set; }
    public int MessageType { get; set; } = 1;
    public string Text { get; set; } = "";
    public string? PlayerName { get; set; }
    public long? AvatarId { get; set; }
    public long? AvatarOuterId { get; set; }
    public string? AvatarUrl { get; set; }
    public long? ChatBoxFrameId { get; set; }
    public string? ReplyMessageId { get; set; }
}

/// <summary>opcode 6 已讀。</summary>
public sealed record ReadReceiptData(string ChatId, string MessageId);

/// <summary>發訊息時帶入的發送者外觀。</summary>
public sealed record SenderProfile(string PlayerName, long AvatarId, long AvatarOuterId, string? AvatarUrl, long ChatBoxFrameId);

/// <summary>opcode 2 推送的私訊物件（data 解開後）。</summary>
public sealed class PrivateMessage
{
    public List<long> ChatPlayerIds { get; set; } = [];
    public string ChatId { get; set; } = "";
    public string MessageId { get; set; } = "";
    public long PlayerId { get; set; }
    public string PlayerName { get; set; } = "";
    public long AvatarId { get; set; }
    public long AvatarOuterId { get; set; }
    public string? AvatarUrl { get; set; }
    public string Text { get; set; } = "";
    public int MessageType { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public ReplyMessageInfo? ReplyMessage { get; set; }
}

public sealed class ReplyMessageInfo
{
    public string? MessageId { get; set; }
    public long PlayerId { get; set; }
    public string? PlayerName { get; set; }
    public int MessageType { get; set; }
    public string? Text { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>原訊息已不存在時為 true，其他欄位都不帶。</summary>
    public bool IsNotFound { get; set; }
}

/// <summary>opcode 3 推送的公頻訊息（data 解開後）。</summary>
public sealed class ChannelMessage
{
    public long ChannelId { get; set; }
    public string MessageId { get; set; } = "";
    public long PlayerId { get; set; }
    public string PlayerName { get; set; } = "";
    public string Text { get; set; } = "";
    public int MessageType { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>公頻成員（opcode 9 的 player_list、opcode 17 的 joined）。</summary>
public sealed class ChannelMember
{
    public long PlayerId { get; set; }
    public string PlayerName { get; set; } = "";
    public int Level { get; set; }
    public string? AvatarUrl { get; set; }
    public long AvatarId { get; set; }
    public long AvatarOuterId { get; set; }
}

/// <summary>opcode 9 上行：第一頁送空字串，下一頁帶 cursor。</summary>
public sealed record ChannelMembersRequest(string Cursor);

/// <summary>opcode 9 推送：公頻成員一頁（每頁 100 人）。</summary>
public sealed class ChannelMembersPage
{
    public long ChannelId { get; set; }
    public string? ChannelName { get; set; }
    public int OnlineCount { get; set; }
    public int TotalCount { get; set; }
    public List<ChannelMember> PlayerList { get; set; } = [];

    /// <summary>空字串表示最後一頁。</summary>
    public string? NextCursor { get; set; }
}

/// <summary>opcode 17 推送：公頻成員異動，每 3 秒彙整一次。</summary>
public sealed class ChannelMembersChangedData
{
    public long ChannelId { get; set; }
    public int OnlineCount { get; set; }
    public int TotalCount { get; set; }
    public List<ChannelMember> Joined { get; set; } = [];
    public List<long> Left { get; set; } = [];
}

/// <summary>翻完所有分頁後的公頻名單。</summary>
public sealed record ChannelSnapshot(
    long ChannelId, string? ChannelName, int OnlineCount, int TotalCount, IReadOnlyList<ChannelMember> Players);

/// <summary>opcode 7 已加入公頻。</summary>
public sealed class ChannelJoinedData
{
    public long ChannelId { get; set; }
    public string? ChannelName { get; set; }
    public int OnlineCount { get; set; }
    public int TotalCount { get; set; }
}
