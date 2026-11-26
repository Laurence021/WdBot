using NLog.Extensions.Logging;

namespace WdBot.Service;

/// <summary>appsettings 的 FileLogging 區段。相對路徑以程式的 content root 為準。</summary>
public sealed class FileLoggingOptions
{
    /// <summary>一般 log 的資料夾，檔名為 wdbot-yyyyMMdd.log；名單檔也放在這底下的 roster 資料夾。</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>訊息內容 log 的資料夾，每個機器人一個子資料夾：私訊為 messages-yyyyMMdd.log，公頻為 channel-yyyyMMdd.log。</summary>
    public string MessageDirectory { get; set; } = "logs/messages";
}

/// <summary>
/// NLog 設定（輸出位置與格式見 nlog.config）：
/// <list type="bullet">
/// <item>一般 log → 主控台＋ logs/wdbot-日期.log</item>
/// <item>收到的私訊內容（類別 WdBot.Messages.Private）→ logs/messages/{bot_id}/messages-日期.log</item>
/// <item>公頻訊息內容（類別 WdBot.Messages.Channel）→ logs/messages/{bot_id}/channel-日期.log</item>
/// </list>
/// 訊息內容都以 Debug 等級寫入：私訊要把 Logging:LogLevel:WdBot.Messages.Private、公頻要把 WdBot.Messages.Channel 設為 Debug 才會產生（可分開開關），也不會出現在一般 log 與主控台。
/// 記錄等級由 appsettings 的 Logging:LogLevel 控制。
/// </summary>
public static class LoggingSetup
{
    /// <summary>訊息內容 log 的共同上層類別；Logging:LogLevel 以前綴比對，涵蓋下面兩個類別。</summary>
    public const string MessageCategory = "WdBot.Messages";

    /// <summary>私訊內容專用的 log 類別。</summary>
    public const string PrivateMessageCategory = MessageCategory + ".Private";

    /// <summary>公頻訊息內容專用的 log 類別。</summary>
    public const string ChannelMessageCategory = MessageCategory + ".Channel";

    /// <summary>程式沒有處理的聊天 WS 推送（未處理的 opcode、帶錯誤的推送）專用的 log 類別。</summary>
    public const string UnhandledPacketCategory = MessageCategory + ".Unhandled";

    /// <summary>訊息內容 log 用來分檔的 scope 屬性名稱（nlog.config 的 ${scopeproperty:BotId}）。</summary>
    public const string BotIdProperty = "BotId";

    /// <summary>nlog.config 的檔名，建置時複製到輸出資料夾。</summary>
    public const string ConfigFileName = "nlog.config";

    /// <summary>以 NLog 取代預設的 log 輸出（主控台、EventLog 等）。</summary>
    /// <param name="logging">主機的 log 設定。</param>
    /// <param name="configuration">應用程式設定，從中讀取 FileLogging 區段。</param>
    /// <param name="contentRoot">content root 路徑；相對的 log 資料夾以它為基準。</param>
    public static void Configure(ILoggingBuilder logging, IConfiguration configuration, string contentRoot)
    {
        var options = configuration.GetSection("FileLogging").Get<FileLoggingOptions>() ?? new FileLoggingOptions();
        NLog.GlobalDiagnosticsContext.Set("LogDirectory", ResolvePath(contentRoot, options.Directory));
        NLog.GlobalDiagnosticsContext.Set("MessageDirectory", ResolvePath(contentRoot, options.MessageDirectory));

        // 依參數型別上色的主控台輸出（比照 Serilog），要在載入 nlog.config 之前註冊
        NLog.SetupBuilderExtensions.SetupExtensions(NLog.LogManager.Setup(), extensions =>
            NLog.SetupExtensionsBuilderExtensions.RegisterTarget<TemplateColoredConsoleTarget>(extensions, TemplateColoredConsoleTarget.TypeName));

        NLog.LogManager.Configuration = new NLog.Config.XmlLoggingConfiguration(Path.Combine(AppContext.BaseDirectory, ConfigFileName));

        logging.ClearProviders();
        logging.AddNLog(new NLogProviderOptions
        {
            // 等級交給 appsettings 的 Logging:LogLevel 篩選，nlog.config 只負責輸出
            RemoveLoggerFactoryFilter = false,
            ShutdownOnDispose = true, // 程式結束時把還沒寫完的 log 寫進檔案
        });
    }

    /// <summary>訊息內容 log 用：帶上 BotId，讓 nlog.config 依機器人分檔。</summary>
    /// <param name="logger">訊息內容 log 用的 logger。</param>
    /// <param name="botId">機器人 ID。</param>
    public static IDisposable? BeginBotScope(ILogger logger, string botId) =>
        logger.BeginScope(new Dictionary<string, object> { [BotIdProperty] = SafeFolderName(botId) });

    /// <summary>相對路徑以 content root 為基準轉成完整路徑；已經是完整路徑時原樣回傳。</summary>
    /// <param name="contentRoot">content root 路徑。</param>
    /// <param name="path">設定中的路徑，可為相對或完整路徑。</param>
    internal static string ResolvePath(string contentRoot, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path);

    /// <summary>bot_id 可能含有不能當資料夾名稱的字元，換成底線。</summary>
    /// <param name="botId">機器人 ID。</param>
    internal static string SafeFolderName(string botId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(botId.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return string.IsNullOrEmpty(safe) ? "unknown" : safe;
    }
}
