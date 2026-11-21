using System.Text.Json;

namespace WdBot.Game.Models;

/// <summary>App API 的共通回應格式。</summary>
public sealed class ApiEnvelope
{
    public int ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public JsonElement? Data { get; set; }
    public string? ServerName { get; set; }
    public DateTimeOffset? ExecutionTime { get; set; }
}

/// <summary>登入／註冊必帶的裝置欄位。device_id 每個機器人固定一個，token 會綁定它。</summary>
public sealed record DeviceInfo(string DeviceId, int DeviceModel, string Os, string AppVersion);

public sealed record AccountCredentials(string Account, string Password, DeviceInfo Device);

internal sealed record LoginAccountRequest(
    string Account, string Password, string DeviceId, int DeviceModel, string Os, string AppVersion);

internal sealed record RefreshTokenRequest(string RefreshToken, string DeviceId);

internal sealed record PlayerIdRequest(long PlayerId);

internal sealed record ChatMessageRequest(long? TargetPlayerId, string? ChatId, string MessageId);

public sealed class LoginResult
{
    public PlayerInfo PlayerInfo { get; set; } = new();
    public bool IsOnline { get; set; }
    public List<string> ChatWsUrls { get; set; } = [];
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
}

public sealed class TokenPair
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
}

/// <summary>登入回傳的 player_info。值為 0、空字串、false 的欄位可能不帶，這裡都用預設值。</summary>
public class PlayerInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public long AvatarId { get; set; }
    public long AvatarOuterId { get; set; }
    public int Level { get; set; }
    public int Type { get; set; }
    public int AuthType { get; set; }
}

/// <summary>GET /api/v1/player/getPlayerInfo（本人資料）。</summary>
public sealed class SelfPlayerInfo : PlayerInfo
{
    public string? Uid { get; set; }
    public string? Account { get; set; }
    public long ChatBoxFrameId { get; set; }
    public int DeletionType { get; set; }
    public PlayerPrivilege? PlayerPrivilege { get; set; }
}

public sealed class PlayerPrivilege
{
    /// <summary>false 時不能發私訊（欄位不帶時也是 false）。</summary>
    public bool ChatEnabled { get; set; }
}

/// <summary>POST /api/v1/player/getOtherPlayerInfo（他人資料）。</summary>
public sealed class OtherPlayerInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public long AvatarId { get; set; }
    public long AvatarOuterId { get; set; }
    public int Level { get; set; }
    public string? Intro { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public long ChatBoxFrameId { get; set; }
    public long GuildId { get; set; }
    public string? GuildName { get; set; }
    public long WarMerit { get; set; }
}

/// <summary>GET /api/v1/chat/getListLastMessage 的一筆。</summary>
public sealed class ChatRoomSummary
{
    public string ChatId { get; set; } = "";
    public int UnreadCount { get; set; }
    public List<long> ChatPlayerIds { get; set; } = [];
    public ChatHistoryMessage? Message { get; set; }
}

/// <summary>私訊／公頻歷史的一筆訊息。</summary>
public sealed class ChatHistoryMessage
{
    public string MessageId { get; set; } = "";
    public long PlayerId { get; set; }
    public string PlayerName { get; set; } = "";
    public string Text { get; set; } = "";
    public int MessageType { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public bool IsRead { get; set; }
}
