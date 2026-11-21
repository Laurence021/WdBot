using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace WdBot.Game.Chat;

public sealed record OutgoingPrivateMessage(string RequestId, long ToPlayerId, string Text, string? ReplyToMessageId);

/// <summary>
/// 一筆私訊請求的結果。文字超過 200 字會拆成多則，MessageIds 依序列出已送出的訊息 ID。
/// ErrCode 為負數表示本地錯誤，見 <see cref="LocalSendErrors"/>。
/// </summary>
public sealed record PrivateSendResult(
    string RequestId, bool Success, IReadOnlyList<string> MessageIds, int ErrCode, string? ErrMsg);

/// <summary>私訊送出失敗時，不是由遊戲伺服器回應的本地錯誤碼。</summary>
public static class LocalSendErrors
{
    public const int InvalidArgument = -1;
    public const int NotConnected = -2;
    public const int Timeout = -3;
    public const int QueueFull = -4;
    public const int BotNotRunning = -5;
    public const int DisconnectedUnknown = -6;
}

public sealed class PrivateMessageSenderOptions
{
    /// <summary>同一玩家私訊每 2 秒 1 則，加 100ms 餘量。</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromMilliseconds(2100);

    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan WaitConnectedTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxTextLength { get; set; } = 200;
    public int MaxAttemptsPerChunk { get; set; } = 4;
    public int QueueCapacity { get; set; } = 1000;
}

/// <summary>
/// 一個機器人的私訊送出佇列：依序送出、遵守 2 秒間隔、長文字切段、5007 退避重試。
/// </summary>
public sealed class PrivateMessageSender
{
    private readonly ChatSupervisor _chat;
    private readonly Func<SenderProfile?> _profile;
    private readonly Func<PrivateSendResult, Task> _onResult;
    private readonly PrivateMessageSenderOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<OutgoingPrivateMessage> _queue;
    private long _lastSendTick = Environment.TickCount64 - 10_000;

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
    public bool TryEnqueue(OutgoingPrivateMessage message) => _queue.Writer.TryWrite(message);

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

    private async Task WaitIntervalAsync(CancellationToken ct)
    {
        var elapsed = Environment.TickCount64 - Interlocked.Read(ref _lastSendTick);
        var wait = (long)_options.MinInterval.TotalMilliseconds - elapsed;
        if (wait > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);
        }
    }

    private PrivateSendResult Fail(OutgoingPrivateMessage message, IReadOnlyList<string> sentIds, int code, string? reason)
    {
        _logger.LogWarning("私訊 {RequestId} 送給玩家 {PlayerId} 失敗：{Code} {Reason}",
            message.RequestId, message.ToPlayerId, code, reason);
        return new PrivateSendResult(message.RequestId, false, sentIds.ToList(), code, reason);
    }

    /// <summary>
    /// 去頭尾空白後依長度切段，不會切斷 emoji 等組合字元；全為空白的段落會被略過。
    /// </summary>
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
