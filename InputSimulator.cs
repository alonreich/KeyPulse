using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_V = 0x56;
        private const ushort VK_ESCAPE = 0x1B;
        private const uint MAPVK_VK_TO_VSC_EX = 4;
        private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

        /// <summary>
        /// ISSUE_14: every event KeyPulse injects carries this stamp in dwExtraInfo so its own
        /// capture hook can recognise and ignore its own output instead of reading it back as if
        /// the user had typed it.
        /// </summary>
        public static readonly IntPtr InjectionTag = new IntPtr(0x4B50_5545); // "KPUE"

        /// <summary>
        /// ISSUE_9: milliseconds paused between batches of characters, mirrored from AppConfig.
        /// The old code slept a hard-coded 12 ms after EVERY character with no way to change it, so
        /// a 3,000-character snippet locked the keyboard for over half a minute.
        /// </summary>
        public static volatile int CharacterDelayMs = 1;

        // ------------------------------------------------------------------
        // ISSUE_10: long "Type text" runs are cancellable, reportable, and never overlap.
        // ------------------------------------------------------------------

        private static int _typingBusy;
        private static volatile bool _cancelRequested;

        /// <summary>Raised when a typing run begins. Argument is the total character count.</summary>
        public static event Action<int>? TypingStarted;

        /// <summary>Raised as typing advances. Arguments are (charactersSent, totalCharacters).</summary>
        public static event Action<int, int>? TypingProgressChanged;

        /// <summary>Raised when a typing run ends. Argument is true when it was cancelled.</summary>
        public static event Action<bool>? TypingFinished;

        public static bool IsTyping => Volatile.Read(ref _typingBusy) != 0;

        /// <summary>
        /// Roughly how long a run of this many characters will take at the current speed setting.
        /// The UI uses it to decide whether a progress window is worth showing at all: at the Fast
        /// setting most snippets finish before a window could even be painted, and a dialog that
        /// flashes up and vanishes is worse than no dialog.
        /// </summary>
        public static int EstimateTypingMs(int characterCount)
        {
            if (characterCount <= 0) return 0;

            var delay = Math.Clamp(CharacterDelayMs, 0, 250);
            var batchSize = delay <= 2 ? 20 : 1;
            var batches = (characterCount + batchSize - 1) / batchSize;

            // 60 ms settle, plus roughly a millisecond of SendInput per batch on top of the pause.
            return 60 + batches * (delay + 1);
        }

        public static void CancelTyping() => _cancelRequested = true;

        public static bool TypeText(string text, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrEmpty(text)) return true;

            // A second press while the first run is still typing used to start a parallel stream
            // and interleave the two into garbage. Ignore it instead.
            if (Interlocked.CompareExchange(ref _typingBusy, 1, 0) != 0)
            {
                Program.LogDebug("TypeText ignored: a typing run is already in progress.");
                return true;
            }

            _cancelRequested = false;
            var cancelled = false;

            try
            {
                // ISSUE_9: characters are sent in batches and the pause is a user setting, not a
                // hard-coded 12 ms per character.
                var delay = Math.Clamp(CharacterDelayMs, 0, 250);
                var batchSize = delay <= 2 ? 20 : 1;

                Program.LogDebug($"TypeText: {text.Length} characters, {delay} ms between batches of {batchSize}.");
                TypingStarted?.Invoke(text.Length);

                // ISSUE_6: ask the window we are typing INTO which keyboard layout it uses. Asking
                // our own thread produced the wrong letters whenever the target app was on another
                // layout (Hebrew, German, French...).
                var keyboardLayout = GetTargetKeyboardLayout();
                SettleBeforeTyping();

                var modifierReleaseInputs = new List<INPUT>();
                AddPressedModifierKeyUps(modifierReleaseInputs, keyboardLayout);
                if (!SendKeyboardInputs(modifierReleaseInputs, "modifier release", out error))
                {
                    return false;
                }

                var i = 0;
                var inputsList = new List<INPUT>();

                while (i < text.Length)
                {
                    if (_cancelRequested || IsKeyPressed(VK_ESCAPE))
                    {
                        cancelled = true;
                        Program.LogDebug($"TypeText cancelled after {i} of {text.Length} characters.");
                        break;
                    }

                    inputsList.Clear();
                    var charsInBatch = 0;

                    while (i < text.Length && charsInBatch < batchSize)
                    {
                        var ch = text[i];
                        if (ch == '\r')
                        {
                            AddKeyStroke(inputsList, VK_RETURN, keyboardLayout);
                            if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                        }
                        else if (ch == '\n')
                        {
                            AddKeyStroke(inputsList, VK_RETURN, keyboardLayout);
                        }
                        else if (ch == '\t')
                        {
                            AddKeyStroke(inputsList, VK_TAB, keyboardLayout);
                        }
                        else if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        {
                            AddUnicodeCharInputs(inputsList, ch);
                            AddUnicodeCharInputs(inputsList, text[i + 1]);
                            i++;
                        }
                        else if (!AddMappedCharInputs(inputsList, ch, keyboardLayout))
                        {
                            AddUnicodeCharInputs(inputsList, ch);
                        }

                        i++;
                        charsInBatch++;
                    }

                    if (!SendKeyboardInputs(inputsList, "text input", out error))
                    {
                        return false;
                    }

                    TypingProgressChanged?.Invoke(i, text.Length);
                    if (delay > 0) Thread.Sleep(delay);
                }

                return true;
            }
            finally
            {
                _cancelRequested = false;
                Volatile.Write(ref _typingBusy, 0);
                TypingFinished?.Invoke(cancelled);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScanEx(char ch, IntPtr dwhkl);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        /// <summary>
        /// Gives the target window a moment to finish processing the hotkey key-ups before we start
        /// injecting. (The old code called SetForegroundWindow on the window that was already
        /// foreground, which did nothing but burn 120 ms.)
        /// </summary>
        private static void SettleBeforeTyping()
        {
            Thread.Sleep(60);
        }

        /// <summary>
        /// ISSUE_6: the keyboard layout of the window we are about to type into.
        ///
        /// GetKeyboardLayout(0) answers for the CALLING thread - that is KeyPulse, not the target.
        /// Whenever the two differed the text arrived scrambled: an English-layout KeyPulse typing
        /// into a French or Hebrew window produced the wrong letters and punctuation. Falling back
        /// to our own layout is only for the case where there is no foreign foreground window.
        /// </summary>
        private static IntPtr GetTargetKeyboardLayout()
        {
            try
            {
                var hWnd = GetForegroundWindow();
                if (hWnd != IntPtr.Zero)
                {
                    var threadId = GetWindowThreadProcessId(hWnd, out var processId);
                    if (threadId != 0 && processId != CurrentProcessId)
                    {
                        var layout = GetKeyboardLayout(threadId);
                        if (layout != IntPtr.Zero) return layout;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug("Could not read the target window's keyboard layout: " + ex.Message);
            }

            return GetKeyboardLayout(0);
        }

        private static INPUT KeyInput(ushort key, bool keyUp = false)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = key,
                        dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                        dwExtraInfo = InjectionTag
                    }
                }
            };
        }

        /// <summary>
        /// ISSUE_14: sends a real hardware scan code, not just a virtual key.
        ///
        /// The old version filled in wVk and wScan but never set KEYEVENTF_SCANCODE (the constant was
        /// declared and never used), so Windows delivered only the virtual key. Games, remote desktop
        /// sessions, virtual machines and anything else reading raw hardware input saw nothing at all
        /// and the shortcut appeared to do absolutely nothing. With the flag set, wVk MUST be 0 and
        /// Windows derives the character from the FOREGROUND layout, which is also what ISSUE_6 wants.
        /// </summary>
        private static INPUT ScanCodeInput(ushort key, IntPtr keyboardLayout, bool keyUp = false)
        {
            var scanCode = MapVirtualKeyEx(key, MAPVK_VK_TO_VSC_EX, keyboardLayout);
            if (scanCode == 0) return KeyInput(key, keyUp);

            uint flags = KEYEVENTF_SCANCODE;
            if (keyUp) flags |= KEYEVENTF_KEYUP;
            if ((scanCode & 0xFF00) != 0) flags |= KEYEVENTF_EXTENDEDKEY;

            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)(scanCode & 0xFF),
                        dwFlags = flags,
                        dwExtraInfo = InjectionTag
                    }
                }
            };
        }

        private static void AddKeyStroke(List<INPUT> inputs, ushort key, IntPtr keyboardLayout)
        {
            inputs.Add(ScanCodeInput(key, keyboardLayout));
            inputs.Add(ScanCodeInput(key, keyboardLayout, true));
        }

        private static bool AddMappedCharInputs(List<INPUT> inputs, char ch, IntPtr keyboardLayout)
        {
            var keyScan = VkKeyScanEx(ch, keyboardLayout);
            if (keyScan == -1) return false;

            var virtualKey = (ushort)(keyScan & 0xff);
            var shiftState = (byte)((keyScan >> 8) & 0xff);
            if (virtualKey == 0) return false;

            if ((shiftState & 1) != 0) inputs.Add(ScanCodeInput(VK_SHIFT, keyboardLayout));
            if ((shiftState & 2) != 0) inputs.Add(ScanCodeInput(VK_CONTROL, keyboardLayout));
            if ((shiftState & 4) != 0) inputs.Add(ScanCodeInput(VK_MENU, keyboardLayout));

            AddKeyStroke(inputs, virtualKey, keyboardLayout);

            if ((shiftState & 4) != 0) inputs.Add(ScanCodeInput(VK_MENU, keyboardLayout, true));
            if ((shiftState & 2) != 0) inputs.Add(ScanCodeInput(VK_CONTROL, keyboardLayout, true));
            if ((shiftState & 1) != 0) inputs.Add(ScanCodeInput(VK_SHIFT, keyboardLayout, true));
            return true;
        }

        private static void AddUnicodeCharInputs(List<INPUT> inputs, char ch)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE, dwExtraInfo = InjectionTag } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, dwExtraInfo = InjectionTag } } });
        }

        private static bool IsKeyPressed(int key)
        {
            return (GetAsyncKeyState(key) & 0x8000) != 0;
        }

        private static void AddPressedModifierKeyUps(List<INPUT> inputs, IntPtr keyboardLayout)
        {
            if (IsKeyPressed(VK_LSHIFT)) inputs.Add(ScanCodeInput(VK_LSHIFT, keyboardLayout, true));
            else if (IsKeyPressed(VK_RSHIFT)) inputs.Add(ScanCodeInput(VK_RSHIFT, keyboardLayout, true));
            else if (IsKeyPressed(VK_SHIFT)) inputs.Add(ScanCodeInput(VK_SHIFT, keyboardLayout, true));

            if (IsKeyPressed(VK_LMENU)) inputs.Add(ScanCodeInput(VK_LMENU, keyboardLayout, true));
            else if (IsKeyPressed(VK_RMENU)) inputs.Add(ScanCodeInput(VK_RMENU, keyboardLayout, true));
            else if (IsKeyPressed(VK_MENU)) inputs.Add(ScanCodeInput(VK_MENU, keyboardLayout, true));

            if (IsKeyPressed(VK_LCONTROL)) inputs.Add(ScanCodeInput(VK_LCONTROL, keyboardLayout, true));
            else if (IsKeyPressed(VK_RCONTROL)) inputs.Add(ScanCodeInput(VK_RCONTROL, keyboardLayout, true));
            else if (IsKeyPressed(VK_CONTROL)) inputs.Add(ScanCodeInput(VK_CONTROL, keyboardLayout, true));

            if (IsKeyPressed(VK_LWIN)) inputs.Add(ScanCodeInput(VK_LWIN, keyboardLayout, true));
            if (IsKeyPressed(VK_RWIN)) inputs.Add(ScanCodeInput(VK_RWIN, keyboardLayout, true));
        }

        private static bool SendKeyboardInputs(List<INPUT> inputsList, string operationName, out string error)
        {
            error = string.Empty;
            var inputs = inputsList.ToArray();
            if (inputs.Length == 0) return true;

            var sent = SendInput((uint)inputs.Length, inputs, INPUT.Size);
            if (sent != inputs.Length)
            {
                var win32Error = Marshal.GetLastWin32Error();
                if (win32Error == 5) // ERROR_ACCESS_DENIED
                {
                    error = "Failed to type. The target app is running as Administrator. You must run KeyPulse as Administrator to type into it.";
                }
                else
                {
                    error = $"Windows accepted {sent} of {inputs.Length} {operationName} events. Win32 error {win32Error}.";
                }
                return false;
            }

            return true;
        }

        // ------------------------------------------------------------------
        // Clipboard
        // ------------------------------------------------------------------

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
        [DllImport("user32.dll")]
        private static extern IntPtr GetOpenClipboardWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClipboardFormatName(uint format, [Out] char[] lpszFormatName, int cchMaxCount);

        private const uint CF_TEXT = 1;
        private const uint CF_BITMAP = 2;
        private const uint CF_METAFILEPICT = 3;
        private const uint CF_PALETTE = 9;
        private const uint CF_ENHMETAFILE = 14;
        private const uint CF_HDROP = 15;
        private const uint CF_OWNERDISPLAY = 0x0080;
        private const uint CF_DSPBITMAP = 0x0082;
        private const uint CF_DSPMETAFILEPICT = 0x0083;
        private const uint CF_DSPENHMETAFILE = 0x008E;
        private const uint CF_GDIOBJFIRST = 0x0300;
        private const uint CF_GDIOBJLAST = 0x03FF;

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVABLE = 0x0002;
        private const uint GMEM_ZEROINIT = 0x0040;

        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_LSHIFT = 0xA0;
        private const ushort VK_RSHIFT = 0xA1;
        private const ushort VK_LCONTROL = 0xA2;
        private const ushort VK_RCONTROL = 0xA3;
        private const ushort VK_LMENU = 0xA4;
        private const ushort VK_RMENU = 0xA5;
        private const ushort VK_LWIN = 0x5B;
        private const ushort VK_RWIN = 0x5C;
        private const ushort VK_RETURN = 0x0D;
        private const ushort VK_TAB = 0x09;

        private static bool SafeOpenClipboard()
        {
            for (int i = 0; i < 20; i++)
            {
                if (OpenClipboard(IntPtr.Zero)) return true;
                Thread.Sleep(15);
            }
            return false;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint RegisterClipboardFormat(string lpszFormat);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint EnumClipboardFormats(uint format);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr GlobalSize(IntPtr hMem);

        /// <summary>
        /// ISSUE_3: only formats whose clipboard handle really is a moveable memory block can be
        /// copied byte-for-byte and handed back. GDI handles (bitmaps, palettes, metafiles) are NOT
        /// memory blocks; the old code read and rewrote them as if they were, which destroyed the
        /// user's copied image instead of preserving it.
        /// </summary>
        private static bool IsGlobalMemoryFormat(uint format)
        {
            switch (format)
            {
                case CF_BITMAP:
                case CF_METAFILEPICT:
                case CF_PALETTE:
                case CF_ENHMETAFILE:
                case CF_OWNERDISPLAY:
                case CF_DSPBITMAP:
                case CF_DSPMETAFILEPICT:
                case CF_DSPENHMETAFILE:
                    return false;
            }

            if (format >= CF_GDIOBJFIRST && format <= CF_GDIOBJLAST) return false;
            return true;
        }

        private static string DescribeFormat(uint format)
        {
            switch (format)
            {
                case CF_BITMAP:
                case CF_DSPBITMAP: return "a bitmap image";
                case CF_METAFILEPICT:
                case CF_ENHMETAFILE:
                case CF_DSPMETAFILEPICT:
                case CF_DSPENHMETAFILE: return "a drawing";
                case CF_PALETTE: return "a colour palette";
                case CF_OWNERDISPLAY: return "an application-drawn item";
                case CF_HDROP: return "copied files";
            }

            try
            {
                var buffer = new char[128];
                var length = GetClipboardFormatName(format, buffer, buffer.Length);
                if (length > 0) return new string(buffer, 0, length);
            }
            catch { }

            return "clipboard format " + format;
        }

        private sealed class ClipboardSnapshot
        {
            public readonly Dictionary<uint, byte[]> Formats = new();
            public readonly List<string> Unpreservable = new();
            public bool Captured;
        }

        private static ClipboardSnapshot BackupClipboard()
        {
            var snapshot = new ClipboardSnapshot();

            if (!SafeOpenClipboard())
            {
                snapshot.Unpreservable.Add("the current clipboard (another app was holding it open)");
                return snapshot;
            }

            try
            {
                uint format = 0;
                while ((format = EnumClipboardFormats(format)) != 0)
                {
                    if (!IsGlobalMemoryFormat(format))
                    {
                        snapshot.Unpreservable.Add(DescribeFormat(format));
                        continue;
                    }

                    IntPtr handle = GetClipboardData(format);
                    if (handle == IntPtr.Zero)
                    {
                        // Delayed-rendered content the owner refused or failed to produce.
                        snapshot.Unpreservable.Add(DescribeFormat(format));
                        continue;
                    }

                    IntPtr pointer = GlobalLock(handle);
                    if (pointer == IntPtr.Zero)
                    {
                        snapshot.Unpreservable.Add(DescribeFormat(format));
                        continue;
                    }

                    try
                    {
                        long size = (long)GlobalSize(handle);
                        if (size <= 0)
                        {
                            snapshot.Unpreservable.Add(DescribeFormat(format));
                        }
                        else if (size >= 64L * 1024 * 1024)
                        {
                            // Too large to hold in memory safely - say so rather than dropping it silently.
                            snapshot.Unpreservable.Add(DescribeFormat(format) + " (too large to hold)");
                        }
                        else
                        {
                            var data = new byte[size];
                            Marshal.Copy(pointer, data, 0, (int)size);
                            snapshot.Formats[format] = data;
                        }
                    }
                    catch
                    {
                        snapshot.Unpreservable.Add(DescribeFormat(format));
                    }
                    finally
                    {
                        GlobalUnlock(handle);
                    }
                }

                snapshot.Captured = true;
                return snapshot;
            }
            catch (Exception ex)
            {
                Program.LogDebug("Clipboard backup failed: " + ex.Message);
                return snapshot;
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static void RestoreClipboard(ClipboardSnapshot snapshot)
        {
            if (!snapshot.Captured) return;

            if (!SafeOpenClipboard())
            {
                Program.LogDebug("Clipboard restore skipped: clipboard could not be opened.");
                return;
            }

            try
            {
                EmptyClipboard();
                foreach (var kvp in snapshot.Formats)
                {
                    IntPtr hGlobal = GlobalAlloc(GMEM_MOVABLE | GMEM_ZEROINIT, (UIntPtr)(uint)kvp.Value.Length);
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

                AddClipboardExclusionFlag("Clipboard Viewer Ignore", 2);
                AddClipboardExclusionFlag("ExcludeClipboardContentFromMonitorProcessing", 2);
                AddClipboardExclusionFlag("ExcludeClipboardContentFromMonitorUI", 2);
                AddClipboardExclusionFlag("CanIncludeInClipboardHistory", 4);
                AddClipboardExclusionFlag("CanUploadToCloudClipboard", 4);

                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static void AddClipboardExclusionFlag(string formatName, uint byteCount)
        {
            uint format = RegisterClipboardFormat(formatName);
            if (format == 0) return;

            IntPtr handle = GlobalAlloc(GMEM_MOVABLE | GMEM_ZEROINIT, (UIntPtr)byteCount);
            if (handle == IntPtr.Zero) return;

            if (SetClipboardData(format, handle) == IntPtr.Zero) GlobalFree(handle);
        }

        /// <summary>
        /// ISSUE_11: wait until the target application has actually read the clipboard before
        /// putting the old contents back. The previous fixed 500 ms guess meant a slow app, a remote
        /// session, or a loaded machine pasted whatever used to be on the clipboard.
        /// </summary>
        private static void WaitForClipboardToBeConsumed(int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            var observedForeignReader = false;

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                var owner = GetOpenClipboardWindow();
                if (owner != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(owner, out var processId);
                    if (processId != CurrentProcessId) observedForeignReader = true;
                }
                else if (observedForeignReader)
                {
                    // The reader finished with the data; give it a beat to copy it out.
                    Thread.Sleep(60);
                    return;
                }

                Thread.Sleep(5);
            }

            if (!observedForeignReader)
            {
                Program.LogDebug("Paste target never opened the clipboard; restoring after timeout.");
            }
        }

        public static bool InsertText(string text, out string error, out string warning)
        {
            error = string.Empty;
            warning = string.Empty;
            if (string.IsNullOrEmpty(text)) return true;

            if (Interlocked.CompareExchange(ref _typingBusy, 1, 0) != 0)
            {
                Program.LogDebug("InsertText ignored: a typing or pasting run is already in progress.");
                return true;
            }

            try
            {
                Program.LogDebug($"InsertText using plain-text clipboard paste for {text.Length} characters.");
                var originalClipboard = BackupClipboard();

                if (originalClipboard.Unpreservable.Count > 0)
                {
                    warning = "Your clipboard held " + string.Join(", ", originalClipboard.Unpreservable) +
                              ", which Windows does not allow KeyPulse to put back. That content was replaced by this shortcut's text.";
                }
                else if (!originalClipboard.Captured)
                {
                    warning = "KeyPulse could not read your existing clipboard, so it could not be restored after pasting.";
                }

                if (!SetWin32ClipboardText(text))
                {
                    error = "Plain-text clipboard setup failed.";
                    RestoreClipboard(originalClipboard);
                    return false;
                }

                Thread.Sleep(50);

                var keyboardLayout = GetTargetKeyboardLayout(); // ISSUE_6
                var inputsList = new List<INPUT>();

                AddPressedModifierKeyUps(inputsList, keyboardLayout);
                inputsList.Add(ScanCodeInput(VK_CONTROL, keyboardLayout));
                inputsList.Add(ScanCodeInput(VK_V, keyboardLayout));
                inputsList.Add(ScanCodeInput(VK_V, keyboardLayout, true));
                inputsList.Add(ScanCodeInput(VK_CONTROL, keyboardLayout, true));

                if (!SendKeyboardInputs(inputsList, "paste input", out error))
                {
                    RestoreClipboard(originalClipboard);
                    return false;
                }

                WaitForClipboardToBeConsumed(2500);
                RestoreClipboard(originalClipboard);

                return true;
            }
            finally
            {
                Volatile.Write(ref _typingBusy, 0);
            }
        }
    }
}
