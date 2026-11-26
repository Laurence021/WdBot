using System.Text.Json;

namespace WdBot.Game.Models;

/// <summary>App API 的共通回應格式。</summary>
public sealed class ApiEnvelope
{
    /// <summary>錯誤碼；1000 表示成功。</summary>
    public int ErrorCode { get; set; }

    /// <summary>錯誤訊息。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>回應資料，依 API 不同而不同。</summary>
    public JsonElement? Data { get; set; }

    /// <summary>處理請求的伺服器名稱。</summary>
    public string? ServerName { get; set; }

    /// <summary>伺服器處理時間。</summary>
    public DateTimeOffset? ExecutionTime { get; set; }
}

/// <summary>登入／註冊必帶的裝置欄位。device_id 每個機器人固定一個，token 會綁定它。</summary>
/// <param name="DeviceId">裝置 ID。</param>
/// <param name="DeviceModel">1 Android、2 iOS、3 APK、4 官網。</param>
/// <param name="Os">作業系統，例如 Android14。</param>
/// <param name="AppVersion">遊戲 App 版本。</param>
/// <param name="DeviceType">選填：裝置型號，例如 Samsung SM-A546E；null 時不送。</param>
/// <param name="Location">選填：所在地區，例如 Taipei District；null 時不送。</param>
public sealed record DeviceInfo(string DeviceId, int DeviceModel, string Os, string AppVersion,
    string? DeviceType = null, string? Location = null);

/// <summary>帳號密碼登入需要的資料。</summary>
/// <param name="Account">遊戲帳號。</param>
/// <param name="Password">遊戲密碼；不可寫進 log。</param>
/// <param name="Device">裝置欄位。</param>
public sealed record AccountCredentials(string Account, string Password, DeviceInfo Device);

/// <summary>POST /api/v1/player/login/account 的 request body；DeviceType、Location 為 null 時不輸出。</summary>
/// <param name="Account">遊戲帳號。</param>
/// <param name="Password">遊戲密碼；不可寫進 log。</param>
/// <param name="DeviceId">裝置 ID；token 綁定它。</param>
/// <param name="DeviceModel">裝置平台代碼（規格書的 device_model）。</param>
/// <param name="Os">作業系統版本，例如 Android 15。</param>
/// <param name="AppVersion">遊戲 App 的版本號。</param>
/// <param name="DeviceType">裝置型號，例如 Samsung SM-A546E；null 時不輸出。</param>
/// <param name="Location">所在地區，例如 Taipei District；null 時不輸出。</param>
internal sealed record LoginAccountRequest(
    string Account, string Password, string DeviceId, int DeviceModel, string Os, string AppVersion,
    string? DeviceType, string? Location);

/// <summary>POST /api/v1/player/refreshAccessToken 的 request body。</summary>
/// <param name="RefreshToken">登入或上次刷新取得的 refresh_token。</param>
/// <param name="DeviceId">取得 refresh_token 時使用的 device_id。</param>
internal sealed record RefreshTokenRequest(string RefreshToken, string DeviceId);

/// <summary>只帶 player_id 的 request body（例如 getOtherPlayerInfo）。</summary>
/// <param name="PlayerId">玩家 ID。</param>
internal sealed record PlayerIdRequest(long PlayerId);

/// <summary>POST /api/v1/chat/getChatMessage 的 request body：以對象玩家或聊天室指定，message_id 有值時往前翻頁。</summary>
/// <param name="TargetPlayerId">對象玩家 ID；以聊天室指定時為 null。</param>
/// <param name="ChatId">聊天室 ID；以對象玩家指定時為 null。</param>
/// <param name="MessageId">從這則訊息往前取；空字串表示取最新的訊息。</param>
internal sealed record ChatMessageRequest(long? TargetPlayerId, string? ChatId, string MessageId);

/// <summary>帳號密碼登入的回應。</summary>
public sealed class LoginResult
{
    /// <summary>登入玩家的基本資料。</summary>
    public PlayerInfo PlayerInfo { get; set; } = new();

    /// <summary>登入前是否已經在線（例如手機上同時登入中）。</summary>
    public bool IsOnline { get; set; }

    /// <summary>可用的聊天 WS 網址，任選一條連線。</summary>
    public List<string> ChatWsUrls { get; set; } = [];

    /// <summary>access_token，有效 1 小時。</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>refresh_token，有效 30 天，綁定 device_id。</summary>
    public string RefreshToken { get; set; } = "";
}

/// <summary>刷新 token 的回應。</summary>
public sealed class TokenPair
{
    /// <summary>新的 access_token。</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>新的 refresh_token；舊的隨即失效。</summary>
    public string RefreshToken { get; set; } = "";
}

/// <summary>登入回傳的 player_info。值為 0、空字串、false 的欄位可能不帶，這裡都用預設值。</summary>
public class PlayerInfo
{
    /// <summary>玩家 ID（player_id）。</summary>
    public long Id { get; set; }

    /// <summary>暱稱。</summary>
    public string Name { get; set; } = "";

    /// <summary>自訂頭像網址。</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>頭像 ID。</summary>
    public long AvatarId { get; set; }

    /// <summary>頭像框 ID。</summary>
    public long AvatarOuterId { get; set; }

    /// <summary>等級；0 級不能發言。</summary>
    public int Level { get; set; }

    /// <summary>玩家類型。</summary>
    public int Type { get; set; }

    /// <summary>驗證類型。</summary>
    public int AuthType { get; set; }
}

/// <summary>GET /api/v1/player/getPlayerInfo（本人資料）。</summary>
public sealed class SelfPlayerInfo : PlayerInfo
{
    /// <summary>使用者 UID。</summary>
    public string? Uid { get; set; }

    /// <summary>帳號。</summary>
    public string? Account { get; set; }

    /// <summary>對話框 ID，發私訊時要帶上。</summary>
    public long ChatBoxFrameId { get; set; }

    /// <summary>帳號狀態；0 表示正常，其他值代表刪除中或已刪除。</summary>
    public int DeletionType { get; set; }

    /// <summary>帳號權限。</summary>
    public PlayerPrivilege? PlayerPrivilege { get; set; }
}

/// <summary>本人資料中的帳號權限。</summary>
public sealed class PlayerPrivilege
{
    /// <summary>false 時不能發私訊（欄位不帶時也是 false）。</summary>
    public bool ChatEnabled { get; set; }
}

/// <summary>POST /api/v1/player/getOtherPlayerInfo（他人資料）。</summary>
public sealed class OtherPlayerInfo
{
    /// <summary>玩家 ID。</summary>
    public long Id { get; set; }

    /// <summary>暱稱。</summary>
    public string Name { get; set; } = "";

    /// <summary>自訂頭像網址。</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>頭像 ID。</summary>
    public long AvatarId { get; set; }

    /// <summary>頭像框 ID。</summary>
    public long AvatarOuterId { get; set; }

    /// <summary>等級。</summary>
    public int Level { get; set; }

    /// <summary>自我介紹。</summary>
    public string? Intro { get; set; }

    /// <summary>加入時間。</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>對話框 ID。</summary>
    public long ChatBoxFrameId { get; set; }

    /// <summary>所屬公會 ID；沒有公會時為 0。</summary>
    public long GuildId { get; set; }

    /// <summary>所屬公會名稱。</summary>
    public string? GuildName { get; set; }

    /// <summary>戰功。</summary>
    public long WarMerit { get; set; }
}

/// <summary>GET /api/v1/chat/getListLastMessage 的一筆。</summary>
public sealed class ChatRoomSummary
{
    /// <summary>私訊聊天室 ID。</summary>
    public string ChatId { get; set; } = "";

    /// <summary>未讀訊息數。</summary>
    public int UnreadCount { get; set; }

    /// <summary>聊天室雙方的 player_id。</summary>
    public List<long> ChatPlayerIds { get; set; } = [];

    /// <summary>最後一則訊息。</summary>
    public ChatHistoryMessage? Message { get; set; }
}

/// <summary>私訊／公頻歷史的一筆訊息。</summary>
public sealed class ChatHistoryMessage
{
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

    /// <summary>對方是否已讀。</summary>
    public bool IsRead { get; set; }
}
