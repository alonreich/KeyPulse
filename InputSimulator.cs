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

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVABLE = 0x0002;
        private const uint GMEM_ZEROINIT = 0x0040;

        private static void SetWin32ClipboardText(string text)
        {
            if (!OpenClipboard(IntPtr.Zero)) return;
            EmptyClipboard();
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVABLE | GMEM_ZEROINIT, (UIntPtr)((text.Length + 1) * 2));
            if (hGlobal != IntPtr.Zero)
            {
                IntPtr target = GlobalLock(hGlobal);
                if (target != IntPtr.Zero)
                {
                    Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                    GlobalUnlock(hGlobal);
                    SetClipboardData(CF_UNICODETEXT, hGlobal);
                }
            }
            CloseClipboard();
        }

        public static void InsertText(string text)
        {
            Thread.Sleep(200); 

            SetWin32ClipboardText(text);
            
            Thread.Sleep(50);
            
            var inputs = new INPUT[4];
            inputs[0] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 } } };
            inputs[1] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = 0 } } };
            inputs[2] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } } };
            inputs[3] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } };
            
            SendInput(4, inputs, INPUT.Size);
        }
    }
}
