using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class ClipboardThreadHwndLeakTests
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        private const uint GR_GDIOBJECTS = 0;
        private const uint GR_USEROBJECTS = 1;

        [TestInitialize]
        public void Setup()
        {
            InputSimulator.ResetClipboardOwnerHwndForTesting();
        }

        [TestCleanup]
        public void Cleanup()
        {
            InputSimulator.ResetClipboardOwnerHwndForTesting();
            HotkeyManager.Stop();
        }

        [TestMethod]
        public void GetClipboardOwnerHwnd_FallbackToZero_WhenHotkeyManagerNotStarted()
        {
            HotkeyManager.Stop();
            InputSimulator.ResetClipboardOwnerHwndForTesting();

            var hwnd = InputSimulator.ClipboardOwnerHwndForTesting;
            Assert.AreEqual(IntPtr.Zero, hwnd, "When HotkeyManager is not started, clipboard owner HWND should fall back to IntPtr.Zero (task binding).");
        }

        [TestMethod]
        public void GetClipboardOwnerHwnd_BindsToDedicatedMessageWindow_WhenHotkeyManagerActive()
        {
            HotkeyManager.Start();
            InputSimulator.ResetClipboardOwnerHwndForTesting();

            var hwnd = InputSimulator.ClipboardOwnerHwndForTesting;
            Assert.AreNotEqual(IntPtr.Zero, hwnd, "When HotkeyManager is started, clipboard owner HWND should be non-zero.");
            Assert.AreEqual(HotkeyManager.MessageWindowHandle, hwnd, "Clipboard owner HWND should match HotkeyManager.MessageWindowHandle.");
        }

        [TestMethod]
        public void GetClipboardOwnerHwnd_SelfHeals_WhenDefunctHandleEncountered()
        {
            var bogusHwnd = new IntPtr(0x000F1234);
            InputSimulator.SetClipboardOwnerHwndForTesting(bogusHwnd);

            HotkeyManager.Stop();
            var recoveredHwnd = InputSimulator.ClipboardOwnerHwndForTesting;
            Assert.AreEqual(IntPtr.Zero, recoveredHwnd, "Defunct handle must be detected by IsWindow and safely reset to IntPtr.Zero.");

            HotkeyManager.Start();
            var validHwnd = InputSimulator.ClipboardOwnerHwndForTesting;
            Assert.AreEqual(HotkeyManager.MessageWindowHandle, validHwnd, "Should re-bind to valid HotkeyManager.MessageWindowHandle.");
        }

        [TestMethod]
        public void SafeOpenClipboard_ThreadPoolRapidSuccession_NeverFailsWithError1400()
        {
            const int taskCount = 20;
            var tasks = new Task[taskCount];

            for (int i = 0; i < taskCount; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    for (int rep = 0; rep < 5; rep++)
                    {
                        bool opened = InputSimulator.SafeOpenClipboardForTesting();
                        if (opened)
                        {
                            try
                            {
                                Thread.Sleep(2);
                            }
                            finally
                            {
                                CloseClipboard();
                            }
                        }
                        else
                        {
                            int err = Marshal.GetLastWin32Error();
                            Assert.AreNotEqual(1400, err, "SafeOpenClipboard must never fail with ERROR_INVALID_WINDOW_HANDLE (1400).");
                        }
                    }
                });
            }

            Task.WaitAll(tasks, TimeSpan.FromSeconds(10));
        }

        [TestMethod]
        public void SafeOpenClipboard_WithHotkeyManager_SucceedsAcrossThreadPoolThreads()
        {
            HotkeyManager.Start();
            InputSimulator.ResetClipboardOwnerHwndForTesting();

            const int iterations = 15;
            for (int i = 0; i < iterations; i++)
            {
                bool success = false;
                var thread = new Thread(() =>
                {
                    bool opened = InputSimulator.SafeOpenClipboardForTesting();
                    if (opened)
                    {
                        success = true;
                        CloseClipboard();
                    }
                });
                thread.IsBackground = true;
                thread.Start();
                thread.Join();

                Assert.IsTrue(success, $"Iteration {i}: SafeOpenClipboard should succeed on ephemeral thread.");
            }
        }

        [TestMethod]
        public void SetWin32ClipboardText_ThreadPoolRapidSuccession_SucceedsWithoutError()
        {
            const int iterations = 10;
            for (int i = 0; i < iterations; i++)
            {
                bool success = false;
                Task.Run(() =>
                {
                    success = InputSimulator.SetWin32ClipboardTextForTesting($"KeyPulse Clipboard Test {i}");
                }).Wait(TimeSpan.FromSeconds(3));

                Assert.IsTrue(success, $"Iteration {i}: SetWin32ClipboardText should succeed without OpenClipboard failure.");
            }
        }

        [TestMethod]
        public void InsertText_RapidSuccessiveThreadPoolInvocations_ZeroOpenClipboardFailures()
        {
            for (int i = 0; i < 5; i++)
            {
                bool completed = false;
                Task.Run(() =>
                {
                    bool result = InputSimulator.InsertText($"KeyPulse Test String {i}", out var error, out var warning, out var busy);
                    if (!result && !busy)
                    {
                        Assert.IsFalse(error.Contains("clipboard setup failed", StringComparison.OrdinalIgnoreCase),
                            $"InsertText clipboard operation failed: {error}");
                    }
                    completed = true;
                }).Wait(TimeSpan.FromSeconds(5));

                Assert.IsTrue(completed, $"InsertText iteration {i} timed out or blocked.");
            }
        }

        [TestMethod]
        public void ClipboardOperations_ZeroUserAndGdiHandleLeaks()
        {
            var process = Process.GetCurrentProcess();

            InputSimulator.SafeOpenClipboardForTesting();
            CloseClipboard();

            uint initialUserObjects = GetGuiResources(process.Handle, GR_USEROBJECTS);
            uint initialGdiObjects = GetGuiResources(process.Handle, GR_GDIOBJECTS);

            for (int i = 0; i < 50; i++)
            {
                Task.Run(() =>
                {
                    if (InputSimulator.SafeOpenClipboardForTesting())
                    {
                        CloseClipboard();
                    }
                }).Wait();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();

            uint finalUserObjects = GetGuiResources(process.Handle, GR_USEROBJECTS);
            uint finalGdiObjects = GetGuiResources(process.Handle, GR_GDIOBJECTS);

            Assert.AreEqual(initialUserObjects, finalUserObjects,
                $"USER objects must not leak. Initial: {initialUserObjects}, Final: {finalUserObjects}");
            Assert.AreEqual(initialGdiObjects, finalGdiObjects,
                $"GDI objects must not leak. Initial: {initialGdiObjects}, Final: {finalGdiObjects}");
        }
    }
}
