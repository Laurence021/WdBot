using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WdBot.Game;

/// <summary>WD API 與聊天 WS 共用的 JSON 設定：欄位一律 snake_case，省略的欄位以 C# 預設值處理。</summary>
public static class GameJson
{
    /// <summary>序列化與反序列化遊戲方資料使用的選項。</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>把 JsonElement 轉成指定型別；null 或不存在時回傳 default。</summary>
    /// <typeparam name="T">要轉成的型別。</typeparam>
    /// <param name="element">要轉換的 JSON；可為 null。</param>
    public static T? ToObject<T>(JsonElement? element)
    {
        if (element is not { } e || e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return default;
        }

        return e.Deserialize<T>(Options);
    }
}
