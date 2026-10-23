using System;
using System.Runtime.InteropServices;

namespace KeyPulse;

internal static class ComActivation
{
    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(
        [In] ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        [In] ref Guid riid,
        out IntPtr ppv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    private const uint COINIT_MULTITHREADED = 0x0;

    public static T CreateInstance<T>(Guid clsid, uint dwClsContext = 5 /* CLSCTX_SERVER */) where T : class
    {
        var iid = typeof(T).GUID;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, dwClsContext, ref iid, out IntPtr ppv);
        if (hr == unchecked((int)0x800401F0))
        {
            CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            hr = CoCreateInstance(ref clsid, IntPtr.Zero, dwClsContext, ref iid, out ppv);
        }

        if (hr != 0 || ppv == IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
        var obj = (T)Marshal.GetObjectForIUnknown(ppv);
        Marshal.Release(ppv);
        return obj;
    }
}
