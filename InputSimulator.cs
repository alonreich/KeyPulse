using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyPulse
{
    public static class InputSimulator
    {
        [DllImport("user32.dll")]
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

        public static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var inputs = new INPUT[text.Length * 2];
            for (int i = 0; i < text.Length; i++)
            {
                inputs[i * 2] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = text[i], dwFlags = KEYEVENTF_UNICODE } } };
                inputs[i * 2 + 1] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = text[i], dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } };
            }
            SendInput((uint)inputs.Length, inputs, INPUT.Size);
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

        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_LWIN = 0x5B;
        private const ushort VK_RWIN = 0x5C;

        private static string? GetWin32ClipboardText()
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            if (!OpenClipboard(IntPtr.Zero)) return null;
            
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        private static void SetWin32ClipboardText(string text)
        {
            if (!OpenClipboard(IntPtr.Zero)) return;
            EmptyClipboard();
            if (string.IsNullOrEmpty(text)) 
            {
                CloseClipboard();
                return;
            }
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVABLE | GMEM_ZEROINIT, (UIntPtr)((text.Length + 1) * 2));
            if (hGlobal != IntPtr.Zero)
            {
                IntPtr target = GlobalLock(hGlobal);
                if (target != IntPtr.Zero)
                {
                    Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                    GlobalUnlock(hGlobal);
                    if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                    }
                }
                else
                {
                    GlobalFree(hGlobal);
                }
            }
            CloseClipboard();
        }

        public static void InsertText(string text)
        {
            Thread.Sleep(200); 

            var backup = GetWin32ClipboardText();
            SetWin32ClipboardText(text);
            
            Thread.Sleep(50);
            
            var inputsList = new System.Collections.Generic.List<INPUT>();

            if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_SHIFT, dwFlags = KEYEVENTF_KEYUP } } });
            if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = KEYEVENTF_KEYUP } } });
            if ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_LWIN, dwFlags = KEYEVENTF_KEYUP } } });
            if ((GetAsyncKeyState(VK_RWIN) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_RWIN, dwFlags = KEYEVENTF_KEYUP } } });
            if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } });

            inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 } } });
            inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = 0 } } });
            inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } } });
            inputsList.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } });
            
            var inputs = inputsList.ToArray();
            SendInput((uint)inputs.Length, inputs, INPUT.Size);

            ThreadPool.QueueUserWorkItem(_ => 
            {
                Thread.Sleep(1500);
                if (backup != null)
                {
                    SetWin32ClipboardText(backup);
                }
                else
                {
                    if (OpenClipboard(IntPtr.Zero))
                    {
                        EmptyClipboard();
                        CloseClipboard();
                    }
                }
            });
        }
    }
}
