using WdBot.Backend;
using WdBot.Game;
using WdBot.Game.Chat;
using WdBot.Game.Models;

namespace WdBot.Service;

/// <summary>
/// 一個機器人的完整生命週期：登入 → 取得本人資料 → 檢查發話資格 → 連聊天 WS →
/// 轉送收到的私訊、送出微服務要求的私訊。暫時性錯誤會自動重來，致命錯誤則停止並回報 error。
/// </summary>
public sealed class BotInstance : IAsyncDisposable
{
    private readonly IBackendLink _backend;
    private readonly BotRuntimeOptions _options;
    private readonly ILogger _logger;
    private readonly ILogger _messageLogger; // 私訊內容專用，依機器人分檔，Debug 等級才寫
    private readonly ILogger _channelLogger; // 公頻訊息內容專用，依機器人分檔，Debug 等級才寫
    private readonly ILogger _unhandledLogger; // 未處理的聊天 WS 推送專用，依機器人分檔
    private readonly GameApiClient _api;
    private readonly GameSession _session;
    private readonly ChatSupervisor _chat;
    private readonly PrivateMessageSender _sender;
    private readonly RecentIdCache _seenMessages = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ChannelMemberScanner _scanner;

    // 公頻名單狀態，皆以 _channelGate 保護：
    // 取得完整名單前 _pendingChanges 不為 null，opcode 17 先暫存；snapshot 送出後才開始直接回報異動
    private readonly object _channelGate = new();
    private CancellationTokenSource? _scanCts;          // 目前佔廳的名單追蹤；null 表示沒有在追蹤
    private ChatWsClient? _trackingClient;              // 追蹤中的聊天連線，翻頁時用
    private ChannelMembersPage? _earlyPushedPage;       // 確認佔廳前就收到的 opcode 9 推送（7 與 9 順序不固定）
    private List<ChannelMembersChangedData>? _pendingChanges;
    private bool _channelReportingActive;
    private bool _fetchingList;                         // 正在翻頁取得名單（推送的後續頁或主動掃描）
    private long _lastListTick;                         // 上次收到 opcode 9 推送或取得名單的時間（Environment.TickCount64）
    private readonly ChannelRoster _roster = new();   // 機器人自己維護的在線名單
    private readonly RosterFileWriter _rosterFile;     // 名單檔（依大廳編號命名），Bot:WriteRosterFile 開啟時才寫

    // 一廳一機器人：以下欄位只在聊天 WS 的接收執行緒與狀態事件中讀寫（依序觸發，不會同時執行）
    private readonly ChannelRegistry _registry;
    private int _collisionAttempts;
    private volatile bool _waitingChannel;
    private long _currentChannelId; // 0 表示目前沒有佔用公頻

    private volatile BotConfig _config;
    private volatile SenderProfile? _profile;
    private long _selfPlayerId;
    private int _selfLevel;
    private volatile string? _onlineNote; // online 時附在 bot.status detail 的提醒，例如等級不足無法發言
    private Task? _runTask;
    private bool _stoppedWithError;

    /// <param name="rosterDirectory">名單檔的資料夾；null 表示不寫名單檔。</param>
    /// <param name="headers">送給遊戲方的額外 Header（App API 與聊天 WS），由 <see cref="RequestHeaderFactory"/> 產生。</param>
    public BotInstance(BotConfig config, GameEnvironmentOptions env, HttpClient http, IBackendLink backend,
        ChannelRegistry registry, BotRuntimeOptions options, ILoggerFactory loggerFactory, string? rosterDirectory,
        RequestHeaders headers)
    {
        _config = config;
        _backend = backend;
        _registry = registry;
        _options = options;
        _logger = loggerFactory.CreateLogger($"WdBot.Bot.{config.BotId}");
        _messageLogger = loggerFactory.CreateLogger(LoggingSetup.PrivateMessageCategory);
        _channelLogger = loggerFactory.CreateLogger(LoggingSetup.ChannelMessageCategory);
        _unhandledLogger = loggerFactory.CreateLogger(LoggingSetup.UnhandledPacketCategory);

        var device = new DeviceInfo(config.DeviceId, config.DeviceModel, config.Os, config.AppVersion);
        _api = new GameApiClient(http, new Uri(env.ApiBaseUrl), headers, _logger);
        _session = new GameSession(_api, new AccountCredentials(config.Account, config.Password, device), _logger);

        var fallbackUrls = string.IsNullOrWhiteSpace(env.ChatWsUrl) ? Array.Empty<string>() : [env.ChatWsUrl];
        _chat = new ChatSupervisor(_session, fallbackUrls, tracePrefix: config.BotId, new ChatSupervisorOptions
        {
            DuplicateLoginCooldown = TimeSpan.FromSeconds(options.DuplicateLoginCooldownSeconds),
            MaintenanceBackoff = TimeSpan.FromSeconds(options.MaintenanceBackoffSeconds),
            ReconnectMax = TimeSpan.FromSeconds(options.ChatReconnectMaxSeconds),
        }, headers, _logger);
        _chat.PushReceived += OnChatPush;
        _chat.StateChanged += OnChatStateChanged;
        _scanner = new ChannelMemberScanner(_logger);
        // 名單檔依環境分資料夾、依大廳編號命名：roster/{env}/channel-{channel_id}.json
        // 允許同廳多個機器人時，檔名再加上 bot_id，避免互相覆蓋或刪除
        _rosterFile = new RosterFileWriter(
            rosterDirectory is null ? null : Path.Combine(rosterDirectory, LoggingSetup.SafeFolderName(config.Env.ToLowerInvariant())),
            options.OneBotPerChannel ? null : "_" + LoggingSetup.SafeFolderName(config.BotId),
            _logger);

        _sender = new PrivateMessageSender(_chat, () => _profile, OnSendResultAsync, new PrivateMessageSenderOptions
        {
            ReplyTimeout = TimeSpan.FromSeconds(options.SendReplyTimeoutSeconds),
            QueueCapacity = options.SendQueueCapacity,
        }, _logger);
    }

    public BotConfig Config => _config;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public void Start()
    {
        if (_runTask is not null)
        {
            throw new InvalidOperationException("機器人已啟動過");
        }

        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>套用不需要重新登入的設定（auto_mark_read、scan_channel_players）。</summary>
    public void UpdateSettings(BotConfig config)
    {
        var previous = _config;
        _config = config;

        if (!previous.ScanChannelPlayers && config.ScanChannelPlayers
            && Interlocked.Read(ref _currentChannelId) != 0 && _chat.Current is { IsOpen: true } client)
        {
            _logger.LogInformation("機器人 {Bot} 開啟公頻名單掃描", config);
            StartChannelTracking(client, scanNow: true); // 連線時的推送已經錯過，主動掃描一次
        }
        else if (previous.ScanChannelPlayers && !config.ScanChannelPlayers)
        {
            _logger.LogInformation("機器人 {Bot} 關閉公頻名單掃描", config);
            StopChannelScan();
        }
    }

    public void EnqueueSend(SendMessageCommand command)
    {
        var message = new OutgoingPrivateMessage(command.RequestId, command.ToPlayerId, command.Text, command.ReplyToMessageId);
        if (!IsRunning)
        {
            ReportSendFailure(command.RequestId, LocalSendErrors.BotNotRunning, "機器人未在執行");
        }
        else if (!_sender.TryEnqueue(message))
        {
            ReportSendFailure(command.RequestId, LocalSendErrors.QueueFull, "送出佇列已滿");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // 停止中
            }
        }

        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var senderTask = _sender.RunAsync(ct);
        var failures = 0;
        Report(BotState.Starting);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var outcome = await RunOneSessionAsync(ct).ConfigureAwait(false);
                    if (outcome is not null)
                    {
                        _stoppedWithError = true;
                        Report(BotState.Error, outcome.Value.Detail, outcome.Value.Code);
                        return;
                    }
                }
                catch (GameApiException ex) when (ex.IsFatalForLogin)
                {
                    _logger.LogError("機器人 {Bot} 登入失敗，停止：{Message}", _config, ex.Message);
                    _stoppedWithError = true;
                    Report(BotState.Error, $"登入失敗：{ex.ErrorCode} {ex.ErrorMessage}", ex.ErrorCode);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failures++;
                    var delay = TimeSpan.FromSeconds(Math.Min(5 * Math.Pow(2, Math.Min(failures - 1, 8)), _options.RestartMaxDelaySeconds));
                    _logger.LogWarning(ex, "機器人 {Bot} 發生錯誤，{Seconds:0} 秒後重新登入", _config, delay.TotalSeconds);
                    Report(BotState.Reconnecting, $"{ex.GetType().Name}: {ex.Message}",
                        (ex as GameApiException)?.ErrorCode);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停止
        }
        finally
        {
            StopChannelScan();
            LeaveChannel();
            _profile = null;
            if (!_stoppedWithError)
            {
                Report(BotState.Stopped);
            }

            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
                await senderTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // 送出佇列隨機器人停止
            }
        }
    }

    /// <summary>登入並維持連線。回傳 null 代表被取消；回傳值代表需要停止的致命原因。</summary>
    private async Task<(string Detail, int? Code)?> RunOneSessionAsync(CancellationToken ct)
    {
        Report(BotState.LoggingIn);
        var login = await _session.LoginAsync(ct).ConfigureAwait(false);
        var self = await _session.CallAsync((token, c) => _api.GetPlayerInfoAsync(token, c), ct).ConfigureAwait(false);

        _selfPlayerId = self.Id != 0 ? self.Id : login.PlayerInfo.Id;
        _selfLevel = self.Level;
        _profile = new SenderProfile(
            PlayerName: string.IsNullOrEmpty(self.Name) ? login.PlayerInfo.Name : self.Name,
            AvatarId: self.AvatarId,
            AvatarOuterId: self.AvatarOuterId,
            AvatarUrl: string.IsNullOrEmpty(self.AvatarUrl) ? null : self.AvatarUrl,
            ChatBoxFrameId: self.ChatBoxFrameId);

        var problem = CheckCanChat(self);
        if (problem is not null)
        {
            _logger.LogError("機器人 {Bot} 無法私訊：{Problem}", _config, problem);
            return (problem, null);
        }

        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var refreshTask = _session.RunAutoRefreshAsync(TimeSpan.FromMinutes(_options.TokenRefreshIntervalMinutes), cycle.Token);
        var chatTask = _chat.RunAsync(cycle.Token);

        var first = await Task.WhenAny(refreshTask, chatTask).ConfigureAwait(false);
        await cycle.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(Quiet(refreshTask), Quiet(chatTask)).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        if (first == chatTask && chatTask.IsCompletedSuccessfully && chatTask.Result is { } fatal)
        {
            return ($"聊天 WS 被踢下線：{fatal.Code} {fatal.Message}", fatal.Code);
        }

        // 其中一個任務出錯（例如刷新時重新登入失敗）：把例外丟給外層決定停止或重來
        if (first.Exception is { } ex)
        {
            throw ex.GetBaseException();
        }

        throw new InvalidOperationException("聊天連線意外結束");
    }

    /// <summary>回傳需要停止的原因；可以上線時回傳 null（等級不足只警告，仍可登入、進公頻、收私訊）。</summary>
    private string? CheckCanChat(SelfPlayerInfo self)
    {
        _onlineNote = null;
        if (self.Level < 1)
        {
            _onlineNote = $"等級 {self.Level}，無法發言（需綁定手機升到等級 1）";
            _logger.LogWarning("機器人 {Bot} {Note}；仍會登入、進入公頻、接收私訊，但送私訊會被遊戲端拒絕（5008）",
                _config, _onlineNote);
        }

        if (self.PlayerPrivilege is { ChatEnabled: false })
        {
            return "後台已關閉此帳號的聊天權限（player_privilege.chat_enabled = false）";
        }

        if (self.PlayerPrivilege is null)
        {
            _logger.LogWarning("機器人 {Bot} 的本人資料沒有 player_privilege，略過聊天權限檢查", _config);
        }

        return null;
    }

    private void OnChatPush(ChatDownPacket packet)
    {
        if (packet.Opcode == ChatOpcodes.ChannelJoined)
        {
            OnChannelJoined(packet);
            return;
        }

        if (packet.Opcode == ChatOpcodes.ChannelMessage && !packet.IsError)
        {
            LogChannelMessage(packet);
            return;
        }

        if (packet.Opcode == ChatOpcodes.ChannelMembersChanged && !packet.IsError)
        {
            OnChannelMembersChanged(packet);
            return;
        }

        if (packet.Opcode == ChatOpcodes.ChannelMembers)
        {
            // 自己送出的 opcode 9 請求，回應已依 trace_id 交給翻頁流程，這裡只會收到遊戲方主動推送的
            OnChannelMembersPushed(packet);
            return;
        }

        if (packet.Opcode != ChatOpcodes.PrivateMessage)
        {
            if (packet.IsError)
            {
                _logger.LogWarning("聊天 WS 推送錯誤 opcode {Opcode}：{Code} {Message}", packet.Opcode, packet.ErrCode, packet.ErrMsg);
            }

            LogUnhandledPacket(packet);
            return;
        }

        if (packet.IsError)
        {
            _logger.LogWarning("私訊推送帶錯誤：{Code} {Message}", packet.ErrCode, packet.ErrMsg);
            return;
        }

        var message = packet.GetData<PrivateMessage>();
        if (message is null || string.IsNullOrEmpty(message.MessageId))
        {
            return;
        }

        // 私訊會推給收發雙方：自己發出的（例如從其他裝置）不轉給微服務
        if (message.PlayerId == _selfPlayerId)
        {
            return;
        }

        if (!_seenMessages.TryAdd(message.MessageId))
        {
            return;
        }

        _logger.LogInformation("收到玩家 {PlayerId}({Name}) 私訊 {MessageId}", message.PlayerId, message.PlayerName, message.MessageId);
        LogMessageContent(message);

        _backend.ReportIncomingMessage(new IncomingMessageReport
        {
            BotId = _config.BotId,
            BotPlayerId = _selfPlayerId,
            ChatId = message.ChatId,
            MessageId = message.MessageId,
            FromPlayerId = message.PlayerId,
            FromPlayerName = message.PlayerName,
            Text = message.Text,
            MessageType = message.MessageType,
            CreatedAt = message.CreatedAt,
            ReplyMessage = message.ReplyMessage is { } r
                ? new ReplyMessageReport
                {
                    MessageId = r.MessageId,
                    PlayerId = r.PlayerId,
                    PlayerName = r.PlayerName,
                    Text = r.Text,
                    MessageType = r.MessageType,
                    IsNotFound = r.IsNotFound,
                }
                : null,
        });

        if (_config.AutoMarkRead && !string.IsNullOrEmpty(message.ChatId))
        {
            _ = MarkReadAsync(message.ChatId, message.MessageId);
        }
    }

    /// <summary>
    /// 私訊內容寫到 logs/messages/{bot_id}/，與一般 log 分開。
    /// 以 Debug 等級寫入：Logging:LogLevel:WdBot.Messages.Private 不是 Debug 時完全不會寫。
    /// </summary>
    private void LogMessageContent(PrivateMessage message)
    {
        if (!_messageLogger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        using (LoggingSetup.BeginBotScope(_messageLogger, _config.BotId))
        {
            _messageLogger.LogDebug("玩家 {PlayerId}({PlayerName}) chat_id={ChatId} message_id={MessageId} type={MessageType}：{Text}",
                message.PlayerId, message.PlayerName, message.ChatId, message.MessageId, message.MessageType, message.Text);
        }
    }

    /// <summary>
    /// 公頻訊息內容寫到 logs/messages/{bot_id}/channel-日期.log，與一般 log 分開，Debug 等級才寫。
    /// 只記錄機器人已佔用的公頻：撞廳、準備換廳期間收到的不記，避免同一個廳被重複記錄。
    /// </summary>
    private void LogChannelMessage(ChatDownPacket packet)
    {
        if (!_channelLogger.IsEnabled(LogLevel.Debug) || Interlocked.Read(ref _currentChannelId) == 0)
        {
            return;
        }

        if (packet.GetData<ChannelMessage>() is not { } message)
        {
            return;
        }

        using (LoggingSetup.BeginBotScope(_channelLogger, _config.BotId))
        {
            _channelLogger.LogDebug("公頻 {ChannelId} 玩家 {PlayerId}({PlayerName}) message_id={MessageId} type={MessageType}：{Text}",
                message.ChannelId, message.PlayerId, message.PlayerName, message.MessageId, message.MessageType, message.Text);
        }
    }

    /// <summary>
    /// 程式沒有處理的聊天 WS 推送（沒有對應處理的 opcode，或帶錯誤的推送）完整寫到
    /// logs/messages/{bot_id}/unhandled-日期.log，方便之後查看遊戲方送了什麼。
    /// 以 Information 等級寫入，Logging:LogLevel:WdBot.Messages.Unhandled 設為 None 可關閉。
    /// </summary>
    private void LogUnhandledPacket(ChatDownPacket packet)
    {
        if (!_unhandledLogger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        using (LoggingSetup.BeginBotScope(_unhandledLogger, _config.BotId))
        {
            _unhandledLogger.LogInformation(
                "opcode={Opcode} success={Success} err_code={ErrCode} err_msg={ErrMsg} trace_id={TraceId} server_time={ServerTime} server_name={ServerName} data={Data}",
                packet.Opcode, packet.Success, packet.ErrCode, packet.ErrMsg, packet.TraceId, packet.ServerTime?.ToString("O"), packet.ServerName, packet.RawDataText);
        }
    }

    private async Task MarkReadAsync(string chatId, string messageId)
    {
        try
        {
            if (_chat.Current is { IsOpen: true } client)
            {
                await client.SendAsync(ChatOpcodes.ReadReceipt, new ReadReceiptData(chatId, messageId),
                    awaitReply: false, TimeSpan.Zero, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "送出已讀失敗 {ChatId}/{MessageId}", chatId, messageId);
        }
    }

    private void OnChatStateChanged(ChatConnectionState state, string? detail)
    {
        switch (state)
        {
            case ChatConnectionState.Connected:
                // 公頻名單掃描等收到 opcode 7（已加入公頻）並確認佔到廳之後才開始
                _logger.LogInformation("機器人 {Bot} 聊天已連線", _config);
                if (!_waitingChannel)
                {
                    Report(BotState.Online, _onlineNote);
                }

                break;
            case ChatConnectionState.Connecting:
                StopChannelScan();
                LeaveChannel();
                Report(BotState.Connecting);
                break;
            case ChatConnectionState.Reconnecting:
                StopChannelScan();
                LeaveChannel();
                Report(_waitingChannel ? BotState.WaitingChannel : BotState.Reconnecting, detail);
                break;
        }
    }

    // ---- 一廳一機器人 ----

    private void OnChannelJoined(ChatDownPacket packet)
    {
        if (packet.IsError)
        {
            // 例如 11016 進入頻道失敗、11020 頻道人數已滿：重連讓伺服器重新分配
            _logger.LogWarning("機器人 {Bot} 進入公頻失敗：{Code} {Message}", _config, packet.ErrCode, packet.ErrMsg);
            RetryChannelAssignment($"進入公頻失敗：{packet.ErrCode} {packet.ErrMsg}");
            return;
        }

        if (packet.GetData<ChannelJoinedData>() is not { } joined)
        {
            return;
        }

        if (_options.OneBotPerChannel
            && !_registry.TryClaim(_config.Env, joined.ChannelId, _config.BotId, out var holder))
        {
            _logger.LogInformation("機器人 {Bot} 被分配到公頻 {ChannelId}，但已由機器人 {Holder} 佔用", _config, joined.ChannelId, holder);
            RetryChannelAssignment($"公頻 {joined.ChannelId} 已有機器人 {holder}");
            return;
        }

        _collisionAttempts = 0;
        _waitingChannel = false;
        Interlocked.Exchange(ref _currentChannelId, joined.ChannelId);
        _logger.LogInformation("機器人 {Bot} 進入公頻 {ChannelId}({ChannelName})，在線 {Online} 人",
            _config, joined.ChannelId, joined.ChannelName, joined.OnlineCount);
        Report(BotState.Online, _onlineNote);

        if (_config.ScanChannelPlayers && _chat.Current is { } client)
        {
            // 每次連線都重新建立名單：重連後伺服器可能分配到不同的公頻
            // 名單等遊戲方推送 opcode 9（可能已經比 opcode 7 先到），不主動掃描
            StartChannelTracking(client, scanNow: false);
        }
    }

    /// <summary>撞廳或進廳失敗：短暫等待後重連；連續失敗達上限則下線等待一段時間。</summary>
    private void RetryChannelAssignment(string reason)
    {
        _collisionAttempts++;
        TimeSpan delay;
        string detail;

        if (_collisionAttempts < _options.ChannelCollisionMaxAttempts)
        {
            var baseSeconds = _options.ChannelCollisionRetrySeconds;
            delay = TimeSpan.FromSeconds(baseSeconds + Random.Shared.NextDouble() * baseSeconds * 0.5);
            detail = $"{reason}，第 {_collisionAttempts} 次重新分配";
        }
        else
        {
            // 可能是機器人比公頻還多：下線等待，之後重新計算次數
            _collisionAttempts = 0;
            _waitingChannel = true;
            delay = TimeSpan.FromSeconds(_options.ChannelWaitingRetrySeconds);
            detail = $"{reason}，已連續 {_options.ChannelCollisionMaxAttempts} 次未分到空廳，下線 {_options.ChannelWaitingRetrySeconds} 秒後再試";
            _logger.LogWarning("機器人 {Bot} {Detail}", _config, detail);
        }

        if (!_chat.RequestReconnect(delay, detail))
        {
            _logger.LogDebug("機器人 {Bot} 要求重連時連線已結束", _config);
        }
    }

    private void LeaveChannel()
    {
        if (Interlocked.Exchange(ref _currentChannelId, 0) != 0)
        {
            _registry.Release(_config.BotId);
        }
    }

    // ---- 公頻名單 ----
    // 名單以遊戲方推送為主：連線後伺服器會主動推送 opcode 9（成員列表第一頁），之後靠 opcode 17 維護增減。
    // 推送的那頁還有 next_cursor（超過 100 人）時，主動要剩下的頁面才湊成完整名單。
    // 超過 Bot:ChannelRescanMinutes 分鐘都沒收到 opcode 9 推送，才主動從第一頁掃描一次。

    /// <param name="scanNow">true 時立刻主動掃描（例如在線中途才開啟 scan_channel_players，不會再收到連線時的推送）。</param>
    private void StartChannelTracking(ChatWsClient client, bool scanNow)
    {
        CancellationTokenSource cts;
        lock (_channelGate)
        {
            CancelScanLocked();
            try
            {
                cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            }
            catch (ObjectDisposedException)
            {
                return; // 機器人已停止
            }

            _scanCts = cts;
            _trackingClient = client;
            _pendingChanges = []; // 拿到完整名單前的異動先暫存
            _channelReportingActive = false;
            _fetchingList = false;
            _lastListTick = Environment.TickCount64;

            var early = _earlyPushedPage;
            _earlyPushedPage = null;
            if (scanNow)
            {
                StartFetchLocked(cts, null);
            }
            else if (early is not null && IsCurrentChannel(early.ChannelId))
            {
                // opcode 9 比 opcode 7 早到：現在才確認佔到廳，補處理
                HandlePushedPageLocked(early);
            }
        }

        if (_options.ChannelRescanMinutes > 0)
        {
            _ = RunPushWatchdogAsync(cts);
        }
    }

    private void StopChannelScan()
    {
        lock (_channelGate)
        {
            CancelScanLocked();
            _trackingClient = null;
            _earlyPushedPage = null;
            _pendingChanges = null;
            _channelReportingActive = false;
            _fetchingList = false;
            _roster.Clear();
        }

        _rosterFile.Delete();
    }

    private void CancelScanLocked()
    {
        if (_scanCts is { } old)
        {
            _scanCts = null;
            old.Cancel();
            old.Dispose();
        }
    }

    private bool IsCurrentChannel(long channelId) =>
        channelId == 0 || channelId == Interlocked.Read(ref _currentChannelId);

    /// <summary>遊戲方主動推送的 opcode 9（不是回應自己送出的請求）。</summary>
    private void OnChannelMembersPushed(ChatDownPacket packet)
    {
        if (packet.IsError)
        {
            _logger.LogWarning("公頻成員列表推送帶錯誤：{Code} {Message}", packet.ErrCode, packet.ErrMsg);
            return;
        }

        if (!_config.ScanChannelPlayers || packet.GetData<ChannelMembersPage>() is not { } page)
        {
            return;
        }

        lock (_channelGate)
        {
            if (_scanCts is null)
            {
                // 還沒確認佔到廳（opcode 7 與 9 順序不固定）：先留著，佔廳後再處理
                _earlyPushedPage = page;
                return;
            }

            if (!IsCurrentChannel(page.ChannelId))
            {
                _logger.LogDebug("略過其他公頻 {ChannelId} 的成員列表推送", page.ChannelId);
                return;
            }

            HandlePushedPageLocked(page);
        }
    }

    /// <summary>呼叫前必須持有 _channelGate，且 _scanCts 不為 null。</summary>
    private void HandlePushedPageLocked(ChannelMembersPage page)
    {
        _lastListTick = Environment.TickCount64;

        if (_fetchingList)
        {
            // 正在翻頁取得名單，結果出來後就是最新的，這次推送不另外處理
            _logger.LogDebug("收到公頻 {ChannelId} 成員列表推送，但正在取得名單，略過", page.ChannelId);
            return;
        }

        if (string.IsNullOrEmpty(page.NextCursor))
        {
            // 只有一頁：推送的內容就是完整名單
            var players = page.PlayerList.Where(p => p.PlayerId != 0).DistinctBy(p => p.PlayerId).ToList();
            ApplySnapshotLocked(new ChannelSnapshot(page.ChannelId, page.ChannelName, page.OnlineCount, page.TotalCount, players), "遊戲方推送");
            return;
        }

        StartFetchLocked(_scanCts!, page);
    }

    /// <summary>
    /// 開始翻頁取得完整名單：firstPage 為推送的第一頁時只要後續頁面，null 時從第一頁主動掃描。
    /// 呼叫前必須持有 _channelGate。
    /// </summary>
    private void StartFetchLocked(CancellationTokenSource cts, ChannelMembersPage? firstPage)
    {
        if (_trackingClient is not { } client)
        {
            return;
        }

        _fetchingList = true;
        _pendingChanges ??= []; // 翻頁期間的異動先暫存，拿到名單後接在後面依序處理
        _ = Task.Run(() => FetchListAsync(client, cts, firstPage)); // 不在鎖內、也不在接收執行緒上送出請求
    }

    private async Task FetchListAsync(ChatWsClient client, CancellationTokenSource cts, ChannelMembersPage? firstPage)
    {
        CancellationToken ct;
        try
        {
            ct = cts.Token;
        }
        catch (ObjectDisposedException)
        {
            return; // 已斷線、換廳或關閉
        }

        ChannelSnapshot? snapshot = null;
        Exception? failure = null;
        try
        {
            snapshot = await _scanner.ScanAsync(client, firstPage, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        bool hadList;
        lock (_channelGate)
        {
            // 取得期間已斷線、重連或關閉：結果作廢
            if (!ReferenceEquals(_scanCts, cts))
            {
                return;
            }

            _fetchingList = false;
            hadList = _roster.HasSnapshot;
            if (snapshot is not null)
            {
                ApplySnapshotLocked(snapshot, firstPage is null ? "主動掃描" : "遊戲方推送＋後續頁");
            }
            else
            {
                // 失敗：沿用舊名單（還沒有名單時至少能回報之後的異動），等下次推送或再次逾時
                FlushPendingLocked();
                _lastListTick = Environment.TickCount64;
            }
        }

        if (failure is not null)
        {
            _logger.LogWarning(failure, "機器人 {Bot} 取得公頻名單失敗", _config);
            if (!hadList)
            {
                Report(BotState.Online, $"公頻名單取得失敗：{failure.Message}");
            }
        }
    }

    /// <summary>超過 ChannelRescanMinutes 沒收到遊戲方推送的成員列表時，主動掃描一次。</summary>
    private async Task RunPushWatchdogAsync(CancellationTokenSource cts)
    {
        CancellationToken ct;
        try
        {
            ct = cts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        var timeoutMs = (long)TimeSpan.FromMinutes(_options.ChannelRescanMinutes).TotalMilliseconds;
        var checkInterval = TimeSpan.FromSeconds(Math.Clamp(timeoutMs / 1000 / 10, 5, 30));

        try
        {
            while (true)
            {
                await Task.Delay(checkInterval, ct).ConfigureAwait(false);
                lock (_channelGate)
                {
                    if (!ReferenceEquals(_scanCts, cts))
                    {
                        return;
                    }

                    if (_fetchingList || Environment.TickCount64 - _lastListTick < timeoutMs)
                    {
                        continue;
                    }

                    _logger.LogInformation("公頻 {ChannelId} 已 {Minutes} 分鐘沒收到遊戲方推送的成員列表，主動掃描一次",
                        Interlocked.Read(ref _currentChannelId), _options.ChannelRescanMinutes);
                    StartFetchLocked(cts, null);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 斷線、換廳或關閉
        }
    }

    /// <summary>以完整名單取代目前名單並回報，再補處理暫存的異動。呼叫前必須持有 _channelGate。</summary>
    private void ApplySnapshotLocked(ChannelSnapshot snapshot, string source)
    {
        _backend.ReportChannelSnapshot(new ChannelSnapshotReport
        {
            BotId = _config.BotId,
            BotPlayerId = _selfPlayerId,
            ChannelId = snapshot.ChannelId,
            ChannelName = snapshot.ChannelName,
            OnlineCount = snapshot.OnlineCount,
            TotalCount = snapshot.TotalCount,
            Players = snapshot.Players.Where(p => p.PlayerId != _selfPlayerId).Select(ToChannelPlayer).ToList(),
        });

        _roster.Replace(snapshot.ChannelId, snapshot.ChannelName, snapshot.Players, _selfPlayerId);
        _logger.LogInformation("公頻 {ChannelId}({ChannelName}) 名單更新（{Source}）：目前在線 {Count} 人",
            snapshot.ChannelId, snapshot.ChannelName, source, _roster.Count);
        _rosterFile.Update(_roster.Snapshot());

        FlushPendingLocked();
        _lastListTick = Environment.TickCount64;
    }

    /// <summary>依序處理暫存的異動，之後改為即時回報。呼叫前必須持有 _channelGate。</summary>
    private void FlushPendingLocked()
    {
        foreach (var change in _pendingChanges ?? [])
        {
            ReportChannelChangeLocked(change);
        }

        _pendingChanges = null;
        _channelReportingActive = true;
    }

    private void OnChannelMembersChanged(ChatDownPacket packet)
    {
        if (!_config.ScanChannelPlayers || packet.GetData<ChannelMembersChangedData>() is not { } change)
        {
            return;
        }

        lock (_channelGate)
        {
            if (_pendingChanges is not null)
            {
                _pendingChanges.Add(change);
            }
            else if (_channelReportingActive)
            {
                ReportChannelChangeLocked(change);
            }
        }
    }

    private void ReportChannelChangeLocked(ChannelMembersChangedData change)
    {
        var joined = change.Joined.Where(p => p.PlayerId != 0 && p.PlayerId != _selfPlayerId).Select(ToChannelPlayer).ToList();
        var left = change.Left.Where(id => id != _selfPlayerId).ToList();
        if (joined.Count == 0 && left.Count == 0)
        {
            return;
        }

        _backend.ReportChannelMembersChanged(new ChannelMembersChangedReport
        {
            BotId = _config.BotId,
            BotPlayerId = _selfPlayerId,
            ChannelId = change.ChannelId,
            OnlineCount = change.OnlineCount,
            TotalCount = change.TotalCount,
            Joined = joined,
            Left = left,
        });

        if (_roster.Apply(change.Joined, left, _selfPlayerId))
        {
            _logger.LogInformation("公頻 {ChannelId} 異動：進入 {Joined}、離開 {Left}，目前在線 {Count} 人",
                change.ChannelId, joined.Count, left.Count, _roster.Count);
            _rosterFile.Update(_roster.Snapshot());
        }
    }

    private static ChannelPlayer ToChannelPlayer(ChannelMember m) => new()
    {
        PlayerId = m.PlayerId,
        PlayerName = m.PlayerName,
        Level = m.Level,
        AvatarId = m.AvatarId,
        AvatarOuterId = m.AvatarOuterId,
        AvatarUrl = string.IsNullOrEmpty(m.AvatarUrl) ? null : m.AvatarUrl,
    };

    private Task OnSendResultAsync(PrivateSendResult result)
    {
        _backend.ReportSendResult(new SendResultReport
        {
            RequestId = result.RequestId,
            BotId = _config.BotId,
            Success = result.Success,
            MessageIds = result.MessageIds.ToList(),
            ErrCode = result.ErrCode,
            ErrMsg = result.ErrMsg,
        });
        return Task.CompletedTask;
    }

    private void ReportSendFailure(string requestId, int code, string message) =>
        _backend.ReportSendResult(new SendResultReport
        {
            RequestId = requestId,
            BotId = _config.BotId,
            Success = false,
            ErrCode = code,
            ErrMsg = message,
        });

    private void Report(BotState state, string? detail = null, int? errCode = null)
    {
        var profile = _profile;
        _backend.ReportBotStatus(new BotStatusReport
        {
            BotId = _config.BotId,
            State = state,
            PlayerId = _selfPlayerId == 0 ? null : _selfPlayerId,
            PlayerName = profile?.PlayerName,
            Level = _selfPlayerId == 0 ? null : _selfLevel,
            ChannelId = Interlocked.Read(ref _currentChannelId) is var channelId and not 0 ? channelId : null,
            ErrCode = errCode,
            Detail = detail,
        });
    }

    private static async Task Quiet(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 結果由呼叫端從 task 本身讀取
        }
    }
}
