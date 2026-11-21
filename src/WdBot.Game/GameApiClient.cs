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
    // 規格：所有 API 合計每 60 秒 200 次，這裡留一點餘量
    private const int RateLimit = 190;
    private const int MaxTooFrequentRetries = 3;
    private static readonly TimeSpan DefaultTooFrequentWait = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private readonly ILogger _logger;
    private readonly SlidingWindowLimiter _limiter = new(RateLimit, TimeSpan.FromSeconds(60));
    private readonly RequestHeaders _headers;

    /// <param name="headers">每個請求都帶上的額外 Header（例如模擬 Unity App 的 User-Agent）。</param>
    public GameApiClient(HttpClient http, Uri baseUrl, RequestHeaders headers, ILogger logger)
    {
        _http = http;
        _baseUrl = baseUrl;
        _headers = headers;
        _logger = logger;
    }

    // ---- 登入／token（不需要 access_token）----

    public Task<LoginResult> LoginAccountAsync(AccountCredentials cred, CancellationToken ct) =>
        SendAsync<LoginResult>(HttpMethod.Post, "/api/v1/player/login/account",
            new LoginAccountRequest(cred.Account, cred.Password, cred.Device.DeviceId,
                cred.Device.DeviceModel, cred.Device.Os, cred.Device.AppVersion),
            accessToken: null, ct);

    public Task<TokenPair> RefreshTokenAsync(string refreshToken, string deviceId, CancellationToken ct) =>
        SendAsync<TokenPair>(HttpMethod.Post, "/api/v1/player/refreshAccessToken",
            new RefreshTokenRequest(refreshToken, deviceId), accessToken: null, ct);

    // ---- 玩家資訊 ----

    public Task<SelfPlayerInfo> GetPlayerInfoAsync(string accessToken, CancellationToken ct) =>
        SendAsync<SelfPlayerInfo>(HttpMethod.Get, "/api/v1/player/getPlayerInfo", null, accessToken, ct);

    public Task<OtherPlayerInfo> GetOtherPlayerInfoAsync(string accessToken, long playerId, CancellationToken ct) =>
        SendAsync<OtherPlayerInfo>(HttpMethod.Post, "/api/v1/player/getOtherPlayerInfo",
            new PlayerIdRequest(playerId), accessToken, ct);

    // ---- 聊天紀錄 ----

    public async Task<List<ChatRoomSummary>> GetListLastMessageAsync(string accessToken, CancellationToken ct) =>
        await SendAsync<List<ChatRoomSummary>?>(HttpMethod.Get, "/api/v1/chat/getListLastMessage", null, accessToken, ct)
            .ConfigureAwait(false) ?? [];

    /// <summary>私訊歷史。beforeMessageId 有帶時取該筆之前的訊息（往前翻頁）。</summary>
    public async Task<List<ChatHistoryMessage>> GetChatMessageAsync(
        string accessToken, long targetPlayerId, string? beforeMessageId, CancellationToken ct) =>
        await SendAsync<List<ChatHistoryMessage>?>(HttpMethod.Post, "/api/v1/chat/getChatMessage",
            new ChatMessageRequest(targetPlayerId, null, beforeMessageId ?? ""), accessToken, ct)
            .ConfigureAwait(false) ?? [];

    // ---- 核心 ----

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
