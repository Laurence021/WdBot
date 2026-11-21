using System.Text.RegularExpressions;
using WdBot.Backend;
using WdBot.Game;

namespace WdBot.Service;

/// <summary>
/// 依 appsettings 的 Game:RequestHeaders 與機器人自己的 headers，組出這個機器人送給遊戲方的 Header。
/// <list type="bullet">
/// <item>機器人的 headers 同名時覆寫全域設定，App API 與聊天 WS 都套用</item>
/// <item>值為空字串的 Header 不送</item>
/// <item>變數：{unity_version}、{app_version}、{os}、{android_version}（從 os 取出的數字，例如 Android14 → 14）、{phone_model}、{device_id}；不認得的變數原樣保留</item>
/// </list>
/// </summary>
public static partial class RequestHeaderFactory
{
    public static RequestHeaders Build(GameOptions options, BotConfig config)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["unity_version"] = options.UnityVersion,
            ["app_version"] = config.AppVersion,
            ["os"] = config.Os,
            ["android_version"] = AndroidVersion().Match(config.Os) is { Success: true } m ? m.Value : "",
            ["phone_model"] = string.IsNullOrWhiteSpace(config.PhoneModel) ? options.DefaultPhoneModel : config.PhoneModel,
            ["device_id"] = config.DeviceId,
        };

        return new RequestHeaders(
            Resolve(options.RequestHeaders.Http, config.Headers, variables),
            Resolve(options.RequestHeaders.WebSocket, config.Headers, variables));
    }

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

    [GeneratedRegex(@"\{([a-zA-Z_]+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\d+(\.\d+)*")]
    private static partial Regex AndroidVersion();
}
