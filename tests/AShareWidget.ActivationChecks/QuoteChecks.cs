using System.Net;
using System.Text;
using AShareWidget.Quotes;

static class QuoteChecks
{
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
    public static async Task RunAsync()
    {
        var stock = new string[33];
        Array.Fill(stock, "");
        stock[1] = "指数"; stock[3] = "110"; stock[4] = "100";
        stock[30] = "20261028100000"; stock[31] = "10"; stock[32] = "10";
        var tencent = new FixtureHandler($"v_usNDX=\"{string.Join('~', stock)}\";\n" +
            "v_hf_XAU=\"4048.82,-0.28,0,0,0,0,10:43:00,4060.13,0,0,0,0,2026-10-28,黄金\";" +
            "v_jj000834=\"000834~基金~0~0~~6.1686~6.1686~1.0451~2026-10-26~\";");
        var sina = new FixtureHandler("var hq_str_gds_AU9999=\"869.69,0,0,0,0,0,10:44:41,860.31,0,0,0,0,2026-10-28,沪金99\";\n" +
            "var hq_str_nf_AU0=\"黄金连续,104505,0,0,0,0,0,0,871.22,0,863.68,0,0,0,0,沪,黄金,2026-10-28\";");
        var service = new QuoteService([new TencentQuoteAdapter(new HttpClient(tencent)), new SinaQuoteAdapter(new HttpClient(sina))]);
        string[] codes = ["sina:gds_AU9999", "tencent:usNDX", "tencent:hf_XAU", "sina:nf_AU0", "tencent:jj000834", "tencent:missing", "tencent:usNDX"];
        var result = await service.FetchAsync(codes);
        Check(result.Select(q => q.Key).SequenceEqual(codes), "mixed providers preserve order, duplicates and missing entries");
        Check(tencent.Queries.Single() == "/q=usNDX,hf_XAU,jj000834,missing" && sina.Queries.Single() == "/list=gds_AU9999,nf_AU0", "batch once per source; native codes unchanged and deduplicated");
        Check(Math.Abs(result[0].ChangePct!.Value - (869.69 - 860.31) / 860.31 * 100) < 0.00001, "Sina domestic gold derives percentage from reference price");
        Check(result[1].Price == 110 && result[1].Change == 10 && result[1].ChangePct == 10, "Tencent stock fields parsed");
        Check(result[2].ChangePct == -0.28, "Tencent gold retains authoritative percentage");
        Check(result[3].QuoteTime == "2026-10-28 10:45:05" && result[3].Price == 871.22, "Sina domestic futures fields parsed");
        Check(result[4].Price == 6.1686 && result[4].QuoteTime == "2026-10-26", "fund NAV preserves precision and actual quote date");
        Check(result[5].Price is null && result[5].Error is not null, "missing quote is an explicit error, never zero");
        var page = new AShareWidget.WidgetPageState();
        page.Resize(5, 1);
        using (var card = System.Text.Json.JsonDocument.Parse(AShareWidget.WidgetCard.Build([result[5]], "查询失败", "", page)))
        {
            var price = card.RootElement.GetProperty("body")[0].GetProperty("columns")[1].GetProperty("items")[0].GetProperty("text").GetString();
            Check(price == "--", "card displays unavailable price as --");
        }
        var broken = new QuoteService([new TencentQuoteAdapter(new HttpClient(new FixtureHandler("", true))), new SinaQuoteAdapter(new HttpClient(sina))]);
        var partial = await broken.FetchAsync(["tencent:usNDX", "sina:gds_AU9999"]);
        Check(partial[0].Error is not null && partial[1].IsAvailable, "provider failure does not discard other provider results");
        var invalid = new QuoteService([new TencentQuoteAdapter(new HttpClient(new FixtureHandler("v_hf_XAU=\"NaN,0,0,0,0,0,10:00:00,100,0,0,0,0,2026-10-28,黄金\";")))]);
        Check((await invalid.FetchAsync(["tencent:hf_XAU"]))[0].Price is null, "nonfinite price rejected");
        var config = StockConfig.Parse("{\"refreshSeconds\":30,\"codes\":[\"sina:gds_AU9999\",\"tencent:usNDX\"]}");
        Check(config.Codes.SequenceEqual(codes.Take(2)), "new config preserves native codes and order");
        foreach (var json in new[] {
            "{\"source\":\"tencent\",\"codes\":[\"tencent:usNDX\"]}",
            "{\"codes\":[\"us.NDX\"]}", "{\"codes\":[\"tencent:hf_XAU \"]}",
            "{\"codes\":[\"unknown:usNDX\"]}", "{\"codes\":[],}" })
        {
            bool rejected = false;
            try { StockConfig.Parse(json); } catch (Exception e) when (e is FormatException or System.Text.Json.JsonException) { rejected = true; }
            Check(rejected, "reject obsolete or malformed config: " + json);
        }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        bool canceled = false;
        try { await service.FetchAsync([], cancel.Token); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "caller cancellation propagates");
    }
    public static async Task RunLiveAsync()
    {
        string[] codes = ["tencent:usNDX", "sina:gds_AU9999", "tencent:hf_XAU", "sina:nf_AU0", "tencent:jj000834", "sina:sh600519"];
        var quotes = await new QuoteService().FetchAsync(codes);
        foreach (var quote in quotes)
        {
            Check(quote.IsAvailable && quote.Error is null, "live " + quote.Key + ": " + quote.Error);
            Console.WriteLine($"{quote.Key} {quote.Name} {quote.Price} {quote.ChangePct:F3}% {quote.QuoteTime}");
        }
    }
    sealed class FixtureHandler(string body, bool fail = false) : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Queries.Add(request.RequestUri!.PathAndQuery);
            if (fail) throw new HttpRequestException("fixture provider unavailable");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.GetEncoding("GBK").GetBytes(body)) });
        }
    }
}
