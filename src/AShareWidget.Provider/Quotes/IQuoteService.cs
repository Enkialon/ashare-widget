namespace AShareWidget.Quotes;

public interface IQuoteService
{
    Task<IReadOnlyList<Quote>> FetchAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
}

public interface IQuoteAdapter
{
    string Name { get; }
    /// <summary>Receives native provider codes, without a routing prefix.</summary>
    Task<IReadOnlyList<Quote>> FetchAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
}
