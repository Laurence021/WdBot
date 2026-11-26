using System.Net;
using Microsoft.Extensions.Logging;

namespace WdBot.Game.Chat;

/// <summary>聊天 WS 的連線狀態。</summary>
public enum ChatConnectionState
{
    /// <summary>第一次連線中。</summary>
    Connecting,

    /// <summary>已連線。</summary>
    Connected,

    /// <summary>斷線後準備重連，或重連中。</summary>
    Reconnecting,
}

/// <summary><see cref="ChatSupervisor"/> 的重連等待設定。</summary>
public sealed class ChatSupervisorOptions
{
    /// <summary>一般斷線後第一次重連前的等待時間；之後每次失敗加倍。</summary>
    public TimeSpan ReconnectMin { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>一般斷線重連等待時間的上限。</summary>
    public TimeSpan ReconnectMax { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>被 7000（帳號在其他地方連線）踢掉後，等多久才重連，避免與另一端互搶。</summary>
    public TimeSpan DuplicateLoginCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>被後台強制下線（7001）後，等多久才重連。</summary>
    public TimeSpan ForcedKickBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>聊天室維護（7005 或連線時回維護訊息）後，等多久才重連。</summary>
    public TimeSpan MaintenanceBackoff { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>聊天連線因無法恢復的原因結束（停權、變更密碼等），需要後台介入。</summary>
/// <param name="Code">opcode 8 的踢線代碼。</param>
/// <param name="Message">踢線訊息；伺服器沒有提供時為空字串。</param>
public sealed record ChatFatalStop(int Code, string Message);

/// <summary>
/// 維持一個帳號的聊天 WS 連線：斷線重連、輪替 chat_ws_urls、依 opcode 8 的 err_code 決定是否重連。
/// </summary>
public sealed class ChatSupervisor
{
    /// <summary>提供 access_token 與登入回傳的 chat_ws_urls。</summary>
    private readonly GameSession _session;

    /// <summary>登入回傳的 chat_ws_urls 為空時使用的網址（appsettings 的 ChatWsUrl）。</summary>
    private readonly IReadOnlyList<string> _fallbackUrls;

    /// <summary>trace_id 的前綴（bot_id），傳給每條連線。</summary>
    private readonly string _tracePrefix;

    /// <summary>重連等待設定。</summary>
    private readonly ChatSupervisorOptions _options;

    /// <summary>連線請求帶上的額外 Header。</summary>
    private readonly RequestHeaders _headers;

    /// <summary>記錄連線狀態與被踢原因。</summary>
    private readonly ILogger _logger;

    /// <summary>保護下面幾個連線狀態欄位。</summary>
    private readonly object _gate = new();

    /// <summary>目前的連線；沒有連線時為 null。</summary>
    private ChatWsClient? _current;

    /// <summary>目前這條連線的取消來源，<see cref="RequestReconnect"/> 用它中斷連線。</summary>
    private CancellationTokenSource? _connectionCts;

    /// <summary>外部要求的重連（等待時間與原因）；斷線後優先採用。</summary>
    private (TimeSpan Delay, string Reason)? _requestedReconnect;

    /// <summary>連線可用時完成，<see cref="WaitConnectedAsync"/> 等待它；斷線後換新的。</summary>
    private TaskCompletionSource<ChatWsClient> _connected = NewConnectedTcs();

    /// <summary>建立聊天連線管理器；建構時不會連線，要呼叫 <see cref="RunAsync"/> 才開始。</summary>
    /// <param name="session">登入狀態，提供 access_token 與登入回傳的 chat_ws_urls。</param>
    /// <param name="fallbackUrls">登入沒有回傳 chat_ws_urls 時改用的聊天 WS 網址（來自環境設定）。</param>
    /// <param name="tracePrefix">trace_id 的前綴，用來分辨是哪個機器人送出的指令。</param>
    /// <param name="options">重連與逾時參數。</param>
    /// <param name="headers">每次連線請求都帶上的額外 Header（例如模擬 Unity App 的 User-Agent）。</param>
    /// <param name="logger">記錄連線狀態與錯誤。</param>
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

    /// <summary>連線狀態變更；第二個參數為說明（網址或斷線原因）。</summary>
    public event Action<ChatConnectionState, string?>? StateChanged;

    /// <summary>目前的連線；沒有連線時為 null。</summary>
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
    /// <param name="timeout">最多等待多久。</param>
    /// <param name="ct">用來取消等待。</param>
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
    /// <param name="delay">斷線後等多久再重連。</param>
    /// <param name="reason">重連原因，寫進 log。</param>
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
    /// <param name="ct">取消時斷線並回傳 null。</param>
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

    /// <summary>重連前刷新 token；致命錯誤（例如帳號被停權）往外拋，其他失敗只記錄並沿用目前的 token。</summary>
    /// <param name="ct">用來取消作業。</param>
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

    /// <summary>把目前連線的推送轉給 <see cref="PushReceived"/> 的訂閱者。</summary>
    /// <param name="packet">收到的推送封包。</param>
    private void OnPush(ChatDownPacket packet) => PushReceived?.Invoke(packet);

    /// <summary>記下新建立的連線，清除先前的重連要求，並通知等待連線的呼叫端。</summary>
    /// <param name="client">新建立的連線。</param>
    /// <param name="connectionCts">這條連線的取消來源，<see cref="RequestReconnect"/> 用它中斷連線。</param>
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

    /// <summary>取出並清除外部要求的重連；沒有要求時回傳 null。</summary>
    private (TimeSpan Delay, string Reason)? TakeRequestedReconnect()
    {
        lock (_gate)
        {
            var requested = _requestedReconnect;
            _requestedReconnect = null;
            return requested;
        }
    }

    /// <summary>連線結束時清除目前連線；只在它仍是目前連線時才清除，並換一個新的等待物件給下次連線。</summary>
    /// <param name="client">剛結束的連線。</param>
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

    /// <summary>一般斷線的重連等待時間：從 ReconnectMin 起每次失敗加倍，上限 ReconnectMax，另加 0–20% 的隨機延遲。</summary>
    /// <param name="failures">連續失敗的次數。</param>
    private TimeSpan Backoff(int failures)
    {
        var baseMs = _options.ReconnectMin.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 10));
        var cappedMs = Math.Min(baseMs, _options.ReconnectMax.TotalMilliseconds);
        var jitter = Random.Shared.NextDouble() * 0.2 * cappedMs;
        return TimeSpan.FromMilliseconds(cappedMs + jitter);
    }

    /// <summary>回傳較長的時間。</summary>
    /// <param name="a">第一個時間。</param>
    /// <param name="b">第二個時間。</param>
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>建立等待連線用的 TaskCompletionSource（接續動作以非同步執行，避免在鎖內執行呼叫端的程式）。</summary>
    private static TaskCompletionSource<ChatWsClient> NewConnectedTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
