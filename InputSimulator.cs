using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyPulse
{
    public static class InputSimulator
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
            public static int Size => Marshal.SizeOf<INPUT>();
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_V = 0x56;

        public static bool TypeText(string text, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrEmpty(text)) return true;
            var inputsList = new System.Collections.Generic.List<INPUT>();

            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            bool lwin = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0;
            bool rwin = (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

            if (shift) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_SHIFT, dwFlags = KEYEVENTF_KEYUP } } });
            if (alt) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = KEYEVENTF_KEYUP } } });
            if (lwin) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_LWIN, dwFlags = KEYEVENTF_KEYUP } } });
            if (rwin) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_RWIN, dwFlags = KEYEVENTF_KEYUP } } });
            if (ctrl) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } });

            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = text[i], dwFlags = KEYEVENTF_UNICODE } } });
                    inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = text[i + 1], dwFlags = KEYEVENTF_UNICODE } } });
                    inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = text[i], dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
                    inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = text[i + 1], dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
                    i++;
                }
                else
                {
                    inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = text[i], dwFlags = KEYEVENTF_UNICODE } } });
                    inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = text[i], dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
                }
            }

            if (shift && (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_SHIFT, dwFlags = 0 } } });
            if (alt && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = 0 } } });
            if (lwin && (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_LWIN, dwFlags = 0 } } });
            if (rwin && (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_RWIN, dwFlags = 0 } } });
            if (ctrl && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 } } });

            var inputs = inputsList.ToArray();
            var sent = SendInput((uint)inputs.Length, inputs, INPUT.Size);
            if (sent != inputs.Length)
            {
                error = $"Windows accepted {sent} of {inputs.Length} text input events.";
                return false;
            }

            return true;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr data);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVABLE = 0x0002;
        private const uint GMEM_ZEROINIT = 0x0040;
        private const ulong MaxClipboardBackupBytes = 64UL * 1024 * 1024;

        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_LWIN = 0x5B;
        private const ushort VK_RWIN = 0x5C;

        private sealed class ClipboardBackup
        {
            public Dictionary<uint, byte[]> Formats { get; } = new();
        }

        private static string? GetWin32ClipboardText()
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            if (!SafeOpenClipboard()) return null;
            
            string? result = null;
            IntPtr hGlobal = GetClipboardData(CF_UNICODETEXT);
            if (hGlobal != IntPtr.Zero)
            {
                IntPtr source = GlobalLock(hGlobal);
                if (source != IntPtr.Zero)
                {
                    result = Marshal.PtrToStringUni(source);
                    GlobalUnlock(hGlobal);
                }
            }
            CloseClipboard();
            return result;
        }

        [DllImport("kernel32.dll")]
        private static extern UIntPtr GlobalSize(IntPtr hMem);

        [DllImport("user32.dll")]
        private static extern uint EnumClipboardFormats(uint format);

        private static bool SafeOpenClipboard()
        {
            for (int i = 0; i < 10; i++)
            {
                if (OpenClipboard(IntPtr.Zero)) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        private static bool TryBackupClipboardAll(out ClipboardBackup backup)
        {
            backup = new ClipboardBackup();
            if (!SafeOpenClipboard()) return false;

            try
            {
                ulong totalBytes = 0;
                uint format = 0;
                while ((format = EnumClipboardFormats(format)) != 0)
                {
                    IntPtr hData = GetClipboardData(format);
                    if (hData == IntPtr.Zero) return false;

                    ulong size = GlobalSize(hData).ToUInt64();
                    if (size == 0 || size > int.MaxValue || totalBytes + size > MaxClipboardBackupBytes)
                    {
                        return false;
                    }

                    IntPtr source = GlobalLock(hData);
                    if (source == IntPtr.Zero) return false;

                    try
                    {
                        var buffer = new byte[(int)size];
                        Marshal.Copy(source, buffer, 0, buffer.Length);
                        backup.Formats[format] = buffer;
                        totalBytes += size;
                    }
                    finally
                    {
                        GlobalUnlock(hData);
                    }
                }

                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        private static bool SetWin32ClipboardText(string text)
        {
            if (!SafeOpenClipboard()) return false;

            try
            {
                if (!EmptyClipboard()) return false;
                if (string.IsNullOrEmpty(text)) return true;

                IntPtr hGlobal = GlobalAlloc(GMEM_MOVABLE | GMEM_ZEROINIT, (UIntPtr)((text.Length + 1) * 2));
                if (hGlobal == IntPtr.Zero) return false;

                IntPtr target = GlobalLock(hGlobal);
                if (target == IntPtr.Zero)
                {
                    GlobalFree(hGlobal);
                    return false;
                }

                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                GlobalUnlock(hGlobal);

                if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                {
                    GlobalFree(hGlobal);
                    return false;
                }

                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static void RestoreClipboardAll(ClipboardBackup backup)
        {
            if (!SafeOpenClipboard()) return;

            try
            {
                EmptyClipboard();
                foreach (var kvp in backup.Formats)
                {
                    IntPtr hGlobal = GlobalAlloc(GMEM_MOVABLE | GMEM_ZEROINIT, (UIntPtr)kvp.Value.Length);
                    if (hGlobal == IntPtr.Zero) continue;

                    IntPtr target = GlobalLock(hGlobal);
                    if (target == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        continue;
                    }

                    Marshal.Copy(kvp.Value, 0, target, kvp.Value.Length);
                    GlobalUnlock(hGlobal);
                    if (SetClipboardData(kvp.Key, hGlobal) == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                    }
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static bool ClipboardContainsText(string text)
        {
            if (!SafeOpenClipboard()) return false;

            try
            {
                IntPtr hGlobal = GetClipboardData(CF_UNICODETEXT);
                if (hGlobal == IntPtr.Zero) return false;

                IntPtr source = GlobalLock(hGlobal);
                if (source == IntPtr.Zero) return false;

                try
                {
                    string? currentClip = Marshal.PtrToStringUni(source);
                    return currentClip == text;
                }
                finally
                {
                    GlobalUnlock(hGlobal);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        public static bool InsertText(string text, out string error)
        {
            error = string.Empty;
            Thread.Sleep(200); 

            if (!TryBackupClipboardAll(out var backup))
            {
                return TypeText(text, out error);
            }

            if (!SetWin32ClipboardText(text))
            {
                RestoreClipboardAll(backup);
                if (TypeText(text, out error)) return true;
                error = "Clipboard setup failed, and fallback typing also failed: " + error;
                return false;
            }
            
            Thread.Sleep(50);
            
            var inputsList = new System.Collections.Generic.List<INPUT>();

            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            bool lwin = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0;
            bool rwin = (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

            if (shift) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_SHIFT, dwFlags = KEYEVENTF_KEYUP } } });
            if (alt) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = KEYEVENTF_KEYUP } } });
            if (lwin) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_LWIN, dwFlags = KEYEVENTF_KEYUP } } });
            if (rwin) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_RWIN, dwFlags = KEYEVENTF_KEYUP } } });
            if (ctrl) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } });

            inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 } } });
            inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = 0 } } });
            inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } } });
            inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } });
            
            if (shift && (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_SHIFT, dwFlags = 0 } } });
            if (alt && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = 0 } } });
            if (lwin && (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_LWIN, dwFlags = 0 } } });
            if (rwin && (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_RWIN, dwFlags = 0 } } });
            if (ctrl && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 } } });

            var inputs = inputsList.ToArray();
            var sent = SendInput((uint)inputs.Length, inputs, INPUT.Size);
            if (sent != inputs.Length)
            {
                RestoreClipboardAll(backup);
                error = $"Windows accepted {sent} of {inputs.Length} paste input events.";
                return false;
            }

            Thread.Sleep(1500);

            if (ClipboardContainsText(text))
            {
                RestoreClipboardAll(backup);
            }

            return true;
        }
    }
}
