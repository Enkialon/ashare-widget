using System.Text.Json;
using System.Text.Json.Serialization;

namespace AShareWidget.Quotes;

/// <summary>
/// User-editable quote list. One ordered <c>codes</c> array; each code uses
/// Tencent style <c>market.symbol</c> (sh/sz/bj/hk/us), e.g. us.NDX, sh.600519.
/// </summary>
public sealed class StockConfig
{
    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    [JsonPropertyName("source")]
    public string Source { get; set; } = QuoteSourceFactory.DefaultName;

    [JsonPropertyName("refreshSeconds")]
    public int RefreshSeconds { get; set; } = 15;

    /// <summary>Display / fetch order. Prefer <c>sh.600519</c> / <c>us.NDX</c>.</summary>
    [JsonPropertyName("codes")]
    public List<string> Codes { get; set; } = [];

    /// <summary>
    /// Real path (not MSIX sandbox):
    /// C:\Users\…\AppData\Local\AShareWidget\stocks.json
    /// </summary>
    public static string UserConfigPath =>
        Path.Combine(AppPaths.UserDataDirectory, "stocks.json");

    public static string UserDataDirectory => AppPaths.UserDataDirectory;

    static string PackagedConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "stocks.json");

    static IEnumerable<string> LegacyConfigCandidates()
    {
        var profileLocal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "Local");
        // Previous Chinese folder name
        yield return Path.Combine(profileLocal, "股票行情", "stocks.json");
        // MSIX-redirected LocalApplicationData
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AShareWidget",
            "stocks.json");
        yield return PackagedConfigPath;
    }

    /// <summary>Map config codes to qt.gtimg.cn query ids, preserving order.</summary>
    public IReadOnlyList<string> ResolveTencentCodes()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in Codes)
        {
            var q = TencentQuoteSource.ToQueryId(raw);
            if (q.Length == 0 || !seen.Add(q))
            {
                continue;
            }

            list.Add(q);
            if (list.Count >= 12)
            {
                break;
            }
        }

        return list;
    }

    public static StockConfig Load()
    {
        try
        {
            EnsureUserConfig();
            return ParseFile(UserConfigPath) ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }

    public static void EnsureUserConfig()
    {
        if (File.Exists(UserConfigPath))
        {
            return;
        }

        foreach (var candidate in LegacyConfigCandidates())
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var migrated = ParseFile(candidate) ?? CreateDefault();
                WriteUserConfig(migrated);
                return;
            }
            catch
            {
                // try next
            }
        }

        WriteUserConfig(CreateDefault());
    }

    static void WriteUserConfig(StockConfig cfg)
    {
        var wire = new StockConfig
        {
            Source = string.IsNullOrWhiteSpace(cfg.Source)
                ? QuoteSourceFactory.DefaultName
                : cfg.Source,
            RefreshSeconds = Math.Clamp(cfg.RefreshSeconds, 5, 300),
            Codes = cfg.Codes
                .Select(TencentQuoteSource.ToCanonical)
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList(),
        };

        if (wire.Codes.Count == 0)
        {
            wire = CreateDefault();
        }

        File.WriteAllText(
            UserConfigPath,
            JsonSerializer.Serialize(wire, WriteOptions) + Environment.NewLine);
    }

    static StockConfig? ParseFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var cfg = new StockConfig();
        if (root.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String)
        {
            cfg.Source = src.GetString() ?? QuoteSourceFactory.DefaultName;
        }

        if (root.TryGetProperty("refreshSeconds", out var rs) && rs.TryGetInt32(out var seconds))
        {
            cfg.RefreshSeconds = Math.Clamp(seconds, 5, 300);
        }

        // Preferred: single ordered codes[].
        if (root.TryGetProperty("codes", out var codesEl) && codesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in codesEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var c = TencentQuoteSource.ToCanonical(item.GetString() ?? "");
                    if (c.Length > 0)
                    {
                        cfg.Codes.Add(c);
                    }
                }
            }
        }

        // Legacy market-split files → flatten (order: sh, sz, bj, hk, us).
        if (cfg.Codes.Count == 0)
        {
            foreach (var market in new[] { "sh", "sz", "bj", "hk", "us" })
            {
                if (!root.TryGetProperty(market, out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var c = TencentQuoteSource.ToCanonical(market + "." + (item.GetString() ?? ""));
                    if (c.Length > 0)
                    {
                        cfg.Codes.Add(c);
                    }
                }
            }
        }

        cfg.Codes = cfg.Codes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        if (string.IsNullOrWhiteSpace(cfg.Source))
        {
            cfg.Source = QuoteSourceFactory.DefaultName;
        }

        return cfg.Codes.Count == 0 ? null : cfg;
    }

    static StockConfig CreateDefault() => new()
    {
        Source = QuoteSourceFactory.DefaultName,
        RefreshSeconds = 15,
        Codes =
        [
            "sh.588080",
            "sh.588760",
            "sh.600438",
            "sz.159632",
            "us.NDX",
        ],
    };
}
