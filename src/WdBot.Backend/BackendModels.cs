using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WdBot.Backend;

/// <summary>與微服務交換資料時共用的 JSON 設定：欄位名稱用 snake_case、null 不輸出、列舉以字串表示。</summary>
public static class BackendJson
{
    /// <summary>序列化與反序列化微服務封包使用的選項。</summary>
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
    /// <summary>封包類型，見 <see cref="BackendMessageTypes"/>。</summary>
    public string Type { get; set; } = "";

    /// <summary>封包 ID，預設為新的 GUID（不含連字號）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>送出時間（UTC）。</summary>
    public DateTimeOffset Ts { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>封包內容，依 <see cref="Type"/> 解析成對應的類別。</summary>
    public JsonElement? Payload { get; set; }

    /// <summary>建立指定類型的封包，並把 payload 序列化成 JSON。</summary>
    /// <typeparam name="T">payload 的型別。</typeparam>
    /// <param name="type">封包類型，例如 bot.status。</param>
    /// <param name="payload">封包內容，會序列化成 JSON 放進 payload。</param>
    public static BackendEnvelope Create<T>(string type, T payload) => new()
    {
        Type = type,
        Payload = JsonSerializer.SerializeToElement(payload, BackendJson.Options),
    };
}

/// <summary>與微服務之間的封包類型名稱。</summary>
public static class BackendMessageTypes
{
    // 程式 → 微服務

    /// <summary>連上微服務後的第一個封包，表明身分與支援的功能。</summary>
    public const string Hello = "hello";

    /// <summary>玩家私訊機器人。</summary>
    public const string MessageReceived = "message.received";

    /// <summary>私訊指令的執行結果。</summary>
    public const string SendResult = "message.send_result";

    /// <summary>機器人狀態變更。</summary>
    public const string BotStatus = "bot.status";

    /// <summary>機器人所在公頻的完整線上名單。</summary>
    public const string ChannelSnapshot = "channel.snapshot";

    /// <summary>公頻成員進出。</summary>
    public const string ChannelMembersChanged = "channel.members_changed";

    // 微服務 → 程式

    /// <summary>全量機器人設定。</summary>
    public const string ConfigSync = "config.sync";

    /// <summary>新增或更新單一機器人設定。</summary>
    public const string ConfigUpsert = "config.upsert";

    /// <summary>移除單一機器人。</summary>
    public const string ConfigRemove = "config.remove";

    /// <summary>要求機器人私訊玩家。</summary>
    public const string MessageSend = "message.send";
}

/// <summary>後台下發的單一機器人設定。</summary>
public sealed class BotConfig
{
    /// <summary>機器人在後台的唯一 ID。</summary>
    public string BotId { get; set; } = "";

    /// <summary>是否啟用；false 時停止這個機器人。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>prod（正式站）或 test（測試站）。</summary>
    public string Env { get; set; } = "test";

    /// <summary>遊戲帳號。</summary>
    public string Account { get; set; } = "";

    /// <summary>遊戲密碼；不可寫進 log。</summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// 選填：裝置 ID，token 綁定它；變更會導致重新登入。
    /// 沒填時由機器人服務從 device id 清單隨機分配（連同清單中的裝置欄位）。
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary>1 Android、2 iOS、3 APK、4 官網。</summary>
    public int DeviceModel { get; set; } = 1;

    /// <summary>作業系統，例如 Android14；從 device id 清單分配且清單有填 os 時，以清單為準。</summary>
    public string Os { get; set; } = "";

    /// <summary>選填：裝置型號，例如 Samsung SM-A546E；登入 API 的選填裝置欄位，沒填時不送。</summary>
    public string? DeviceType { get; set; }

    /// <summary>選填：所在地區，例如 Taipei District；登入 API 的選填裝置欄位，沒填時不送。</summary>
    public string? Location { get; set; }

    /// <summary>遊戲 App 版本，例如 1.2.0。</summary>
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
    /// <param name="other">要比較的另一份設定，通常是新收到的設定。</param>
    public bool HasSameIdentity(BotConfig other) =>
        BotId == other.BotId
        && string.Equals(Env, other.Env, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Account, other.Account, StringComparison.OrdinalIgnoreCase)
        && Password == other.Password
        && DeviceId == other.DeviceId
        && DeviceModel == other.DeviceModel
        && Os == other.Os
        && (DeviceType ?? "") == (other.DeviceType ?? "")
        && (Location ?? "") == (other.Location ?? "")
        && AppVersion == other.AppVersion
        && (PhoneModel ?? "") == (other.PhoneModel ?? "")
        && SameHeaders(Headers, other.Headers);

    /// <summary>Header 名稱不分大小寫、值完全相同；null 與空集合視為相同。</summary>
    /// <param name="a">第一組 Header；可為 null。</param>
    /// <param name="b">第二組 Header；可為 null。</param>
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

    /// <summary>
    /// 回傳錯誤說明；設定有效時回傳 null。
    /// device_id、os 可以由 device id 清單提供，所以不在這裡檢查，分配完後再檢查。
    /// </summary>
    public string? Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(BotId)) missing.Add("bot_id");
        if (string.IsNullOrWhiteSpace(Account)) missing.Add("account");
        if (string.IsNullOrWhiteSpace(Password)) missing.Add("password");
        if (string.IsNullOrWhiteSpace(AppVersion)) missing.Add("app_version");
        if (missing.Count > 0) return "缺少必填欄位：" + string.Join("、", missing);
        if (DeviceModel is < 1 or > 4) return "device_model 必須是 1–4";
        return null;
    }

    /// <summary>淺層複本（Headers 字典共用同一個實例，不會被修改）。</summary>
    public BotConfig Copy() => (BotConfig)MemberwiseClone();

    /// <summary>不含密碼，可安全寫進 log。</summary>
    public override string ToString() => $"{BotId}({Account}@{Env})";
}

/// <summary>config.sync 的內容：全部機器人的設定。</summary>
public sealed class ConfigSyncPayload
{
    /// <summary>機器人設定清單；不在清單中的機器人會被停止。</summary>
    public List<BotConfig> Bots { get; set; } = [];
}

/// <summary>config.remove 的內容。</summary>
public sealed class ConfigRemovePayload
{
    /// <summary>要移除的機器人 ID。</summary>
    public string BotId { get; set; } = "";
}

/// <summary>微服務要求機器人私訊玩家（回覆與主動私訊共用）。</summary>
public sealed class SendMessageCommand
{
    /// <summary>微服務產生的請求 ID，回報結果時原樣帶回。</summary>
    public string RequestId { get; set; } = "";

    /// <summary>由哪個機器人送出。</summary>
    public string BotId { get; set; } = "";

    /// <summary>收訊玩家的 player_id。</summary>
    public long ToPlayerId { get; set; }

    /// <summary>訊息內容；超過 200 字會自動分段送出。</summary>
    public string Text { get; set; } = "";

    /// <summary>要回覆（引用）的訊息 ID；不是回覆時為 null。</summary>
    public string? ReplyToMessageId { get; set; }
}

/// <summary>hello 的內容。</summary>
/// <param name="ServiceInstanceId">機器人服務的實例 ID。</param>
/// <param name="Version">機器人服務的版本。</param>
/// <param name="Capabilities">支援的功能清單。</param>
public sealed record HelloPayload(string ServiceInstanceId, string Version, IReadOnlyList<string> Capabilities);

/// <summary>玩家私訊機器人。</summary>
public sealed class IncomingMessageReport
{
    /// <summary>收到私訊的機器人 ID。</summary>
    public string BotId { get; set; } = "";

    /// <summary>機器人在遊戲中的 player_id。</summary>
    public long BotPlayerId { get; set; }

    /// <summary>私訊聊天室 ID。</summary>
    public string ChatId { get; set; } = "";

    /// <summary>訊息 ID。</summary>
    public string MessageId { get; set; } = "";

    /// <summary>發訊玩家的 player_id。</summary>
    public long FromPlayerId { get; set; }

    /// <summary>發訊玩家的暱稱。</summary>
    public string FromPlayerName { get; set; } = "";

    /// <summary>訊息內容。</summary>
    public string Text { get; set; } = "";

    /// <summary>1 文字、2 圖片、3 貼圖、4 贈禮、5 大聲公、6 大獎時刻、7 系統訊息。</summary>
    public int MessageType { get; set; }

    /// <summary>訊息建立時間。</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>這則訊息引用的原訊息；不是回覆時為 null。</summary>
    public ReplyMessageReport? ReplyMessage { get; set; }
}

/// <summary>私訊引用的原訊息。</summary>
public sealed class ReplyMessageReport
{
    /// <summary>原訊息 ID。</summary>
    public string? MessageId { get; set; }

    /// <summary>原訊息發送者的 player_id。</summary>
    public long PlayerId { get; set; }

    /// <summary>原訊息發送者的暱稱。</summary>
    public string? PlayerName { get; set; }

    /// <summary>原訊息內容。</summary>
    public string? Text { get; set; }

    /// <summary>原訊息類型，數值同 <see cref="IncomingMessageReport.MessageType"/>。</summary>
    public int MessageType { get; set; }

    /// <summary>原訊息是否已不存在（例如已被刪除）。</summary>
    public bool IsNotFound { get; set; }
}

/// <summary>私訊指令的執行結果。</summary>
public sealed class SendResultReport
{
    /// <summary>對應的私訊指令請求 ID。</summary>
    public string RequestId { get; set; } = "";

    /// <summary>執行的機器人 ID。</summary>
    public string BotId { get; set; } = "";

    /// <summary>是否全部送出成功。</summary>
    public bool Success { get; set; }

    /// <summary>遊戲回傳的訊息 ID；分段送出時有多筆。</summary>
    public List<string> MessageIds { get; set; } = [];

    /// <summary>失敗時的錯誤碼：遊戲方的錯誤碼，或本機的負數錯誤碼。</summary>
    public int ErrCode { get; set; }

    /// <summary>失敗時的錯誤說明。</summary>
    public string? ErrMsg { get; set; }
}

/// <summary>公頻中的一位玩家。</summary>
public sealed class ChannelPlayer
{
    /// <summary>玩家 ID。</summary>
    public long PlayerId { get; set; }

    /// <summary>暱稱。</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>等級。</summary>
    public int Level { get; set; }

    /// <summary>頭像 ID。</summary>
    public long AvatarId { get; set; }

    /// <summary>頭像框 ID。</summary>
    public long AvatarOuterId { get; set; }

    /// <summary>自訂頭像網址；沒有自訂頭像時為 null。</summary>
    public string? AvatarUrl { get; set; }
}

/// <summary>機器人所在公頻的完整線上名單（不含機器人自己）。</summary>
public sealed class ChannelSnapshotReport
{
    /// <summary>回報的機器人 ID。</summary>
    public string BotId { get; set; } = "";

    /// <summary>機器人在遊戲中的 player_id。</summary>
    public long BotPlayerId { get; set; }

    /// <summary>公頻 ID。</summary>
    public long ChannelId { get; set; }

    /// <summary>公頻名稱。</summary>
    public string? ChannelName { get; set; }

    /// <summary>伺服器回報的在線人數。</summary>
    public int OnlineCount { get; set; }

    /// <summary>伺服器回報的頻道總人數。</summary>
    public int TotalCount { get; set; }

    /// <summary>在線玩家；同一位玩家只出現一次。</summary>
    public List<ChannelPlayer> Players { get; set; } = [];
}

/// <summary>公頻成員進出（不含機器人自己）。</summary>
public sealed class ChannelMembersChangedReport
{
    /// <summary>回報的機器人 ID。</summary>
    public string BotId { get; set; } = "";

    /// <summary>機器人在遊戲中的 player_id。</summary>
    public long BotPlayerId { get; set; }

    /// <summary>公頻 ID。</summary>
    public long ChannelId { get; set; }

    /// <summary>異動後的在線人數。</summary>
    public int OnlineCount { get; set; }

    /// <summary>異動後的頻道總人數。</summary>
    public int TotalCount { get; set; }

    /// <summary>進入公頻的玩家。</summary>
    public List<ChannelPlayer> Joined { get; set; } = [];

    /// <summary>離開公頻的玩家 ID。</summary>
    public List<long> Left { get; set; } = [];
}

/// <summary>機器人狀態。</summary>
public enum BotState
{
    /// <summary>啟動中。</summary>
    Starting,

    /// <summary>登入遊戲中。</summary>
    LoggingIn,

    /// <summary>連線聊天 WS 中。</summary>
    Connecting,

    /// <summary>已上線。</summary>
    Online,

    /// <summary>連線中斷或發生暫時性錯誤，準備重連。</summary>
    Reconnecting,

    /// <summary>每個公頻都已有其他機器人，暫時下線，定時再試。</summary>
    WaitingChannel,

    /// <summary>已停止。</summary>
    Stopped,

    /// <summary>發生無法自動恢復的錯誤而停止（例如帳號密碼錯誤、設定無效）。</summary>
    Error,
}

/// <summary>機器人狀態回報（bot.status）。</summary>
public sealed class BotStatusReport
{
    /// <summary>機器人 ID。</summary>
    public string BotId { get; set; } = "";

    /// <summary>目前狀態。</summary>
    public BotState State { get; set; }

    /// <summary>機器人在遊戲中的 player_id；尚未登入時不帶。</summary>
    public long? PlayerId { get; set; }

    /// <summary>機器人在遊戲中的暱稱；尚未登入時不帶。</summary>
    public string? PlayerName { get; set; }

    /// <summary>機器人的等級；尚未登入時不帶。</summary>
    public int? Level { get; set; }

    /// <summary>機器人目前佔用的公頻；沒有時不帶。</summary>
    public long? ChannelId { get; set; }

    /// <summary>錯誤碼（遊戲方或本機）；沒有錯誤時不帶。</summary>
    public int? ErrCode { get; set; }

    /// <summary>狀態說明，例如錯誤原因或提醒。</summary>
    public string? Detail { get; set; }

    /// <summary>狀態更新時間（UTC）。</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
