using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets;
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
    public WidgetPageState Page { get; } = new();
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
                var rows = RowsPerPage(ctx.Size);
                var state = info.CustomState;
                lock (LifecycleGate)
                {
                    // A Delete callback can arrive while GetWidgetInfos is in flight.
                    if (DeletedWidgets.Contains(id))
                        continue;
                    var restored = RunningWidgets.GetOrAdd(id, new CompactWidgetInfo
                    {
                        WidgetId = id,
                        DefinitionId = definition,
                        IsActive = false,
                    });
                    lock (restored.UpdateGate)
                    {
                        var previousPage = restored.Page.Index;
                        bool hasQuotes;
                        lock (ContentGate)
                        {
                            hasQuotes = _lastQuotes.Count > 0;
                            restored.Page.Resize(rows, _lastQuotes.Count);
                        }
                        restored.Page.Restore(state);
                        if (hasQuotes && restored.Page.Index != previousPage)
                            _ = Task.Run(() => UpdateWidget(restored));
                    }
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
        info.Page.Resize(RowsPerPage(widgetContext.Size), 0);
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

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        var delta = actionInvokedArgs.Verb switch
        {
            "page.previous" => -1,
            "page.next" => 1,
            _ => 0,
        };
        if (delta == 0) return;
        var id = actionInvokedArgs.WidgetContext.Id;
        if (!RunningWidgets.TryGetValue(id, out var info)) return;
        lock (info.UpdateGate)
        {
            lock (ContentGate)
            {
                // After a process restart, leave the cached page intact until
                // activation has reloaded quotes. Paging itself never fetches.
                if (_lastQuotes.Count == 0 || !info.Page.Move(delta, _lastQuotes.Count)) return;
            }
        }
        _ = Task.Run(() => UpdateWidget(info));
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        var context = contextChangedArgs.WidgetContext;
        if (RunningWidgets.TryGetValue(context.Id, out var info))
            ResizePage(info, RowsPerPage(context.Size));
    }

    static int RowsPerPage(WidgetSize size) => size switch
    {
        WidgetSize.Small => 2,
        WidgetSize.Large => 9,
        _ => 5,
    };

    static void ResizePage(CompactWidgetInfo info, int rows)
    {
        bool changed;
        bool hasQuotes;
        lock (info.UpdateGate)
        {
            lock (ContentGate)
            {
                hasQuotes = _lastQuotes.Count > 0;
                changed = info.Page.Resize(rows, _lastQuotes.Count);
            }
        }
        if (changed && hasQuotes)
            _ = Task.Run(() => UpdateWidget(info));
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
        ResizePage(info, RowsPerPage(widgetContext.Size));
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
                    template = WidgetCard.Build(_lastQuotes, _statusLine, _lastError, info.Page);
                if (template == info.LastTemplate)
                    return;
                // Pre-expand Adaptive Card in-process. Host-side ${} templating + dynamic
                // color bindings have been observed to render a blank Widgets Board card.
                var options = new WidgetUpdateRequestOptions(info.WidgetId)
                {
                    Template = template,
                    Data = "{}",
                    CustomState = info.Page.Save(),
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


    static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
