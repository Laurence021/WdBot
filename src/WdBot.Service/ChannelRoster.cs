using System.Text.Encodings.Web;
using System.Text.Json;
using WdBot.Game.Chat;

namespace WdBot.Service;

/// <summary>某個時間點的公頻在線名單（複本）。</summary>
public sealed record RosterSnapshot(long ChannelId, string? ChannelName, DateTimeOffset UpdatedAt, IReadOnlyList<ChannelMember> Players);

/// <summary>
/// 機器人目前所在公頻的在線名單，以「完整名單＋之後的增減」維護。不含機器人自己。
/// 不自己加鎖，由呼叫端（BotInstance 的 _channelGate）保護。
/// </summary>
public sealed class ChannelRoster
{
    private readonly Dictionary<long, ChannelMember> _members = new();

    public long ChannelId { get; private set; }
    public string? ChannelName { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>是否已經有完整名單；沒有時不套用增減，避免只有部分玩家的錯誤名單。</summary>
    public bool HasSnapshot { get; private set; }

    public int Count => _members.Count;

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

    public void Clear()
    {
        _members.Clear();
        ChannelId = 0;
        ChannelName = null;
        HasSnapshot = false;
    }

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
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string? _directory;
    private readonly string _suffix;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly object _fileLock = new();
    private RosterSnapshot? _latest;
    private bool _scheduled;
    private long _lastWriteTick = Environment.TickCount64 - 60_000;
    private int _generation;
    private string? _currentPath; // 目前寫出的檔案，以 _fileLock 保護

    /// <param name="directory">名單檔資料夾；null 表示不寫。</param>
    /// <param name="suffix">附加在檔名後的字串（同一廳可能有多個機器人時用來區分），一般為 null。</param>
    public RosterFileWriter(string? directory, string? suffix, ILogger logger)
    {
        _directory = directory;
        _suffix = suffix ?? "";
        _logger = logger;
    }

    /// <summary>某個大廳的名單檔路徑；不寫檔時為 null。</summary>
    public string? PathFor(long channelId) =>
        _directory is null ? null : Path.Combine(_directory, $"channel-{channelId}{_suffix}.json");

    /// <summary>記下最新名單並排程寫入；呼叫很快，可以在鎖內呼叫。</summary>
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
