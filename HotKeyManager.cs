using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyPulse
{
    public static class HotkeyManager
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
           uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
           int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
           IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern void PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        private const uint WM_HOTKEY = 0x0312;
        private const uint WM_QUIT = 0x0012;
        private static IntPtr _hWnd;
        private static Thread? _thread;
        private static Dictionary<int, Action> _actions = new();
        private static int _currentId = 0;

        public static void Start()
        {
            var tcs = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                _hWnd = CreateWindowEx(0, "Static", "KeyPulseMessageWindow", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                tcs.Set();

                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_HOTKEY)
                    {
                        int id = msg.wParam.ToInt32();
                        if (_actions.TryGetValue(id, out var action))
                        {
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try { action(); } catch { }
                            });
                        }
                    }
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                DestroyWindow(_hWnd);
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();
            tcs.Wait();
        }

        public static void Stop()
        {
            if (_hWnd != IntPtr.Zero)
            {
                PostMessage(_hWnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _thread?.Join();
            }
        }

        public static void Clear()
        {
            foreach (var id in _actions.Keys)
            {
                UnregisterHotKey(_hWnd, id);
            }
            _actions.Clear();
            _currentId = 0;
        }

        public static bool Probe(string combo)
        {
            if (!ParseCombo(combo, out uint modifiers, out uint vk)) return false;
            int probeId = 99999;
            if (RegisterHotKey(_hWnd, probeId, modifiers | 0x4000, vk))
            {
                UnregisterHotKey(_hWnd, probeId);
                return true;
            }
            return false;
        }

        private static bool ParseCombo(string combo, out uint modifiers, out uint vk)
        {
            modifiers = 0;
            vk = 0;

            if (string.IsNullOrWhiteSpace(combo)) return false;

            var parts = combo.Split('+');
            foreach (var p in parts)
            {
                var trim = p.Trim().ToLowerInvariant();
                if (trim == "ctrl" || trim == "control") modifiers |= 0x0002;
                else if (trim == "alt") modifiers |= 0x0001;
                else if (trim == "shift") modifiers |= 0x0004;
                else if (trim == "win" || trim == "windows") modifiers |= 0x0008;
                else
                {
                    if (Enum.TryParse<ConsoleKey>(p.Trim(), true, out var key))
                    {
                        vk = (uint)key;
                    }
                    else if (p.Trim().Length == 1)
                    {
                        vk = (uint)p.Trim().ToUpperInvariant()[0];
                    }
                }
            }

            return vk != 0;
        }

        public static bool Register(string combo, Action action)
        {
            if (!ParseCombo(combo, out uint modifiers, out uint vk)) return false;

            _currentId++;
            if (RegisterHotKey(_hWnd, _currentId, modifiers | 0x4000, vk)) // 0x4000 = MOD_NOREPEAT
            {
                _actions[_currentId] = action;
                return true;
            }
            return false;
        }
    }
}
