using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Windows.Widgets.Providers;
using AShareWidget.Quotes;

namespace AShareWidget;

public static class ProviderIds
{
    public const string Clsid = "B2C4D6E8-1A3F-4B5C-9D7E-8F0A1B2C3D4E";
    public const string DefinitionId = "AShareWidget_Quote";
}

sealed class CompactWidgetInfo
{
    public required string WidgetId { get; init; }
    public required string DefinitionId { get; init; }
    public bool IsActive { get; set; }
    public object UpdateGate { get; } = new();
    public string? LastTemplate { get; set; }
}

[ComVisible(true)]
[Guid(ProviderIds.Clsid)]
sealed class QuoteWidgetProvider : IWidgetProvider
{
    static readonly ConcurrentDictionary<string, CompactWidgetInfo> RunningWidgets = new();
    // The message loop waits on this native event alongside Windows messages.
    static readonly ManualResetEvent EmptyWidgetListEvent = new(false);
    static readonly object LifecycleGate = new();
    static readonly object ContentGate = new();
    static readonly HashSet<string> DeletedWidgets = new();
    static StockConfig _config = new();
    static readonly Lazy<IQuoteSource> Source = new(CreateSource);

    static readonly RefreshScheduler Refresh = new(
        () => TimeSpan.FromSeconds(Config.RefreshSeconds), RefreshAndPushAsync);
    static IReadOnlyList<Quote> _lastQuotes = [];
    static string _statusLine = "加载中…";
    static string _lastError = "";
    static DateTimeOffset? _lastOk;
    /// <summary>Bumps to cancel a pending process-exit after the last widget is removed.</summary>
    static int _exitGeneration;

    static StockConfig Config => _config;

    static IQuoteSource CreateSource()
    {
        try
        {
            var src = QuoteSourceFactory.Create(_config.Source);
            var codes = _config.ResolveTencentCodes();
            ProviderLog.Info(
                $"quote source={src.Name} ({src.DisplayName}), codes={string.Join(',', codes)}, config={StockConfig.UserConfigPath}");
            return src;
        }
        catch (Exception ex)
        {
            ProviderLog.Error("CreateSource failed, fallback tencent", ex);
            return QuoteSourceFactory.Create(QuoteSourceFactory.DefaultName);
        }
    }

    public QuoteWidgetProvider()
    {
        ProviderLog.Info($"ctor log={ProviderLog.PathOnDisk}");
        // Defer host RPC and stock configuration I/O until after construction.
        _ = Task.Run(RestoreWidgets);
    }

    static void RestoreWidgets()
    {
        try
        {
            foreach (var info in WidgetManager.GetDefault().GetWidgetInfos())
            {
                var ctx = info.WidgetContext;
                var id = ctx.Id;
                var definition = ctx.DefinitionId;
                lock (LifecycleGate)
                {
                    // A Delete callback can arrive while GetWidgetInfos is in flight.
                    if (DeletedWidgets.Contains(id))
                        continue;
                    RunningWidgets.TryAdd(id, new CompactWidgetInfo
                    {
                        WidgetId = id,
                        DefinitionId = definition,
                        IsActive = false,
                    });
                    Interlocked.Increment(ref _exitGeneration);
                    EmptyWidgetListEvent.Reset();
                }
            }

            ProviderLog.Info($"restored widgets={RunningWidgets.Count}");
            // Existing cards already belong to Windows. Restore identity only;
            // do not replace their content with a loading card on process restart.
        }
        catch (Exception ex)
        {
            ProviderLog.Error("GetWidgetInfos", ex);
        }
    }

    public static ManualResetEvent GetEmptyWidgetListEvent() => EmptyWidgetListEvent;

    public void CreateWidget(WidgetContext widgetContext)
    {
        ProviderLog.Info($"CreateWidget id={widgetContext.Id} def={widgetContext.DefinitionId}");
        var info = new CompactWidgetInfo
        {
            WidgetId = widgetContext.Id,
            DefinitionId = widgetContext.DefinitionId,
            IsActive = true,
        };
        lock (LifecycleGate)
        {
            Interlocked.Increment(ref _exitGeneration);
            EmptyWidgetListEvent.Reset();
            DeletedWidgets.Remove(info.WidgetId);
            info = RunningWidgets.GetOrAdd(info.WidgetId, info);
            info.IsActive = true;
        }
        QueueRefresh(info, initialCreation: true);
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        _ = customState;
        ProviderLog.Info($"DeleteWidget id={widgetId}");
        int gen;
        lock (LifecycleGate)
        {
            DeletedWidgets.Add(widgetId);
            RunningWidgets.TryRemove(widgetId, out _);
            Refresh.SetActive(RunningWidgets.Values.Any(w => w.IsActive));
            if (!RunningWidgets.IsEmpty)
                return;
            gen = Interlocked.Increment(ref _exitGeneration);
        }

        // Allow the host to delete/recreate during pinning without a cold start.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                lock (LifecycleGate)
                {
                    if (gen == _exitGeneration && RunningWidgets.IsEmpty)
                    {
                        ProviderLog.Info("no widgets left — exiting COM server");
                        EmptyWidgetListEvent.Set();
                    }
                }
            }
            catch (Exception ex)
            {
                ProviderLog.Error("exit delay", ex);
            }
        });
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs) =>
        _ = actionInvokedArgs;

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        // The same card supports every declared size. A resize or monitor change
        // does not invalidate its content and does not require an update.
        _ = contextChangedArgs;
    }

    public void Activate(WidgetContext widgetContext)
    {
        // Copy callback values before scheduling work; WidgetContext is only valid
        // for the duration of this callback.
        var id = widgetContext.Id;
        var definition = widgetContext.DefinitionId;
        ProviderLog.Info($"Activate id={id}");
        CompactWidgetInfo info;
        lock (LifecycleGate)
        {
            Interlocked.Increment(ref _exitGeneration);
            EmptyWidgetListEvent.Reset();
            DeletedWidgets.Remove(id);
            info = RunningWidgets.GetOrAdd(id, _ => new CompactWidgetInfo
            {
                WidgetId = id,
                DefinitionId = definition,
                IsActive = true,
            });
            info.IsActive = true;
        }
        QueueRefresh(info);
    }

    static void QueueRefresh(CompactWidgetInfo info, bool initialCreation = false)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!RunningWidgets.TryGetValue(info.WidgetId, out var current) || !ReferenceEquals(current, info))
                    return;
                // Only a newly added card needs an initial submission. On Activate
                // Windows keeps the previously submitted card, including while HTTP runs.
                if (initialCreation)
                    UpdateWidget(info);
                _config = StockConfig.Load();
                lock (LifecycleGate)
                    Refresh.SetActive(RunningWidgets.Values.Any(w => w.IsActive));
                await Refresh.RequestAsync(initialCreation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ProviderLog.Error($"queued update id={info.WidgetId}", ex);
            }
        });
    }

    public void Deactivate(string widgetId)
    {
        lock (LifecycleGate)
        {
            if (RunningWidgets.TryGetValue(widgetId, out var info))
                info.IsActive = false;
            Refresh.SetActive(RunningWidgets.Values.Any(w => w.IsActive));
        }
    }

    static async Task RefreshAndPushAsync()
    {
        try
        {
            // Reload AppData stocks.json each tick so manual edits apply without reinstall.
            _config = StockConfig.Load();
            var codes = _config.ResolveTencentCodes();
            var quotes = await Source.Value.FetchAsync(codes).ConfigureAwait(false);
            lock (ContentGate)
            {
                if (quotes.Count > 0)
                {
                    _lastQuotes = quotes;
                    _lastError = "";
                    _lastOk = DateTimeOffset.Now;
                    _statusLine = _lastOk.Value.ToLocalTime().ToString("HH:mm:ss");
                    ProviderLog.Info($"fetch ok count={quotes.Count}");
                }
                else
                {
                    _lastError = "未拿到行情（检查代码或网络）";
                    _statusLine = _lastError;
                    ProviderLog.Error("fetch empty");
                }
            }
        }
        catch (Exception ex)
        {
            lock (ContentGate)
            {
                _lastError = "刷新失败: " + Truncate(ex.Message, 80);
                _statusLine = _lastError;
            }
            ProviderLog.Error("fetch", ex);
        }
        foreach (var info in RunningWidgets.Values.ToArray())
        {
            UpdateWidget(info);
        }
    }

    static void UpdateWidget(CompactWidgetInfo info)
    {
        if (!RunningWidgets.TryGetValue(info.WidgetId, out var current) || !ReferenceEquals(current, info))
            return;

        if (info.DefinitionId != ProviderIds.DefinitionId)
        {
            ProviderLog.Error($"skip unknown definition={info.DefinitionId}");
            return;
        }

        try
        {
            lock (info.UpdateGate)
            {
                string template;
                lock (ContentGate)
                    template = BuildCardJson();
                if (template == info.LastTemplate)
                    return;
                // Pre-expand Adaptive Card in-process. Host-side ${} templating + dynamic
                // color bindings have been observed to render a blank Widgets Board card.
                var options = new WidgetUpdateRequestOptions(info.WidgetId)
                {
                    Template = template,
                    Data = "{}",
                    CustomState = string.Empty,
                };
                WidgetManager.GetDefault().UpdateWidget(options);
                info.LastTemplate = template;
                ProviderLog.Info($"UpdateWidget id={info.WidgetId}");
            }
        }
        catch (Exception ex)
        {
            ProviderLog.Error($"UpdateWidget id={info.WidgetId}", ex);
        }
    }

    /// <summary>
    /// Fully materialised Adaptive Card 1.3 JSON (no Adaptive Templating placeholders).
    /// </summary>
    static string BuildCardJson()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(1024);
        sb.Append("""
            {"type":"AdaptiveCard","version":"1.3","$schema":"http://adaptivecards.io/schemas/adaptive-card.json","body":[
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

        if (_lastQuotes.Count == 0)
        {
            AppendComma();
            sb.Append("{\"type\":\"TextBlock\",\"text\":");
            AppendJsonString(sb, string.IsNullOrEmpty(_lastError) ? "等待行情…" : _lastError);
            sb.Append(",\"wrap\":true}");
        }
        else
        {
            foreach (var q in _lastQuotes)
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
                sb.Append("{\"type\":\"TextBlock\",\"weight\":\"Bolder\",\"wrap\":true,\"text\":");
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

        // Bottom-right time (avoid height:stretch — can blank the Widgets Board card).
        AppendComma();
        sb.Append("{\"type\":\"TextBlock\",\"text\":");
        AppendJsonString(sb, _statusLine);
        sb.Append(",\"isSubtle\":true,\"spacing\":\"Small\",\"size\":\"Small\",\"horizontalAlignment\":\"Right\",\"wrap\":true}");

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

    static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
