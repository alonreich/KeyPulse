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

    public static T CreateInstance<T>(Guid clsid, uint dwClsContext = 5 /* CLSCTX_SERVER */) where T : class
    {
        var iid = typeof(T).GUID;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, dwClsContext, ref iid, out IntPtr ppv);
        if (hr != 0 || ppv == IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
        var obj = (T)Marshal.GetObjectForIUnknown(ppv);
        Marshal.Release(ppv);
        return obj;
    }
}
