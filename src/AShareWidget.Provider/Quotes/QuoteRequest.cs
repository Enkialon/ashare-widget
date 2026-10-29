using System.Text.RegularExpressions;

namespace AShareWidget.Quotes;

public sealed record QuoteRequest(string Source, string Code)
{
    public string Key => $"{Source}:{Code}";
    public static QuoteRequest Parse(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            throw new FormatException($"代码“{value}”必须使用 数据源:原生代码，例如 tencent:usNDX。");
        var source = value[..separator];
        var code = value[(separator + 1)..];
        if (!Regex.IsMatch(source, "^[a-z][a-z0-9_-]*$") || !Regex.IsMatch(code, "^[A-Za-z0-9_.$-]+$"))
            throw new FormatException($"代码“{value}”格式无效，请填写原生代码，不要添加空格或转换市场代码。");
        return new(source, code);
    }
}
