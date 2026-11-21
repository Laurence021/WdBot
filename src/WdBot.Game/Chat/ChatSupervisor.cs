using System.Net;
using Microsoft.Extensions.Logging;

namespace WdBot.Game.Chat;

public enum ChatConnectionState
{
    Connecting,
    Connected,
    Reconnecting,
}

public sealed class ChatSupervisorOptions
{
    public TimeSpan ReconnectMin { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMax { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>被 7000（帳號在其他地方連線）踢掉後，等多久才重連，避免與另一端互搶。</summary>
    public TimeSpan DuplicateLoginCooldown { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ForcedKickBackoff { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaintenanceBackoff { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>聊天連線因無法恢復的原因結束（停權、變更密碼等），需要後台介入。</summary>
public sealed record ChatFatalStop(int Code, string Message);

/// <summary>
/// 維持一個帳號的聊天 WS 連線：斷線重連、輪替 chat_ws_urls、依 opcode 8 的 err_code 決定是否重連。
/// </summary>
public sealed class ChatSupervisor
{
    private readonly GameSession _session;
    private readonly IReadOnlyList<string> _fallbackUrls;
    private readonly string _tracePrefix;
    private readonly ChatSupervisorOptions _options;
    private readonly RequestHeaders _headers;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private ChatWsClient? _current;
    private CancellationTokenSource? _connectionCts;
    private (TimeSpan Delay, string Reason)? _requestedReconnect;
    private TaskCompletionSource<ChatWsClient> _connected = NewConnectedTcs();

    /// <param name="headers">每次連線請求都帶上的額外 Header（例如模擬 Unity App 的 User-Agent）。</param>
    public ChatSupervisor(GameSession session, IReadOnlyList<string> fallbackUrls, string tracePrefix,
        ChatSupervisorOptions options, RequestHeaders headers, ILogger logger)
    {
        _session = session;
        _fallbackUrls = fallbackUrls;
        _tracePrefix = tracePrefix;
        _options = options;
        _headers = headers;
        _logger = logger;
    }

    /// <summary>聊天 WS 推送（已排除送出指令的回包）。</summary>
    public event Action<ChatDownPacket>? PushReceived;

    public event Action<ChatConnectionState, string?>? StateChanged;

    public ChatWsClient? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>等待連線可用，逾時回傳 null。</summary>
    public async Task<ChatWsClient?> WaitConnectedAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task<ChatWsClient> task;
        lock (_gate)
        {
            if (_current is { IsOpen: true } c)
            {
                return c;
            }

            task = _connected.Task;
        }

        try
        {
            return await task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// 主動斷開目前的連線，等 delay 之後重連（伺服器會重新分配公頻）。
    /// 目前沒有連線時回傳 false。可以在 <see cref="PushReceived"/> 的處理中呼叫。
    /// </summary>
    public bool RequestReconnect(TimeSpan delay, string reason)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _connectionCts;
            if (_current is null || cts is null)
            {
                return false;
            }

            _requestedReconnect = (delay, reason);
        }

        // 在鎖外取消，避免取消回呼與接續工作在持有鎖的狀態下執行
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false; // 連線剛好結束
        }

        return true;
    }

    /// <summary>
    /// 持續維持連線直到取消（回傳 null）或遇到無法恢復的踢線原因（回傳 <see cref="ChatFatalStop"/>）。
    /// 呼叫前必須已登入。
    /// </summary>
    public async Task<ChatFatalStop?> RunAsync(CancellationToken ct)
    {
        var failures = 0;
        var urlIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            var urls = _session.LastLogin?.ChatWsUrls is { Count: > 0 } fromLogin ? fromLogin : _fallbackUrls;
            if (urls.Count == 0)
            {
                throw new InvalidOperationException("沒有可用的聊天 WS 網址");
            }

            var url = urls[urlIndex % urls.Count];
            StateChanged?.Invoke(failures == 0 ? ChatConnectionState.Connecting : ChatConnectionState.Reconnecting, url);

            var client = new ChatWsClient(_tracePrefix, _headers, _logger);
            client.PushReceived += OnPush;

            try
            {
                await client.ConnectAsync(url, _session.AccessToken, ct).ConfigureAwait(false);
            }
            catch (ChatConnectException ex) when (!ct.IsCancellationRequested)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                failures++;
                urlIndex++;

                var delay = Backoff(failures);
                if (ex.StatusCode is HttpStatusCode.ServiceUnavailable)
                {
                    delay = Max(delay, _options.MaintenanceBackoff);
                }

                _logger.LogWarning("{Message}，{Seconds:0} 秒後重連（第 {Failures} 次失敗）", ex.Message, delay.TotalSeconds, failures);
                StateChanged?.Invoke(ChatConnectionState.Reconnecting, ex.Message);
                await Task.Delay(delay, ct).ConfigureAwait(false);

                // token 無效時伺服器不升級連線，所以重連前先換一組新 token
                await TryRefreshTokenAsync(ct).ConfigureAwait(false);
                continue;
            }

            failures = 0;
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            SetCurrent(client, connectionCts);
            _logger.LogInformation("聊天 WS 已連線：{Url}", url);
            StateChanged?.Invoke(ChatConnectionState.Connected, url);

            ChatCloseInfo close;
            try
            {
                close = await client.ReceiveLoopAsync(connectionCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                close = new ChatCloseInfo(null, "主動斷線");
            }
            finally
            {
                ClearCurrent(client);
                await client.DisposeAsync().ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            if (TakeRequestedReconnect() is { } requested)
            {
                _logger.LogInformation("主動斷線：{Reason}，{Seconds:0} 秒後重連", requested.Reason, requested.Delay.TotalSeconds);
                StateChanged?.Invoke(ChatConnectionState.Reconnecting, requested.Reason);
                await Task.Delay(requested.Delay, ct).ConfigureAwait(false);
                continue;
            }

            TimeSpan retryDelay;
            switch (close.KickCode)
            {
                case ChatErrorCodes.KickPasswordChanged:
                case ChatErrorCodes.KickPendingDeletion:
                case ChatErrorCodes.KickSuspended:
                case ChatErrorCodes.KickDisabled:
                    _logger.LogError("聊天 WS 被踢下線 {Code} {Message}，停止重連", close.KickCode, close.Message);
                    return new ChatFatalStop(close.KickCode.Value, close.Message ?? "");

                case ChatErrorCodes.KickDuplicateLogin:
                    retryDelay = _options.DuplicateLoginCooldown;
                    break;

                case ChatErrorCodes.KickForced:
                    retryDelay = _options.ForcedKickBackoff;
                    break;

                case ChatErrorCodes.KickMaintenance:
                    retryDelay = _options.MaintenanceBackoff;
                    break;

                default:
                    failures = 1;
                    retryDelay = Backoff(failures);
                    break;
            }

            var detail = close.KickCode is { } code ? $"被踢下線 {code} {close.Message}" : close.Message;
            _logger.LogWarning("聊天 WS 中斷：{Detail}，{Seconds:0} 秒後重連", detail, retryDelay.TotalSeconds);
            StateChanged?.Invoke(ChatConnectionState.Reconnecting, detail);
            await Task.Delay(retryDelay, ct).ConfigureAwait(false);
        }

        return null;
    }

    private async Task TryRefreshTokenAsync(CancellationToken ct)
    {
        try
        {
            await _session.RefreshAsync(null, ct).ConfigureAwait(false);
        }
        catch (GameApiException ex) when (ex.IsFatalForLogin)
        {
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "重連前刷新 token 失敗，沿用目前的 token");
        }
    }

    private void OnPush(ChatDownPacket packet) => PushReceived?.Invoke(packet);

    private void SetCurrent(ChatWsClient client, CancellationTokenSource connectionCts)
    {
        lock (_gate)
        {
            _current = client;
            _connectionCts = connectionCts;
            _requestedReconnect = null;
            _connected.TrySetResult(client);
        }
    }

    private (TimeSpan Delay, string Reason)? TakeRequestedReconnect()
    {
        lock (_gate)
        {
            var requested = _requestedReconnect;
            _requestedReconnect = null;
            return requested;
        }
    }

    private void ClearCurrent(ChatWsClient client)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, client))
            {
                _current = null;
                _connectionCts = null;
                if (_connected.Task.IsCompleted)
                {
                    _connected = NewConnectedTcs();
                }
            }
        }
    }

    private TimeSpan Backoff(int failures)
    {
        var baseMs = _options.ReconnectMin.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 10));
        var cappedMs = Math.Min(baseMs, _options.ReconnectMax.TotalMilliseconds);
        var jitter = Random.Shared.NextDouble() * 0.2 * cappedMs;
        return TimeSpan.FromMilliseconds(cappedMs + jitter);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TaskCompletionSource<ChatWsClient> NewConnectedTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
