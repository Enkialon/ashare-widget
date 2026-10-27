namespace AShareWidget.Quotes;

/// <summary>
/// Pluggable realtime quote provider (quote-only; swap implementations via QuoteSourceFactory).
/// </summary>
public interface IQuoteSource
{
    /// <summary>Stable id for config, e.g. "tencent" / "sina".</summary>
    string Name { get; }

    /// <summary>Human-readable label for UI / logs.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Fetch quotes for the given codes (6-digit or sh/sz/bj prefixed).
    /// Missing symbols may be omitted; empty list means none parsed.
    /// </summary>
    Task<IReadOnlyList<Quote>> FetchAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);
}
