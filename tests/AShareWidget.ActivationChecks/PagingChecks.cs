using System.Text.Json;
using AShareWidget;
using AShareWidget.Quotes;

static class PagingChecks
{
    public static void Run()
    {
        var quotes = Enumerable.Range(1, 12).Select(i => new Quote
        {
            Source = "tencent", Code = $"sh{i:000000}", Name = $"股票{i} \"名称\"", Price = 100 + i,
            Change = i % 2 == 0 ? -1 : 1, ChangePct = i % 2 == 0 ? -1 : 1,
        }).ToArray();

        foreach (int capacity in new[] { 1, 5, 9 })
        {
            var page = new WidgetPageState();
            page.Resize(capacity, quotes.Length);
            var seen = new List<string>();
            do
            {
                using var card = JsonDocument.Parse(WidgetCard.Build(quotes, "12:34:56", "", page));
                var body = card.RootElement.GetProperty("body");
                var rows = body.EnumerateArray().Where(e => e.GetProperty("type").GetString() == "ColumnSet").ToArray();
                Check(rows.Length <= capacity, $"capacity {capacity}: page {page.Index + 1} fits");
                seen.AddRange(rows.Select(r => r.GetProperty("columns")[0].GetProperty("items")[0].GetProperty("text").GetString()!));
                var footer = body[body.GetArrayLength() - 1];
                Check(footer.GetProperty("height").GetString() == "stretch"
                    && footer.GetProperty("verticalContentAlignment").GetString() == "Bottom", "footer remains at bottom");
                var columns = footer.GetProperty("items")[0].GetProperty("columns");
                Check(columns[0].TryGetProperty("selectAction", out _) == (page.Index > 0), "previous arrow boundary");
                Check(columns[3].TryGetProperty("selectAction", out var next) == (page.Index < page.Count(quotes.Length) - 1), "next arrow boundary");
                if (page.Index < page.Count(quotes.Length) - 1)
                    Check(next.GetProperty("type").GetString() == "Action.Execute"
                        && next.GetProperty("verb").GetString() == "page.next", "next arrow dispatches paging action");
            } while (page.Move(1, quotes.Length));
            Check(seen.SequenceEqual(quotes.Select(q => q.Name)), $"capacity {capacity}: every stock appears exactly once");
            Check(!page.Move(1, quotes.Length), "last page does not wrap");
            int last = page.Index;
            WidgetCard.Build(quotes, "12:35:11", "", page);
            Check(page.Index == last, "data refresh does not turn pages");
            var restored = new WidgetPageState();
            restored.Resize(capacity, quotes.Length);
            restored.Restore(page.Save());
            Check(restored.Index == page.Index, "saved page survives provider restart");
            while (page.Move(-1, quotes.Length)) { }
            Check(!page.Move(-1, quotes.Length), "first page does not wrap");
        }

        var shrinking = new WidgetPageState();
        shrinking.Move(1, 12);
        shrinking.Move(1, 12);
        shrinking.Resize(9, 12);
        Check(shrinking.Index == 1, "resize retains first previously visible stock");
        WidgetCard.Build(quotes.Take(2).ToArray(), "12:34:56", "", shrinking);
        Check(shrinking.Index == 0, "removing stocks clamps page into range");

        foreach (var state in new[] { "", "bad json", "[]", "{\"page\":\"bad\"}" })
        {
            var invalid = new WidgetPageState();
            invalid.Restore(state);
            Check(invalid.Index == 0, "invalid saved state falls back to first page");
        }
        var delayed = new WidgetPageState();
        delayed.Restore("{\"page\":2}");
        delayed.Resize(1, 0);
        Check(delayed.Start == 10, "cold-start resize retains anchor before quotes arrive");
        var clicked = new WidgetPageState();
        clicked.Move(1, quotes.Length);
        clicked.Restore("{\"page\":0}");
        Check(clicked.Index == 1, "late restoration cannot undo manual navigation");
        foreach (int count in new[] { 0, 1, 5 })
        {
            var page = new WidgetPageState();
            using var card = JsonDocument.Parse(WidgetCard.Build(quotes.Take(count).ToArray(), "加载中…", "", page));
            Check(!card.RootElement.GetRawText().Contains("selectAction"), "empty/single page has no navigation controls");
        }
        var independent = new WidgetPageState();
        Check(independent.Index == 0, "widgets have independent page state");
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
