using System.Runtime.InteropServices;
using AShareWidget.Com;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

ComWrappersSupport.InitializeComWrappers();
var factory = new WidgetProviderFactory<TestProvider>();
foreach (var iid in new[] { Guid.Parse("00000000-0000-0000-C000-000000000046"), typeof(IWidgetProvider).GUID })
{
    var requested = iid;
    int hr = factory.CreateInstance(IntPtr.Zero, ref requested, out var pointer);
    Check(hr == 0 && pointer != IntPtr.Zero, $"CreateInstance supports {iid}");
    Marshal.Release(pointer);
}
var unsupported = Guid.NewGuid();
Check(factory.CreateInstance(IntPtr.Zero, ref unsupported, out var rejected) == unchecked((int)0x80004002)
    && rejected == IntPtr.Zero, "unsupported IID returns E_NOINTERFACE and null");
Check(factory.CreateInstance(new IntPtr(1), ref unsupported, out rejected) == unchecked((int)0x80040110)
    && rejected == IntPtr.Zero, "aggregation is rejected");
Check(TestProvider.Instances == 1, "repeated activation shares one provider");

// WM_QUIT must wake and terminate an idle MTA loop. The previous CoWait-based
// implementation blocked here because it did not process the message queue.
using var stop = new ManualResetEvent(false);
using var ready = new ManualResetEvent(false);
uint threadId = 0;
Exception? failure = null;
var thread = new Thread(() =>
{
    try
    {
        Native.PeekMessage(new byte[64], IntPtr.Zero, 0, 0, 0);
        threadId = Native.GetCurrentThreadId();
        ready.Set();
        ClassObject.WaitWithComDispatch(stop);
    }
    catch (Exception ex) { failure = ex; ready.Set(); }
}) { IsBackground = true };
thread.SetApartmentState(ApartmentState.MTA);
thread.Start();
Check(ready.WaitOne(3000) && failure is null, "MTA message queue starts");
try
{
    Check(Native.PostThreadMessage(threadId, 0x0012, UIntPtr.Zero, IntPtr.Zero), "post WM_QUIT");
    Check(thread.Join(3000) && failure is null, "MTA loop processes WM_QUIT while idle");
}
finally { stop.Set(); thread.Join(3000); }
ClassObject.WaitWithComDispatch(stop);
Console.WriteLine("PASS: signaled shutdown event exits immediately");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

[ComVisible(true)]
public sealed class TestProvider : IWidgetProvider
{
    public static int Instances;
    public TestProvider() => Instances++;
    public void CreateWidget(WidgetContext context) { }
    public void DeleteWidget(string id, string state) { }
    public void Activate(WidgetContext context) { }
    public void Deactivate(string id) { }
    public void OnActionInvoked(WidgetActionInvokedArgs args) { }
    public void OnWidgetContextChanged(WidgetContextChangedArgs args) { }
}

static class Native
{
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint id, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool PeekMessage([Out] byte[] message, IntPtr window, uint min, uint max, uint remove);
}

namespace AShareWidget
{
    // Activation checks must not write to the installed provider's user logs.
    static class ProviderLog
    {
        public static void Info(string message) => Console.WriteLine(message);
        public static void Error(string message, Exception? ex = null) => Console.WriteLine(message + ex);
    }
}
