using System.Text.Json;
using WdBot.Backend;

namespace WdBot.Service;

/// <summary>appsettings 的 DevicePool 區段：device id 清單的位置與分配規則。</summary>
public sealed class DevicePoolOptions
{
    /// <summary>device id 清單檔，相對路徑以 content root 為準；格式見 devices.example.json。</summary>
    public string File { get; set; } = "devices.json";

    /// <summary>同一個 device_id 最多同時分給幾個機器人使用。</summary>
    public int MaxBotsPerDevice { get; set; } = 1;
}

/// <summary>device id 清單中的一支「手機」。裝置欄位沒填時，沿用機器人設定中的值。</summary>
public sealed class DeviceProfile
{
    /// <summary>裝置 ID。</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>選填：1 Android、2 iOS、3 APK、4 官網。</summary>
    public int? DeviceModel { get; set; }

    /// <summary>選填：裝置型號，例如 Samsung SM-A546E。</summary>
    public string? DeviceType { get; set; }

    /// <summary>選填：作業系統，例如 Android 15。</summary>
    public string? Os { get; set; }
}

/// <summary>device id 清單檔的內容：{ "devices": [ ... ] }。</summary>
public sealed class DevicePoolFile
{
    /// <summary>可以分配的裝置。</summary>
    public List<DeviceProfile> Devices { get; set; } = [];
}

/// <summary>
/// 從 device id 清單隨機分配 device_id 給沒有指定 device_id 的機器人。
/// <list type="bullet">
/// <item>分配只存在記憶體：同一個服務執行期間，機器人重新登入、設定變更都沿用同一個；服務重啟後重新隨機</item>
/// <item>每個 device_id 最多分給 <see cref="DevicePoolOptions.MaxBotsPerDevice"/> 個機器人；設定中自己指定 device_id 的機器人也算在名額內</item>
/// <item>機器人被移除（後台移除、不在全量設定中）時釋放名額；只是停用則保留</item>
/// <item>清單檔每次分配前檢查修改時間，有變更就重新讀取；已分配的不受影響</item>
/// </list>
/// </summary>
public sealed class DevicePool
{
    /// <summary>清單檔的完整路徑。</summary>
    private readonly string _path;

    /// <summary>同一個 device_id 最多分給幾個機器人。</summary>
    private readonly int _maxPerDevice;

    /// <summary>記錄載入結果與分配情形。</summary>
    private readonly ILogger<DevicePool> _logger;

    /// <summary>保護以下欄位。</summary>
    private readonly object _gate = new();

    /// <summary>bot_id → 使用中的 device_id（含從清單分配與設定中指定的）。</summary>
    private readonly Dictionary<string, string> _inUse = new(StringComparer.Ordinal);

    /// <summary>bot_id → 從清單分配到的裝置；設定中自己指定 device_id 的機器人不在這裡。</summary>
    private readonly Dictionary<string, DeviceProfile> _assigned = new(StringComparer.Ordinal);

    /// <summary>目前載入的清單。</summary>
    private List<DeviceProfile> _devices = [];

    /// <summary>上次載入時清單檔的修改時間；用來判斷是否要重新讀取。</summary>
    private DateTime? _loadedWriteTime;

    /// <summary>建立裝置池；清單檔在第一次分配時才讀取。</summary>
    /// <param name="path">清單檔的完整路徑。</param>
    /// <param name="maxPerDevice">同一個 device_id 最多同時給幾個機器人。</param>
    /// <param name="logger">記錄分配、釋放與讀檔錯誤。</param>
    public DevicePool(string path, int maxPerDevice, ILogger<DevicePool> logger)
    {
        _path = path;
        _maxPerDevice = Math.Max(1, maxPerDevice);
        _logger = logger;
    }

    /// <summary>
    /// 決定機器人實際使用的裝置欄位，回傳套用後的設定複本；失敗時回傳 null 並在 error 說明原因。
    /// <list type="bullet">
    /// <item>設定中有 device_id：直接使用，並佔用一個名額（即使已滿也允許）</item>
    /// <item>沒有 device_id：沿用這個機器人先前分到的；沒有的話從清單隨機挑一個還有名額的</item>
    /// </list>
    /// </summary>
    /// <param name="config">機器人原本的設定；不會被修改。</param>
    /// <param name="error">輸出：失敗原因；成功時為 null。</param>
    public BotConfig? Resolve(BotConfig config, out string? error)
    {
        error = null;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(config.DeviceId))
            {
                // 設定自己指定 device_id：不再使用清單分配的
                _assigned.Remove(config.BotId);
                _inUse[config.BotId] = config.DeviceId.Trim();
                return config;
            }

            if (!_assigned.TryGetValue(config.BotId, out var device))
            {
                ReloadIfChangedLocked();
                var candidates = _devices
                    .Where(d => CountLocked(d.DeviceId) < _maxPerDevice)
                    .ToList();
                if (candidates.Count == 0)
                {
                    error = _devices.Count == 0
                        ? $"沒有設定 device_id，device id 清單（{Path.GetFileName(_path)}）也沒有可用的裝置"
                        : $"device id 清單的 {_devices.Count} 個裝置都已達到上限（每個最多 {_maxPerDevice} 個機器人）";
                    return null;
                }

                device = candidates[Random.Shared.Next(candidates.Count)];
                _assigned[config.BotId] = device;
                _inUse[config.BotId] = device.DeviceId;
                _logger.LogInformation("機器人 {BotId} 分配到 device_id {DeviceId}（{DeviceType}，目前 {Count}/{Max} 個機器人使用）",
                    config.BotId, device.DeviceId, device.DeviceType ?? config.DeviceType, CountLocked(device.DeviceId), _maxPerDevice);
            }

            var resolved = config.Copy();
            resolved.DeviceId = device.DeviceId;
            resolved.DeviceModel = device.DeviceModel ?? config.DeviceModel;
            resolved.DeviceType = string.IsNullOrWhiteSpace(device.DeviceType) ? config.DeviceType : device.DeviceType;
            resolved.Os = string.IsNullOrWhiteSpace(device.Os) ? config.Os : device.Os;
            return resolved;
        }
    }

    /// <summary>機器人被移除時釋放它的名額。</summary>
    /// <param name="botId">被移除的機器人 ID。</param>
    public void Release(string botId)
    {
        lock (_gate)
        {
            _assigned.Remove(botId);
            if (_inUse.Remove(botId, out var deviceId))
            {
                _logger.LogInformation("機器人 {BotId} 釋放 device_id {DeviceId}", botId, deviceId);
            }
        }
    }

    /// <summary>目前有幾個機器人使用這個 device_id。呼叫前必須持有 _gate。</summary>
    /// <param name="deviceId">要計算的 device_id。</param>
    private int CountLocked(string deviceId) =>
        _inUse.Values.Count(id => string.Equals(id, deviceId, StringComparison.Ordinal));

    /// <summary>清單檔有變更（或第一次）時重新讀取；讀取失敗時保留原本的清單。呼叫前必須持有 _gate。</summary>
    private void ReloadIfChangedLocked()
    {
        if (!System.IO.File.Exists(_path))
        {
            if (_loadedWriteTime is not null || _devices.Count > 0)
            {
                _logger.LogWarning("找不到 device id 清單 {Path}", _path);
            }

            _devices = [];
            _loadedWriteTime = null;
            return;
        }

        var writeTime = System.IO.File.GetLastWriteTimeUtc(_path);
        if (writeTime == _loadedWriteTime)
        {
            return;
        }

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var file = JsonSerializer.Deserialize<DevicePoolFile>(stream, BackendJson.Options) ?? new DevicePoolFile();
            _devices = file.Devices
                .Where(d => !string.IsNullOrWhiteSpace(d.DeviceId))
                .Select(d => { d.DeviceId = d.DeviceId.Trim(); return d; })
                .DistinctBy(d => d.DeviceId, StringComparer.Ordinal)
                .ToList();
            _loadedWriteTime = writeTime;
            _logger.LogInformation("載入 device id 清單：{Count} 個裝置，每個最多 {Max} 個機器人使用（{Path}）", _devices.Count, _maxPerDevice, _path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError("無法讀取 device id 清單 {Path}：{Message}；沿用先前載入的 {Count} 個裝置", _path, ex.Message, _devices.Count);
        }
    }
}
