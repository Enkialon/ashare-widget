namespace AShareWidget.Quotes;

/// <summary>
/// Minimal quote fields for the widget: name, price, change, change%.
/// </summary>
public sealed class Quote
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public double Price { get; init; }
    public double Change { get; init; }
    public double ChangePct { get; init; }
}
