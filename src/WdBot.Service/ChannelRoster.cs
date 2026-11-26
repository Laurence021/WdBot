using System.Text.Encodings.Web;
using System.Text.Json;
using WdBot.Game.Chat;

namespace WdBot.Service;

/// <summary>某個時間點的公頻在線名單（複本）。</summary>
/// <param name="ChannelId">公頻 ID。</param>
/// <param name="ChannelName">公頻名稱。</param>
/// <param name="UpdatedAt">名單更新時間（UTC）。</param>
/// <param name="Players">在線玩家（不含機器人自己）。</param>
public sealed record RosterSnapshot(long ChannelId, string? ChannelName, DateTimeOffset UpdatedAt, IReadOnlyList<ChannelMember> Players);

/// <summary>
/// 機器人目前所在公頻的在線名單，以「完整名單＋之後的增減」維護。不含機器人自己。
/// 不自己加鎖，由呼叫端（BotInstance 的 _channelGate）保護。
/// </summary>
public sealed class ChannelRoster
{
    /// <summary>在線玩家，以 player_id 為鍵。</summary>
    private readonly Dictionary<long, ChannelMember> _members = new();

    /// <summary>公頻 ID；沒有名單時為 0。</summary>
    public long ChannelId { get; private set; }

    /// <summary>公頻名稱。</summary>
    public string? ChannelName { get; private set; }

    /// <summary>名單最後更新的時間（UTC）。</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>是否已經有完整名單；沒有時不套用增減，避免只有部分玩家的錯誤名單。</summary>
    public bool HasSnapshot { get; private set; }

    /// <summary>目前在線人數（不含機器人自己）。</summary>
    public int Count => _members.Count;

    /// <summary>以完整名單取代目前名單，排除 player_id 為 0 與機器人自己。</summary>
    /// <param name="channelId">公頻 ID。</param>
    /// <param name="channelName">公頻名稱；可為 null。</param>
    /// <param name="members">完整的成員清單。</param>
    /// <param name="selfPlayerId">機器人自己的 player_id，不列入名單。</param>
    public void Replace(long channelId, string? channelName, IEnumerable<ChannelMember> members, long selfPlayerId)
    {
        _members.Clear();
        foreach (var member in members)
        {
            if (member.PlayerId != 0 && member.PlayerId != selfPlayerId)
            {
                _members[member.PlayerId] = member;
            }
        }

        ChannelId = channelId;
        ChannelName = channelName;
        HasSnapshot = true;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>套用一次成員異動；還沒有完整名單時忽略並回傳 false。</summary>
    /// <param name="joined">進入的玩家。</param>
    /// <param name="left">離開的玩家 player_id。</param>
    /// <param name="selfPlayerId">機器人自己的 player_id，不列入名單。</param>
    public bool Apply(IEnumerable<ChannelMember> joined, IEnumerable<long> left, long selfPlayerId)
    {
        if (!HasSnapshot)
        {
            return false;
        }

        foreach (var id in left)
        {
            _members.Remove(id);
        }

        foreach (var member in joined)
        {
            if (member.PlayerId != 0 && member.PlayerId != selfPlayerId)
            {
                _members[member.PlayerId] = member;
            }
        }

        UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>清空名單（離開公頻、斷線時），之後要等新的完整名單才會再套用異動。</summary>
    public void Clear()
    {
        _members.Clear();
        ChannelId = 0;
        ChannelName = null;
        HasSnapshot = false;
    }

    /// <summary>取得目前名單的複本，可以在鎖外安全使用。</summary>
    public RosterSnapshot Snapshot() =>
        new(ChannelId, ChannelName, UpdatedAt, _members.Values.ToList());
}

/// <summary>
/// 把在線名單寫成 JSON 檔，檔名依大廳編號區分：{directory}/channel-{channel_id}{suffix}.json。
/// 最多每 3 秒寫一次；先寫暫存檔再取代，避免讀到寫一半的內容。換廳時會先刪除舊大廳的檔案。
/// directory 為 null 時不寫。
/// </summary>
public sealed class RosterFileWriter
{
    /// <summary>兩次寫檔的最短間隔（伺服器本來就每 3 秒彙整一次異動）。</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);

    /// <summary>名單檔的 JSON 格式：縮排、中文不跳脫，方便直接打開閱讀。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>名單檔資料夾；null 表示不寫。</summary>
    private readonly string? _directory;

    /// <summary>附加在檔名後的字串，一般為空字串。</summary>
    private readonly string _suffix;

    /// <summary>記錄寫檔失敗。</summary>
    private readonly ILogger _logger;

    /// <summary>保護 _latest、_scheduled、_generation。</summary>
    private readonly object _gate = new();

    /// <summary>確保寫檔與刪檔不會同時進行。</summary>
    private readonly object _fileLock = new();

    /// <summary>等待寫出的最新名單；已寫出或已清空時為 null。</summary>
    private RosterSnapshot? _latest;

    /// <summary>是否已排程一次寫檔（排程期間的更新只更換 _latest）。</summary>
    private bool _scheduled;

    /// <summary>上一次寫檔的時間（Environment.TickCount64）。</summary>
    private long _lastWriteTick = Environment.TickCount64 - 60_000;

    /// <summary>每次刪檔加 1；排程中的寫檔發現世代不同就放棄，避免刪除後又被寫回。</summary>
    private int _generation;

    /// <summary>目前寫出的檔案路徑，以 _fileLock 保護；沒有檔案時為 null。</summary>
    private string? _currentPath;

    /// <summary>建立名單檔寫入器。</summary>
    /// <param name="directory">名單檔資料夾；null 表示不寫。</param>
    /// <param name="suffix">附加在檔名後的字串（同一廳可能有多個機器人時用來區分），一般為 null。</param>
    /// <param name="logger">記錄寫檔與刪檔失敗。</param>
    public RosterFileWriter(string? directory, string? suffix, ILogger logger)
    {
        _directory = directory;
        _suffix = suffix ?? "";
        _logger = logger;
    }

    /// <summary>某個大廳的名單檔路徑；不寫檔時為 null。</summary>
    /// <param name="channelId">大廳編號（公頻 ID）。</param>
    public string? PathFor(long channelId) =>
        _directory is null ? null : Path.Combine(_directory, $"channel-{channelId}{_suffix}.json");

    /// <summary>記下最新名單並排程寫入；呼叫很快，可以在鎖內呼叫。</summary>
    /// <param name="snapshot">最新的名單。</param>
    public void Update(RosterSnapshot snapshot)
    {
        if (_directory is null || snapshot.ChannelId == 0)
        {
            return;
        }

        int generation;
        lock (_gate)
        {
            _latest = snapshot;
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
            generation = _generation;
        }

        _ = WriteLaterAsync(generation);
    }

    /// <summary>刪除名單檔（離開公頻、斷線或停止時）。</summary>
    public void Delete()
    {
        if (_directory is null)
        {
            return;
        }

        lock (_gate)
        {
            _latest = null;
            _generation++;
        }

        lock (_fileLock)
        {
            DeleteCurrentLocked();
        }
    }

    /// <summary>呼叫前必須持有 _fileLock。</summary>
    private void DeleteCurrentLocked()
    {
        if (_currentPath is not { } path)
        {
            return;
        }

        _currentPath = null;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "刪除名單檔失敗：{Path}", path);
        }
    }

    /// <summary>等到距離上次寫檔滿 MinInterval 後，寫出最新的名單；期間如果已刪檔（世代改變）就放棄。</summary>
    /// <param name="generation">排程當時的世代編號。</param>
    private async Task WriteLaterAsync(int generation)
    {
        var wait = MinInterval.TotalMilliseconds - (Environment.TickCount64 - Interlocked.Read(ref _lastWriteTick));
        if (wait > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(wait)).ConfigureAwait(false);
        }

        RosterSnapshot? snapshot;
        lock (_gate)
        {
            snapshot = _latest;
            _latest = null;
            _scheduled = false;
            if (generation != _generation)
            {
                return; // 排程期間名單已被清空
            }
        }

        if (snapshot is null)
        {
            return;
        }

        lock (_fileLock)
        {
            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }
            }

            Write(snapshot);
        }

        Interlocked.Exchange(ref _lastWriteTick, Environment.TickCount64);
    }

    /// <summary>呼叫前必須持有 _fileLock。</summary>
    /// <param name="snapshot">要寫出的名單。</param>
    private void Write(RosterSnapshot snapshot)
    {
        var path = PathFor(snapshot.ChannelId)!;
        if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            DeleteCurrentLocked(); // 換廳：舊大廳的檔案不會再更新，先刪掉
        }

        var content = new Dictionary<string, object?>
        {
            ["channel_id"] = snapshot.ChannelId,
            ["channel_name"] = snapshot.ChannelName,
            ["updated_at"] = snapshot.UpdatedAt,
            ["count"] = snapshot.Players.Count,
            ["players"] = snapshot.Players
                .OrderBy(p => p.PlayerName, StringComparer.CurrentCulture)
                .Select(p => new Dictionary<string, object?>
                {
                    ["player_id"] = p.PlayerId,
                    ["player_name"] = p.PlayerName,
                    ["level"] = p.Level,
                })
                .ToList(),
        };

        try
        {
            Directory.CreateDirectory(_directory!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(content, JsonOptions));
            File.Move(temp, path, overwrite: true);
            _currentPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("寫入名單檔失敗：{Path}：{Message}", path, ex.Message);
        }
    }

    /// <summary>服務啟動時清掉上次留下的名單檔（例如程式被強制結束、來不及刪除）。</summary>
    /// <param name="directory">名單檔的根資料夾。</param>
    /// <param name="logger">記錄清除結果與失敗。</param>
    public static void CleanDirectory(string directory, ILogger logger)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "channel-*.json*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "清除舊名單檔失敗：{Path}", file);
            }
        }
    }
}
