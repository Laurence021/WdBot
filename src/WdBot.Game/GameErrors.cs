using System.Text.Json;

namespace WdBot.Game;

/// <summary>規格書附錄 A 中，程式需要特別處理的錯誤碼。</summary>
public static class GameErrorCodes
{
    public const int Success = 1000;
    public const int Maintenance = 1012;
    public const int ValidationFailed = 2001;     // 參數驗證失敗，例如帳號密碼格式不符
    public const int AccountNotFound = 2002;
    public const int LoginFailed = 2003;          // 刷新 token 時也代表 device_id 不符
    public const int TooManyWrongPasswords = 2023;
    public const int AccountPendingDeletion = 2024;
    public const int Unauthorized = 2029;
    public const int AccountPasswordMismatch = 2041;
    public const int TooFrequent = 2044;
    public const int AccountUnavailable = 2045;
    public const int AccountSuspended = 4038;
    public const int PasswordChanged = 4079;

    /// <summary>
    /// 登入時遇到這些錯誤碼，重試也不會成功，要停下來等後台修正設定。
    /// 2001 代表送出的參數本身不合格（例如帳號不是 5–20 字英數），重送同樣的內容結果不會變。
    /// </summary>
    public static bool IsFatalForLogin(int code) => code is
        ValidationFailed
        or AccountNotFound or LoginFailed or TooManyWrongPasswords or AccountPendingDeletion
        or AccountPasswordMismatch or AccountUnavailable or AccountSuspended;
}

/// <summary>App API 回傳 error_code 不是 1000。</summary>
public sealed class GameApiException : Exception
{
    public GameApiException(string path, int errorCode, string? errorMessage, JsonElement? data)
        : base($"WD API {path} 失敗：{errorCode} {errorMessage}")
    {
        Path = path;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        Data = data;
    }

    public string Path { get; }
    public int ErrorCode { get; }
    public string? ErrorMessage { get; }
    public new JsonElement? Data { get; }

    public bool IsFatalForLogin => GameErrorCodes.IsFatalForLogin(ErrorCode);
}
