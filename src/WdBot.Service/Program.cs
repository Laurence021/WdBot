using Microsoft.Extensions.Options;
using WdBot.Backend;
using WdBot.Service;

var builder = Host.CreateApplicationBuilder(args);

// 以 Windows 服務執行時生效；直接執行（主控台）時不影響
builder.Services.AddWindowsService(options => options.ServiceName = "WdBot");

// log 使用 NLog：主控台＋每日檔案；私訊與公頻內容另外依機器人分檔
// 輸出位置與格式見 nlog.config；等級見 appsettings 的 Logging:LogLevel；資料夾見 FileLogging
LoggingSetup.Configure(builder.Logging, builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.Configure<GameOptions>(builder.Configuration.GetSection("Game"));
builder.Services.Configure<BotRuntimeOptions>(builder.Configuration.GetSection("Bot"));
builder.Services.Configure<FileLoggingOptions>(builder.Configuration.GetSection("FileLogging"));
builder.Services.Configure<BackendLinkOptions>(builder.Configuration.GetSection("Backend"));

// 微服務介面：Backend:Mode = WebSocket（預設，連微服務）或 Local（本機測試，從 JSON 檔讀設定）
// 改用 HTTP／MQ 時，只要在這裡加一個新的實作
var backendMode = builder.Configuration["Backend:Mode"] ?? "WebSocket";
if (string.Equals(backendMode, "Local", StringComparison.OrdinalIgnoreCase))
{
    var botsFile = builder.Configuration["Backend:BotsFile"] ?? "bots.local.json";
    var localOptions = new LocalBackendOptions
    {
        // 相對路徑以專案目錄（content root）為準
        BotsFile = Path.Combine(builder.Environment.ContentRootPath, botsFile),
    };
    builder.Services.AddSingleton<IBackendLink>(sp => new LocalFileBackendLink(
        localOptions, sp.GetRequiredService<ILogger<LocalFileBackendLink>>()));
}
else
{
    builder.Services.AddSingleton<IBackendLink>(sp => new WebSocketBackendLink(
        sp.GetRequiredService<IOptions<BackendLinkOptions>>().Value,
        sp.GetRequiredService<ILogger<WebSocketBackendLink>>()));
}

// 一廳一機器人的對照表，只在本程序內有效（請只執行一個機器人服務實例）
builder.Services.AddSingleton<ChannelRegistry>();

builder.Services.AddHostedService<BotManager>();

var host = builder.Build();
host.Run();
