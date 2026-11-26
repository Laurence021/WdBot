using Microsoft.Extensions.Options;
using WdBot.Backend;
using WdBot.Game.Chat;

namespace WdBot.Service;

/// <summary>
/// 依微服務下發的設定啟停機器人，並把私訊指令轉給對應的機器人。
/// </summary>
public sealed class BotManager : BackgroundService, IBackendHandler
{
    /// <summary>與微服務的連線：接收設定與私訊指令，回報結果。</summary>
    private readonly IBackendLink _backend;

    /// <summary>「公頻 → 機器人」對照表，傳給每個機器人。</summary>
    private readonly ChannelRegistry _channelRegistry;

    /// <summary>沒指定 device_id 的機器人，從這裡分配 device_id。</summary>
    private readonly DevicePool _devicePool;

    /// <summary>遊戲環境網址與 Header 設定（appsettings 的 Game 區段）。</summary>
    private readonly GameOptions _gameOptions;

    /// <summary>機器人執行參數（appsettings 的 Bot 區段）。</summary>
    private readonly BotRuntimeOptions _runtimeOptions;

    /// <summary>建立每個機器人自己的 logger。</summary>
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>記錄機器人的啟動與停止。</summary>
    private readonly ILogger<BotManager> _logger;

    /// <summary>所有機器人共用的 HttpClient（共用連線池）。</summary>
    private readonly HttpClient _http;

    /// <summary>名單檔根資料夾（底下依 env 分資料夾、依大廳編號命名）；Bot:WriteRosterFile 關閉時為 null。</summary>
    private readonly string? _rosterDirectory;

    /// <summary>執行中的機器人，以 bot_id 為鍵；以 _lock 保護。</summary>
    private readonly Dictionary<string, BotInstance> _bots = new(StringComparer.Ordinal);

    /// <summary>讓設定變更與私訊指令依序處理，避免同時啟停同一個機器人。</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>建立管理器：清掉上次留下的名單檔，並建立共用的 HttpClient（自動解 gzip／deflate）。</summary>
    /// <param name="backend">微服務連線。</param>
    /// <param name="channelRegistry">公頻佔用登記，所有機器人共用。</param>
    /// <param name="devicePool">device id 清單，啟動機器人前從這裡分配裝置。</param>
    /// <param name="gameOptions">遊戲端設定（Game 區段：環境網址與 Header）。</param>
    /// <param name="runtimeOptions">機器人執行參數（Bot 區段）。</param>
    /// <param name="fileLoggingOptions">log 資料夾設定（FileLogging 區段）；名單檔寫在它底下的 roster 資料夾。</param>
    /// <param name="environment">主機環境，用來取得 content root。</param>
    /// <param name="loggerFactory">用來建立各機器人的 logger。</param>
    /// <param name="logger">記錄機器人的啟動、停止與錯誤。</param>
    public BotManager(IBackendLink backend, ChannelRegistry channelRegistry, DevicePool devicePool, IOptions<GameOptions> gameOptions,
        IOptions<BotRuntimeOptions> runtimeOptions, IOptions<FileLoggingOptions> fileLoggingOptions, IHostEnvironment environment,
        ILoggerFactory loggerFactory, ILogger<BotManager> logger)
    {
        _rosterDirectory = runtimeOptions.Value.WriteRosterFile
            ? Path.Combine(LoggingSetup.ResolvePath(environment.ContentRootPath, fileLoggingOptions.Value.Directory), "roster")
            : null;
        if (_rosterDirectory is not null)
        {
            // 上次執行留下的名單檔已經不準了，啟動時先清掉
            RosterFileWriter.CleanDirectory(_rosterDirectory, logger);
        }

        _backend = backend;
        _channelRegistry = channelRegistry;
        _devicePool = devicePool;
        _gameOptions = gameOptions.Value;
        _runtimeOptions = runtimeOptions.Value;
        _loggerFactory = loggerFactory;
        _logger = logger;
        // 只解 gzip／deflate：Header 設定送的是 "deflate, gzip"（同 Unity），不加 Brotli，.NET 才不會自己多帶 br
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        })
        {
            Timeout = TimeSpan.FromSeconds(_runtimeOptions.HttpTimeoutSeconds),
        };
    }

    /// <summary>服務啟動後維持與微服務的連線，直到服務停止。</summary>
    /// <param name="stoppingToken">服務停止時取消。</param>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _backend.RunAsync(this, stoppingToken);

    /// <summary>服務停止時，先停止微服務連線，再停止所有機器人。</summary>
    /// <param name="cancellationToken">主機要求立即結束（不再等待正常停止）時取消。</param>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("停止所有機器人（{Count} 個）", _bots.Count);
            await Task.WhenAll(_bots.Values.Select(b => b.DisposeAsync().AsTask())).ConfigureAwait(false);
            _bots.Clear();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>釋放共用的 HttpClient。</summary>
    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    /// <summary>套用全量設定：停止不在清單中的機器人，再逐一套用清單中的設定。</summary>
    /// <param name="configs">微服務下發的所有機器人設定。</param>
    /// <param name="ct">用來取消作業。</param>
    public async Task OnConfigSnapshotAsync(IReadOnlyList<BotConfig> configs, CancellationToken ct)
    {
        // 同一個 bot_id 出現多次時以最後一筆為準
        var desired = configs
            .Where(c => !string.IsNullOrWhiteSpace(c.BotId))
            .GroupBy(c => c.BotId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var botId in _bots.Keys.Where(id => !desired.ContainsKey(id)).ToList())
            {
                await StopBotAsync(botId, "不在全量設定中").ConfigureAwait(false);
                _devicePool.Release(botId);
            }

            foreach (var config in desired.Values)
            {
                await ApplyAsync(config).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>套用單一機器人設定（新增、更新或依 enabled 停用）。</summary>
    /// <param name="config">要套用的機器人設定。</param>
    /// <param name="ct">用來取消作業。</param>
    public async Task OnConfigUpsertAsync(BotConfig config, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ApplyAsync(config).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>停止並移除指定的機器人，回報 stopped。</summary>
    /// <param name="botId">要移除的機器人 ID。</param>
    /// <param name="ct">用來取消作業。</param>
    public async Task OnConfigRemoveAsync(string botId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopBotAsync(botId, "後台移除").ConfigureAwait(false);
            _devicePool.Release(botId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>把私訊指令交給對應的機器人；機器人不存在時直接回報失敗（-5）。</summary>
    /// <param name="command">微服務下發的私訊指令。</param>
    /// <param name="ct">用來取消作業。</param>
    public async Task OnSendMessageAsync(SendMessageCommand command, CancellationToken ct)
    {
        BotInstance? bot;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _bots.TryGetValue(command.BotId, out bot);
        }
        finally
        {
            _lock.Release();
        }

        if (bot is null)
        {
            _logger.LogWarning("私訊指令 {RequestId} 指定的機器人 {BotId} 不存在", command.RequestId, command.BotId);
            _backend.ReportSendResult(new SendResultReport
            {
                RequestId = command.RequestId,
                BotId = command.BotId,
                Success = false,
                ErrCode = LocalSendErrors.BotNotRunning,
                ErrMsg = "機器人不存在或未啟用",
            });
            return;
        }

        bot.EnqueueSend(command);
    }

    /// <summary>呼叫前必須持有 _lock。</summary>
    /// <param name="config">要套用的機器人設定（尚未分配裝置）。</param>
    private async Task ApplyAsync(BotConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.BotId))
        {
            _logger.LogWarning("忽略沒有 bot_id 的設定");
            return;
        }

        var error = config.Validate();
        if (error is null && _gameOptions.Find(config.Env) is null)
        {
            error = $"未知的環境 env={config.Env}";
        }

        if (error is null && _bots.Values.Any(b => b.Config.BotId != config.BotId
                                                   && string.Equals(b.Config.Account, config.Account, StringComparison.OrdinalIgnoreCase)
                                                   && string.Equals(b.Config.Env, config.Env, StringComparison.OrdinalIgnoreCase)))
        {
            // 同帳號連兩條聊天 WS 會互相踢線（7000）
            error = $"帳號 {config.Account} 已被其他機器人使用";
        }

        if (error is not null)
        {
            await StopBotAsync(config.BotId, "設定無效", reportStopped: false).ConfigureAwait(false);
            _logger.LogError("機器人 {Bot} 設定無效：{Error}", config, error);
            _backend.ReportBotStatus(new BotStatusReport { BotId = config.BotId, State = BotState.Error, Detail = error });
            return;
        }

        if (!config.Enabled)
        {
            // 停用不釋放 device_id 名額，重新啟用時沿用
            if (!await StopBotAsync(config.BotId, "設定為停用").ConfigureAwait(false))
            {
                _backend.ReportBotStatus(new BotStatusReport { BotId = config.BotId, State = BotState.Stopped, Detail = "已停用" });
            }

            return;
        }

        // 沒指定 device_id 時從 device id 清單分配（同一個機器人沿用先前分到的），之後都用分配後的設定
        var resolved = _devicePool.Resolve(config, out error);
        if (resolved is not null && string.IsNullOrWhiteSpace(resolved.Os))
        {
            error = "缺少必填欄位：os（BOT 設定與 device id 清單都沒有提供）";
        }

        if (resolved is null || error is not null)
        {
            await StopBotAsync(config.BotId, "設定無效", reportStopped: false).ConfigureAwait(false);
            _logger.LogError("機器人 {Bot} 無法啟動：{Error}", config, error);
            _backend.ReportBotStatus(new BotStatusReport { BotId = config.BotId, State = BotState.Error, Detail = error });
            return;
        }

        config = resolved;

        if (_bots.TryGetValue(config.BotId, out var existing))
        {
            if (existing.IsRunning && existing.Config.HasSameIdentity(config))
            {
                existing.UpdateSettings(config);
                return;
            }

            // 帳號相關欄位改變，或先前因錯誤停止：重建
            await StopBotAsync(config.BotId, "設定變更，重新啟動", reportStopped: false).ConfigureAwait(false);
        }

        var env = _gameOptions.Find(config.Env)!;
        var headers = RequestHeaderFactory.Build(_gameOptions, config);
        var bot = new BotInstance(config, env, _http, _backend, _channelRegistry, _runtimeOptions, _loggerFactory, _rosterDirectory, headers);
        _bots[config.BotId] = bot;
        _logger.LogInformation("啟動機器人 {Bot}，使用 {Env} 環境：API {ApiBaseUrl}", config, config.Env, env.ApiBaseUrl);
        if (headers.HttpUserAgent is { } userAgent)
        {
            _logger.LogInformation("機器人 {Bot} User-Agent：{UserAgent}", config, userAgent);
        }
        bot.Start();
    }

    /// <summary>停止並移除機器人。呼叫前必須持有 _lock。回傳是否真的有停止。</summary>
    /// <param name="botId">要停止的機器人 ID。</param>
    /// <param name="reason">停止原因，寫進 log。</param>
    /// <param name="reportStopped">是否向微服務回報 stopped。</param>
    private async Task<bool> StopBotAsync(string botId, string reason, bool reportStopped = true)
    {
        if (!_bots.Remove(botId, out var bot))
        {
            return false;
        }

        _logger.LogInformation("停止機器人 {Bot}：{Reason}", bot.Config, reason);
        await bot.DisposeAsync().ConfigureAwait(false);

        if (reportStopped)
        {
            _backend.ReportBotStatus(new BotStatusReport { BotId = botId, State = BotState.Stopped, Detail = reason });
        }

        return true;
    }
}
