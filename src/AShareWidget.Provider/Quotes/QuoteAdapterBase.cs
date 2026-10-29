using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AShareWidget.Quotes;

public abstract class QuoteAdapterBase(HttpClient http, string endpoint, string variablePrefix) : IQuoteAdapter
{
    static readonly Encoding Gbk = CreateEncoding();
    public abstract string Name { get; }
    static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GBK");
    }
    protected static HttpClient CreateClient(string referer)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.Referrer = new Uri(referer);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AShareWidget/0.3");
        return client;
    }
    public async Task<IReadOnlyList<Quote>> FetchAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default)
    {
        if (codes.Count == 0) return [];
        // Only transport escaping: never rewrite or guess a native code.
        var url = endpoint + string.Join(',', codes.Select(Uri.EscapeDataString));
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = Gbk.GetString(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        var quotes = new Dictionary<string, Quote>(StringComparer.Ordinal);
        var pattern = @"(?:^|;)\s*(?:var\s+)?" + Regex.Escape(variablePrefix) + "(?<code>[^=\\s]+)\\s*=\\s*\"(?<data>[^\"\\r\\n]*)\"";
        foreach (Match match in Regex.Matches(body, pattern, RegexOptions.Multiline))
        {
            var code = match.Groups["code"].Value;
            var payload = match.Groups["data"].Value;
            if (!string.IsNullOrEmpty(payload)) quotes[code] = Parse(code, payload);
        }
        return codes.Select(code => quotes.TryGetValue(code, out var quote) ? quote :
            Quote.Failure(Name, code, "接口未返回此原生代码的行情。")).ToArray();
    }
    protected abstract Quote Parse(string code, string payload);
    protected static double? Number(string[] fields, int index) => index < fields.Length &&
        double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : null;
    protected Quote Result(string code, string name, double? price, double? reference, double? change = null,
        double? percent = null, string time = "")
    {
        if (string.IsNullOrWhiteSpace(name) || !price.HasValue)
            return Quote.Failure(Name, code, "行情价格缺失或响应格式暂不支持。");
        if (!change.HasValue && reference.HasValue && reference > 0) change = price - reference;
        if (!percent.HasValue && reference.HasValue && reference > 0) percent = (price - reference) / reference * 100;
        return new Quote { Source = Name, Code = code, Name = name, Price = price, Change = change,
            ChangePct = percent, QuoteTime = time };
    }
}
