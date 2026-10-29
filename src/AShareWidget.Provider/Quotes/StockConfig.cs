using System.Text.Json;
using System.Text.Json.Serialization;

namespace AShareWidget.Quotes;

public sealed class StockConfig
{
    [JsonPropertyName("refreshSeconds")]
    public int RefreshSeconds { get; set; } = 15;
    [JsonPropertyName("codes")]
    public List<string> Codes { get; set; } = [];
    public static string UserConfigPath => Path.Combine(AppPaths.UserDataDirectory, "stocks.json");
    public static string UserDataDirectory => AppPaths.UserDataDirectory;
    public static StockConfig Load()
    {
        EnsureUserConfig();
        // Do not silently turn malformed or obsolete input into a different list.
        return Parse(File.ReadAllText(UserConfigPath));
    }
    public static void EnsureUserConfig()
    {
        if (File.Exists(UserConfigPath)) return;
        var config = Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "stocks.json")));
        File.WriteAllText(UserConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static StockConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("配置必须为 JSON 对象。");
        foreach (var property in root.EnumerateObject())
            if (property.Name is not ("refreshSeconds" or "codes"))
                throw new FormatException($"不支持配置字段“{property.Name}”。请使用 数据源:原生代码，删除旧 source 字段。");
        var config = new StockConfig();
        if (root.TryGetProperty("refreshSeconds", out var refresh))
        {
            if (refresh.ValueKind != JsonValueKind.Number || !refresh.TryGetInt32(out var seconds) || seconds is < 5 or > 300)
                throw new FormatException("refreshSeconds 必须是 5 到 300 之间的整数。");
            config.RefreshSeconds = seconds;
        }
        if (!root.TryGetProperty("codes", out var codes) || codes.ValueKind != JsonValueKind.Array)
            throw new FormatException("配置需要 codes 数组。");
        if (codes.GetArrayLength() > 12) throw new FormatException("目前最多支持 12 个关注项。");
        foreach (var code in codes.EnumerateArray())
        {
            if (code.ValueKind != JsonValueKind.String) throw new FormatException("codes 中的每一项必须为字符串。");
            var value = code.GetString()!;
            var request = QuoteRequest.Parse(value);
            if (request.Source is not ("tencent" or "sina")) throw new FormatException($"未知数据源“{request.Source}”。");
            config.Codes.Add(value);
        }
        return config;
    }
}
