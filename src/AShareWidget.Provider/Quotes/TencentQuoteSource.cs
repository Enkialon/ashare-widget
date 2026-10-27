using System.Text;
using System.Text.RegularExpressions;

namespace AShareWidget.Quotes;

/// <summary>
/// Tencent Securities realtime quotes.
/// GET http://qt.gtimg.cn/q=sh600519,sz000001,usNDX,hk00700 (GBK, ~ fields).
/// Config codes prefer Tencent search style: sh.600519 / us.NDX / hk.00700.
/// </summary>
public sealed class TencentQuoteSource : IQuoteSource
{
    const string BaseUrl = "http://qt.gtimg.cn/q=";
    static readonly HttpClient Http = CreateClient();
    static readonly Encoding Gbk;
    static readonly HashSet<string> Markets =
        new(StringComparer.OrdinalIgnoreCase) { "sh", "sz", "bj", "hk", "us" };

    static TencentQuoteSource()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding("GBK");
    }

    public string Name => "tencent";
    public string DisplayName => "腾讯证券";

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://gu.qq.com");
        return client;
    }

    /// <summary>
    /// Canonical config form: <c>market.symbol</c> (us symbols uppercased).
    /// Accepts <c>us.NDX</c>, <c>usNDX</c>, <c>sh600519</c>, bare <c>600519</c>/<c>NDX</c>.
    /// </summary>
    public static string ToCanonical(string code)
    {
        if (!TrySplit(code, out var market, out var symbol))
        {
            return "";
        }

        if (market == "us")
        {
            symbol = symbol.Replace(".", "", StringComparison.Ordinal).ToUpperInvariant();
        }
        else if (market == "hk")
        {
            var digits = new string(symbol.Where(char.IsDigit).ToArray());
            symbol = digits.Length > 0 ? digits.PadLeft(5, '0') : symbol;
        }

        return market + "." + symbol;
    }

    /// <summary>qt.gtimg.cn query id: sh600519 / usNDX / hk00700 (also accepts us.NDX).</summary>
    public static string ToQueryId(string code)
    {
        if (!TrySplit(code, out var market, out var symbol))
        {
            return "";
        }

        if (market == "us")
        {
            // Both usNDX and us.NDX work; keep dotted form for indices/tickers alike.
            return "us." + symbol.Replace(".", "", StringComparison.Ordinal).ToUpperInvariant();
        }

        if (market == "hk")
        {
            var digits = new string(symbol.Where(char.IsDigit).ToArray());
            symbol = digits.Length > 0 ? digits.PadLeft(5, '0') : symbol;
            return "hk" + symbol;
        }

        return market + symbol;
    }

    /// <summary>Backward-compatible alias.</summary>
    public static string CodeToTencent(string code, string? marketHint = null)
    {
        if (!string.IsNullOrWhiteSpace(marketHint)
            && code.IndexOf('.') < 0
            && !StartsWithMarket(code))
        {
            return ToQueryId(marketHint.Trim() + "." + code.Trim());
        }

        return ToQueryId(code);
    }

    static bool TrySplit(string code, out string market, out string symbol)
    {
        market = "";
        symbol = "";
        var raw = code.Trim();
        if (raw.Length == 0)
        {
            return false;
        }

        // market.symbol (Tencent search style)
        var dot = raw.IndexOf('.');
        if (dot == 2 && Markets.Contains(raw[..2]))
        {
            market = raw[..2].ToLowerInvariant();
            symbol = raw[3..].Trim();
            return symbol.Length > 0;
        }

        // us.NDX already handled when dot==2; us.BABA.N → market us, symbol BABA.N
        if (dot > 2 && raw.StartsWith("us.", StringComparison.OrdinalIgnoreCase))
        {
            market = "us";
            symbol = raw[3..].Trim();
            return symbol.Length > 0;
        }

        var lower = raw.ToLowerInvariant();
        foreach (var m in new[] { "sh", "sz", "bj", "hk", "us" })
        {
            if (lower.StartsWith(m, StringComparison.Ordinal) && raw.Length > m.Length)
            {
                market = m;
                symbol = raw[m.Length..];
                if (symbol.StartsWith('.'))
                {
                    symbol = symbol[1..];
                }

                return symbol.Length > 0;
            }
        }

        // Bare symbol inference (still allowed; canonical write-back adds market).
        if (raw.All(ch => char.IsLetter(ch)))
        {
            market = "us";
            symbol = raw;
            return true;
        }

        if (lower.StartsWith('6') || lower.StartsWith('5') || lower.StartsWith('9'))
        {
            market = "sh";
            symbol = raw;
            return true;
        }

        if (lower.StartsWith('8') || lower.StartsWith('4'))
        {
            market = "bj";
            symbol = raw;
            return true;
        }

        // Default A-share digits → Shenzhen (0/1/3…)
        if (raw.All(char.IsDigit))
        {
            market = "sz";
            symbol = raw;
            return true;
        }

        return false;
    }

    static bool StartsWithMarket(string code)
    {
        var lower = code.Trim().ToLowerInvariant();
        return lower.StartsWith("sh") || lower.StartsWith("sz") || lower.StartsWith("bj")
               || lower.StartsWith("hk") || lower.StartsWith("us");
    }

    public async Task<IReadOnlyList<Quote>> FetchAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        // Preserve caller order (already query ids or raw — normalize once).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var c in codes)
        {
            var q = ToQueryId(c);
            if (q.Length == 0 || !seen.Add(q))
            {
                continue;
            }

            list.Add(q);
        }

        if (list.Count == 0)
        {
            return [];
        }

        var url = BaseUrl + string.Join(",", list);
        using var resp = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var body = Gbk.GetString(bytes);

        var byCode = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseQuoteLine(line, out var q) && q is not null)
            {
                byCode[q.Code] = q;
            }
        }

        // Reorder to match request (config) order.
        var quotes = new List<Quote>(list.Count);
        foreach (var id in list)
        {
            if (byCode.TryGetValue(id, out var q)
                || byCode.TryGetValue(id.Replace(".", "", StringComparison.Ordinal), out q))
            {
                quotes.Add(q);
                continue;
            }

            // us.NDX response key may be v_us.NDX or v_usNDX
            var alt = id.StartsWith("us.", StringComparison.OrdinalIgnoreCase)
                ? "us" + id[3..]
                : null;
            if (alt is not null && byCode.TryGetValue(alt, out q))
            {
                quotes.Add(q);
            }
        }

        return quotes;
    }

    public static bool TryParseQuoteLine(string line, out Quote? quote)
    {
        quote = null;
        var eq = line.IndexOf('=');
        if (eq < 0)
        {
            return false;
        }

        var varPart = line[..eq];
        if (!varPart.StartsWith("v_", StringComparison.Ordinal))
        {
            return false;
        }

        if (varPart.Contains("none_match", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var codeRaw = varPart[2..];
        var m = Regex.Match(line[(eq + 1)..], "\"([^\"]*)\"");
        if (!m.Success)
        {
            return false;
        }

        var fields = m.Groups[1].Value.Split('~');
        if (fields.Length < 33)
        {
            if (fields.Length < 6)
            {
                return false;
            }

            quote = new Quote
            {
                Code = codeRaw,
                Name = fields[1],
                Price = ParseD(fields[3]),
                Change = ParseD(fields[4]),
                ChangePct = ParseD(fields[5]),
            };
            return !string.IsNullOrWhiteSpace(quote.Name);
        }

        var price = ParseD(fields[3]);
        var prevClose = ParseD(fields[4]);
        var changePct = ParseD(fields[32]);
        var change = fields.Length > 31 && !string.IsNullOrWhiteSpace(fields[31])
            ? ParseD(fields[31])
            : price > 0 && prevClose > 0 ? Math.Round(price - prevClose, 2) : 0;

        quote = new Quote
        {
            Code = codeRaw,
            Name = fields[1],
            Price = price,
            Change = change,
            ChangePct = changePct,
        };
        return !string.IsNullOrWhiteSpace(quote.Name);
    }

    static double ParseD(string s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : 0;
}
