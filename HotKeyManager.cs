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
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern void PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
        private const uint WM_USER = 0x0400;
        public const uint ModAlt = 0x0001;
        public const uint ModCtrl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint ModWin = 0x0008;
        private const uint ModNoRepeat = 0x4000;
        private static IntPtr _hWnd;
        private static Thread? _thread;
        private static Dictionary<int, Action> _actions = new();
        private static int _currentId = 0;
        private static System.Collections.Concurrent.ConcurrentQueue<Action> _taskQueue = new();

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelKeyboardProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private static volatile bool _captureCtrlDown;
        private static volatile bool _captureAltDown;
        private static volatile bool _captureShiftDown;
        private static volatile bool _captureWinDown;

        private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

        public static bool IsCaptureMode { get; set; } = false;

        /// <summary>Last Win32 error seen by a failed RegisterHotKey call.</summary>
        public static int LastRegisterError { get; private set; }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        public static Action<int, bool, bool, bool, bool>? OnRawKey;

        public static void EnableCaptureHook()
        {
            ResetCaptureModifierState();
            if (_hookID == IntPtr.Zero)
            {
                using (var curProcess = Process.GetCurrentProcess())
                using (var curModule = curProcess.MainModule)
                {
                    if (curModule != null)
                        _hookID = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
                }
            }
            IsCaptureMode = true;
        }

        public static void DisableCaptureHook()
        {
            IsCaptureMode = false;
            ResetCaptureModifierState();
            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool IsKeyDown(int vKey)
        {
            return (GetAsyncKeyState(vKey) & 0x8000) != 0;
        }

        private static void ResetCaptureModifierState()
        {
            _captureCtrlDown = false;
            _captureAltDown = false;
            _captureShiftDown = false;
            _captureWinDown = false;
        }

        private static bool IsCtrlVk(int vk)
        {
            return vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL;
        }

        private static bool IsAltVk(int vk)
        {
            return vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU;
        }

        private static bool IsShiftVk(int vk)
        {
            return vk == VK_SHIFT || vk == VK_LSHIFT || vk == VK_RSHIFT;
        }

        private static bool IsWinVk(int vk)
        {
            return vk == VK_LWIN || vk == VK_RWIN;
        }

        private static void UpdateCaptureModifierState(int vk, bool isDown)
        {
            if (IsCtrlVk(vk)) _captureCtrlDown = isDown;
            else if (IsAltVk(vk)) _captureAltDown = isDown;
            else if (IsShiftVk(vk)) _captureShiftDown = isDown;
            else if (IsWinVk(vk)) _captureWinDown = isDown;
        }

        public static void GetModifierSnapshot(int currentVk, out bool ctrl, out bool alt, out bool shift, out bool win)
        {
            ctrl = IsKeyDown(VK_CONTROL) || IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL)
                || _captureCtrlDown || IsCtrlVk(currentVk);
            alt = IsKeyDown(VK_MENU) || IsKeyDown(VK_LMENU) || IsKeyDown(VK_RMENU)
                || _captureAltDown || IsAltVk(currentVk);
            shift = IsKeyDown(VK_SHIFT) || IsKeyDown(VK_LSHIFT) || IsKeyDown(VK_RSHIFT)
                || _captureShiftDown || IsShiftVk(currentVk);
            win = IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN)
                || _captureWinDown || IsWinVk(currentVk);
        }

        /// <summary>
        /// ISSUE_1: the capture hook must only ever swallow keys while a KeyPulse window is the
        /// foreground window. Previously it kept eating the whole machine's keyboard as soon as the
        /// shortcut box held focus, including after the user alt-tabbed to another application.
        /// </summary>
        private static bool IsKeyPulseForeground()
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return false;

            GetWindowThreadProcessId(hWnd, out var processId);
            return processId == CurrentProcessId;
        }

        private const int HookExtraInfoOffset = 16;

        /// <summary>
        /// ISSUE_14: keystrokes KeyPulse injects itself carry InputSimulator.InjectionTag in
        /// dwExtraInfo, and are ignored here. Without this the capture hook can read back the very
        /// keys InputSimulator is sending and record them as if the user had pressed them.
        ///
        /// This deliberately tests OUR tag and not Windows' generic LLKHF_INJECTED flag: remote
        /// desktop sessions, virtual machines and some laptop function-key drivers deliver ordinary
        /// user keystrokes with that flag set, and treating those as ours would stop the shortcut
        /// box from recording anything at all in exactly those environments.
        /// </summary>
        private static bool IsOwnInjectedEvent(IntPtr lParam)
        {
            try
            {
                return Marshal.ReadIntPtr(lParam, HookExtraInfoOffset) == InputSimulator.InjectionTag;
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && IsCaptureMode && !IsOwnInjectedEvent(lParam) && IsKeyPulseForeground())
            {
                int vkCode = Marshal.ReadInt32(lParam);

                if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
                {
                    UpdateCaptureModifierState(vkCode, true);
                    GetModifierSnapshot(vkCode, out var ctrl, out var alt, out var shift, out var win);
                    OnRawKey?.Invoke(vkCode, ctrl, alt, shift, win);
                }
                else if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP)
                {
                    UpdateCaptureModifierState(vkCode, false);
                }

                if (vkCode == 0x1B || vkCode == 0x09 || vkCode == 0x5B || vkCode == 0x5C)
                {
                    return CallNextHookEx(_hookID, nCode, wParam, lParam);
                }

                return (IntPtr)1;
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        public static void Start()
        {
            if (_thread != null) return;

            var tcs = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                _hWnd = CreateWindowEx(0, "Static", "KeyPulseMessageWindow", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                tcs.Set();

                int bRet;
                while ((bRet = GetMessage(out MSG msg, IntPtr.Zero, 0, 0)) != 0)
                {
                    if (bRet == -1)
                    {
                        Thread.Sleep(500);
                        continue;
                    }

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
                    else if (msg.message == WM_USER)
                    {
                        while (_taskQueue.TryDequeue(out var task))
                        {
                            try { task(); } catch { }
                        }
                    }
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                DestroyWindow(_hWnd);
                _hWnd = IntPtr.Zero;
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();
            tcs.Wait();
            tcs.Dispose();
        }

        public static void Stop()
        {
            if (_hWnd != IntPtr.Zero)
            {
                PostMessage(_hWnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _thread?.Join(2000);
                _thread = null;
            }
        }

        private static bool RunOnThread(Action action)
        {
            if (_hWnd == IntPtr.Zero)
            {
                Program.LogDebug("HotkeyManager.RunOnThread called before the message window existed.");
                return false;
            }

            if (Thread.CurrentThread == _thread)
            {
                action();
                return true;
            }

            var signal = new ManualResetEventSlim(false);
            var completed = false;

            _taskQueue.Enqueue(() =>
            {
                try { action(); }
                finally
                {
                    Volatile.Write(ref completed, true);
                    try { signal.Set(); } catch { }
                }
            });

            PostMessage(_hWnd, WM_USER, IntPtr.Zero, IntPtr.Zero);

            if (signal.Wait(5000))
            {
                signal.Dispose();
                return true;
            }

            Program.LogDebug("HotkeyManager.RunOnThread timed out after 5s; the queued work may still run.");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    signal.Wait(30000);
                    if (!Volatile.Read(ref completed)) Program.LogCrash("A hotkey thread task never completed.");
                }
                catch { }
                finally { try { signal.Dispose(); } catch { } }
            });

            return false;
        }

        private static Dictionary<int, (uint modifiers, uint vk)> _activeCombos = new();

        /// <summary>
        /// Releases every hotkey. Used at shutdown only. Do NOT call this to apply an edit: ISSUE_5
        /// was exactly that - every add, edit, toggle and 30-second retry unregistered the user's
        /// whole set and re-registered it, leaving a window in which no shortcut worked at all.
        /// Use Unregister(id) / Register(...) for individual rows instead.
        /// </summary>
        public static void Clear()
        {
            RunOnThread(() => {
                foreach (var id in _actions.Keys)
                {
                    UnregisterHotKey(_hWnd, id);
                }
                _actions.Clear();
                _activeCombos.Clear();
            });
        }

        /// <summary>Releases a single hotkey previously handed out by Register.</summary>
        public static void Unregister(int hotkeyId)
        {
            if (hotkeyId <= 0) return;

            RunOnThread(() =>
            {
                if (!_actions.ContainsKey(hotkeyId) && !_activeCombos.ContainsKey(hotkeyId)) return;
                UnregisterHotKey(_hWnd, hotkeyId);
                _actions.Remove(hotkeyId);
                _activeCombos.Remove(hotkeyId);
            });
        }

        public static bool Probe(string combo)
        {
            if (!ParseCombo(combo, out uint modifiers, out uint vk)) return false;

            bool success = false;
            var completed = RunOnThread(() => {
                foreach (var active in _activeCombos.Values)
                {
                    if (active.modifiers == modifiers && active.vk == vk)
                    {
                        success = true;
                        return;
                    }
                }

                success = TestComboAvailableOnHotkeyThread(modifiers, vk);
            });

            return completed && success;
        }

        private const int ProbeHotkeyId = 99999;

        /// <summary>Must only be called on the hotkey message thread.</summary>
        private static bool TestComboAvailableOnHotkeyThread(uint modifiers, uint vk)
        {
            if (RegisterHotKey(_hWnd, ProbeHotkeyId, modifiers | ModNoRepeat, vk))
            {
                UnregisterHotKey(_hWnd, ProbeHotkeyId);
                return true;
            }

            return false;
        }

        /// <summary>
        /// ISSUE_20: when a combination is taken, offer the nearest free alternative instead of a
        /// dead-end "Conflict" message.
        /// </summary>
        public static string? SuggestAlternative(string combo)
        {
            if (!ParseCombo(combo, out uint modifiers, out uint vk)) return null;

            uint[] extras = { ModShift, ModCtrl, ModAlt, ModWin };
            string? suggestion = null;

            RunOnThread(() =>
            {
                foreach (var extra in extras)
                {
                    if ((modifiers & extra) != 0) continue;
                    var candidate = modifiers | extra;
                    if (IsComboOwnedByUs(candidate, vk)) continue;
                    if (TestComboAvailableOnHotkeyThread(candidate, vk))
                    {
                        suggestion = DescribeCombo(candidate, vk);
                        return;
                    }
                }

                foreach (var first in extras)
                {
                    if ((modifiers & first) != 0) continue;
                    foreach (var second in extras)
                    {
                        if (second == first) continue;
                        if ((modifiers & second) != 0) continue;
                        var candidate = modifiers | first | second;
                        if (IsComboOwnedByUs(candidate, vk)) continue;
                        if (TestComboAvailableOnHotkeyThread(candidate, vk))
                        {
                            suggestion = DescribeCombo(candidate, vk);
                            return;
                        }
                    }
                }
            });

            return suggestion;
        }

        private static bool IsComboOwnedByUs(uint modifiers, uint vk)
        {
            foreach (var active in _activeCombos.Values)
            {
                if (active.modifiers == modifiers && active.vk == vk) return true;
            }
            return false;
        }

        public static string DescribeCombo(uint modifiers, uint vk)
        {
            var parts = new List<string>();
            if ((modifiers & ModCtrl) != 0) parts.Add("Ctrl");
            if ((modifiers & ModAlt) != 0) parts.Add("Alt");
            if ((modifiers & ModShift) != 0) parts.Add("Shift");
            if ((modifiers & ModWin) != 0) parts.Add("Win");
            parts.Add(VirtualKeyToName(vk));
            return string.Join("+", parts);
        }


        private static readonly (uint Vk, string Name)[] CanonicalKeyNames =
        {
            (0x0D, "Enter"), (0x20, "Space"), (0x1B, "Escape"), (0x09, "Tab"), (0x08, "Back"),
            (0x2E, "Delete"), (0x2D, "Insert"), (0x24, "Home"), (0x23, "End"),
            (0x21, "PageUp"), (0x22, "PageDown"),
            (0x26, "Up"), (0x28, "Down"), (0x25, "Left"), (0x27, "Right"),
            (0x14, "CapsLock"), (0x90, "NumLock"), (0x91, "ScrollLock"),
            (0x2C, "PrintScreen"), (0x13, "Pause"), (0x5D, "Apps"),

            (0x70, "F1"), (0x71, "F2"), (0x72, "F3"), (0x73, "F4"), (0x74, "F5"), (0x75, "F6"),
            (0x76, "F7"), (0x77, "F8"), (0x78, "F9"), (0x79, "F10"), (0x7A, "F11"), (0x7B, "F12"),
            (0x7C, "F13"), (0x7D, "F14"), (0x7E, "F15"), (0x7F, "F16"), (0x80, "F17"), (0x81, "F18"),
            (0x82, "F19"), (0x83, "F20"), (0x84, "F21"), (0x85, "F22"), (0x86, "F23"), (0x87, "F24"),

            (0x60, "NumPad0"), (0x61, "NumPad1"), (0x62, "NumPad2"), (0x63, "NumPad3"),
            (0x64, "NumPad4"), (0x65, "NumPad5"), (0x66, "NumPad6"), (0x67, "NumPad7"),
            (0x68, "NumPad8"), (0x69, "NumPad9"),
            (0x6A, "NumPadMultiply"), (0x6B, "NumPadAdd"), (0x6C, "NumPadSeparator"),
            (0x6D, "NumPadSubtract"), (0x6E, "NumPadDecimal"), (0x6F, "NumPadDivide"),

            (0xBA, "OemSemicolon"), (0xBB, "OemPlus"), (0xBC, "OemComma"), (0xBD, "OemMinus"),
            (0xBE, "OemPeriod"), (0xBF, "OemQuestion"), (0xC0, "OemTilde"),
            (0xDB, "OemOpenBrackets"), (0xDC, "OemPipe"), (0xDD, "OemCloseBrackets"),
            (0xDE, "OemQuotes"), (0xDF, "Oem8"), (0xE2, "OemBackslash"),

            (0xB0, "MediaNextTrack"), (0xB1, "MediaPreviousTrack"), (0xB2, "MediaStop"),
            (0xB3, "MediaPlayPause"),
            (0xAD, "VolumeMute"), (0xAE, "VolumeDown"), (0xAF, "VolumeUp"),

            (0xA6, "BrowserBack"), (0xA7, "BrowserForward"), (0xA8, "BrowserRefresh"),
            (0xA9, "BrowserStop"), (0xAA, "BrowserSearch"), (0xAB, "BrowserFavorites"),
            (0xAC, "BrowserHome")
        };

        /// <summary>Spellings accepted on input but never produced on output.</summary>
        private static readonly (string Alias, uint Vk)[] KeyNameAliases =
        {
            ("return", 0x0D), ("spacebar", 0x20), ("esc", 0x1B), ("backspace", 0x08),
            ("del", 0x2E), ("ins", 0x2D), ("pgup", 0x21), ("pgdn", 0x22), ("prtsc", 0x2C),
            ("menu", 0x5D), ("contextmenu", 0x5D), ("capital", 0x14), ("snapshot", 0x2C),

            ("plus", 0xBB), ("+", 0xBB), ("equals", 0xBB), ("oemquotes", 0xDE),
            ("oem3", 0xC0), ("oem1", 0xBA), ("oem2", 0xBF), ("oem4", 0xDB), ("oem5", 0xDC),
            ("oem6", 0xDD), ("oem7", 0xDE), ("oem102", 0xE2),

            ("add", 0x6B), ("numpadplus", 0x6B), ("numpadadd", 0x6B),
            ("subtract", 0x6D), ("numpadminus", 0x6D), ("numpadsubtract", 0x6D),
            ("multiply", 0x6A), ("numpadmultiply", 0x6A),
            ("divide", 0x6F), ("numpaddivide", 0x6F),
            ("decimal", 0x6E), ("numpaddecimal", 0x6E), ("separator", 0x6C),

            ("playpause", 0xB3), ("play", 0xB3), ("medianexttrack", 0xB0),
            ("mediaprevioustrack", 0xB1), ("mediastop", 0xB2)
        };

        private static readonly Dictionary<uint, string> VkToNameMap = BuildVkToNameMap();
        private static readonly Dictionary<string, uint> NameToVkMap = BuildNameToVkMap();

        private static Dictionary<uint, string> BuildVkToNameMap()
        {
            var map = new Dictionary<uint, string>();
            foreach (var (vk, name) in CanonicalKeyNames) map[vk] = name;
            return map;
        }

        private static Dictionary<string, uint> BuildNameToVkMap()
        {
            var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var (vk, name) in CanonicalKeyNames) map[name] = vk;
            foreach (var (alias, vk) in KeyNameAliases) map[alias] = vk;

            for (uint digit = 0; digit <= 9; digit++) map["D" + digit] = 0x30 + digit;
            return map;
        }

        /// <summary>Inverse of the name table used by ParseCombo, so round-tripping is stable.</summary>
        public static string VirtualKeyToName(uint vk)
        {
            if (VkToNameMap.TryGetValue(vk, out var name)) return name;

            if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9'))
            {
                return ((char)vk).ToString();
            }

            return "VK" + vk;
        }

        /// <summary>Resolves one non-modifier token to a virtual key, or 0 when unknown.</summary>
        public static uint NameToVirtualKey(string token)
        {
            var trimmed = (token ?? string.Empty).Trim();
            if (trimmed.Length == 0) return 0;

            if (NameToVkMap.TryGetValue(trimmed, out var mapped)) return mapped;

            if (trimmed.Length == 1)
            {
                var c = char.ToUpperInvariant(trimmed[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
            }

            if (trimmed.StartsWith("VK", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(trimmed.AsSpan(2), out var raw) && raw > 0 && raw <= 0xFF)
            {
                return raw;
            }

            return 0;
        }

        private static bool ParseCombo(string combo, out uint modifiers, out uint vk)
        {
            modifiers = 0;
            vk = 0;

            if (string.IsNullOrWhiteSpace(combo)) return false;

            var raw = combo.Split('+');
            var parts = new List<string>();
            for (var i = 0; i < raw.Length; i++)
            {
                var token = raw[i].Trim();
                if (token.Length > 0) { parts.Add(token); continue; }
                if (i > 0 && i == raw.Length - 1) parts.Add("+");
            }

            foreach (var p in parts)
            {
                var trim = p.ToLowerInvariant();
                if (trim == "ctrl" || trim == "control") modifiers |= ModCtrl;
                else if (trim == "alt") modifiers |= ModAlt;
                else if (trim == "shift") modifiers |= ModShift;
                else if (trim == "win" || trim == "windows" || trim == "meta") modifiers |= ModWin;
                else
                {
                    var resolved = NameToVirtualKey(p);
                    if (resolved != 0) vk = resolved;
                }
            }

            return vk != 0;
        }

        public static bool TryParseCombo(string combo, out uint modifiers, out uint vk)
        {
            return ParseCombo(combo, out modifiers, out vk);
        }

        public static bool IsTypingKeyWithoutModifier(string combo)
        {
            if (!ParseCombo(combo, out uint modifiers, out uint vk)) return false;
            return modifiers == 0 && IsTypingVirtualKey(vk);
        }

        public static bool IsTypingVirtualKey(uint vk)
        {
            return (vk >= 'A' && vk <= 'Z')
                || (vk >= '0' && vk <= '9')
                || vk == 0x20
                || (vk >= 0xBA && vk <= 0xC0)
                || (vk >= 0xDB && vk <= 0xDE);
        }

        public static bool Register(string combo, Action action) => Register(combo, action, out _);

        /// <summary>
        /// Takes out one hotkey and returns the id that owns it, so the caller can release exactly
        /// that one later without disturbing any other shortcut. ISSUE_5.
        /// </summary>
        public static bool Register(string combo, Action action, out int hotkeyId)
        {
            hotkeyId = 0;
            if (!ParseCombo(combo, out uint modifiers, out uint vk)) return false;

            bool success = false;
            int lastError = 0;
            int assignedId = 0;

            var completed = RunOnThread(() => {
                var id = ++_currentId;
                if (RegisterHotKey(_hWnd, id, modifiers | ModNoRepeat, vk))
                {
                    _actions[id] = action;
                    _activeCombos[id] = (modifiers, vk);
                    assignedId = id;
                    success = true;
                }
                else
                {
                    lastError = Marshal.GetLastWin32Error();
                }
            });

            LastRegisterError = success ? 0 : lastError;

            if (success)
            {
                hotkeyId = assignedId;
                return true;
            }

            return completed && success;
        }
    }
}
