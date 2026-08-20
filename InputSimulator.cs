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
        private const uint MAPVK_VK_TO_VSC_EX = 4;
        private const int LegacyCharacterDelayMs = 12;

        public static bool TypeText(string text, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrEmpty(text)) return true;
            Program.LogDebug($"TypeText using legacy virtual-key typing for {text.Length} characters.");
            var keyboardLayout = GetKeyboardLayout(0);
            var targetWindow = GetForegroundWindow();

            RestoreForegroundWindow(targetWindow);

            var modifierReleaseInputs = new System.Collections.Generic.List<INPUT>();
            AddPressedModifierKeyUps(modifierReleaseInputs, keyboardLayout);
            if (!SendKeyboardInputs(modifierReleaseInputs, "modifier release", out error))
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                var inputsList = new System.Collections.Generic.List<INPUT>();
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

                if (!SendKeyboardInputs(inputsList, "text input", out error))
                {
                    return false;
                }

                Thread.Sleep(LegacyCharacterDelayMs);
            }

            return true;
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
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static void RestoreForegroundWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            try
            {
                SetForegroundWindow(hWnd);
                Thread.Sleep(120);
            }
            catch
            {
            }
        }

        private static INPUT KeyInput(ushort key, bool keyUp = false)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = key, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0 } }
            };
        }

        private static INPUT ScanCodeInput(ushort key, IntPtr keyboardLayout, bool keyUp = false)
        {
            var scanCode = MapVirtualKeyEx(key, MAPVK_VK_TO_VSC_EX, keyboardLayout);
            if (scanCode == 0) return KeyInput(key, keyUp);

            uint flags = 0;
            if (keyUp) flags |= KEYEVENTF_KEYUP;
            if ((scanCode & 0xFF00) != 0) flags |= KEYEVENTF_EXTENDEDKEY;

            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = key,
                        wScan = (ushort)(scanCode & 0xFF),
                        dwFlags = flags
                    }
                }
            };
        }

        private static void AddKeyStroke(System.Collections.Generic.List<INPUT> inputs, ushort key, IntPtr keyboardLayout)
        {
            inputs.Add(ScanCodeInput(key, keyboardLayout));
            inputs.Add(ScanCodeInput(key, keyboardLayout, true));
        }

        private static bool AddMappedCharInputs(System.Collections.Generic.List<INPUT> inputs, char ch, IntPtr keyboardLayout)
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

        private static void AddUnicodeCharInputs(System.Collections.Generic.List<INPUT> inputs, char ch)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }

        private static bool IsKeyPressed(int key)
        {
            return (GetAsyncKeyState(key) & 0x8000) != 0;
        }

        private static void AddPressedModifierKeyUps(System.Collections.Generic.List<INPUT> inputs, IntPtr keyboardLayout)
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

        private static bool SendKeyboardInputs(System.Collections.Generic.List<INPUT> inputsList, string operationName, out string error)
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

        private static bool SafeOpenClipboard()
        {
            for (int i = 0; i < 10; i++)
            {
                if (OpenClipboard(IntPtr.Zero)) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint RegisterClipboardFormat(string lpszFormat);

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

                // Add clipboard viewer ignore formats to bypass Clipboard History and Ditto
                uint ignoreFormat = RegisterClipboardFormat("Clipboard Viewer Ignore");
                if (ignoreFormat != 0)
                {
                    IntPtr hIgnore = GlobalAlloc(GMEM_MOVABLE | GMEM_ZEROINIT, (UIntPtr)2);
                    if (hIgnore != IntPtr.Zero) SetClipboardData(ignoreFormat, hIgnore);
                }

                uint excludeFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
                if (excludeFormat != 0)
                {
                    IntPtr hExclude = GlobalAlloc(GMEM_MOVABLE | GMEM_ZEROINIT, (UIntPtr)2);
                    if (hExclude != IntPtr.Zero) SetClipboardData(excludeFormat, hExclude);
                }

                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static bool ClearClipboard()
        {
            if (!SafeOpenClipboard()) return false;

            try
            {
                return EmptyClipboard();
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static void RestoreTextClipboard(string? originalText, string insertedText)
        {
            try
            {
                if (!ClipboardContainsText(insertedText)) return;

                if (originalText != null)
                {
                    SetWin32ClipboardText(originalText);
                }
                else
                {
                    ClearClipboard();
                }
            }
            catch
            {
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
            if (string.IsNullOrEmpty(text)) return true;

            Program.LogDebug($"InsertText using plain-text clipboard paste for {text.Length} characters.");
            var originalText = GetWin32ClipboardText();

            if (!SetWin32ClipboardText(text))
            {
                error = "Plain-text clipboard setup failed.";
                return false;
            }
            
            Thread.Sleep(50);
            
            var keyboardLayout = GetKeyboardLayout(0);
            var inputsList = new System.Collections.Generic.List<INPUT>();

            AddPressedModifierKeyUps(inputsList, keyboardLayout);
            inputsList.Add(ScanCodeInput(VK_CONTROL, keyboardLayout));
            inputsList.Add(ScanCodeInput(VK_V, keyboardLayout));
            inputsList.Add(ScanCodeInput(VK_V, keyboardLayout, true));
            inputsList.Add(ScanCodeInput(VK_CONTROL, keyboardLayout, true));

            if (!SendKeyboardInputs(inputsList, "paste input", out error))
            {
                RestoreTextClipboard(originalText, text);
                return false;
            }

            Thread.Sleep(1000);
            RestoreTextClipboard(originalText, text);

            return true;
        }
    }
}
