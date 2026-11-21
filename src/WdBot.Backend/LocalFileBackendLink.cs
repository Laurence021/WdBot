using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WdBot.Backend;

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
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromSeconds(1);

    private readonly LocalBackendOptions _options;
    private readonly ILogger<LocalFileBackendLink> _logger;

    public LocalFileBackendLink(LocalBackendOptions options, ILogger<LocalFileBackendLink> logger)
    {
        _options = options;
        _logger = logger;
    }

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

    // 私訊內容不寫進一般 log，只寫在依機器人分檔的私訊 log（Debug 等級才會寫）
    public void ReportIncomingMessage(IncomingMessageReport r) =>
        _logger.LogInformation("[{Bot}] 收到私訊：玩家 {PlayerId}({Name}) message_id={MessageId}（message_type={Type}）",
            r.BotId, r.FromPlayerId, r.FromPlayerName, r.MessageId, r.MessageType);

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

    public void ReportChannelSnapshot(ChannelSnapshotReport r)
    {
        var sample = string.Join("、", r.Players.Select(p => $"{p.PlayerName}"));
        _logger.LogInformation("[{Bot}] 公頻 {ChannelId}({ChannelName}) 名單：{Count} 人（伺服器在線 {Online}／總數 {Total}）：{Sample}",
            r.BotId, r.ChannelId, r.ChannelName, r.Players.Count, r.OnlineCount, r.TotalCount,
            sample);
    }

    public void ReportChannelMembersChanged(ChannelMembersChangedReport r) =>
        _logger.LogInformation("[{Bot}] 公頻 {ChannelId} 成員異動：進入 {Joined} 人、離開 {Left} 人（在線 {Online}）{Names}",
            r.BotId, r.ChannelId, r.Joined.Count, r.Left.Count, r.OnlineCount,
            r.Joined.Count > 0 ? "，進入：" + string.Join("、", r.Joined.Take(10).Select(p => p.PlayerName)) : "");

    public void ReportBotStatus(BotStatusReport r)
    {
        var level = r.State == BotState.Error ? LogLevel.Error : LogLevel.Information;
        _logger.Log(level, "[{Bot}] 狀態 {State}｜暱稱={Name} player_id={PlayerId} 等級={Level} channel_id={ChannelId}{Detail}",
            r.BotId, r.State, r.PlayerName, r.PlayerId, r.Level, r.ChannelId,
            string.IsNullOrEmpty(r.Detail) ? "" : "｜" + r.Detail + (r.ErrCode is { } code ? $"（{code}）" : ""));
    }

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
