using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace WdBot.Game.Chat;

/// <summary>一筆待送出的私訊請求。</summary>
/// <param name="RequestId">微服務的請求 ID，回報結果時帶回。</param>
/// <param name="ToPlayerId">收訊玩家的 player_id。</param>
/// <param name="Text">訊息內容；超過 200 字會自動分段。</param>
/// <param name="ReplyToMessageId">要回覆（引用）的訊息 ID；只套用在第一段。</param>
public sealed record OutgoingPrivateMessage(string RequestId, long ToPlayerId, string Text, string? ReplyToMessageId);

/// <summary>
/// 一筆私訊請求的結果。文字超過 200 字會拆成多則，MessageIds 依序列出已送出的訊息 ID。
/// ErrCode 為負數表示本地錯誤，見 <see cref="LocalSendErrors"/>。
/// </summary>
/// <param name="RequestId">微服務私訊指令的 request_id。</param>
/// <param name="Success">是否全部送出成功。</param>
/// <param name="MessageIds">已送出的訊息 ID，依送出順序排列；失敗時為失敗前已送出的部分。</param>
/// <param name="ErrCode">錯誤碼；成功時為 0，負數為本地錯誤。</param>
/// <param name="ErrMsg">錯誤說明；成功時為 null。</param>
public sealed record PrivateSendResult(
    string RequestId, bool Success, IReadOnlyList<string> MessageIds, int ErrCode, string? ErrMsg);

/// <summary>私訊送出失敗時，不是由遊戲伺服器回應的本地錯誤碼。</summary>
public static class LocalSendErrors
{
    /// <summary>參數錯誤，例如文字為空或玩家 ID 無效。</summary>
    public const int InvalidArgument = -1;

    /// <summary>機器人沒有連線，等待後仍未恢復。</summary>
    public const int NotConnected = -2;

    /// <summary>送出後等待回包逾時，無法確定是否送達。</summary>
    public const int Timeout = -3;

    /// <summary>送出佇列已滿。</summary>
    public const int QueueFull = -4;

    /// <summary>指定的機器人不存在或未啟用。</summary>
    public const int BotNotRunning = -5;

    /// <summary>等待回包時連線中斷，無法確定是否送達。</summary>
    public const int DisconnectedUnknown = -6;
}

/// <summary><see cref="PrivateMessageSender"/> 的設定。</summary>
public sealed class PrivateMessageSenderOptions
{
    /// <summary>同一玩家私訊每 2 秒 1 則，加 100ms 餘量。</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromMilliseconds(2100);

    /// <summary>送出後等待回包的時間。</summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>沒有連線時最多等待多久，仍未恢復就回報失敗（-2）。</summary>
    public TimeSpan WaitConnectedTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>單則訊息的字數上限，超過就分段送出。</summary>
    public int MaxTextLength { get; set; } = 200;

    /// <summary>每一段最多嘗試幾次（遇到 5007 發送太快時重試）。</summary>
    public int MaxAttemptsPerChunk { get; set; } = 4;

    /// <summary>送出佇列的容量。</summary>
    public int QueueCapacity { get; set; } = 1000;
}

/// <summary>
/// 一個機器人的私訊送出佇列：依序送出、遵守 2 秒間隔、長文字切段、5007 退避重試。
/// </summary>
public sealed class PrivateMessageSender
{
    /// <summary>提供目前的聊天連線。</summary>
    private readonly ChatSupervisor _chat;

    /// <summary>取得機器人目前的外觀（暱稱、頭像、對話框）；尚未登入時回傳 null。</summary>
    private readonly Func<SenderProfile?> _profile;

    /// <summary>每筆請求完成（成功或失敗）時呼叫，用來回報結果。</summary>
    private readonly Func<PrivateSendResult, Task> _onResult;

    /// <summary>送出設定。</summary>
    private readonly PrivateMessageSenderOptions _options;

    /// <summary>記錄送出結果與重試。</summary>
    private readonly ILogger _logger;

    /// <summary>待送出的私訊佇列，由 <see cref="RunAsync"/> 依序取出。</summary>
    private readonly Channel<OutgoingPrivateMessage> _queue;

    /// <summary>上一次送出的時間（Environment.TickCount64），用來維持送出間隔。</summary>
    private long _lastSendTick = Environment.TickCount64 - 10_000;

    /// <summary>建立送出佇列；要呼叫 <see cref="RunAsync"/> 才會開始送出。</summary>
    /// <param name="chat">聊天連線管理器，用來取得目前的連線。</param>
    /// <param name="profile">取得機器人外觀（暱稱、頭像、對話框）的函式；還沒取得本人資料時回傳 null。</param>
    /// <param name="onResult">每筆私訊處理完時呼叫，用來回報結果。</param>
    /// <param name="options">佇列大小、送出間隔與重試參數。</param>
    /// <param name="logger">記錄送出失敗與重試。</param>
    public PrivateMessageSender(ChatSupervisor chat, Func<SenderProfile?> profile, Func<PrivateSendResult, Task> onResult,
        PrivateMessageSenderOptions options, ILogger logger)
    {
        _chat = chat;
        _profile = profile;
        _onResult = onResult;
        _options = options;
        _logger = logger;
        _queue = Channel.CreateBounded<OutgoingPrivateMessage>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>放進佇列；佇列已滿時回傳 false。</summary>
    /// <param name="message">要送出的私訊。</param>
    public bool TryEnqueue(OutgoingPrivateMessage message) => _queue.Writer.TryWrite(message);

    /// <summary>依序取出佇列中的私訊並送出，直到取消。</summary>
    /// <param name="ct">取消時停止送出。</param>
    public async Task RunAsync(CancellationToken ct)
    {
        await foreach (var message in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            PrivateSendResult result;
            try
            {
                result = await SendOneAsync(message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "送出私訊 {RequestId} 時發生未預期錯誤", message.RequestId);
                result = Fail(message, [], LocalSendErrors.DisconnectedUnknown, ex.Message);
            }

            try
            {
                await _onResult(result).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "回報私訊結果 {RequestId} 失敗", message.RequestId);
            }
        }
    }

    /// <summary>送出一筆私訊請求：檢查參數、依長度分段，逐段帶上機器人外觀送出；任何一段失敗就停止並回報。</summary>
    /// <param name="message">要送出的私訊請求。</param>
    /// <param name="ct">用來取消作業。</param>
    private async Task<PrivateSendResult> SendOneAsync(OutgoingPrivateMessage message, CancellationToken ct)
    {
        if (message.ToPlayerId <= 0)
        {
            // receive_player_id 為 0 時伺服器會直接丟棄、不回任何訊息，所以先在本地擋下
            return Fail(message, [], LocalSendErrors.InvalidArgument, "to_player_id 必須大於 0");
        }

        var chunks = SplitText(message.Text, _options.MaxTextLength);
        if (chunks.Count == 0)
        {
            return Fail(message, [], ChatErrorCodes.EmptyText, "訊息為空");
        }

        var messageIds = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var profile = _profile();
            if (profile is null)
            {
                return Fail(message, messageIds, LocalSendErrors.BotNotRunning, "機器人尚未登入完成");
            }

            var data = new SendPrivateMessageData
            {
                ReceivePlayerId = message.ToPlayerId,
                MessageType = 1,
                Text = chunks[i],
                PlayerName = profile.PlayerName,
                AvatarId = profile.AvatarId,
                AvatarOuterId = profile.AvatarOuterId,
                AvatarUrl = profile.AvatarUrl,
                ChatBoxFrameId = profile.ChatBoxFrameId,
                ReplyMessageId = i == 0 && !string.IsNullOrEmpty(message.ReplyToMessageId) ? message.ReplyToMessageId : null,
            };

            var (packet, failure) = await SendChunkAsync(message, data, messageIds, ct).ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }

            var sent = packet!.GetData<PrivateMessage>();
            if (!string.IsNullOrEmpty(sent?.MessageId))
            {
                messageIds.Add(sent.MessageId);
            }
        }

        _logger.LogInformation("私訊 {RequestId} 已送給玩家 {PlayerId}（{Count} 則）", message.RequestId, message.ToPlayerId, chunks.Count);
        return new PrivateSendResult(message.RequestId, true, messageIds, 0, null);
    }

    /// <summary>
    /// 送出一段私訊並等待回包。遇到 5007／2044 或送出本身失敗時退避重試；
    /// 已送出但逾時或斷線時不重送（可能已送達），直接回報失敗。成功時回傳回包，失敗時回傳失敗結果。
    /// </summary>
    /// <param name="message">這一段所屬的私訊請求。</param>
    /// <param name="data">這一段要送出的內容。</param>
    /// <param name="sentIds">先前已送出的訊息 ID，失敗時一併回報。</param>
    /// <param name="ct">用來取消作業。</param>
    private async Task<(ChatDownPacket? Packet, PrivateSendResult? Failure)> SendChunkAsync(
        OutgoingPrivateMessage message, SendPrivateMessageData data, List<string> sentIds, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await WaitIntervalAsync(ct).ConfigureAwait(false);

            var client = await _chat.WaitConnectedAsync(_options.WaitConnectedTimeout, ct).ConfigureAwait(false);
            if (client is null)
            {
                return (null, Fail(message, sentIds, LocalSendErrors.NotConnected, "聊天 WS 未連線"));
            }

            ChatDownPacket? packet;
            try
            {
                packet = await client.SendAsync(ChatOpcodes.PrivateMessage, data, awaitReply: true, _options.ReplyTimeout, ct)
                    .ConfigureAwait(false);
            }
            catch (ChatTimeoutException)
            {
                return (null, Fail(message, sentIds, LocalSendErrors.Timeout, "等待伺服器回應逾時，無法確定是否送達"));
            }
            catch (ChatDisconnectedException)
            {
                // 已送出但連線在回應前中斷，可能已送達；不重送以免重複
                return (null, Fail(message, sentIds, LocalSendErrors.DisconnectedUnknown, "等待回應時斷線，無法確定是否送達"));
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException
                                       && !ct.IsCancellationRequested)
            {
                // 送出本身失敗（訊息沒有出去），等重連後重試
                if (attempt >= _options.MaxAttemptsPerChunk)
                {
                    return (null, Fail(message, sentIds, LocalSendErrors.NotConnected, ex.Message));
                }

                continue;
            }
            finally
            {
                Interlocked.Exchange(ref _lastSendTick, Environment.TickCount64);
            }

            if (packet is null || !packet.IsError)
            {
                return (packet, null);
            }

            if (packet.ErrCode is ChatErrorCodes.SendTooFast or ChatErrorCodes.TooFrequent
                && attempt < _options.MaxAttemptsPerChunk)
            {
                _logger.LogDebug("私訊 {RequestId} 回 {Code}，退避後重試", message.RequestId, packet.ErrCode);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct).ConfigureAwait(false);
                continue;
            }

            return (null, Fail(message, sentIds, packet.ErrCode, packet.ErrMsg));
        }
    }

    /// <summary>距離上一次送出未滿 MinInterval 時，等到滿為止。</summary>
    /// <param name="ct">用來取消等待。</param>
    private async Task WaitIntervalAsync(CancellationToken ct)
    {
        var elapsed = Environment.TickCount64 - Interlocked.Read(ref _lastSendTick);
        var wait = (long)_options.MinInterval.TotalMilliseconds - elapsed;
        if (wait > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);
        }
    }

    /// <summary>記錄失敗並建立失敗結果；sentIds 為失敗前已送出的訊息 ID。</summary>
    /// <param name="message">失敗的私訊請求。</param>
    /// <param name="sentIds">失敗前已送出的訊息 ID。</param>
    /// <param name="code">錯誤碼；負數為本地錯誤，見 <see cref="LocalSendErrors"/>。</param>
    /// <param name="reason">錯誤說明。</param>
    private PrivateSendResult Fail(OutgoingPrivateMessage message, IReadOnlyList<string> sentIds, int code, string? reason)
    {
        _logger.LogWarning("私訊 {RequestId} 送給玩家 {PlayerId} 失敗：{Code} {Reason}",
            message.RequestId, message.ToPlayerId, code, reason);
        return new PrivateSendResult(message.RequestId, false, sentIds.ToList(), code, reason);
    }

    /// <summary>
    /// 去頭尾空白後依長度切段，不會切斷 emoji 等組合字元；全為空白的段落會被略過。
    /// </summary>
    /// <param name="text">要切段的文字；null 視為空字串。</param>
    /// <param name="maxLength">每段的長度上限。</param>
    public static List<string> SplitText(string? text, int maxLength)
    {
        var result = new List<string>();
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return result;
        }

        if (trimmed.Length <= maxLength)
        {
            result.Add(trimmed);
            return result;
        }

        var current = new StringBuilder(maxLength);
        var enumerator = StringInfo.GetTextElementEnumerator(trimmed);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (current.Length > 0 && current.Length + element.Length > maxLength)
            {
                AddChunk(result, current);
            }

            current.Append(element);
        }

        AddChunk(result, current);
        return result;

        static void AddChunk(List<string> list, StringBuilder sb)
        {
            var chunk = sb.ToString().Trim();
            if (chunk.Length > 0)
            {
                list.Add(chunk);
            }

            sb.Clear();
        }
    }
}
