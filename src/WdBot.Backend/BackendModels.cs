using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WdBot.Backend;

public static class BackendJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}

/// <summary>與微服務之間的封包外層：{ type, id, ts, payload }。</summary>
public sealed class BackendEnvelope
{
    public string Type { get; set; } = "";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Ts { get; set; } = DateTimeOffset.UtcNow;
    public JsonElement? Payload { get; set; }

    public static BackendEnvelope Create<T>(string type, T payload) => new()
    {
        Type = type,
        Payload = JsonSerializer.SerializeToElement(payload, BackendJson.Options),
    };
}

public static class BackendMessageTypes
{
    // 程式 → 微服務
    public const string Hello = "hello";
    public const string MessageReceived = "message.received";
    public const string SendResult = "message.send_result";
    public const string BotStatus = "bot.status";
    public const string ChannelSnapshot = "channel.snapshot";
    public const string ChannelMembersChanged = "channel.members_changed";

    // 微服務 → 程式
    public const string ConfigSync = "config.sync";
    public const string ConfigUpsert = "config.upsert";
    public const string ConfigRemove = "config.remove";
    public const string MessageSend = "message.send";
}

/// <summary>後台下發的單一機器人設定。</summary>
public sealed class BotConfig
{
    public string BotId { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>prod（正式站）或 test（測試站）。</summary>
    public string Env { get; set; } = "test";

    public string Account { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>每個機器人固定一個，token 綁定它；變更會導致重新登入。</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>1 Android、2 iOS、3 APK、4 官網。</summary>
    public int DeviceModel { get; set; } = 1;

    public string Os { get; set; } = "";
    public string AppVersion { get; set; } = "";

    /// <summary>選填：手機型號，提供給 Header 設定中的 {phone_model} 變數；沒填時用 Game:DefaultPhoneModel。</summary>
    public string? PhoneModel { get; set; }

    /// <summary>
    /// 選填：這個機器人自己的 Header，同名時覆寫 appsettings 的 Game:RequestHeaders（App API 與聊天 WS 都套用）。
    /// 值為空字串表示不送這個 Header。
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>收到私訊後是否自動送已讀（opcode 6）。</summary>
    public bool AutoMarkRead { get; set; } = true;

    /// <summary>上線後是否掃描公頻線上名單，並持續回報成員進出。</summary>
    public bool ScanChannelPlayers { get; set; }

    /// <summary>這些欄位改變時必須重新登入（重啟機器人）。</summary>
    public bool HasSameIdentity(BotConfig other) =>
        BotId == other.BotId
        && string.Equals(Env, other.Env, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Account, other.Account, StringComparison.OrdinalIgnoreCase)
        && Password == other.Password
        && DeviceId == other.DeviceId
        && DeviceModel == other.DeviceModel
        && Os == other.Os
        && AppVersion == other.AppVersion
        && (PhoneModel ?? "") == (other.PhoneModel ?? "")
        && SameHeaders(Headers, other.Headers);

    /// <summary>Header 名稱不分大小寫、值完全相同；null 與空集合視為相同。</summary>
    private static bool SameHeaders(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        a ??= [];
        b ??= [];
        if (a.Count != b.Count)
        {
            return false;
        }

        var other = new Dictionary<string, string>(b, StringComparer.OrdinalIgnoreCase);
        return a.All(kv => other.TryGetValue(kv.Key, out var value) && value == kv.Value);
    }

    /// <summary>回傳錯誤說明；設定有效時回傳 null。</summary>
    public string? Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(BotId)) missing.Add("bot_id");
        if (string.IsNullOrWhiteSpace(Account)) missing.Add("account");
        if (string.IsNullOrWhiteSpace(Password)) missing.Add("password");
        if (string.IsNullOrWhiteSpace(DeviceId)) missing.Add("device_id");
        if (string.IsNullOrWhiteSpace(Os)) missing.Add("os");
        if (string.IsNullOrWhiteSpace(AppVersion)) missing.Add("app_version");
        if (missing.Count > 0) return "缺少必填欄位：" + string.Join("、", missing);
        if (DeviceModel is < 1 or > 4) return "device_model 必須是 1–4";
        return null;
    }

    /// <summary>不含密碼，可安全寫進 log。</summary>
    public override string ToString() => $"{BotId}({Account}@{Env})";
}

public sealed class ConfigSyncPayload
{
    public List<BotConfig> Bots { get; set; } = [];
}

public sealed class ConfigRemovePayload
{
    public string BotId { get; set; } = "";
}

/// <summary>微服務要求機器人私訊玩家（回覆與主動私訊共用）。</summary>
public sealed class SendMessageCommand
{
    public string RequestId { get; set; } = "";
    public string BotId { get; set; } = "";
    public long ToPlayerId { get; set; }
    public string Text { get; set; } = "";
    public string? ReplyToMessageId { get; set; }
}

public sealed record HelloPayload(string ServiceInstanceId, string Version, IReadOnlyList<string> Capabilities);

/// <summary>玩家私訊機器人。</summary>
public sealed class IncomingMessageReport
{
    public string BotId { get; set; } = "";
    public long BotPlayerId { get; set; }
    public string ChatId { get; set; } = "";
    public string MessageId { get; set; } = "";
    public long FromPlayerId { get; set; }
    public string FromPlayerName { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>1 文字、2 圖片、3 貼圖、4 贈禮、5 大聲公、6 大獎時刻、7 系統訊息。</summary>
    public int MessageType { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }
    public ReplyMessageReport? ReplyMessage { get; set; }
}

public sealed class ReplyMessageReport
{
    public string? MessageId { get; set; }
    public long PlayerId { get; set; }
    public string? PlayerName { get; set; }
    public string? Text { get; set; }
    public int MessageType { get; set; }
    public bool IsNotFound { get; set; }
}

public sealed class SendResultReport
{
    public string RequestId { get; set; } = "";
    public string BotId { get; set; } = "";
    public bool Success { get; set; }
    public List<string> MessageIds { get; set; } = [];
    public int ErrCode { get; set; }
    public string? ErrMsg { get; set; }
}

/// <summary>公頻中的一位玩家。</summary>
public sealed class ChannelPlayer
{
    public long PlayerId { get; set; }
    public string PlayerName { get; set; } = "";
    public int Level { get; set; }
    public long AvatarId { get; set; }
    public long AvatarOuterId { get; set; }
    public string? AvatarUrl { get; set; }
}

/// <summary>機器人所在公頻的完整線上名單（不含機器人自己）。</summary>
public sealed class ChannelSnapshotReport
{
    public string BotId { get; set; } = "";
    public long BotPlayerId { get; set; }
    public long ChannelId { get; set; }
    public string? ChannelName { get; set; }
    public int OnlineCount { get; set; }
    public int TotalCount { get; set; }
    public List<ChannelPlayer> Players { get; set; } = [];
}

/// <summary>公頻成員進出（不含機器人自己）。</summary>
public sealed class ChannelMembersChangedReport
{
    public string BotId { get; set; } = "";
    public long BotPlayerId { get; set; }
    public long ChannelId { get; set; }
    public int OnlineCount { get; set; }
    public int TotalCount { get; set; }
    public List<ChannelPlayer> Joined { get; set; } = [];
    public List<long> Left { get; set; } = [];
}

public enum BotState
{
    Starting,
    LoggingIn,
    Connecting,
    Online,
    Reconnecting,

    /// <summary>每個公頻都已有其他機器人，暫時下線，定時再試。</summary>
    WaitingChannel,

    Stopped,
    Error,
}

public sealed class BotStatusReport
{
    public string BotId { get; set; } = "";
    public BotState State { get; set; }
    public long? PlayerId { get; set; }
    public string? PlayerName { get; set; }
    public int? Level { get; set; }

    /// <summary>機器人目前佔用的公頻；沒有時不帶。</summary>
    public long? ChannelId { get; set; }

    public int? ErrCode { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
