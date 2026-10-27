// COM class-factory boilerplate adapted from Microsoft Learn:
// https://learn.microsoft.com/windows/apps/develop/widgets/implement-widget-provider-cs
// The MTA server also pumps Win32 messages for runtime-created windows.
using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace AShareWidget.Com;

static class Guids
{
    public const string IClassFactory = "00000001-0000-0000-C000-000000000046";
    public const string IUnknown = "00000000-0000-0000-C000-000000000046";
}

[ComImport]
[ComVisible(false)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid(Guids.IClassFactory)]
interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

    [PreserveSig]
    int LockServer(bool fLock);
}

/// <summary>
/// Singleton factory — host may CoCreate repeatedly; one provider instance shares state.
/// </summary>
[ComVisible(true)]
sealed class WidgetProviderFactory<T> : IClassFactory
    where T : IWidgetProvider, new()
{
    const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    static readonly object Gate = new();
    static IWidgetProvider? _instance;

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;

        if (pUnkOuter != IntPtr.Zero)
        {
            return CLASS_E_NOAGGREGATION;
        }

        try
        {
            lock (Gate)
            {
                _instance ??= new T();
            }

            var unknown = MarshalInspectable<IWidgetProvider>.FromManaged(_instance);
            try
            {
                // riid is an interface IID, not the provider's class CLSID.
                int hr = Marshal.QueryInterface(unknown, ref riid, out ppvObject);
                ProviderLog.Info($"CreateInstance iid={riid} hr=0x{hr:X8}");
                return hr;
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }
        catch (Exception ex)
        {
            ProviderLog.Error("IClassFactory.CreateInstance", ex);
            return unchecked((int)0x80004005); // E_FAIL
        }
    }

    int IClassFactory.LockServer(bool fLock) => 0;
}

static class ClassObject
{
    [StructLayout(LayoutKind.Sequential)]
    struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint MsgWaitForMultipleObjectsEx(
        uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref NativeMessage message);

    public static void Register(Guid clsid, object punk, out uint cookie)
    {
        [DllImport("ole32.dll")]
        static extern int CoRegisterClassObject(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
            [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
            uint dwClsContext,
            uint flags,
            out uint lpdwRegister);

        // CLSCTX_LOCAL_SERVER + REGCLS_MULTIPLEUSE (same as Microsoft sample).
        const uint CLSCTX_LOCAL_SERVER = 0x4;
        const uint REGCLS_MULTIPLEUSE = 0x1;
        int hr = CoRegisterClassObject(
            clsid,
            punk,
            CLSCTX_LOCAL_SERVER,
            REGCLS_MULTIPLEUSE,
            out cookie);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public static void Revoke(uint cookie)
    {
        [DllImport("ole32.dll")]
        static extern int CoRevokeClassObject(uint dwRegister);

        CoRevokeClassObject(cookie);
    }

    /// <summary>
    /// Pump runtime-created windows as well as waiting for shutdown. CoWait on an
    /// MTA thread is only a kernel wait, even with COWAIT_DISPATCH_* flags.
    /// </summary>
    public static void WaitWithComDispatch(WaitHandle handle)
    {
        const uint QS_ALLINPUT = 0x04FF;
        const uint MWMO_INPUTAVAILABLE = 0x0004;
        const uint INFINITE = 0xFFFFFFFF;
        var handles = new[] { handle.SafeWaitHandle.DangerousGetHandle() };
        while (true)
        {
            uint result = MsgWaitForMultipleObjectsEx(1, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (result == 0)
                return;
            if (result == uint.MaxValue)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
            {
                if (message.Message == 0x0012) // WM_QUIT
                    return;
                TranslateMessage(ref message);
                DispatchMessage(ref message);
                if (handle.WaitOne(0))
                    return;
            }
        }
    }
}
