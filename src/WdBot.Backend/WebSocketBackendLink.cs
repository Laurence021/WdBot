using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace WdBot.Backend;

public sealed class BackendLinkOptions
{
    /// <summary>微服務的 WebSocket 網址，例如 wss://bot-admin.example.com/ws/bot-service。</summary>
    public string Url { get; set; } = "";

    /// <summary>連線時以 Header X-Api-Key 帶入。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>本服務實例的識別，連線時以 Header X-Service-Instance-Id 與 hello 帶入。</summary>
    public string ServiceInstanceId { get; set; } = Environment.MachineName;

    public int ReconnectMinSeconds { get; set; } = 1;
    public int ReconnectMaxSeconds { get; set; } = 30;

    /// <summary>斷線期間暫存的上行訊息上限，超過時丟棄最舊的。</summary>
    public int OutboxCapacity { get; set; } = 10000;
}

/// <summary>以 WebSocket 連接微服務的 <see cref="IBackendLink"/> 實作，協定見 docs/backend-protocol.md。</summary>
public sealed class WebSocketBackendLink : IBackendLink
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    private readonly BackendLinkOptions _options;
    private readonly ILogger<WebSocketBackendLink> _logger;
    private readonly Channel<BackendEnvelope?> _outbox; // null 代表「送出待更新的狀態」
    private readonly ConcurrentDictionary<string, BotStatusReport> _latestStatus = new();
    private readonly ConcurrentDictionary<string, byte> _dirtyStatus = new();
    private BackendEnvelope? _carry; // 已取出但送出失敗，重連後先送

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

    public void ReportIncomingMessage(IncomingMessageReport report) =>
        Enqueue(BackendEnvelope.Create(BackendMessageTypes.MessageReceived, report));

    public void ReportSendResult(SendResultReport report) =>
        Enqueue(BackendEnvelope.Create(BackendMessageTypes.SendResult, report));

    public void ReportChannelSnapshot(ChannelSnapshotReport report) =>
        Enqueue(BackendEnvelope.Create(BackendMessageTypes.ChannelSnapshot, report));

    public void ReportChannelMembersChanged(ChannelMembersChangedReport report) =>
        Enqueue(BackendEnvelope.Create(BackendMessageTypes.ChannelMembersChanged, report));

    public void ReportBotStatus(BotStatusReport report)
    {
        _latestStatus[report.BotId] = report;
        _dirtyStatus[report.BotId] = 0;
        _outbox.Writer.TryWrite(null);
    }

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

    private static T Payload<T>(BackendEnvelope envelope) where T : class =>
        (envelope.Payload is { ValueKind: JsonValueKind.Object } p ? p.Deserialize<T>(BackendJson.Options) : null)
        ?? throw new InvalidDataException($"{envelope.Type} 缺少 payload");

    private void Enqueue(BackendEnvelope envelope)
    {
        if (!_outbox.Writer.TryWrite(envelope))
        {
            _logger.LogWarning("無法放入微服務上行佇列：{Type}", envelope.Type);
        }
    }

    private static async Task SendAsync(ClientWebSocket ws, BackendEnvelope envelope, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, BackendJson.Options);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

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

    private static string GetVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
}
