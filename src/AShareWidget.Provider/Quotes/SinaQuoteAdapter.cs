namespace AShareWidget.Quotes;

public sealed class SinaQuoteAdapter(HttpClient? http = null)
    : QuoteAdapterBase(http ?? SharedHttp, "http://hq.sinajs.cn/list=", "hq_str_")
{
    static readonly HttpClient SharedHttp = CreateClient("https://finance.sina.com.cn/");
    public override string Name => "sina";
    protected override Quote Parse(string code, string payload)
    {
        var f = payload.Split(',');
        if ((code.StartsWith("gds_", StringComparison.Ordinal) || code.StartsWith("hf_", StringComparison.Ordinal)) && f.Length >= 14)
        {
            // Sina field 1 is not a reliable percentage in these layouts.
            return Result(code, f[13], Number(f, 0), Number(f, 7), time: $"{f[12]} {f[6]}");
        }
        if (code.StartsWith("nf_", StringComparison.Ordinal) && f.Length >= 18)
        {
            var time = f[1].Length == 6 ? $"{f[1][..2]}:{f[1][2..4]}:{f[1][4..]}" : f[1];
            return Result(code, f[0], Number(f, 8), Number(f, 10), time: $"{f[17]} {time}");
        }
        if ((code.StartsWith("sh", StringComparison.Ordinal) || code.StartsWith("sz", StringComparison.Ordinal) ||
            code.StartsWith("bj", StringComparison.Ordinal)) && f.Length >= 32)
            return Result(code, f[0], Number(f, 3), Number(f, 2), time: $"{f[30]} {f[31]}");
        return Quote.Failure(Name, code, "新浪行情响应格式暂不支持。");
    }
}
