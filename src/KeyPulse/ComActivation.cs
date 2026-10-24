using System;
using System.Runtime.InteropServices;

namespace KeyPulse;

/// <summary>
/// Raw COM activation P/Invokes. There is deliberately no typed CreateInstance&lt;T&gt;: it needed
/// built-in COM interop (RCWs), which NativeAOT does not have. COM objects are used through
/// vtable calls instead (DesktopOrganizerService.Shell.cs, TaskSchedulerCom.cs, SetupWindow.cs).
/// </summary>
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
}
