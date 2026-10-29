namespace AShareWidget.Quotes;

/// <summary>Fixed adapter response. Missing numbers are never fabricated as zero.</summary>
public sealed record Quote
{
    public required string Source { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public double? Price { get; init; }
    public double? Change { get; init; }
    public double? ChangePct { get; init; }
    public string QuoteTime { get; init; } = "";
    public string? Error { get; init; }
    public string Key => $"{Source}:{Code}";
    public bool IsAvailable => Price.HasValue;
    public static Quote Failure(string source, string code, string error) => new()
    {
        Source = source, Code = code, Name = code, Error = error,
    };
}
