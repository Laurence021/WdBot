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
    /// <summary>與微服務的連線，用來回報狀態、私訊與公頻名單。</summary>
    private readonly IBackendLink _backend;

    /// <summary>機器人執行參數（appsettings 的 Bot 區段）。</summary>
    private readonly BotRuntimeOptions _options;

    /// <summary>這個機器人的一般 log（類別 WdBot.Bot.{bot_id}）。</summary>
    private readonly ILogger _logger;

    /// <summary>私訊內容專用的 log，依機器人分檔，Debug 等級才寫。</summary>
    private readonly ILogger _messageLogger;

    /// <summary>公頻訊息內容專用的 log，依機器人分檔，Debug 等級才寫。</summary>
    private readonly ILogger _channelLogger;

    /// <summary>未處理的聊天 WS 推送專用的 log，依機器人分檔。</summary>
    private readonly ILogger _unhandledLogger;
    /// <summary>呼叫遊戲方的 App API。</summary>
    private readonly GameApiClient _api;

    /// <summary>登入狀態與 token 管理。</summary>
    private readonly GameSession _session;

    /// <summary>聊天 WS 連線與自動重連。</summary>
    private readonly ChatSupervisor _chat;

    /// <summary>私訊送出佇列。</summary>
    private readonly PrivateMessageSender _sender;

    /// <summary>最近收過的私訊 message_id，避免同一則私訊重複轉給微服務。</summary>
    private readonly RecentIdCache _seenMessages = new();

    /// <summary>停止機器人時取消，所有背景工作都連動這個 token。</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>以 opcode 9 翻頁取得公頻完整名單。</summary>
    private readonly ChannelMemberScanner _scanner;

    // 公頻名單狀態，皆以 _channelGate 保護：
    // 取得完整名單前 _pendingChanges 不為 null，opcode 17 先暫存；snapshot 送出後才開始直接回報異動
    /// <summary>保護公頻名單相關狀態的鎖。</summary>
    private readonly object _channelGate = new();

    /// <summary>目前佔廳的名單追蹤；null 表示沒有在追蹤。</summary>
    private CancellationTokenSource? _scanCts;

    /// <summary>追蹤中的聊天連線，翻頁時使用。</summary>
    private ChatWsClient? _trackingClient;

    /// <summary>確認佔廳前就收到的 opcode 9 推送（opcode 7 與 9 的順序不固定）。</summary>
    private ChannelMembersPage? _earlyPushedPage;

    /// <summary>取得完整名單期間暫存的 opcode 17 異動；null 表示不在取得名單中。</summary>
    private List<ChannelMembersChangedData>? _pendingChanges;

    /// <summary>是否已送出完整名單，之後收到的異動可以直接回報。</summary>
    private bool _channelReportingActive;

    /// <summary>是否正在翻頁取得名單（推送的後續頁或主動掃描）。</summary>
    private bool _fetchingList;

    /// <summary>上次收到 opcode 9 推送或取得名單的時間（Environment.TickCount64）。</summary>
    private long _lastListTick;

    /// <summary>機器人自己維護的在線名單。</summary>
    private readonly ChannelRoster _roster = new();

    /// <summary>名單檔（依大廳編號命名），Bot:WriteRosterFile 開啟時才寫。</summary>
    private readonly RosterFileWriter _rosterFile;

    // 一廳一機器人：以下欄位只在聊天 WS 的接收執行緒與狀態事件中讀寫（依序觸發，不會同時執行）
    /// <summary>整個服務共用的「公頻 → 機器人」對照表。</summary>
    private readonly ChannelRegistry _registry;

    /// <summary>連續撞廳（被分配到已有機器人的公頻）的次數。</summary>
    private int _collisionAttempts;

    /// <summary>是否因連續撞廳而下線等待中。</summary>
    private volatile bool _waitingChannel;

    /// <summary>目前佔用的公頻 ID；0 表示沒有佔用公頻。</summary>
    private long _currentChannelId;

    /// <summary>目前的機器人設定；不需要重新登入的欄位會在執行中更新。</summary>
    private volatile BotConfig _config;

    /// <summary>發私訊時帶入的外觀（暱稱、頭像、對話框）；尚未取得本人資料時為 null。</summary>
    private volatile SenderProfile? _profile;

    /// <summary>機器人自己的 player_id，用來排除自己的訊息與名單。</summary>
    private long _selfPlayerId;

    /// <summary>機器人的等級，回報狀態時帶上。</summary>
    private int _selfLevel;

    /// <summary>online 時附在 bot.status detail 的提醒，例如等級不足無法發言；沒有時為 null。</summary>
    private volatile string? _onlineNote;

    /// <summary>主流程（<see cref="RunAsync"/>）的工作；尚未啟動時為 null。</summary>
    private Task? _runTask;

    /// <summary>是否因致命錯誤停止（已回報 error），結束時不再回報 stopped。</summary>
    private bool _stoppedWithError;

    /// <summary>建立機器人；建構時不會登入，要呼叫 <see cref="Start"/> 才開始。</summary>
    /// <param name="config">機器人設定（已套用 device id 清單分配的裝置）。</param>
    /// <param name="env">這個機器人所屬環境的網址設定。</param>
    /// <param name="http">所有機器人共用的 HttpClient。</param>
    /// <param name="backend">微服務連線，用來上報私訊、名單與狀態。</param>
    /// <param name="registry">公頻佔用登記，確保一廳一機器人。</param>
    /// <param name="options">機器人執行參數（Bot 區段）。</param>
    /// <param name="loggerFactory">用來建立這個機器人的各類 logger。</param>
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

        // device_type、location 為選填：空白時轉成 null，登入時就不會送出空字串
        var device = new DeviceInfo(config.DeviceId, config.DeviceModel, config.Os, config.AppVersion,
            string.IsNullOrWhiteSpace(config.DeviceType) ? null : config.DeviceType.Trim(),
            string.IsNullOrWhiteSpace(config.Location) ? null : config.Location.Trim());
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

    /// <summary>目前的機器人設定。</summary>
    public BotConfig Config => _config;

    /// <summary>主流程是否還在執行（因致命錯誤停止後為 false）。</summary>
    public bool IsRunning => _runTask is { IsCompleted: false };

    /// <summary>在背景啟動主流程；只能呼叫一次。</summary>
    public void Start()
    {
        if (_runTask is not null)
        {
            throw new InvalidOperationException("機器人已啟動過");
        }

        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>套用不需要重新登入的設定（auto_mark_read、scan_channel_players）。</summary>
    /// <param name="config">新的機器人設定。</param>
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

    /// <summary>把微服務的私訊指令放進送出佇列；機器人沒在執行或佇列已滿時直接回報失敗。</summary>
    /// <param name="command">微服務下發的私訊指令。</param>
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

    /// <summary>停止機器人：取消所有背景工作，最多等 10 秒讓主流程結束（離開公頻、回報 stopped）。</summary>
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

    /// <summary>
    /// 主流程：登入 → 取得本人資料 → 連聊天 WS，並同時執行 token 自動刷新與聊天連線維持。
    /// 暫時性錯誤以指數退避重新整個流程；致命錯誤回報 error 後停止。
    /// </summary>
    /// <param name="ct">取消時停止機器人。</param>
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
    /// <param name="ct">取消時結束並回傳 null。</param>
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
    /// <param name="self">登入後取得的本人資料。</param>
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

    /// <summary>
    /// 處理聊天 WS 推送：依 opcode 分派給公頻、名單等處理；私訊去重後轉給微服務並視設定送已讀；
    /// 其他未處理的推送寫進 unhandled log。
    /// </summary>
    /// <param name="packet">收到的推送封包。</param>
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
    /// <param name="message">收到的私訊。</param>
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
    /// <param name="packet">公頻訊息的推送封包。</param>
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
    /// <param name="packet">沒有處理的推送封包。</param>
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

    /// <summary>送出私訊已讀（opcode 6），不等回包；失敗只記 Debug log。</summary>
    /// <param name="chatId">私訊聊天室 ID。</param>
    /// <param name="messageId">要標記為已讀的訊息 ID。</param>
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

    /// <summary>聊天連線狀態變更：連上時回報 online；連線中或重連時停止名單追蹤、釋放公頻並回報狀態。</summary>
    /// <param name="state">新的連線狀態。</param>
    /// <param name="detail">狀態說明，例如斷線原因；可為 null。</param>
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

    /// <summary>
    /// 處理 opcode 7 進入公頻：進廳失敗或公頻已被其他機器人佔用時重新分配；
    /// 成功佔廳後回報 online，並在 scan_channel_players 開啟時開始追蹤名單。
    /// </summary>
    /// <param name="packet">opcode 7 的推送封包。</param>
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
    /// <param name="reason">重新分配公頻的原因，寫進 log 與狀態說明。</param>
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

    /// <summary>離開目前佔用的公頻，並在對照表中釋放，讓其他機器人可以進入。</summary>
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

    /// <summary>
    /// 開始追蹤目前公頻的成員名單：清空舊的追蹤狀態，處理佔廳前就收到的推送，
    /// 並在 ChannelRescanMinutes 大於 0 時啟動「太久沒收到推送就主動掃描」的監看。
    /// </summary>
    /// <param name="client">已佔用公頻的聊天連線，翻頁時使用。</param>
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

    /// <summary>停止名單追蹤：取消翻頁與逾時檢查、清空暫存與名單，並刪除名單檔。</summary>
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

    /// <summary>取消目前的名單追蹤工作。呼叫前必須持有 _channelGate。</summary>
    private void CancelScanLocked()
    {
        if (_scanCts is { } old)
        {
            _scanCts = null;
            old.Cancel();
            old.Dispose();
        }
    }

    /// <summary>是否為目前佔用的公頻；channelId 為 0（推送沒帶公頻）時視為是。</summary>
    /// <param name="channelId">推送中的公頻 ID。</param>
    private bool IsCurrentChannel(long channelId) =>
        channelId == 0 || channelId == Interlocked.Read(ref _currentChannelId);

    /// <summary>遊戲方主動推送的 opcode 9（不是回應自己送出的請求）。</summary>
    /// <param name="packet">opcode 9 的推送封包。</param>
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
    /// <param name="page">推送的成員列表頁。</param>
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
    /// <param name="cts">這次名單追蹤的取消來源。</param>
    /// <param name="firstPage">推送的第一頁；null 時從第一頁主動掃描。</param>
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

    /// <summary>
    /// 在背景翻頁取得完整名單；成功時取代名單並回報 snapshot，失敗時沿用舊名單並補處理暫存的異動。
    /// 取得期間如果已斷線或換廳，結果作廢。
    /// </summary>
    /// <param name="client">追蹤中的聊天連線。</param>
    /// <param name="cts">這次名單追蹤的取消來源；已取消代表斷線或換廳，結果作廢。</param>
    /// <param name="firstPage">推送的第一頁；null 時從第一頁主動掃描。</param>
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
    /// <param name="cts">這次名單追蹤的取消來源；取消時停止監看。</param>
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
    /// <param name="snapshot">取得的完整名單。</param>
    /// <param name="source">名單來源，寫進 log，例如「遊戲方推送」「主動掃描」。</param>
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

    /// <summary>收到 opcode 17 成員異動：取得完整名單期間先暫存，名單已送出後直接回報。</summary>
    /// <param name="packet">opcode 17 的推送封包。</param>
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

    /// <summary>回報一筆成員異動（排除機器人自己），同時更新名單與名單檔。呼叫前必須持有 _channelGate。</summary>
    /// <param name="change">一筆成員異動（進入與離開的玩家）。</param>
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

    /// <summary>把遊戲方的公頻成員轉成回報給微服務的格式（空的頭像網址轉成 null）。</summary>
    /// <param name="m">遊戲方的公頻成員資料。</param>
    private static ChannelPlayer ToChannelPlayer(ChannelMember m) => new()
    {
        PlayerId = m.PlayerId,
        PlayerName = m.PlayerName,
        Level = m.Level,
        AvatarId = m.AvatarId,
        AvatarOuterId = m.AvatarOuterId,
        AvatarUrl = string.IsNullOrEmpty(m.AvatarUrl) ? null : m.AvatarUrl,
    };

    /// <summary>私訊送出完成時，把結果回報給微服務。</summary>
    /// <param name="result">私訊送出的結果。</param>
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

    /// <summary>私訊還沒進入送出佇列就失敗時，直接回報失敗結果。</summary>
    /// <param name="requestId">私訊指令的 request_id。</param>
    /// <param name="code">錯誤碼；負數為本地錯誤，見 <see cref="LocalSendErrors"/>。</param>
    /// <param name="message">錯誤說明。</param>
    private void ReportSendFailure(string requestId, int code, string message) =>
        _backend.ReportSendResult(new SendResultReport
        {
            RequestId = requestId,
            BotId = _config.BotId,
            Success = false,
            ErrCode = code,
            ErrMsg = message,
        });

    /// <summary>回報機器人狀態，自動帶上 player_id、暱稱、等級與目前佔用的公頻。</summary>
    /// <param name="state">機器人狀態。</param>
    /// <param name="detail">狀態說明；可為 null。</param>
    /// <param name="errCode">錯誤碼；沒有時為 null。</param>
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

    /// <summary>等待工作結束但不拋出例外，用在只需要等它停下來的地方。</summary>
    /// <param name="task">要等待的工作。</param>
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
