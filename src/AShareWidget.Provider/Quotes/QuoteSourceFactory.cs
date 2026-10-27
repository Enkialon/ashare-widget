namespace AShareWidget.Quotes;

public static class QuoteSourceFactory
{
    public const string DefaultName = "tencent";

    public static IReadOnlyList<string> KnownNames { get; } = ["tencent"];

    public static IQuoteSource Create(string? name)
    {
        var key = string.IsNullOrWhiteSpace(name)
            ? DefaultName
            : name.Trim().ToLowerInvariant();

        return key switch
        {
            "tencent" => new TencentQuoteSource(),
            // "sina" => new SinaQuoteSource(), // TODO: port when needed
            _ => throw new ArgumentException(
                $"Unknown quote source '{name}'. Known: {string.Join(", ", KnownNames)}",
                nameof(name)),
        };
    }
}
