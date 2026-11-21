using Microsoft.Extensions.Options;
using WdBot.Backend;
using WdBot.Game.Chat;

namespace WdBot.Service;

/// <summary>
/// 依微服務下發的設定啟停機器人，並把私訊指令轉給對應的機器人。
/// </summary>
public sealed class BotManager : BackgroundService, IBackendHandler
{
    private readonly IBackendLink _backend;
    private readonly ChannelRegistry _channelRegistry;
    private readonly GameOptions _gameOptions;
    private readonly BotRuntimeOptions _runtimeOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<BotManager> _logger;
    private readonly HttpClient _http;
    private readonly string? _rosterDirectory; // 名單檔根資料夾（底下依 env 分資料夾、依大廳編號命名）；Bot:WriteRosterFile 關閉時為 null
    private readonly Dictionary<string, BotInstance> _bots = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BotManager(IBackendLink backend, ChannelRegistry channelRegistry, IOptions<GameOptions> gameOptions,
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

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _backend.RunAsync(this, stoppingToken);

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

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

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

    public async Task OnConfigRemoveAsync(string botId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopBotAsync(botId, "後台移除").ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

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
            if (!await StopBotAsync(config.BotId, "設定為停用").ConfigureAwait(false))
            {
                _backend.ReportBotStatus(new BotStatusReport { BotId = config.BotId, State = BotState.Stopped, Detail = "已停用" });
            }

            return;
        }

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
