using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WdBot.Game.Models;

namespace WdBot.Game;

/// <summary>
/// 一個機器人帳號的登入狀態與 token 管理（規格「token 機制」）：
/// <list type="bullet">
/// <item>API 回 2029 → 用 refresh_token 刷新 → 重送一次</item>
/// <item>刷新回 2029／4079／2003 → 重新帳密登入</item>
/// <item>刷新網路逾時 → 60 秒寬限內以同一個 refresh_token 重送</item>
/// <item>定期主動刷新（預設 50 分鐘）</item>
/// </list>
/// </summary>
public sealed class GameSession
{
    private static readonly TimeSpan RefreshGraceWindow = TimeSpan.FromSeconds(50); // 伺服器寬限 60 秒，留餘量
    private const int MaxRefreshAttempts = 4;

    private readonly GameApiClient _api;
    private readonly AccountCredentials _credentials;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private volatile string? _accessToken;
    private string? _refreshToken;
    private long _tokenIssuedTick;

    public GameSession(GameApiClient api, AccountCredentials credentials, ILogger logger)
    {
        _api = api;
        _credentials = credentials;
        _logger = logger;
    }

    public GameApiClient Api => _api;

    /// <summary>最近一次登入的回應（含 player_info 與 chat_ws_urls）。</summary>
    public LoginResult? LastLogin { get; private set; }

    public string AccessToken => _accessToken ?? throw new InvalidOperationException("尚未登入");

    public bool IsLoggedIn => _accessToken is not null;

    public async Task<LoginResult> LoginAsync(CancellationToken ct)
    {
        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoginCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>
    /// 刷新 token。staleAccessToken 是剛剛失敗的那個 token；若目前 token 已經不是它，
    /// 代表別的呼叫已經刷新過，直接返回，避免重複刷新讓剛拿到的 token 又失效。
    /// </summary>
    public async Task RefreshAsync(string? staleAccessToken, CancellationToken ct)
    {
        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (staleAccessToken is not null && staleAccessToken != _accessToken)
            {
                return;
            }

            if (_refreshToken is null)
            {
                await LoginCoreAsync(ct).ConfigureAwait(false);
                return;
            }

            try
            {
                var pair = await RefreshWithRetryAsync(_refreshToken, ct).ConfigureAwait(false);
                SetTokens(pair.AccessToken, pair.RefreshToken);
                _logger.LogInformation("帳號 {Account} token 已刷新", _credentials.Account);
            }
            catch (GameApiException ex) when (ex.ErrorCode is GameErrorCodes.Unauthorized
                                                  or GameErrorCodes.PasswordChanged
                                                  or GameErrorCodes.LoginFailed
                                                  or GameErrorCodes.ValidationFailed)
            {
                _logger.LogWarning("帳號 {Account} 刷新 token 失敗（{Code}），改為重新登入", _credentials.Account, ex.ErrorCode);
                await LoginCoreAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>以目前的 access_token 呼叫 API；回 2029 時刷新後重送一次。</summary>
    public async Task<T> CallAsync<T>(Func<string, CancellationToken, Task<T>> call, CancellationToken ct)
    {
        var token = AccessToken;
        try
        {
            return await call(token, ct).ConfigureAwait(false);
        }
        catch (GameApiException ex) when (ex.ErrorCode == GameErrorCodes.Unauthorized)
        {
            await RefreshAsync(token, ct).ConfigureAwait(false);
            return await call(AccessToken, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 定期主動刷新，直到取消。遇到登入類的致命錯誤時拋出 <see cref="GameApiException"/>，
    /// 其他錯誤記錄後 30 秒再試。
    /// </summary>
    public async Task RunAutoRefreshAsync(TimeSpan interval, CancellationToken ct)
    {
        while (true)
        {
            var age = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _tokenIssuedTick));
            if (age < interval)
            {
                await Task.Delay(interval - age, ct).ConfigureAwait(false);
                continue; // 等待期間可能已被其他流程刷新，重新計算
            }

            try
            {
                await RefreshAsync(null, ct).ConfigureAwait(false);
            }
            catch (GameApiException ex) when (ex.IsFatalForLogin)
            {
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "帳號 {Account} 主動刷新 token 失敗，30 秒後重試", _credentials.Account);
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<LoginResult> LoginCoreAsync(CancellationToken ct)
    {
        var result = await _api.LoginAccountAsync(_credentials, ct).ConfigureAwait(false);
        LastLogin = result;
        SetTokens(result.AccessToken, result.RefreshToken);
        _logger.LogInformation("帳號 {Account} 登入成功，暱稱={Name} player_id={PlayerId} 等級={Level}",
            _credentials.Account, result.PlayerInfo.Name, result.PlayerInfo.Id, result.PlayerInfo.Level);
        return result;
    }

    private async Task<TokenPair> RefreshWithRetryAsync(string refreshToken, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _api.RefreshTokenAsync(refreshToken, _credentials.Device.DeviceId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < MaxRefreshAttempts && elapsed.Elapsed < RefreshGraceWindow)
            {
                // 網路逾時時，伺服器可能已經換發；60 秒內用舊 refresh_token 重送會拿到同一組新 token
                _logger.LogWarning(ex, "帳號 {Account} 刷新 token 網路錯誤，第 {Attempt} 次重試", _credentials.Account, attempt);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private void SetTokens(string accessToken, string refreshToken)
    {
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        Interlocked.Exchange(ref _tokenIssuedTick, Environment.TickCount64);
    }

    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);
}
