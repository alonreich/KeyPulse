using System;
using System.Runtime.InteropServices;

namespace KeyPulse
{
    public static partial class DesktopOrganizerService
    {
        /// <summary>
        /// NativeAOT-safe access to the desktop's shell view.
        ///
        /// The old path used ComImport-attributed interfaces and runtime-created RCWs.
        /// Built-in COM interop does not exist under NativeAOT, so every release build logged "COM Interop
        /// requires ComWrapper instance registered for marshalling" and Save / Restore layout failed;
        /// it only worked under the JIT (tests, dev.cmd). Every call here goes through the interface's
        /// vtable with blittable arguments - the same technique SetupWindow uses for IShellLinkW - so
        /// no runtime COM marshalling is involved. Slot numbers come from the Windows SDK headers
        /// (ExDisp.h, ShObjIdl_core.h). Must be used on the STA worker (RunInSta).
        /// </summary>
        private sealed unsafe class DesktopShellView : IDisposable
        {
            private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
            private static readonly Guid IID_IShellWindows = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");
            private static readonly Guid IID_IServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
            private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
            private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
            private static readonly Guid IID_IFolderView = new("cde725b0-ccc9-4519-917e-325d72fab4ce");
            private static readonly Guid IID_IFolderView2Guid = new("1af3a467-214f-4298-908e-06b03e0b39f9");

            private const int ShellWindows_FindWindowSW = 15;
            private const int ServiceProvider_QueryService = 3;
            private const int ShellBrowser_QueryActiveShellView = 15;
            private const int ShellView_Refresh = 8;
            private const int FolderView_Item = 6;
            private const int FolderView_ItemCount = 7;
            private const int FolderView_GetItemPosition = 11;
            private const int FolderView_GetSpacing = 12;
            private const int FolderView_GetDefaultSpacing = 13;
            private const int FolderView_GetAutoArrange = 14;
            private const int FolderView_SelectAndPositionItems = 16;

            private const int SWC_DESKTOP = 8;
            private const int SWFO_NEEDDISPATCH = 1;
            private const ushort VT_I4 = 3;
            private const uint CLSCTX_ALL = 0x17;

            [StructLayout(LayoutKind.Explicit, Size = 24)]
            private struct VARIANT
            {
                [FieldOffset(0)] public ushort vt;
                [FieldOffset(8)] public int intVal;
            }

            public IntPtr ShellView { get; private set; }
            public IntPtr FolderView { get; private set; }
            public IntPtr FolderView2 { get; private set; }
            public IntPtr DesktopHwnd { get; private set; }

            private DesktopShellView() { }

            public static DesktopShellView? TryOpen(bool wantFolderView2, out string error)
            {
                error = string.Empty;
                IntPtr shellWindows = IntPtr.Zero, disp = IntPtr.Zero, serviceProvider = IntPtr.Zero, browser = IntPtr.Zero;
                var view = new DesktopShellView();
                try
                {
                    var clsid = CLSID_ShellWindows;
                    var iid = IID_IShellWindows;
                    int hr = ComActivation.CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out shellWindows);
                    if (hr != 0 || shellWindows == IntPtr.Zero) { error = $"ShellWindows unavailable (hr=0x{hr:X8})"; return null; }

                    var loc = new VARIANT { vt = VT_I4, intVal = 0 }; // CSIDL_DESKTOP
                    var root = new VARIANT();                         // VT_EMPTY
                    int hwnd = 0;
                    var find = (delegate* unmanaged[Stdcall]<IntPtr, VARIANT*, VARIANT*, int, int*, int, IntPtr*, int>)Slot(shellWindows, ShellWindows_FindWindowSW);
                    hr = find(shellWindows, &loc, &root, SWC_DESKTOP, &hwnd, SWFO_NEEDDISPATCH, &disp);
                    if (hr != 0 || disp == IntPtr.Zero) { error = $"Desktop window not found (hr=0x{hr:X8})"; return null; }
                    view.DesktopHwnd = (IntPtr)hwnd;

                    var iidSp = IID_IServiceProvider;
                    hr = Marshal.QueryInterface(disp, in iidSp, out serviceProvider);
                    if (hr != 0 || serviceProvider == IntPtr.Zero) { error = $"IServiceProvider unavailable (hr=0x{hr:X8})"; return null; }

                    var sid = SID_STopLevelBrowser;
                    var iidBrowser = IID_IShellBrowser;
                    var queryService = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)Slot(serviceProvider, ServiceProvider_QueryService);
                    hr = queryService(serviceProvider, &sid, &iidBrowser, &browser);
                    if (hr != 0 || browser == IntPtr.Zero) { error = $"IShellBrowser unavailable (hr=0x{hr:X8})"; return null; }

                    IntPtr shellView = IntPtr.Zero;
                    var queryView = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(browser, ShellBrowser_QueryActiveShellView);
                    hr = queryView(browser, &shellView);
                    if (hr != 0 || shellView == IntPtr.Zero) { error = $"Active shell view unavailable (hr=0x{hr:X8})"; return null; }
                    view.ShellView = shellView;

                    var iidFv = IID_IFolderView;
                    hr = Marshal.QueryInterface(shellView, in iidFv, out var folderView);
                    if (hr != 0 || folderView == IntPtr.Zero) { error = $"IFolderView unavailable (hr=0x{hr:X8})"; return null; }
                    view.FolderView = folderView;

                    if (wantFolderView2)
                    {
                        var iidFv2 = IID_IFolderView2Guid;
                        if (Marshal.QueryInterface(shellView, in iidFv2, out var folderView2) == 0) view.FolderView2 = folderView2;
                    }

                    var result = view;
                    view = null!;
                    return result;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return null;
                }
                finally
                {
                    view?.Dispose();
                    if (browser != IntPtr.Zero) Marshal.Release(browser);
                    if (serviceProvider != IntPtr.Zero) Marshal.Release(serviceProvider);
                    if (disp != IntPtr.Zero) Marshal.Release(disp);
                    if (shellWindows != IntPtr.Zero) Marshal.Release(shellWindows);
                }
            }

            private static IntPtr Slot(IntPtr comObject, int index) => (*(IntPtr**)comObject)[index];

            private static void ThrowIfFailed(int hr, string what)
            {
                if (hr < 0) throw new COMException($"{what} failed (hr=0x{hr:X8})", hr);
            }

            public int ItemCount()
            {
                int count = 0;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int*, int>)Slot(FolderView, FolderView_ItemCount);
                ThrowIfFailed(fn(FolderView, 0 /* SVGIO_BACKGROUND */, &count), "IFolderView.ItemCount");
                return count;
            }

            /// <summary>Child PIDL of item <paramref name="index"/>; free it with CoTaskMemFree.</summary>
            public IntPtr Item(int index)
            {
                IntPtr pidl = IntPtr.Zero;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Slot(FolderView, FolderView_Item);
                return fn(FolderView, index, &pidl) == 0 ? pidl : IntPtr.Zero;
            }

            public POINT GetItemPosition(IntPtr pidl)
            {
                var pt = new POINT();
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, POINT*, int>)Slot(FolderView, FolderView_GetItemPosition);
                ThrowIfFailed(fn(FolderView, pidl, &pt), "IFolderView.GetItemPosition");
                return pt;
            }

            public POINT GetSpacing()
            {
                var pt = new POINT();
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, POINT*, int>)Slot(FolderView, FolderView_GetSpacing);
                if (fn(FolderView, &pt) != 0 || pt.X <= 0 || pt.Y <= 0)
                {
                    pt = new POINT();
                    var def = (delegate* unmanaged[Stdcall]<IntPtr, POINT*, int>)Slot(FolderView, FolderView_GetDefaultSpacing);
                    def(FolderView, &pt);
                }
                return pt;
            }

            /// <summary>S_OK (0) means Windows "Auto arrange icons" is on.</summary>
            public bool IsAutoArrangeOn()
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(FolderView, FolderView_GetAutoArrange);
                return fn(FolderView) == 0;
            }

            public void PositionItem(IntPtr pidl, POINT pt)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, POINT*, uint, int>)Slot(FolderView, FolderView_SelectAndPositionItems);
                ThrowIfFailed(fn(FolderView, 1, &pidl, &pt, 0), "IFolderView.SelectAndPositionItems");
            }

            public void Refresh()
            {
                if (ShellView == IntPtr.Zero) return;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(ShellView, ShellView_Refresh);
                fn(ShellView);
            }

            public void Dispose()
            {
                if (FolderView2 != IntPtr.Zero) { Marshal.Release(FolderView2); FolderView2 = IntPtr.Zero; }
                if (FolderView != IntPtr.Zero) { Marshal.Release(FolderView); FolderView = IntPtr.Zero; }
                if (ShellView != IntPtr.Zero) { Marshal.Release(ShellView); ShellView = IntPtr.Zero; }
            }
        }
    }
}
