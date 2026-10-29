using System.Runtime.InteropServices;
using System.Text;
using AShareWidget;
using AShareWidget.Com;
using AShareWidget.Quotes;
using WinRT;

namespace AShareWidget;

static class Program
{
    const string ComServerArg = "-RegisterProcessAsComServer";
    const string TestFetchArg = "-TestFetch";

    [DllImport("kernel32.dll")]
    static extern bool AllocConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    /// <summary>
    /// MSIX / sparse package identity. Packaged processes must NOT call
    /// DynamicDependency Bootstrap — the framework comes from AppxManifest PackageDependency.
    /// Calling Bootstrap inside a package commonly fails with "不支持该请求" / E_NOTIMPL.
    /// </summary>
    static bool IsPackaged()
    {
        int length = 0;
        const int ERROR_INSUFFICIENT_BUFFER = 122;
        int hr = GetCurrentPackageFullName(ref length, null);
        return hr == ERROR_INSUFFICIENT_BUFFER && length > 0;
    }

    [MTAThread]
    static void Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, TestFetchArg, StringComparison.OrdinalIgnoreCase)))
        {
            RunTestFetchAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Length == 0 || !args.Contains(ComServerArg, StringComparer.OrdinalIgnoreCase))
        {
            // Not launched by the widget host — exit quietly (WinExe).
            return;
        }

        var packaged = IsPackaged();
        ProviderLog.Info($"COM server start packaged={packaged} base={AppContext.BaseDirectory}");

        if (!packaged)
        {
            // Unpackaged only: pull in Windows App Runtime 1.6 via dynamic dependency.
            try
            {
                Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.Initialize(0x00010006);
                ProviderLog.Info("WASDK bootstrap ok (unpackaged)");
            }
            catch (Exception ex)
            {
                ProviderLog.Error("WASDK bootstrap failed (unpackaged)", ex);
                AllocConsole();
                Console.WriteLine("Windows App Runtime 1.6 bootstrap failed: " + ex.Message);
                Console.WriteLine("Install WASDK runtime, or use scripts/sideload.ps1 (MSIX).");
                Console.ReadLine();
                return;
            }
        }
        else
        {
            ProviderLog.Info("skip WASDK bootstrap (packaged — use PackageDependency)");
        }

        try
        {
            ComWrappersSupport.InitializeComWrappers();

            ClassObject.Register(
                Guid.Parse(ProviderIds.Clsid),
                new WidgetProviderFactory<QuoteWidgetProvider>(),
                out uint cookie);

            ProviderLog.Info("COM class registered, waiting for widgets");

            try
            {
                ClassObject.WaitWithComDispatch(
                    QuoteWidgetProvider.GetEmptyWidgetListEvent());
            }
            finally
            {
                ClassObject.Revoke(cookie);
                ProviderLog.Info("COM class revoked");
            }
        }
        catch (Exception ex)
        {
            ProviderLog.Error("COM server fatal", ex);
            // Don't pop a console dialog for packaged activation — host would look "hung".
            if (!packaged)
            {
                AllocConsole();
                Console.WriteLine(ex);
                Console.ReadLine();
            }
        }
    }

    static async Task RunTestFetchAsync()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "test-fetch.log");
        try { AllocConsole(); } catch { /* ignore */ }

        void Log(string line)
        {
            try { Console.WriteLine(line); } catch { /* no console */ }
            File.AppendAllText(logPath, line + Environment.NewLine);
        }

        if (File.Exists(logPath))
        {
            File.Delete(logPath);
        }

        try
        {
            var cfg = StockConfig.Load();
            Log("codes: " + string.Join(", ", cfg.Codes));
            IQuoteService service = new QuoteService();
            var quotes = await service.FetchAsync(cfg.Codes);
            if (quotes.Count == 0)
            {
                Log("no quotes returned");
                return;
            }

            foreach (var q in quotes)
            {
                if (q.Error is not null)
                {
                    Log($"ERROR {q.Key}: {q.Error}");
                    continue;
                }
                Log(
                    $"{q.Name} ({q.Key})  {q.Price:0.0000}  {q.Change:+0.00;-0.00;0.00}  {q.ChangePct:+0.00;-0.00;0.00}%  {q.QuoteTime}");
            }

            Log("OK -> " + logPath);
        }
        catch (Exception ex)
        {
            Log("error: " + ex);
        }
    }
}
