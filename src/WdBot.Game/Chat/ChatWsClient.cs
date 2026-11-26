using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WdBot.Game.Chat;

/// <summary>建立聊天 WS 失敗（token 無效時伺服器不升級連線；維護中時回 HTTP JSON）。</summary>
/// <param name="message">錯誤說明。</param>
/// <param name="statusCode">伺服器回應的 HTTP 狀態碼。</param>
/// <param name="inner">原始例外。</param>
public sealed class ChatConnectException(string message, HttpStatusCode statusCode, Exception inner)
    : Exception(message, inner)
{
    /// <summary>升級連線時伺服器回應的 HTTP 狀態碼；沒有回應時為 0。</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>等待回包時連線已中斷，無法確定訊息是否送達。</summary>
public sealed class ChatDisconnectedException() : Exception("聊天 WS 已斷線");

/// <summary>送出後在時限內沒有收到同 trace_id 的回包。</summary>
/// <param name="opcode">等待逾時的指令 opcode。</param>
public sealed class ChatTimeoutException(int opcode) : Exception($"聊天 WS opcode {opcode} 等待回應逾時");

/// <summary>連線結束的原因。KickCode 有值代表被 opcode 8 踢下線。</summary>
/// <param name="KickCode">opcode 8 的踢線代碼；不是被踢線時為 null。</param>
/// <param name="Message">結束原因的說明；可為 null。</param>
public sealed record ChatCloseInfo(int? KickCode, string? Message);

/// <summary>
/// 單一條聊天 WS 連線。不負責重連（見 <see cref="ChatSupervisor"/>）。
/// 協定層 ping／pong 由 .NET ClientWebSocket 自動處理。
/// </summary>
public sealed class ChatWsClient : IAsyncDisposable
{
    /// <summary>單則訊息上限（規格 256 KB），收到超過的訊息就結束連線。</summary>
    private const int MaxMessageBytes = 256 * 1024;

    /// <summary>底層的 WebSocket 連線。</summary>
    private readonly ClientWebSocket _ws = new();

    /// <summary>確保同一時間只有一個送出動作（WebSocket 不允許同時送出）。</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>等待回包的請求，以 trace_id 對應。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ChatDownPacket>> _pending = new();

    /// <summary>trace_id 的前綴（bot_id）。</summary>
    private readonly string _tracePrefix;

    /// <summary>記錄連線與封包處理的錯誤。</summary>
    private readonly ILogger _logger;

    /// <summary>trace_id 的流水號。</summary>
    private long _sequence;

    /// <summary>建立聊天 WS 用戶端；建構時不會連線，要呼叫 <see cref="ConnectAsync"/>。</summary>
    /// <param name="tracePrefix">trace_id 的前綴，實際的 trace_id 為「前綴-流水號」。</param>
    /// <param name="headers">連線請求帶上的額外 Header（例如模擬 Unity App 的 User-Agent）。</param>
    /// <param name="logger">記錄連線與封包處理的錯誤。</param>
    public ChatWsClient(string tracePrefix, RequestHeaders headers, ILogger logger)
    {
        _tracePrefix = tracePrefix;
        _logger = logger;
        _ws.Options.CollectHttpResponseDetails = true;
        headers.Apply(_ws.Options, logger);
    }

    /// <summary>沒有對應到等待中 trace_id 的推送（新私訊、公頻訊息、成員異動等）。</summary>
    public event Action<ChatDownPacket>? PushReceived;

    /// <summary>連線是否開啟中。</summary>
    public bool IsOpen => _ws.State == WebSocketState.Open;

    /// <summary>以 {chat_ws_url}?token={access_token} 建立連線；失敗時拋出 <see cref="ChatConnectException"/>。</summary>
    /// <param name="chatWsUrl">聊天 WS 網址（登入回傳的 chat_ws_urls 之一）。</param>
    /// <param name="accessToken">目前的 access_token，帶在網址的 token 參數。</param>
    /// <param name="ct">用來取消連線。</param>
    public async Task ConnectAsync(string chatWsUrl, string accessToken, CancellationToken ct)
    {
        var builder = new UriBuilder(chatWsUrl);
        var tokenParam = "token=" + Uri.EscapeDataString(accessToken);
        builder.Query = string.IsNullOrEmpty(builder.Query) ? tokenParam : builder.Query.TrimStart('?') + "&" + tokenParam;

        try
        {
            await _ws.ConnectAsync(builder.Uri, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new ChatConnectException($"聊天 WS 連線失敗（HTTP {(int)_ws.HttpStatusCode}）：{ex.Message}", _ws.HttpStatusCode, ex);
        }
    }

    /// <summary>
    /// 送出指令。awaitReply 為 true 時等待同 trace_id 的回包（發送者本人會收到同 opcode 的推送）。
    /// 回包帶 err_code 時不拋例外，由呼叫端判斷。
    /// </summary>
    /// <param name="opcode">指令代碼，見 <see cref="ChatOpcodes"/>。</param>
    /// <param name="data">指令的 data：字串原樣送出，物件序列化成 JSON 字串，null 送空字串。</param>
    /// <param name="awaitReply">是否等待同 trace_id 的回包。</param>
    /// <param name="timeout">等待回包的時限；awaitReply 為 false 時不使用。</param>
    /// <param name="ct">用來取消送出與等待。</param>
    public async Task<ChatDownPacket?> SendAsync(int opcode, object? data, bool awaitReply, TimeSpan timeout, CancellationToken ct)
    {
        var traceId = $"{_tracePrefix}-{Interlocked.Increment(ref _sequence)}";
        var packet = new ChatUpPacket
        {
            Opcode = opcode,
            Data = data switch
            {
                null => "",
                string s => s,
                _ => JsonSerializer.Serialize(data, data.GetType(), GameJson.Options),
            },
            TraceId = traceId,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, GameJson.Options);

        TaskCompletionSource<ChatDownPacket>? tcs = null;
        if (awaitReply)
        {
            tcs = new TaskCompletionSource<ChatDownPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[traceId] = tcs;
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(traceId, out _);
            throw;
        }
        finally
        {
            _sendLock.Release();
        }

        if (tcs is null)
        {
            return null;
        }

        try
        {
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(traceId, out _);
            throw new ChatTimeoutException(opcode);
        }
    }

    /// <summary>讀取直到連線結束，回傳結束原因。</summary>
    /// <param name="ct">取消時停止讀取。</param>
    public async Task<ChatCloseInfo> ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return new ChatCloseInfo(null, $"伺服器關閉連線：{_ws.CloseStatus} {_ws.CloseStatusDescription}");
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxMessageBytes)
                    {
                        return new ChatCloseInfo(null, "收到超過 256 KB 的訊息");
                    }
                }
                while (!result.EndOfMessage);

                ChatDownPacket? packet;
                try
                {
                    packet = JsonSerializer.Deserialize<ChatDownPacket>(message.GetBuffer().AsSpan(0, (int)message.Length), GameJson.Options);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "無法解析聊天 WS 封包");
                    continue;
                }

                if (packet is null)
                {
                    continue;
                }

                if (packet.Opcode == ChatOpcodes.Kicked)
                {
                    return new ChatCloseInfo(packet.ErrCode, packet.ErrMsg);
                }

                if (packet.TraceId is { } traceId && _pending.TryRemove(traceId, out var tcs))
                {
                    tcs.TrySetResult(packet);
                    continue;
                }

                try
                {
                    PushReceived?.Invoke(packet);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "處理聊天 WS 推送 opcode {Opcode} 時發生錯誤", packet.Opcode);
                }
            }

            return new ChatCloseInfo(null, $"連線狀態變為 {_ws.State}");
        }
        catch (WebSocketException ex)
        {
            return new ChatCloseInfo(null, $"連線中斷：{ex.Message}");
        }
        finally
        {
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var tcs))
                {
                    tcs.TrySetException(new ChatDisconnectedException());
                }
            }
        }
    }

    /// <summary>嘗試正常關閉連線（最多等 3 秒）並釋放 WebSocket。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 關閉失敗不影響後續流程
            }
        }

        // _sendLock 不 Dispose：可能還有送訊息的呼叫卡在 WaitAsync，讓它們拿到 WebSocket 的例外即可
        _ws.Dispose();
    }
}
