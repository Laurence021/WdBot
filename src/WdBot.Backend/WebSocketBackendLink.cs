using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace WdBot.Backend;

/// <summary>appsettings 的 Backend 區段：以 WebSocket 連接微服務的設定。</summary>
public sealed class BackendLinkOptions
{
    /// <summary>微服務的 WebSocket 網址，例如 wss://bot-admin.example.com/ws/bot-service。</summary>
    public string Url { get; set; } = "";

    /// <summary>連線時以 Header X-Api-Key 帶入。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>本服務實例的識別，連線時以 Header X-Service-Instance-Id 與 hello 帶入。</summary>
    public string ServiceInstanceId { get; set; } = Environment.MachineName;

    /// <summary>斷線後第一次重連前等待的秒數；之後每次失敗加倍。</summary>
    public int ReconnectMinSeconds { get; set; } = 1;

    /// <summary>重連等待秒數的上限。</summary>
    public int ReconnectMaxSeconds { get; set; } = 30;

    /// <summary>斷線期間暫存的上行訊息上限，超過時丟棄最舊的。</summary>
    public int OutboxCapacity { get; set; } = 10000;
}

/// <summary>以 WebSocket 連接微服務的 <see cref="IBackendLink"/> 實作，協定見 docs/backend-protocol.md。</summary>
public sealed class WebSocketBackendLink : IBackendLink
{
    /// <summary>單一則微服務訊息的大小上限（4 MB），超過時斷線重連。</summary>
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>連線設定。</summary>
    private readonly BackendLinkOptions _options;

    /// <summary>記錄連線狀態與錯誤。</summary>
    private readonly ILogger<WebSocketBackendLink> _logger;

    /// <summary>上行佇列；null 代表「送出待更新的狀態」。</summary>
    private readonly Channel<BackendEnvelope?> _outbox;

    /// <summary>每個機器人最新的狀態回報；重連後會全部重送。</summary>
    private readonly ConcurrentDictionary<string, BotStatusReport> _latestStatus = new();

    /// <summary>狀態有更新、還沒送出的機器人 ID（值不使用，只當集合）。</summary>
    private readonly ConcurrentDictionary<string, byte> _dirtyStatus = new();

    /// <summary>已從佇列取出但送出失敗的封包，重連後先送。</summary>
    private BackendEnvelope? _carry;

    /// <summary>建立連線物件；建構時不會連線，連線在 <see cref="RunAsync"/> 開始。</summary>
    /// <param name="options">微服務連線設定（網址、API key、服務實例 ID）。</param>
    /// <param name="logger">記錄連線狀態與錯誤。</param>
    public WebSocketBackendLink(BackendLinkOptions options, ILogger<WebSocketBackendLink> logger)
    {
        _options = options;
        _logger = logger;
        _outbox = Channel.CreateBounded<BackendEnvelope?>(
            new BoundedChannelOptions(Math.Max(options.OutboxCapacity, 100))
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            },
            dropped =>
            {
                if (dropped is not null)
                {
                    _logger.LogWarning("微服務上行佇列已滿，丟棄最舊的 {Type} {Id}", dropped.Type, dropped.Id);
                }
            });
    }

    /// <summary>把收到的私訊（message.received）放進上行佇列。</summary>
    /// <param name="report">收到的玩家私訊。</param>
    public void ReportIncomingMessage(IncomingMessageReport report) =>
        Enqueue(BackendEnvelope.Create(BackendMessageTypes.MessageReceived, report));

    /// <summary>把私訊結果（message.send_result）放進上行佇列。</summary>
    /// <param name="report">私訊指令的執行結果。</param>
    public void ReportSendResult(SendResultReport report) =>
        Enqueue(BackendEnvelope.Create(BackendMessageTypes.SendResult, report));

    /// <summary>把公頻完整名單（channel.snapshot）放進上行佇列。</summary>
    /// <param name="report">公頻的完整線上名單。</param>
    public void ReportChannelSnapshot(ChannelSnapshotReport report) =>
        Enqueue(BackendEnvelope.Create(BackendMessageTypes.ChannelSnapshot, report));

    /// <summary>把公頻成員進出（channel.members_changed）放進上行佇列。</summary>
    /// <param name="report">公頻成員進出的內容。</param>
    public void ReportChannelMembersChanged(ChannelMembersChangedReport report) =>
        Enqueue(BackendEnvelope.Create(BackendMessageTypes.ChannelMembersChanged, report));

    /// <summary>記下最新狀態並標記待送；佇列中只放一個 null 當作「送出待更新的狀態」的訊號，不會累積舊狀態。</summary>
    /// <param name="report">機器人目前的狀態。</param>
    public void ReportBotStatus(BotStatusReport report)
    {
        _latestStatus[report.BotId] = report;
        _dirtyStatus[report.BotId] = 0;
        _outbox.Writer.TryWrite(null);
    }

    /// <summary>
    /// 連線微服務、送出 hello 與所有機器人的最新狀態，之後同時執行送出與接收迴圈；
    /// 斷線後以指數退避重連，直到取消。
    /// </summary>
    /// <param name="handler">處理下行指令的對象，通常是 BotManager。</param>
    /// <param name="ct">取消時斷線並結束。</param>
    public async Task RunAsync(IBackendHandler handler, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Url))
        {
            throw new InvalidOperationException("未設定 Backend:Url");
        }

        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            if (!string.IsNullOrEmpty(_options.ApiKey))
            {
                ws.Options.SetRequestHeader("X-Api-Key", _options.ApiKey);
            }

            ws.Options.SetRequestHeader("X-Service-Instance-Id", _options.ServiceInstanceId);

            try
            {
                await ws.ConnectAsync(new Uri(_options.Url), ct).ConfigureAwait(false);
                failures = 0;
                _logger.LogInformation("已連上微服務 {Url}", _options.Url);

                await SendAsync(ws, BackendEnvelope.Create(BackendMessageTypes.Hello, new HelloPayload(
                    _options.ServiceInstanceId, GetVersion(), ["config.sync", "config.upsert", "config.remove", "message.send"])), ct)
                    .ConfigureAwait(false);

                // 重連後讓微服務拿到所有機器人的最新狀態
                foreach (var botId in _latestStatus.Keys)
                {
                    _dirtyStatus[botId] = 0;
                }

                _outbox.Writer.TryWrite(null);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var sendTask = SendLoopAsync(ws, linked.Token);
                var receiveTask = ReceiveLoopAsync(ws, handler, linked.Token);
                var first = await Task.WhenAny(sendTask, receiveTask).ConfigureAwait(false);
                await linked.CancelAsync().ConfigureAwait(false);

                try
                {
                    await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
                }
                catch (Exception) when (first.IsCompletedSuccessfully || linked.IsCancellationRequested)
                {
                    // 另一個迴圈因取消而結束，忽略
                }

                if (first.Exception is { } ex)
                {
                    throw ex.GetBaseException();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                _logger.LogWarning("微服務連線中斷或失敗：{Message}", ex.Message);
            }
            finally
            {
                await CloseQuietlyAsync(ws).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            var delay = TimeSpan.FromSeconds(Math.Min(
                _options.ReconnectMinSeconds * Math.Pow(2, Math.Min(Math.Max(failures, 1) - 1, 10)),
                _options.ReconnectMaxSeconds));
            _logger.LogInformation("{Seconds:0} 秒後重連微服務", delay.TotalSeconds);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>依序送出上行佇列的訊息；先補送上次斷線時沒送成功的那一筆，收到 null 時改為送出待更新的狀態。</summary>
    /// <param name="ws">目前與微服務的連線。</param>
    /// <param name="ct">取消時停止送出。</param>
    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (true)
        {
            if (_carry is { } carried)
            {
                await SendAsync(ws, carried, ct).ConfigureAwait(false);
                _carry = null;
            }

            var item = await _outbox.Reader.ReadAsync(ct).ConfigureAwait(false);
            if (item is null)
            {
                await FlushStatusAsync(ws, ct).ConfigureAwait(false);
                continue;
            }

            _carry = item;
            await SendAsync(ws, item, ct).ConfigureAwait(false);
            _carry = null;
        }
    }

    /// <summary>送出所有標記為待送的機器人最新狀態（bot.status）。</summary>
    /// <param name="ws">目前與微服務的連線。</param>
    /// <param name="ct">用來取消作業。</param>
    private async Task FlushStatusAsync(ClientWebSocket ws, CancellationToken ct)
    {
        foreach (var botId in _dirtyStatus.Keys)
        {
            if (!_dirtyStatus.TryRemove(botId, out _) || !_latestStatus.TryGetValue(botId, out var status))
            {
                continue;
            }

            try
            {
                await SendAsync(ws, BackendEnvelope.Create(BackendMessageTypes.BotStatus, status), ct).ConfigureAwait(false);
            }
            catch
            {
                _dirtyStatus[botId] = 0; // 送失敗，重連後再送
                throw;
            }
        }
    }

    /// <summary>接收微服務的訊息（可能分成多個片段）並交給 <see cref="DispatchAsync"/>；單則訊息處理失敗只記錄，不斷線。</summary>
    /// <param name="ws">目前與微服務的連線。</param>
    /// <param name="handler">處理下行指令的對象。</param>
    /// <param name="ct">取消時停止接收。</param>
    private async Task ReceiveLoopAsync(ClientWebSocket ws, IBackendHandler handler, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var message = new MemoryStream();

        while (ws.State == WebSocketState.Open)
        {
            message.SetLength(0);
            ValueWebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarning("微服務關閉連線：{Status} {Description}", ws.CloseStatus, ws.CloseStatusDescription);
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    throw new InvalidOperationException("微服務訊息超過 4 MB");
                }
            }
            while (!result.EndOfMessage);

            BackendEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<BackendEnvelope>(
                    message.GetBuffer().AsSpan(0, (int)message.Length), BackendJson.Options);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "無法解析微服務訊息");
                continue;
            }

            if (envelope is null)
            {
                continue;
            }

            try
            {
                await DispatchAsync(envelope, handler, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "處理微服務訊息 {Type} {Id} 失敗", envelope.Type, envelope.Id);
            }
        }
    }

    /// <summary>依封包類型呼叫 handler 對應的方法；未知的類型忽略。</summary>
    /// <param name="envelope">收到的封包。</param>
    /// <param name="handler">處理下行指令的對象。</param>
    /// <param name="ct">用來取消作業。</param>
    private async Task DispatchAsync(BackendEnvelope envelope, IBackendHandler handler, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case BackendMessageTypes.ConfigSync:
                var sync = Payload<ConfigSyncPayload>(envelope);
                _logger.LogInformation("收到全量設定：{Count} 個機器人", sync.Bots.Count);
                await handler.OnConfigSnapshotAsync(sync.Bots, ct).ConfigureAwait(false);
                break;

            case BackendMessageTypes.ConfigUpsert:
                await handler.OnConfigUpsertAsync(Payload<BotConfig>(envelope), ct).ConfigureAwait(false);
                break;

            case BackendMessageTypes.ConfigRemove:
                await handler.OnConfigRemoveAsync(Payload<ConfigRemovePayload>(envelope).BotId, ct).ConfigureAwait(false);
                break;

            case BackendMessageTypes.MessageSend:
                await handler.OnSendMessageAsync(Payload<SendMessageCommand>(envelope), ct).ConfigureAwait(false);
                break;

            default:
                _logger.LogDebug("忽略未知的微服務訊息類型 {Type}", envelope.Type);
                break;
        }
    }

    /// <summary>把封包的 payload 解析成指定類別；沒有 payload 或不是物件時拋出例外。</summary>
    /// <typeparam name="T">payload 要解析成的類別。</typeparam>
    /// <param name="envelope">收到的封包。</param>
    private static T Payload<T>(BackendEnvelope envelope) where T : class =>
        (envelope.Payload is { ValueKind: JsonValueKind.Object } p ? p.Deserialize<T>(BackendJson.Options) : null)
        ?? throw new InvalidDataException($"{envelope.Type} 缺少 payload");

    /// <summary>放進上行佇列；佇列滿時會丟棄最舊的一筆（見建構子）。</summary>
    /// <param name="envelope">要送出的封包。</param>
    private void Enqueue(BackendEnvelope envelope)
    {
        if (!_outbox.Writer.TryWrite(envelope))
        {
            _logger.LogWarning("無法放入微服務上行佇列：{Type}", envelope.Type);
        }
    }

    /// <summary>把封包序列化成 JSON，以一則文字訊息送出。</summary>
    /// <param name="ws">目前與微服務的連線。</param>
    /// <param name="envelope">要送出的封包。</param>
    /// <param name="ct">用來取消作業。</param>
    private static async Task SendAsync(ClientWebSocket ws, BackendEnvelope envelope, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, BackendJson.Options);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    /// <summary>連線還開著時嘗試正常關閉（最多等 3 秒），失敗也不拋出例外。</summary>
    /// <param name="ws">要關閉的連線。</param>
    private static async Task CloseQuietlyAsync(ClientWebSocket ws)
    {
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 關閉失敗不影響重連
        }
    }

    /// <summary>機器人服務的版本（進入點組件的版本），放在 hello 裡。</summary>
    private static string GetVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
}
