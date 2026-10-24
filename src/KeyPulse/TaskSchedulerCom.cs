using System;
using System.Runtime.InteropServices;

namespace KeyPulse
{
    /// <summary>
    /// NativeAOT-safe Task Scheduler 2.0 access for the KeyPulse Administrator logon task.
    ///
    /// The old code used ComImport-attributed ITaskService / ITaskFolder / IRegisteredTask interfaces
    /// and runtime-created RCWs. Built-in COM interop does not exist under NativeAOT, so every release
    /// build logged "Task Scheduler COM query failed: COM Interop requires ComWrapper instance registered
    /// for marshalling" and silently fell back to schtasks.exe for everything. Every call here goes
    /// through the interface's vtable with blittable arguments (BSTR as IntPtr, VARIANT as a 24-byte
    /// struct passed by value, VARIANT_BOOL as short). Slot numbers come from the Windows SDK header
    /// taskschd.h (IUnknown 3 + IDispatch 4 come first). Callers keep schtasks.exe as the fallback.
    /// </summary>
    internal static unsafe class TaskSchedulerCom
    {
        private static readonly Guid CLSID_TaskScheduler = new("0f87369f-a4e5-4cfc-bd3e-73e6154572dd");
        private static readonly Guid IID_ITaskService = new("2FABA4C7-4DA9-4013-9697-20CC3FD40F85");

        private const int TaskService_GetFolder = 7;
        private const int TaskService_Connect = 10;
        private const int TaskFolder_GetTask = 13;
        private const int TaskFolder_DeleteTask = 15;
        private const int TaskFolder_RegisterTask = 16;
        private const int RegisteredTask_get_Enabled = 10;
        private const int RegisteredTask_put_Enabled = 11;
        private const int RegisteredTask_get_Xml = 20;

        private const uint CLSCTX_INPROC_SERVER = 0x1;
        private const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);
        private const uint COINIT_MULTITHREADED = 0x0;
        private const int TASK_CREATE_OR_UPDATE = 6;
        private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;
        private const short VARIANT_TRUE = -1;

        /// <summary>HRESULTs Task Scheduler returns for a task (or path) that does not exist.</summary>
        internal static bool IsNotFound(int hr) => unchecked((uint)hr) is 0x80070002 or 0x80070003 or 0x80041318;

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct VARIANT
        {
            [FieldOffset(0)] public ushort vt; // VT_EMPTY = 0: "not supplied"
        }

        private static IntPtr Slot(IntPtr comObject, int index) => (*(IntPtr**)comObject)[index];

        /// <summary>Connects to the local Task Scheduler and returns the root folder "\" (both AddRef'd).</summary>
        private static int OpenRootFolder(out IntPtr service, out IntPtr rootFolder)
        {
            service = IntPtr.Zero;
            rootFolder = IntPtr.Zero;

            var clsid = CLSID_TaskScheduler;
            var iid = IID_ITaskService;
            int hr = ComActivation.CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out service);
            if (hr == CO_E_NOTINITIALIZED)
            {
                ComActivation.CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
                hr = ComActivation.CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out service);
            }
            if (hr < 0 || service == IntPtr.Zero) return hr < 0 ? hr : unchecked((int)0x80004005);

            var empty = new VARIANT();
            var connect = (delegate* unmanaged[Stdcall]<IntPtr, VARIANT, VARIANT, VARIANT, VARIANT, int>)Slot(service, TaskService_Connect);
            hr = connect(service, empty, empty, empty, empty);
            if (hr < 0) return hr;

            var path = Marshal.StringToBSTR(@"\");
            try
            {
                IntPtr folder = IntPtr.Zero;
                var getFolder = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(service, TaskService_GetFolder);
                hr = getFolder(service, path, &folder);
                rootFolder = folder;
                return hr;
            }
            finally
            {
                Marshal.FreeBSTR(path);
            }
        }

        private static int GetTask(IntPtr rootFolder, string taskName, out IntPtr task)
        {
            task = IntPtr.Zero;
            var name = Marshal.StringToBSTR(taskName);
            try
            {
                IntPtr t = IntPtr.Zero;
                var getTask = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(rootFolder, TaskFolder_GetTask);
                int hr = getTask(rootFolder, name, &t);
                task = t;
                return hr;
            }
            finally
            {
                Marshal.FreeBSTR(name);
            }
        }

        private static void Release(ref IntPtr p)
        {
            if (p != IntPtr.Zero) { Marshal.Release(p); p = IntPtr.Zero; }
        }

        /// <summary>Reads the task's XML and enabled flag. <paramref name="notFound"/> is set only when Windows says it does not exist.</summary>
        public static bool TryGetTask(string taskName, out string? xml, out bool isEnabled, out bool notFound, out string error)
        {
            xml = null;
            isEnabled = false;
            notFound = false;
            error = string.Empty;
            IntPtr service = IntPtr.Zero, folder = IntPtr.Zero, task = IntPtr.Zero;
            try
            {
                int hr = OpenRootFolder(out service, out folder);
                if (hr < 0) { error = $"connect failed (hr=0x{hr:X8})"; return false; }

                hr = GetTask(folder, taskName, out task);
                if (IsNotFound(hr)) { notFound = true; return false; }
                if (hr < 0 || task == IntPtr.Zero) { error = $"GetTask failed (hr=0x{hr:X8})"; return false; }

                short enabled = 0;
                var getEnabled = (delegate* unmanaged[Stdcall]<IntPtr, short*, int>)Slot(task, RegisteredTask_get_Enabled);
                hr = getEnabled(task, &enabled);
                if (hr < 0) { error = $"get_Enabled failed (hr=0x{hr:X8})"; return false; }

                IntPtr bstr = IntPtr.Zero;
                var getXml = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(task, RegisteredTask_get_Xml);
                hr = getXml(task, &bstr);
                try
                {
                    if (hr < 0) { error = $"get_Xml failed (hr=0x{hr:X8})"; return false; }
                    xml = bstr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringBSTR(bstr);
                }
                finally
                {
                    if (bstr != IntPtr.Zero) Marshal.FreeBSTR(bstr);
                }

                isEnabled = enabled != 0;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                Release(ref task);
                Release(ref folder);
                Release(ref service);
            }
        }

        /// <summary>Creates or replaces the task from XML, running with the interactive user's token.</summary>
        public static bool TryRegisterTask(string taskName, string xmlText, out string error)
        {
            error = string.Empty;
            IntPtr service = IntPtr.Zero, folder = IntPtr.Zero, task = IntPtr.Zero;
            IntPtr name = IntPtr.Zero, xml = IntPtr.Zero;
            try
            {
                int hr = OpenRootFolder(out service, out folder);
                if (hr < 0) { error = $"connect failed (hr=0x{hr:X8})"; return false; }

                name = Marshal.StringToBSTR(taskName);
                xml = Marshal.StringToBSTR(xmlText);
                var empty = new VARIANT();
                IntPtr registered = IntPtr.Zero;
                var register = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int, VARIANT, VARIANT, int, VARIANT, IntPtr*, int>)Slot(folder, TaskFolder_RegisterTask);
                hr = register(folder, name, xml, TASK_CREATE_OR_UPDATE, empty, empty, TASK_LOGON_INTERACTIVE_TOKEN, empty, &registered);
                task = registered;
                if (hr < 0 || task == IntPtr.Zero) { error = $"RegisterTask failed (hr=0x{hr:X8})"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (name != IntPtr.Zero) Marshal.FreeBSTR(name);
                if (xml != IntPtr.Zero) Marshal.FreeBSTR(xml);
                Release(ref task);
                Release(ref folder);
                Release(ref service);
            }
        }

        /// <summary>Deletes the task. A task that is already gone counts as deleted.</summary>
        public static bool TryDeleteTask(string taskName, out string error)
        {
            error = string.Empty;
            IntPtr service = IntPtr.Zero, folder = IntPtr.Zero, name = IntPtr.Zero;
            try
            {
                int hr = OpenRootFolder(out service, out folder);
                if (hr < 0) { error = $"connect failed (hr=0x{hr:X8})"; return false; }

                name = Marshal.StringToBSTR(taskName);
                var delete = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)Slot(folder, TaskFolder_DeleteTask);
                hr = delete(folder, name, 0);
                if (hr >= 0 || IsNotFound(hr)) return true;
                error = $"DeleteTask failed (hr=0x{hr:X8})";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (name != IntPtr.Zero) Marshal.FreeBSTR(name);
                Release(ref folder);
                Release(ref service);
            }
        }

        /// <summary>Turns an existing task on or off.</summary>
        public static bool TrySetEnabled(string taskName, bool enabled, out string error)
        {
            error = string.Empty;
            IntPtr service = IntPtr.Zero, folder = IntPtr.Zero, task = IntPtr.Zero;
            try
            {
                int hr = OpenRootFolder(out service, out folder);
                if (hr < 0) { error = $"connect failed (hr=0x{hr:X8})"; return false; }

                hr = GetTask(folder, taskName, out task);
                if (hr < 0 || task == IntPtr.Zero) { error = $"GetTask failed (hr=0x{hr:X8})"; return false; }

                var putEnabled = (delegate* unmanaged[Stdcall]<IntPtr, short, int>)Slot(task, RegisteredTask_put_Enabled);
                hr = putEnabled(task, enabled ? VARIANT_TRUE : (short)0);
                if (hr < 0) { error = $"put_Enabled failed (hr=0x{hr:X8})"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                Release(ref task);
                Release(ref folder);
                Release(ref service);
            }
        }
    }
}
