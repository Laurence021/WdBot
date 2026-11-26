using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WdBot.Backend;

/// <summary>本機測試模式（<see cref="LocalFileBackendLink"/>）的設定。</summary>
public sealed class LocalBackendOptions
{
    /// <summary>機器人設定檔的完整路徑，格式同 config.sync 的 payload：{ "bots": [ ... ] }。</summary>
    public string BotsFile { get; set; } = "bots.local.json";
}

/// <summary>
/// 本機測試用的 <see cref="IBackendLink"/>：不連微服務，從本機 JSON 檔讀取機器人設定，
/// 所有回報都寫進 log。檔案存檔後會自動重新套用。
/// </summary>
public sealed class LocalFileBackendLink : IBackendLink
{
    /// <summary>偵測到檔案變更後等待的時間，把編輯器存檔時連續觸發的多個事件合併成一次。</summary>
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromSeconds(1);

    /// <summary>本機測試模式的設定。</summary>
    private readonly LocalBackendOptions _options;

    /// <summary>記錄載入結果與所有回報內容。</summary>
    private readonly ILogger<LocalFileBackendLink> _logger;

    /// <summary>建立本機測試用的連線。</summary>
    /// <param name="options">本機測試設定，包含 BOT 設定檔的路徑（Backend:BotsFile）。</param>
    /// <param name="logger">記錄設定檔載入結果與上報的內容。</param>
    public LocalFileBackendLink(LocalBackendOptions options, ILogger<LocalFileBackendLink> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>載入設定檔並交給 handler，之後監看檔案變更並重新套用，直到取消。</summary>
    /// <param name="handler">接收設定的對象，通常是 BotManager。</param>
    /// <param name="ct">取消時停止監看設定檔並結束。</param>
    public async Task RunAsync(IBackendHandler handler, CancellationToken ct)
    {
        var path = Path.GetFullPath(_options.BotsFile);
        _logger.LogWarning("本機測試模式：不連微服務，機器人設定讀自 {Path}", path);

        var changed = new SemaphoreSlim(0);
        using var watcher = CreateWatcher(path, changed);

        await LoadAndApplyAsync(path, handler, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            await changed.WaitAsync(ct).ConfigureAwait(false);

            // 編輯器存檔常會連續觸發多次事件，等一下再一起處理
            await Task.Delay(ReloadDebounce, ct).ConfigureAwait(false);
            while (changed.CurrentCount > 0)
            {
                await changed.WaitAsync(ct).ConfigureAwait(false);
            }

            _logger.LogInformation("偵測到 {File} 變更，重新套用設定", Path.GetFileName(path));
            await LoadAndApplyAsync(path, handler, ct).ConfigureAwait(false);
        }
    }

    /// <summary>收到私訊時寫進 log；私訊內容不寫進一般 log，只寫在依機器人分檔的私訊 log（Debug 等級才會寫）。</summary>
    /// <param name="r">收到的玩家私訊。</param>
    public void ReportIncomingMessage(IncomingMessageReport r) =>
        _logger.LogInformation("[{Bot}] 收到私訊：玩家 {PlayerId}({Name}) message_id={MessageId}（message_type={Type}）",
            r.BotId, r.FromPlayerId, r.FromPlayerName, r.MessageId, r.MessageType);

    /// <summary>私訊結果寫進 log：成功為 Information，失敗為 Warning。</summary>
    /// <param name="r">私訊指令的執行結果。</param>
    public void ReportSendResult(SendResultReport r)
    {
        if (r.Success)
        {
            _logger.LogInformation("[{Bot}] 私訊 {RequestId} 送出成功，message_ids={Ids}",
                r.BotId, r.RequestId, string.Join(",", r.MessageIds));
        }
        else
        {
            _logger.LogWarning("[{Bot}] 私訊 {RequestId} 送出失敗：{Code} {Message}", r.BotId, r.RequestId, r.ErrCode, r.ErrMsg);
        }
    }

    /// <summary>公頻完整名單寫進 log（含所有玩家暱稱）。</summary>
    /// <param name="r">公頻的完整線上名單。</param>
    public void ReportChannelSnapshot(ChannelSnapshotReport r)
    {
        var sample = string.Join("、", r.Players.Select(p => $"{p.PlayerName}"));
        _logger.LogInformation("[{Bot}] 公頻 {ChannelId}({ChannelName}) 名單：{Count} 人（伺服器在線 {Online}／總數 {Total}）：{Sample}",
            r.BotId, r.ChannelId, r.ChannelName, r.Players.Count, r.OnlineCount, r.TotalCount,
            sample);
    }

    /// <summary>公頻成員進出寫進 log（進入的玩家最多列 10 位）。</summary>
    /// <param name="r">公頻成員進出的內容。</param>
    public void ReportChannelMembersChanged(ChannelMembersChangedReport r) =>
        _logger.LogInformation("[{Bot}] 公頻 {ChannelId} 成員異動：進入 {Joined} 人、離開 {Left} 人（在線 {Online}）{Names}",
            r.BotId, r.ChannelId, r.Joined.Count, r.Left.Count, r.OnlineCount,
            r.Joined.Count > 0 ? "，進入：" + string.Join("、", r.Joined.Take(10).Select(p => p.PlayerName)) : "");

    /// <summary>機器人狀態寫進 log：Error 狀態用 Error 等級，其他用 Information。</summary>
    /// <param name="r">機器人目前的狀態。</param>
    public void ReportBotStatus(BotStatusReport r)
    {
        var level = r.State == BotState.Error ? LogLevel.Error : LogLevel.Information;
        _logger.Log(level, "[{Bot}] 狀態 {State}｜暱稱={Name} player_id={PlayerId} 等級={Level} channel_id={ChannelId}{Detail}",
            r.BotId, r.State, r.PlayerName, r.PlayerId, r.Level, r.ChannelId,
            string.IsNullOrEmpty(r.Detail) ? "" : "｜" + r.Detail + (r.ErrCode is { } code ? $"（{code}）" : ""));
    }

    /// <summary>讀取並解析設定檔，以全量設定交給 handler；檔案不存在或格式錯誤時只記錄錯誤，不改變目前的機器人。</summary>
    /// <param name="path">BOT 設定檔的完整路徑。</param>
    /// <param name="handler">接收全量設定的對象。</param>
    /// <param name="ct">用來取消作業。</param>
    private async Task LoadAndApplyAsync(string path, IBackendHandler handler, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            _logger.LogError("找不到 {Path}。請複製 bots.local.example.json 成 bots.local.json 並填入測試帳號，存檔後會自動套用", path);
            return;
        }

        ConfigSyncPayload? payload;
        try
        {
            // 編輯器存檔時檔案可能暫時被鎖住，用共用讀取模式並稍微重試
            var json = await ReadWithRetryAsync(path, ct).ConfigureAwait(false);
            payload = JsonSerializer.Deserialize<ConfigSyncPayload>(json, BackendJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogError("{File} 的 JSON 格式錯誤（第 {Line} 行）：{Message}。修正後存檔即可", Path.GetFileName(path),
                (ex.LineNumber ?? 0) + 1, ex.Message);
            return;
        }
        catch (IOException ex)
        {
            _logger.LogError("無法讀取 {Path}：{Message}", path, ex.Message);
            return;
        }

        var bots = payload?.Bots ?? [];
        _logger.LogInformation("載入 {Count} 個機器人設定：{Bots}", bots.Count, string.Join("、", bots.Select(b => b.ToString())));
        await handler.OnConfigSnapshotAsync(bots, ct).ConfigureAwait(false);
    }

    /// <summary>以共用模式讀取整個檔案；檔案暫時被鎖住時每 200 毫秒重試，最多 5 次。</summary>
    /// <param name="path">要讀取的檔案路徑。</param>
    /// <param name="ct">用來取消作業，包含重試前的等待。</param>
    private static async Task<string> ReadWithRetryAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>監看設定檔的新增、修改、刪除與更名，每次變更釋放一次 changed；資料夾不存在時回傳 null（不自動偵測）。</summary>
    /// <param name="path">要監看的設定檔路徑。</param>
    /// <param name="changed">檔案變更時釋放的號誌；讀取端等待它來得知檔案有變更。</param>
    private FileSystemWatcher? CreateWatcher(string path, SemaphoreSlim changed)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            _logger.LogWarning("設定檔所在的資料夾不存在，不會自動偵測變更：{Directory}", directory);
            return null;
        }

        var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        FileSystemEventHandler onChange = (_, _) => changed.Release();
        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Deleted += onChange;
        watcher.Renamed += (_, _) => changed.Release();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }
}
