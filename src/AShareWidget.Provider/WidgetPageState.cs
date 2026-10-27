using System.Text.Json;

namespace AShareWidget;

sealed class WidgetPageState
{
    public int Index { get; private set; }
    public int PageSize { get; private set; } = 5;
    public bool WasNavigated { get; private set; }

    public int Count(int total) => Math.Max(1, (total + PageSize - 1) / PageSize);
    public int Start => Index * PageSize;
    public void Clamp(int total) => Index = Math.Clamp(Index, 0, Count(total) - 1);

    public bool Resize(int pageSize, int total)
    {
        if (pageSize == PageSize) return false;
        var firstVisible = Start;
        PageSize = pageSize;
        Index = firstVisible / PageSize;
        if (total > 0) Clamp(total);
        return true;
    }

    public bool Move(int delta, int total)
    {
        Clamp(total);
        var next = Math.Clamp(Index + delta, 0, Count(total) - 1);
        if (next == Index) return false;
        Index = next;
        WasNavigated = true;
        return true;
    }

    public string Save() => JsonSerializer.Serialize(new { page = Index });

    public void Restore(string state)
    {
        if (WasNavigated || string.IsNullOrWhiteSpace(state)) return;
        try
        {
            using var json = JsonDocument.Parse(state);
            if (json.RootElement.TryGetProperty("page", out var page) && page.TryGetInt32(out var index))
                Index = Math.Clamp(index, 0, 11); // At most 12 configured quotes.
        }
        catch (JsonException) { /* Older versions stored empty state. */ }
        catch (InvalidOperationException) { /* Ignore incompatible custom state. */ }
    }
}
