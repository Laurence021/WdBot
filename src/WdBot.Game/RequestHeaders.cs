using System.Net.Http.Headers;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace WdBot.Game;

/// <summary>
/// 送給遊戲方的額外 Header（讓請求看起來像手機 App 送出的）。值已經替換好變數。
/// Http 用於 App API，WebSocket 用於聊天 WS 的連線請求。
/// </summary>
public sealed class RequestHeaders
{
    public static readonly RequestHeaders None = new([], []);

    public RequestHeaders(IReadOnlyList<KeyValuePair<string, string>> http, IReadOnlyList<KeyValuePair<string, string>> webSocket)
    {
        Http = http;
        WebSocket = webSocket;
    }

    public IReadOnlyList<KeyValuePair<string, string>> Http { get; }
    public IReadOnlyList<KeyValuePair<string, string>> WebSocket { get; }

    /// <summary>App API 實際送出的 User-Agent；沒有設定時為 null。</summary>
    public string? HttpUserAgent => Find(Http, "User-Agent");

    /// <summary>
    /// 套用到 App API 請求。Content-Type 只在有 body 時設定，並取代 .NET 預設的 "application/json; charset=utf-8"。
    /// 要在 request.Content 設定之後呼叫。
    /// </summary>
    public void Apply(HttpRequestMessage request)
    {
        foreach (var (name, value) in Http)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Content is not null)
                {
                    request.Content.Headers.Remove("Content-Type");
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", value);
                }

                continue;
            }

            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>套用到聊天 WS 的連線請求。Connection、Upgrade、Sec-WebSocket-* 等由 .NET 控制的 Header 會略過。</summary>
    public void Apply(ClientWebSocketOptions options, ILogger logger)
    {
        foreach (var (name, value) in WebSocket)
        {
            try
            {
                options.SetRequestHeader(name, value);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning("聊天 WS 無法設定 Header {Name}，略過：{Message}", name, ex.Message);
            }
        }
    }

    private static string? Find(IReadOnlyList<KeyValuePair<string, string>> headers, string name) =>
        headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
