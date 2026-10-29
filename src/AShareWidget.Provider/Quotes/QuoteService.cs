namespace AShareWidget.Quotes;

/// <summary>Routes requests, batches by provider, and restores configuration order.</summary>
public sealed class QuoteService : IQuoteService
{
    readonly IReadOnlyDictionary<string, IQuoteAdapter> _adapters;
    public QuoteService() : this([new TencentQuoteAdapter(), new SinaQuoteAdapter()]) { }
    public QuoteService(IEnumerable<IQuoteAdapter> adapters) =>
        _adapters = adapters.ToDictionary(a => a.Name, StringComparer.Ordinal);

    public async Task<IReadOnlyList<Quote>> FetchAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requests = codes.Select(QuoteRequest.Parse).ToArray();
        foreach (var request in requests)
            if (!_adapters.ContainsKey(request.Source))
                throw new FormatException($"未知数据源“{request.Source}”，可用数据源：{string.Join(", ", _adapters.Keys)}。");
        var groups = await Task.WhenAll(requests.GroupBy(r => r.Source).Select(async group =>
        {
            var nativeCodes = group.Select(r => r.Code).Distinct(StringComparer.Ordinal).ToArray();
            try
            {
                return await _adapters[group.Key].FetchAsync(nativeCodes, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return (IReadOnlyList<Quote>)nativeCodes.Select(code => Quote.Failure(group.Key, code,
                    $"{group.Key} 查询失败：{ex.Message}")).ToArray();
            }
        })).ConfigureAwait(false);
        var byKey = groups.SelectMany(g => g).ToDictionary(q => q.Key, StringComparer.Ordinal);
        return requests.Select(r => byKey.TryGetValue(r.Key, out var quote) ? quote :
            Quote.Failure(r.Source, r.Code, "接口未返回此代码的行情。")).ToArray();
    }
}
