using System.Globalization;
using System.Text;
using System.Text.Json;
using AShareWidget.Quotes;

namespace AShareWidget;

static class WidgetCard
{
    /// <summary>
    /// Fully materialised Adaptive Card 1.4 JSON (no Adaptive Templating placeholders).
    /// </summary>
    public static string Build(IReadOnlyList<Quote> quotes, string statusLine, string lastError, WidgetPageState page)
    {
        page.Clamp(quotes.Count);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(1024);
        sb.Append("""
            {"type":"AdaptiveCard","version":"1.4","$schema":"http://adaptivecards.io/schemas/adaptive-card.json","body":[
            """);

        var first = true;
        void AppendComma()
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
        }

        if (quotes.Count == 0)
        {
            AppendComma();
            sb.Append("{\"type\":\"TextBlock\",\"text\":");
            AppendJsonString(sb, string.IsNullOrEmpty(lastError) ? "等待行情…" : lastError);
            sb.Append(",\"wrap\":true,\"maxLines\":1}");
        }
        else
        {
            foreach (var q in quotes.Skip(page.Start).Take(page.PageSize))
            {
                var up = q.ChangePct > 0;
                var flat = Math.Abs(q.ChangePct) < 0.000_000_1;
                var sign = up ? "+" : "";
                var changeLine = flat
                    ? "0.00  0.00%"
                    : $"{sign}{q.Change.ToString("0.00", inv)}  {sign}{q.ChangePct.ToString("0.00", inv)}%";
                // Attention≈红 / Good≈绿 — A-share convention, set as literals (not ${}).
                var color = flat ? "Default" : up ? "Attention" : "Good";
                var code = StripPrefix(q.Code);
                var price = q.Price.ToString("0.00", inv);

                AppendComma();
                sb.Append("{\"type\":\"ColumnSet\",\"spacing\":\"Small\",\"columns\":[");
                sb.Append("{\"type\":\"Column\",\"width\":\"stretch\",\"items\":[");
                sb.Append("{\"type\":\"TextBlock\",\"weight\":\"Bolder\",\"wrap\":false,\"maxLines\":1,\"text\":");
                AppendJsonString(sb, q.Name);
                sb.Append("},{\"type\":\"TextBlock\",\"isSubtle\":true,\"size\":\"Small\",\"spacing\":\"None\",\"text\":");
                AppendJsonString(sb, code);
                sb.Append("}]},");
                sb.Append("{\"type\":\"Column\",\"width\":\"auto\",\"verticalContentAlignment\":\"Center\",\"items\":[");
                sb.Append("{\"type\":\"TextBlock\",\"horizontalAlignment\":\"Right\",\"weight\":\"Bolder\",\"text\":");
                AppendJsonString(sb, price);
                sb.Append("},{\"type\":\"TextBlock\",\"horizontalAlignment\":\"Right\",\"spacing\":\"None\",\"size\":\"Small\",\"color\":\"");
                sb.Append(color);
                sb.Append("\",\"text\":");
                AppendJsonString(sb, changeLine);
                sb.Append("}]}]}");
            }
        }

        // Reserve one bottom row for navigation and time, including on small cards.
        AppendComma();
        var pages = page.Count(quotes.Count);
        var footerColumns = new List<object>();
        if (pages > 1)
        {
            footerColumns.Add(Arrow("‹", "page.previous", page.Index > 0));
            footerColumns.Add(new
            {
                type = "Column", width = "36px",
                items = new[] { new { type = "TextBlock", text = $"{page.Index + 1}/{pages}",
                    size = "Small", isSubtle = true, horizontalAlignment = "Center", spacing = "None" } },
            });
        }
        footerColumns.Add(new
        {
            type = "Column", width = "stretch",
            items = new[] { new { type = "TextBlock", text = statusLine, size = "Small",
                isSubtle = true, horizontalAlignment = "Right", spacing = "None", wrap = false, maxLines = 1 } },
        });
        if (pages > 1)
            footerColumns.Add(Arrow("›", "page.next", page.Index < pages - 1));
        sb.Append(JsonSerializer.Serialize(new
        {
            type = "Container", id = "footer", height = "stretch",
            verticalContentAlignment = "Bottom", spacing = "Small",
            items = new[] { new { type = "ColumnSet", spacing = "None", columns = footerColumns } },
        }));

        sb.Append("]}");
        return sb.ToString();
    }

    static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append(JsonSerializer.Serialize(value));
    }

    static string StripPrefix(string code)
    {
        if (code.StartsWith("us.", StringComparison.OrdinalIgnoreCase))
        {
            return code[3..];
        }

        foreach (var prefix in new[] { "sh", "sz", "bj", "hk", "us" })
        {
            if (code.Length > prefix.Length
                && code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = code[prefix.Length..];
                return rest.StartsWith('.') ? rest[1..] : rest;
            }
        }

        return code;
    }

    static object Arrow(string label, string verb, bool enabled)
    {
        var column = new Dictionary<string, object>
        {
            ["type"] = "Column",
            ["width"] = "28px",
            ["verticalContentAlignment"] = "Center",
            ["items"] = new[] { new { type = "TextBlock", text = label,
                horizontalAlignment = "Center", size = "Medium", isSubtle = !enabled, spacing = "None" } },
        };
        if (enabled)
            column["selectAction"] = new { type = "Action.Execute", verb,
                title = verb == "page.previous" ? "上一页" : "下一页" };
        return column;
    }

}

