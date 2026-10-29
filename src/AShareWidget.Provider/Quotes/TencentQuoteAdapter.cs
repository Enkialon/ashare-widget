namespace AShareWidget.Quotes;

public sealed class TencentQuoteAdapter(HttpClient? http = null)
    : QuoteAdapterBase(http ?? SharedHttp, "http://qt.gtimg.cn/q=", "v_")
{
    static readonly HttpClient SharedHttp = CreateClient("https://gu.qq.com/");
    public override string Name => "tencent";
    protected override Quote Parse(string code, string payload)
    {
        if (code.StartsWith("hf_", StringComparison.Ordinal))
        {
            var f = payload.Split(',');
            if (f.Length < 14) return Quote.Failure(Name, code, "黄金/期货响应格式不完整。");
            return Result(code, f[13], Number(f, 0), Number(f, 7), percent: Number(f, 1),
                time: $"{f[12]} {f[6]}");
        }
        var fields = payload.Split('~');
        if (code.StartsWith("jj", StringComparison.Ordinal) && fields.Length >= 9)
        {
            var price = Number(fields, 5);
            var percent = Number(fields, 7);
            var reference = percent.HasValue && percent > -100 ? price / (1 + percent / 100) : null;
            return Result(code, fields[1], price, reference, percent: percent, time: fields[8]);
        }
        if (fields.Length < 33) return Quote.Failure(Name, code, "腾讯行情响应格式不支持或不完整。");
        return Result(code, fields[1], Number(fields, 3), Number(fields, 4), Number(fields, 31),
            Number(fields, 32), time: fields[30]);
    }
}
