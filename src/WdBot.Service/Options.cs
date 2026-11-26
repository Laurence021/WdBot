namespace WdBot.Service;

/// <summary>appsettings 的 Game 區段：各環境的網址。</summary>
public sealed class GameOptions
{
    /// <summary>各環境（prod、test）的網址，以環境名稱為鍵。</summary>
    public Dictionary<string, GameEnvironmentOptions> Environments { get; set; } = new();

    /// <summary>真的 App 使用的 Unity 版本，提供給 Header 設定中的 {unity_version}。要以實際抓到的封包為準。</summary>
    public string UnityVersion { get; set; } = "";

    /// <summary>機器人沒有設定 phone_model 時，{phone_model} 使用的值。</summary>
    public string DefaultPhoneModel { get; set; } = "";

    /// <summary>送給遊戲方的額外 Header，讓請求看起來像手機 App。見 <see cref="RequestHeaderFactory"/>。</summary>
    public RequestHeaderOptions RequestHeaders { get; set; } = new();

    /// <summary>依環境名稱（不分大小寫）取得網址設定；找不到時回傳 null。</summary>
    /// <param name="env">環境名稱，例如 prod、test。</param>
    public GameEnvironmentOptions? Find(string env) =>
        Environments.FirstOrDefault(kv => string.Equals(kv.Key, env, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>
/// appsettings 的 Game:RequestHeaders。值可以使用 {unity_version}、{app_version}、{os}、{android_version}、{phone_model}、{device_type}、{device_id} 變數；
/// 值為空字串表示不送這個 Header（appsettings 的覆寫檔無法刪除鍵，所以用空字串代表移除）。
/// </summary>
public sealed class RequestHeaderOptions
{
    /// <summary>App API 請求的 Header。</summary>
    public Dictionary<string, string> Http { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>聊天 WS 連線請求的 Header。</summary>
    public Dictionary<string, string> WebSocket { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>單一遊戲環境的網址。</summary>
public sealed class GameEnvironmentOptions
{
    /// <summary>App API 的基底網址，例如 https://api.wdking.tw。</summary>
    public string ApiBaseUrl { get; set; } = "";

    /// <summary>登入回傳的 chat_ws_urls 為空時才使用。</summary>
    public string ChatWsUrl { get; set; } = "";
}

/// <summary>appsettings 的 Bot 區段：機器人執行參數。</summary>
public sealed class BotRuntimeOptions
{
    /// <summary>每隔幾分鐘主動刷新 token（access_token 有效 1 小時）。</summary>
    public int TokenRefreshIntervalMinutes { get; set; } = 50;

    /// <summary>被 7000（同帳號在其他地方連線）踢掉後，等幾秒才重連。</summary>
    public int DuplicateLoginCooldownSeconds { get; set; } = 300;

    /// <summary>聊天室維護時，等幾秒才重連。</summary>
    public int MaintenanceBackoffSeconds { get; set; } = 120;

    /// <summary>聊天 WS 一般斷線重連等待秒數的上限。</summary>
    public int ChatReconnectMaxSeconds { get; set; } = 60;

    /// <summary>私訊送出後等待回包的秒數。</summary>
    public int SendReplyTimeoutSeconds { get; set; } = 10;

    /// <summary>每個機器人私訊送出佇列的容量。</summary>
    public int SendQueueCapacity { get; set; } = 1000;

    /// <summary>登入或連線發生暫時性錯誤後，重新整個登入流程的最長等待秒數。</summary>
    public int RestartMaxDelaySeconds { get; set; } = 300;

    /// <summary>App API 請求的逾時秒數。</summary>
    public int HttpTimeoutSeconds { get; set; } = 20;

    /// <summary>一個公頻（大廳）只允許一個機器人；撞廳的機器人會斷線重連，讓伺服器重新分配。</summary>
    public bool OneBotPerChannel { get; set; } = true;

    /// <summary>連續撞廳幾次後放棄，改為下線等待。</summary>
    public int ChannelCollisionMaxAttempts { get; set; } = 10;

    /// <summary>撞廳後隔幾秒重連（另加 0–50% 的隨機延遲，避免多個機器人同時重連）。</summary>
    public int ChannelCollisionRetrySeconds { get; set; } = 5;

    /// <summary>放棄後下線多久再重新嘗試。</summary>
    public int ChannelWaitingRetrySeconds { get; set; } = 300;

    /// <summary>
    /// 公頻名單以遊戲方推送的 opcode 9／17 維護；超過幾分鐘沒收到 opcode 9 推送，才主動掃描一次。
    /// 0 表示不主動掃描，完全依賴推送。
    /// </summary>
    public int ChannelRescanMinutes { get; set; } = 10;

    /// <summary>
    /// 是否把機器人維護的在線名單寫成 JSON 檔，依大廳編號命名：{FileLogging:Directory}/roster/{env}/channel-{channel_id}.json。
    /// 機器人離開大廳時刪除檔案；服務啟動時會清掉上次留下的檔案。
    /// </summary>
    public bool WriteRosterFile { get; set; } = true;
}
