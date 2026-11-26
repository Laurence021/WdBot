using System.Collections;
using System.Globalization;
using System.Text;
using NLog.Targets;

namespace WdBot.Service;

/// <summary>
/// 依參數型別上色的主控台輸出，上色方式比照 Serilog 的預設佈景（Literate）：
/// 字串青色、數字洋紅色、true/false 與 null 藍色、其他型別綠色；固定文字白色；時間與類別灰色；等級標籤依等級上色。
/// 格式：HH:mm:ss [INF] 類別: 訊息，有例外時換行接著輸出例外內容。
/// 在 nlog.config 以 xsi:type="WdColoredConsole" 使用（LoggingSetup 會先註冊）。
/// NLog 會依序呼叫同一個 target 的 Write，不會同時執行，因此不另外加鎖。
/// </summary>
[Target(TypeName)]
public sealed class TemplateColoredConsoleTarget : Target
{
    /// <summary>在 nlog.config 使用的 target 類型名稱（xsi:type）。</summary>
    public const string TypeName = "WdColoredConsole";

    /// <summary>時間、類別的顏色。</summary>
    private const ConsoleColor SecondaryText = ConsoleColor.Gray;

    /// <summary>訊息中固定文字的顏色。</summary>
    private const ConsoleColor Text = ConsoleColor.White;

    /// <summary>字串參數的顏色。</summary>
    private const ConsoleColor StringValue = ConsoleColor.Cyan;

    /// <summary>數字參數的顏色。</summary>
    private const ConsoleColor NumberValue = ConsoleColor.Magenta;

    /// <summary>true／false 與 null 的顏色。</summary>
    private const ConsoleColor KeywordValue = ConsoleColor.Blue;

    /// <summary>其他型別參數（列舉、時間、物件）的顏色。</summary>
    private const ConsoleColor OtherValue = ConsoleColor.Green;

    /// <summary>一段要輸出的文字與它的顏色；顏色為 null 表示使用主控台預設顏色。</summary>
    /// <param name="Text">文字。</param>
    /// <param name="Foreground">文字顏色。</param>
    /// <param name="Background">背景顏色。</param>
    private readonly record struct Segment(string Text, ConsoleColor? Foreground, ConsoleColor? Background = null);

    /// <summary>把一筆 log 組成「時間 [等級] 類別: 訊息」並分段上色輸出；有例外時換行接著輸出例外內容。</summary>
    /// <param name="logEvent">要輸出的 log。</param>
    protected override void Write(NLog.LogEventInfo logEvent)
    {
        var segments = new List<Segment>(16)
        {
            new(logEvent.TimeStamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " [", SecondaryText),
        };

        var (tag, foreground, background) = LevelStyle(logEvent.Level);
        segments.Add(new(tag, foreground, background));
        segments.Add(new("] " + logEvent.LoggerName + ": ", SecondaryText));

        if (!TryAppendTemplate(segments, logEvent.Message, GetParameterValues(logEvent)))
        {
            // 範本與參數對不上時，輸出 NLog 已經格式化好的訊息，不漏內容
            segments.Add(new(logEvent.FormattedMessage ?? "", Text));
        }

        if (logEvent.Exception is { } ex)
        {
            segments.Add(new(Environment.NewLine + ex, null));
        }

        Output(segments);
    }

    /// <summary>
    /// 依範本順序取出參數值。從 Microsoft.Extensions.Logging 進來的 log，參數放在 MessageTemplateParameters（Parameters 為 null）；
    /// 直接用 NLog 記錄的 log 則可能只有 Parameters。
    /// </summary>
    /// <param name="logEvent">要取出參數的 log。</param>
    private static object?[]? GetParameterValues(NLog.LogEventInfo logEvent)
    {
        try
        {
            var parameters = logEvent.MessageTemplateParameters;
            if (parameters.Count > 0)
            {
                return parameters.Select(p => p.Value).ToArray();
            }
        }
        catch (Exception)
        {
            // 範本無法解析：改用 Parameters，仍不行就走備援
        }

        return logEvent.Parameters;
    }

    /// <summary>等級標籤的文字與顏色：TRC／DBG 灰色、INF 白色、WRN 黃色、ERR／FTL 紅底白字。</summary>
    /// <param name="level">log 等級。</param>
    private static (string Tag, ConsoleColor Foreground, ConsoleColor? Background) LevelStyle(NLog.LogLevel level)
    {
        if (level == NLog.LogLevel.Trace) return ("TRC", SecondaryText, null);
        if (level == NLog.LogLevel.Debug) return ("DBG", SecondaryText, null);
        if (level == NLog.LogLevel.Info) return ("INF", Text, null);
        if (level == NLog.LogLevel.Warn) return ("WRN", ConsoleColor.Yellow, null);
        if (level == NLog.LogLevel.Error) return ("ERR", ConsoleColor.White, ConsoleColor.Red);
        return ("FTL", ConsoleColor.White, ConsoleColor.Red);
    }

    /// <summary>
    /// 逐字掃描訊息範本，固定文字與參數值分段上色。參數依出現順序對應 parameters（與 Microsoft.Extensions.Logging 相同）。
    /// 支援 {名稱}、{名稱,對齊}、{名稱:格式}，以及 {{、}} 跳脫。範本與參數數量對不上時回傳 false，segments 不變。
    /// </summary>
    /// <param name="segments">輸出：分段結果會加到這裡。</param>
    /// <param name="template">訊息範本；可為 null。</param>
    /// <param name="parameters">參數值，依範本中出現的順序；可為 null。</param>
    private static bool TryAppendTemplate(List<Segment> segments, string? template, object?[]? parameters)
    {
        if (template is null)
        {
            return false;
        }

        parameters ??= [];
        var parts = new List<Segment>();
        var literal = new StringBuilder();
        var index = 0;

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                parts.Add(new(literal.ToString(), Text));
                literal.Clear();
            }
        }

        for (var i = 0; i < template.Length;)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                literal.Append('{');
                i += 2;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                literal.Append('}');
                i += 2;
                continue;
            }

            if (c != '{')
            {
                literal.Append(c);
                i++;
                continue;
            }

            var end = template.IndexOf('}', i + 1);
            if (end < 0 || index >= parameters.Length)
            {
                return false;
            }

            var hole = template.AsSpan(i + 1, end - i - 1);
            string? format = null;
            var colon = hole.IndexOf(':');
            if (colon >= 0)
            {
                format = hole[(colon + 1)..].ToString();
                hole = hole[..colon];
            }

            var alignment = 0;
            var comma = hole.IndexOf(',');
            if (comma >= 0 && !int.TryParse(hole[(comma + 1)..].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out alignment))
            {
                return false;
            }

            FlushLiteral();
            var (text, color) = FormatValue(parameters[index++], format);
            if (alignment > 0)
            {
                text = text.PadLeft(alignment);
            }
            else if (alignment < 0)
            {
                text = text.PadRight(-alignment);
            }

            parts.Add(new(text, color));
            i = end + 1;
        }

        FlushLiteral();
        if (index != parameters.Length)
        {
            return false;
        }

        segments.AddRange(parts);
        return true;
    }

    /// <summary>
    /// 參數值轉成文字並決定顏色。結果與 Microsoft.Extensions.Logging 格式化的相同（它用 InvariantCulture），
    /// 所以主控台與檔案 log 的內容一致。
    /// </summary>
    /// <param name="value">參數值；可為 null。</param>
    /// <param name="format">範本中 {名稱:格式} 的格式字串；沒有時為 null。</param>
    private static (string Text, ConsoleColor Color) FormatValue(object? value, string? format)
    {
        var culture = CultureInfo.InvariantCulture;
        switch (value)
        {
            case null:
                return ("(null)", KeywordValue);
            case string s:
                return (s, StringValue);
            case char ch:
                return (ch.ToString(), StringValue);
            case bool b:
                return (b.ToString(culture), KeywordValue);
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                or System.Numerics.BigInteger or Half or Int128 or UInt128:
                return (ToText(value, format, culture), NumberValue);
            case IEnumerable items:
                // 集合（字串在上面已處理）：與 Microsoft.Extensions.Logging 相同，以 ", " 串接
                return (string.Join(", ", items.Cast<object?>().Select(item => item is null ? "(null)" : ToText(item, null, culture))), OtherValue);
            default:
                return (ToText(value, format, culture), OtherValue);
        }
    }

    /// <summary>值轉成文字：IFormattable 套用格式與文化設定，其他用 ToString()。</summary>
    /// <param name="value">要轉換的值。</param>
    /// <param name="format">格式字串；沒有時為 null。</param>
    /// <param name="culture">格式化用的文化設定。</param>
    private static string ToText(object value, string? format, CultureInfo culture) =>
        value is IFormattable formattable ? formattable.ToString(format, culture) : value.ToString() ?? "";

    /// <summary>逐段設定顏色並寫到主控台，最後還原顏色並換行；輸出被導向時只寫純文字。</summary>
    /// <param name="segments">要輸出的文字段落。</param>
    private static void Output(List<Segment> segments)
    {
        if (Console.IsOutputRedirected)
        {
            // 輸出被導向檔案或管線時不上色，只寫純文字
            Console.Out.WriteLine(string.Concat(segments.Select(s => s.Text)));
            return;
        }

        try
        {
            foreach (var segment in segments)
            {
                Console.ResetColor();
                if (segment.Foreground is { } foreground)
                {
                    Console.ForegroundColor = foreground;
                }

                if (segment.Background is { } background)
                {
                    Console.BackgroundColor = background;
                }

                Console.Out.Write(segment.Text);
            }
        }
        finally
        {
            Console.ResetColor();
            Console.Out.WriteLine();
        }
    }
}
