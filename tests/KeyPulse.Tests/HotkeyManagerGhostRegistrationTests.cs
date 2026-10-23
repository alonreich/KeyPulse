using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class HotkeyManagerGhostRegistrationTests
    {
        private const string Combo = "Ctrl+Alt+Shift+F11";

        [TestInitialize]
        public void Setup()
        {
            HotkeyManager.Start();
            HotkeyManager.RunOnThreadTimeoutMs = 300;
        }

        [TestCleanup]
        public void Cleanup()
        {
            HotkeyManager.RunOnThreadTimeoutMs = 5000;
            HotkeyManager.Clear();
            HotkeyManager.Stop();
        }

        [TestMethod]
        public void Register_TimedOutWhilePending_NeverRegistersWithOs()
        {
            using var release = new ManualResetEventSlim(false);
            using var blockerRunning = new ManualResetEventSlim(false);
            var blocker = Task.Run(() => HotkeyManager.RunOnThreadForTesting(() => { blockerRunning.Set(); release.Wait(5000); }));
            Assert.IsTrue(blockerRunning.Wait(2000));
            HotkeyManager.RunOnThreadTimeoutMs = 100;

            var ok = HotkeyManager.Register(Combo, () => { }, out var id);

            Assert.IsFalse(ok);
            Assert.AreEqual(0, id);
            release.Set();
            HotkeyManager.RunOnThreadTimeoutMs = 5000;
            blocker.Wait(3000);

            Assert.IsTrue(HotkeyManager.Register(Combo, () => { }, out var id2), "Cancelled registration left a ghost in the OS.");
            Assert.AreNotEqual(0, id2);
            HotkeyManager.Unregister(id2);
        }

        [TestMethod]
        public void RunOnThread_TimedOutWhilePending_ActionNeverExecutes()
        {
            using var release = new ManualResetEventSlim(false);
            using var blockerRunning = new ManualResetEventSlim(false);
            var blocker = Task.Run(() => HotkeyManager.RunOnThreadForTesting(() => { blockerRunning.Set(); release.Wait(5000); }));
            Assert.IsTrue(blockerRunning.Wait(2000));

            var ran = 0;
            var result = HotkeyManager.RunOnThreadForTesting(() => Interlocked.Exchange(ref ran, 1));
            Assert.IsFalse(result);

            release.Set();
            HotkeyManager.RunOnThreadTimeoutMs = 5000;
            blocker.Wait(3000);
            Assert.IsTrue(HotkeyManager.RunOnThreadForTesting(() => { }));
            Assert.AreEqual(0, Volatile.Read(ref ran));
        }

        [TestMethod]
        public void RunOnThread_TimedOutMidExecution_RunsRollback()
        {
            var rolledBack = new ManualResetEventSlim(false);
            var result = HotkeyManager.RunOnThreadForTesting(
                () => Thread.Sleep(900),
                () => rolledBack.Set());

            Assert.IsFalse(result);
            Assert.IsTrue(rolledBack.Wait(3000), "Abandoned work was not rolled back.");
        }

        [TestMethod]
        public void Register_TimedOutMidRegistration_UnregistersOrphan()
        {
            using var ownerRunning = new ManualResetEventSlim(false);
            for (int i = 0; i < 20; i++)
            {
                HotkeyManager.RunOnThreadTimeoutMs = 1;
                var ok = HotkeyManager.Register(Combo, () => { }, out var id);
                HotkeyManager.RunOnThreadTimeoutMs = 5000;
                HotkeyManager.RunOnThreadForTesting(() => { });
                if (ok) HotkeyManager.Unregister(id);

                Assert.IsTrue(HotkeyManager.Register(Combo, () => { }, out var id2), $"Ghost registration on iteration {i}.");
                HotkeyManager.Unregister(id2);
            }
        }

        [TestMethod]
        public void Register_Normal_StillWorksAndDuplicateIsRejected()
        {
            HotkeyManager.RunOnThreadTimeoutMs = 5000;
            Assert.IsTrue(HotkeyManager.Register(Combo, () => { }, out var id));
            Assert.AreNotEqual(0, id);
            Assert.IsFalse(HotkeyManager.Register(Combo, () => { }, out _));
            Assert.AreNotEqual(0, HotkeyManager.LastRegisterError);
            HotkeyManager.Unregister(id);
        }
    }
}
