using System.Text.Json;

namespace WdBot.Game;

/// <summary>規格書附錄 A 中，程式需要特別處理的錯誤碼。</summary>
public static class GameErrorCodes
{
    /// <summary>成功。</summary>
    public const int Success = 1000;

    /// <summary>系統維護中。</summary>
    public const int Maintenance = 1012;

    /// <summary>參數驗證失敗，例如帳號密碼格式不符。</summary>
    public const int ValidationFailed = 2001;

    /// <summary>帳號不存在。</summary>
    public const int AccountNotFound = 2002;

    /// <summary>登入失敗；刷新 token 時也代表 device_id 不符。</summary>
    public const int LoginFailed = 2003;

    /// <summary>密碼錯誤次數過多。</summary>
    public const int TooManyWrongPasswords = 2023;

    /// <summary>帳號申請刪除中。</summary>
    public const int AccountPendingDeletion = 2024;

    /// <summary>未授權：access_token 無效或過期，需要刷新 token。</summary>
    public const int Unauthorized = 2029;

    /// <summary>帳號或密碼錯誤。</summary>
    public const int AccountPasswordMismatch = 2041;

    /// <summary>呼叫太頻繁，回應的 data 可能帶 retry_after_seconds。</summary>
    public const int TooFrequent = 2044;

    /// <summary>帳號無法使用。</summary>
    public const int AccountUnavailable = 2045;

    /// <summary>帳號已停權。</summary>
    public const int AccountSuspended = 4038;

    /// <summary>密碼已變更，舊的 refresh_token 失效，需要重新登入。</summary>
    public const int PasswordChanged = 4079;

    /// <summary>
    /// 登入時遇到這些錯誤碼，重試也不會成功，要停下來等後台修正設定。
    /// 2001 代表送出的參數本身不合格（例如帳號不是 5–20 字英數），重送同樣的內容結果不會變。
    /// </summary>
    /// <param name="code">API 回傳的錯誤碼。</param>
    public static bool IsFatalForLogin(int code) => code is
        ValidationFailed
        or AccountNotFound or LoginFailed or TooManyWrongPasswords or AccountPendingDeletion
        or AccountPasswordMismatch or AccountUnavailable or AccountSuspended;
}

/// <summary>App API 回傳 error_code 不是 1000。</summary>
public sealed class GameApiException : Exception
{
    /// <summary>建立例外；訊息格式為「WD API {path} 失敗：{錯誤碼} {錯誤訊息}」。</summary>
    /// <param name="path">呼叫的 API 路徑。</param>
    /// <param name="errorCode">API 回傳的 error_code。</param>
    /// <param name="errorMessage">API 回傳的錯誤訊息；可為 null。</param>
    /// <param name="data">API 回傳的 data 欄位（例如 2044 的 retry_after_seconds）；可為 null。</param>
    public GameApiException(string path, int errorCode, string? errorMessage, JsonElement? data)
        : base($"WD API {path} 失敗：{errorCode} {errorMessage}")
    {
        Path = path;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        Data = data;
    }

    /// <summary>呼叫的 API 路徑。</summary>
    public string Path { get; }

    /// <summary>遊戲方回傳的 error_code。</summary>
    public int ErrorCode { get; }

    /// <summary>遊戲方回傳的 error_message。</summary>
    public string? ErrorMessage { get; }

    /// <summary>遊戲方回傳的 data（部分錯誤會帶補充資訊，例如 retry_after_seconds）。</summary>
    public new JsonElement? Data { get; }

    /// <summary>這個錯誤是否代表登入不可能成功（重試無效）。</summary>
    public bool IsFatalForLogin => GameErrorCodes.IsFatalForLogin(ErrorCode);
}
