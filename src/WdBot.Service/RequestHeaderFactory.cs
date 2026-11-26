using System.Text.RegularExpressions;
using WdBot.Backend;
using WdBot.Game;

namespace WdBot.Service;

/// <summary>
/// 依 appsettings 的 Game:RequestHeaders 與機器人自己的 headers，組出這個機器人送給遊戲方的 Header。
/// <list type="bullet">
/// <item>機器人的 headers 同名時覆寫全域設定，App API 與聊天 WS 都套用</item>
/// <item>值為空字串的 Header 不送</item>
/// <item>變數：{unity_version}、{app_version}、{os}、{android_version}（從 os 取出的數字，例如 Android14 → 14）、{phone_model}、{device_type}、{device_id}；不認得的變數原樣保留</item>
/// </list>
/// </summary>
public static partial class RequestHeaderFactory
{
    /// <summary>組出這個機器人的 App API 與聊天 WS Header。</summary>
    /// <param name="options">遊戲端設定（Game 區段）。</param>
    /// <param name="config">機器人設定。</param>
    public static RequestHeaders Build(GameOptions options, BotConfig config)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["unity_version"] = options.UnityVersion,
            ["app_version"] = config.AppVersion,
            ["os"] = config.Os,
            ["android_version"] = AndroidVersion().Match(config.Os) is { Success: true } m ? m.Value : "",
            ["phone_model"] = string.IsNullOrWhiteSpace(config.PhoneModel) ? options.DefaultPhoneModel : config.PhoneModel,
            ["device_type"] = config.DeviceType?.Trim() ?? "",
            ["device_id"] = config.DeviceId,
        };

        return new RequestHeaders(
            Resolve(options.RequestHeaders.Http, config.Headers, variables),
            Resolve(options.RequestHeaders.WebSocket, config.Headers, variables));
    }

    /// <summary>合併全域與機器人的 Header、移除值為空字串的項目，並替換變數。</summary>
    /// <param name="global">全域 Header（Game:RequestHeaders 的 Http 或 WebSocket）。</param>
    /// <param name="perBot">機器人自己的 headers；可為 null。</param>
    /// <param name="variables">變數名稱（不含大括號）與替換值，例如 unity_version。</param>
    private static List<KeyValuePair<string, string>> Resolve(
        Dictionary<string, string> global, Dictionary<string, string>? perBot, Dictionary<string, string> variables)
    {
        // 依全域設定的順序，機器人的覆寫取代同名的值；機器人才有的 Header 接在後面
        var merged = new Dictionary<string, string>(global, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in perBot ?? [])
        {
            merged[name] = value;
        }

        return merged
            .Where(h => !string.IsNullOrWhiteSpace(h.Key) && !string.IsNullOrEmpty(h.Value))
            .Select(h => new KeyValuePair<string, string>(h.Key.Trim(), Placeholder().Replace(h.Value,
                m => variables.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value)))
            .ToList();
    }

    /// <summary>比對 {變數名稱}，第 1 組為變數名稱。</summary>
    [GeneratedRegex(@"\{([a-zA-Z_]+)\}")]
    private static partial Regex Placeholder();

    /// <summary>從 os 取出版本數字，例如 Android14 → 14、Android 13.1 → 13.1。</summary>
    [GeneratedRegex(@"\d+(\.\d+)*")]
    private static partial Regex AndroidVersion();
}
