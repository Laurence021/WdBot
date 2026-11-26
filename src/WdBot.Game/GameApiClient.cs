using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WdBot.Game.Models;

namespace WdBot.Game;

/// <summary>
/// WD App API 的底層呼叫。一個機器人一個實例（限流依帳號計算）。
/// 只處理 envelope、限流與 2044；2029 的刷新重送由 <see cref="GameSession"/> 負責。
/// </summary>
public sealed class GameApiClient
{
    /// <summary>每 60 秒最多呼叫幾次。規格是所有 API 合計每 60 秒 200 次，這裡留一點餘量。</summary>
    private const int RateLimit = 190;

    /// <summary>遇到 2044 呼叫太頻繁時最多重試幾次。</summary>
    private const int MaxTooFrequentRetries = 3;

    /// <summary>2044 沒有帶 retry_after_seconds 時，重試前等待的時間。</summary>
    private static readonly TimeSpan DefaultTooFrequentWait = TimeSpan.FromSeconds(5);

    /// <summary>共用的 HttpClient（由 BotManager 建立）。</summary>
    private readonly HttpClient _http;

    /// <summary>App API 的基底網址，例如 https://api.wdking.tw。</summary>
    private readonly Uri _baseUrl;

    /// <summary>記錄重試等資訊。</summary>
    private readonly ILogger _logger;

    /// <summary>限制這個帳號每 60 秒最多呼叫 RateLimit 次。</summary>
    private readonly SlidingWindowLimiter _limiter = new(RateLimit, TimeSpan.FromSeconds(60));

    /// <summary>每個請求都帶上的額外 Header。</summary>
    private readonly RequestHeaders _headers;

    /// <summary>建立 App API 用戶端。</summary>
    /// <param name="http">所有機器人共用的 HttpClient。</param>
    /// <param name="baseUrl">App API 的基底網址。</param>
    /// <param name="headers">每個請求都帶上的額外 Header（例如模擬 Unity App 的 User-Agent）。</param>
    /// <param name="logger">記錄請求失敗與重試。</param>
    public GameApiClient(HttpClient http, Uri baseUrl, RequestHeaders headers, ILogger logger)
    {
        _http = http;
        _baseUrl = baseUrl;
        _headers = headers;
        _logger = logger;
    }

    // ---- 登入／token（不需要 access_token）----

    /// <summary>帳號密碼登入（POST /api/v1/player/login/account），帶上裝置欄位。</summary>
    /// <param name="cred">帳號密碼與裝置欄位。</param>
    /// <param name="ct">用來取消請求。</param>
    public Task<LoginResult> LoginAccountAsync(AccountCredentials cred, CancellationToken ct) =>
        SendAsync<LoginResult>(HttpMethod.Post, "/api/v1/player/login/account",
            new LoginAccountRequest(cred.Account, cred.Password, cred.Device.DeviceId,
                cred.Device.DeviceModel, cred.Device.Os, cred.Device.AppVersion,
                cred.Device.DeviceType, cred.Device.Location),
            accessToken: null, ct);

    /// <summary>刷新 token（POST /api/v1/player/refreshAccessToken）；device_id 必須與取得 refresh_token 時相同。</summary>
    /// <param name="refreshToken">登入或上次刷新取得的 refresh_token。</param>
    /// <param name="deviceId">取得 refresh_token 時使用的 device_id。</param>
    /// <param name="ct">用來取消請求。</param>
    public Task<TokenPair> RefreshTokenAsync(string refreshToken, string deviceId, CancellationToken ct) =>
        SendAsync<TokenPair>(HttpMethod.Post, "/api/v1/player/refreshAccessToken",
            new RefreshTokenRequest(refreshToken, deviceId), accessToken: null, ct);

    // ---- 玩家資訊 ----

    /// <summary>取得本人資料（GET /api/v1/player/getPlayerInfo）。</summary>
    /// <param name="accessToken">目前的 access_token。</param>
    /// <param name="ct">用來取消請求。</param>
    public Task<SelfPlayerInfo> GetPlayerInfoAsync(string accessToken, CancellationToken ct) =>
        SendAsync<SelfPlayerInfo>(HttpMethod.Get, "/api/v1/player/getPlayerInfo", null, accessToken, ct);

    /// <summary>取得其他玩家的資料（POST /api/v1/player/getOtherPlayerInfo）。</summary>
    /// <param name="accessToken">目前的 access_token。</param>
    /// <param name="playerId">要查詢的玩家 ID。</param>
    /// <param name="ct">用來取消請求。</param>
    public Task<OtherPlayerInfo> GetOtherPlayerInfoAsync(string accessToken, long playerId, CancellationToken ct) =>
        SendAsync<OtherPlayerInfo>(HttpMethod.Post, "/api/v1/player/getOtherPlayerInfo",
            new PlayerIdRequest(playerId), accessToken, ct);

    // ---- 聊天紀錄 ----

    /// <summary>取得各私訊聊天室的最後一則訊息（GET /api/v1/chat/getListLastMessage）。</summary>
    /// <param name="accessToken">目前的 access_token。</param>
    /// <param name="ct">用來取消請求。</param>
    public async Task<List<ChatRoomSummary>> GetListLastMessageAsync(string accessToken, CancellationToken ct) =>
        await SendAsync<List<ChatRoomSummary>?>(HttpMethod.Get, "/api/v1/chat/getListLastMessage", null, accessToken, ct)
            .ConfigureAwait(false) ?? [];

    /// <summary>私訊歷史。beforeMessageId 有帶時取該筆之前的訊息（往前翻頁）。</summary>
    /// <param name="accessToken">目前的 access_token。</param>
    /// <param name="targetPlayerId">對象玩家 ID。</param>
    /// <param name="beforeMessageId">從這則訊息往前取；null 時取最新的訊息。</param>
    /// <param name="ct">用來取消請求。</param>
    public async Task<List<ChatHistoryMessage>> GetChatMessageAsync(
        string accessToken, long targetPlayerId, string? beforeMessageId, CancellationToken ct) =>
        await SendAsync<List<ChatHistoryMessage>?>(HttpMethod.Post, "/api/v1/chat/getChatMessage",
            new ChatMessageRequest(targetPlayerId, null, beforeMessageId ?? ""), accessToken, ct)
            .ConfigureAwait(false) ?? [];

    // ---- 核心 ----

    /// <summary>
    /// 送出一個 App API 請求並解開 envelope：先等限流名額，帶上 Authorization 與額外 Header；
    /// error_code 為 1000 時回傳 data，遇到 2044 依 retry_after_seconds 等待後重試，其他錯誤拋出 <see cref="GameApiException"/>。
    /// </summary>
    /// <typeparam name="T">回應的 data 要解析成的類別。</typeparam>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="path">API 路徑，例如 /api/v1/player/getPlayerInfo。</param>
    /// <param name="body">request body，序列化成 JSON；null 時不帶 body。</param>
    /// <param name="accessToken">放在 Authorization 的 access_token；null 時不帶（例如登入）。</param>
    /// <param name="ct">用來取消請求與等待。</param>
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? accessToken, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _limiter.WaitAsync(ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(method, new Uri(_baseUrl, path));
            if (accessToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }

            if (body is not null)
            {
                // 先序列化成位元組再送：會帶 Content-Length，跟手機 App 一樣（JsonContent 會改用 chunked 傳送）
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), GameJson.Options));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }

            _headers.Apply(request);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode(); // 規格一律回 200；不是 200 代表閘道層問題，視為暫時性錯誤

            var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope>(GameJson.Options, ct).ConfigureAwait(false)
                ?? throw new GameApiException(path, -1, "回應內容為空", null);

            if (envelope.ErrorCode == GameErrorCodes.Success)
            {
                return GameJson.ToObject<T>(envelope.Data)!;
            }

            if (envelope.ErrorCode == GameErrorCodes.TooFrequent && attempt < MaxTooFrequentRetries)
            {
                var wait = GetRetryAfter(envelope.Data) ?? DefaultTooFrequentWait;
                _logger.LogWarning("WD API {Path} 回 2044 呼叫太頻繁，{Seconds} 秒後重試", path, wait.TotalSeconds);
                await Task.Delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            throw new GameApiException(path, envelope.ErrorCode, envelope.ErrorMessage, envelope.Data);
        }
    }

    /// <summary>從 2044 回應的 data 取出 retry_after_seconds；沒有或無效時回傳 null。</summary>
    /// <param name="data">2044 回應的 data 欄位。</param>
    private static TimeSpan? GetRetryAfter(JsonElement? data)
    {
        if (data is { ValueKind: JsonValueKind.Object } d
            && d.TryGetProperty("retry_after_seconds", out var v)
            && v.TryGetDouble(out var seconds)
            && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }
}
