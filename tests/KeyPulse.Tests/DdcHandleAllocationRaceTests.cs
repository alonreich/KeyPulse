using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class DdcHandleAllocationRaceTests
    {
        private string? _testSandboxDir;

        [TestInitialize]
        public void SetupTestSandbox()
        {
            _testSandboxDir = Path.Combine(Path.GetTempPath(), $"kp_test_ddc_race_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testSandboxDir);
            ConfigStore.ConfigDirectory = _testSandboxDir;
            ScreenBrightnessService.PhysicalMonitorsProviderForTesting = null;
            ScreenBrightnessService.DestroyPhysicalMonitorsHookForTesting = null;
            ScreenBrightnessService.InvalidateAllSessions();
        }

        [TestCleanup]
        public void CleanupTestSandbox()
        {
            ScreenBrightnessService.PhysicalMonitorsProviderForTesting = null;
            ScreenBrightnessService.DestroyPhysicalMonitorsHookForTesting = null;
            ScreenBrightnessService.InvalidateAllSessions();
            ConfigStore.ConfigDirectory = null!;
            if (!string.IsNullOrEmpty(_testSandboxDir) && Directory.Exists(_testSandboxDir))
            {
                try { Directory.Delete(_testSandboxDir, true); } catch { }
            }
        }

        [TestMethod]
        public void GetOrCreateSession_Concurrent20Threads_AllocatesExactlyOnce_AndZeroLeakedHandles()
        {
            var hMon = new IntPtr(0x5555);
            var fakePhys = new IntPtr(0x9999);
            int allocationCalls = 0;
            int destroyCalls = 0;

            ScreenBrightnessService.PhysicalMonitorsProviderForTesting = mon =>
            {
                Interlocked.Increment(ref allocationCalls);
                var arr = new ScreenBrightnessService.PHYSICAL_MONITOR[1];
                arr[0].hPhysicalMonitor = fakePhys;
                arr[0].szPhysicalMonitorDescription = "Race Test Monitor";
                return (true, 1, arr);
            };

            ScreenBrightnessService.DestroyPhysicalMonitorsHookForTesting = (cnt, arr) =>
            {
                Interlocked.Increment(ref destroyCalls);
            };

            const int threadCount = 20;
            var barrier = new Barrier(threadCount);
            var sessions = new ConcurrentBag<ScreenBrightnessService.DdcMonitorSession?>();
            var exceptions = new ConcurrentBag<Exception>();

            var threads = new Thread[threadCount];
            for (int i = 0; i < threadCount; i++)
            {
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        barrier.SignalAndWait();
                        var s = ScreenBrightnessService.GetOrCreateSessionForTesting(hMon);
                        sessions.Add(s);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
                threads[i].Start();
            }

            foreach (var t in threads)
            {
                t.Join(5000);
            }

            Assert.AreEqual(0, exceptions.Count, "No thread should throw during concurrent GetOrCreateSession: " + string.Join("; ", exceptions));
            Assert.AreEqual(threadCount, sessions.Count, "All 20 threads must produce a session result");

            ScreenBrightnessService.DdcMonitorSession? firstSession = null;
            foreach (var s in sessions)
            {
                Assert.IsNotNull(s, "Session returned must not be null");
                firstSession ??= s;
                Assert.AreSame(firstSession, s, "All concurrent threads must receive the exact same singleton session instance");
            }

            Assert.AreEqual(1, allocationCalls, "Physical monitor provider must be invoked exactly once across 20 concurrent threads");
            Assert.AreEqual(0, destroyCalls, "No physical monitor handles should be destroyed during non-racing/racing creation");
            Assert.IsTrue(ScreenBrightnessService.HasActiveSessionForTesting(hMon), "Active session must exist for hMon");
            var keysStr = string.Join(", ", System.Linq.Enumerable.Select(ScreenBrightnessService.ActiveSessionKeysForTesting, k => $"0x{k:X}"));
            Assert.AreEqual(1, ScreenBrightnessService.ActiveSessionCountForTesting, $"Expected 1 active session. Found: {keysStr}");

            ScreenBrightnessService.InvalidateAllSessions();
            Assert.AreEqual(1, destroyCalls, "Disposing session must invoke DestroyPhysicalMonitors exactly once");
            Assert.AreEqual(0, ScreenBrightnessService.ActiveSessionCountForTesting, "ActiveSessions must be 0 after invalidation");
            Assert.IsFalse(ScreenBrightnessService.HasActiveSessionForTesting(hMon), "No active session should exist for hMon after invalidation");
        }

        [TestMethod]
        public void GetOrCreateSession_FastPath_LockFreeAndNegligibleLatency()
        {
            var hMon = new IntPtr(0x6666);
            var fakePhys = new IntPtr(0x8888);
            int allocationCalls = 0;

            ScreenBrightnessService.PhysicalMonitorsProviderForTesting = mon =>
            {
                Interlocked.Increment(ref allocationCalls);
                var arr = new ScreenBrightnessService.PHYSICAL_MONITOR[1];
                arr[0].hPhysicalMonitor = fakePhys;
                arr[0].szPhysicalMonitorDescription = "FastPath Test Monitor";
                return (true, 1, arr);
            };

            var initial = ScreenBrightnessService.GetOrCreateSessionForTesting(hMon);
            Assert.IsNotNull(initial);
            Assert.AreEqual(1, allocationCalls);

            const int totalLookups = 100_000;
            const int workerCount = 10;
            var sw = Stopwatch.StartNew();

            Parallel.For(0, workerCount, _ =>
            {
                for (int i = 0; i < totalLookups / workerCount; i++)
                {
                    var s = ScreenBrightnessService.GetOrCreateSessionForTesting(hMon);
                    Assert.AreSame(initial, s);
                }
            });

            sw.Stop();

            Assert.AreEqual(1, allocationCalls, "No additional handle allocations should occur during fast-path lookups");
            Assert.IsTrue(sw.ElapsedMilliseconds < 500, $"Fast-path lookups took too long: {sw.ElapsedMilliseconds}ms for {totalLookups} iterations");
        }

        [TestMethod]
        public void GetOrCreateSession_InvalidOrDisposedSession_IsReplacedDeterministically()
        {
            var hMon = new IntPtr(0x7777);
            int allocationCalls = 0;
            int destroyCalls = 0;

            ScreenBrightnessService.PhysicalMonitorsProviderForTesting = mon =>
            {
                int id = Interlocked.Increment(ref allocationCalls);
                var arr = new ScreenBrightnessService.PHYSICAL_MONITOR[1];
                arr[0].hPhysicalMonitor = new IntPtr(0x3000 + id);
                arr[0].szPhysicalMonitorDescription = $"Monitor Session {id}";
                return (true, 1, arr);
            };

            ScreenBrightnessService.DestroyPhysicalMonitorsHookForTesting = (cnt, arr) =>
            {
                Interlocked.Increment(ref destroyCalls);
            };

            var session1 = ScreenBrightnessService.GetOrCreateSessionForTesting(hMon);
            Assert.IsNotNull(session1);
            Assert.AreEqual(1, allocationCalls);
            Assert.AreEqual(0, destroyCalls);

            session1.Invalidate();
            Assert.IsTrue(session1.IsInvalidated);

            var session2 = ScreenBrightnessService.GetOrCreateSessionForTesting(hMon);
            Assert.IsNotNull(session2);
            Assert.AreNotSame(session1, session2, "Invalidated session must be replaced by a new session instance");
            Assert.AreEqual(2, allocationCalls, "New session must allocate fresh handles");
            Assert.AreEqual(1, destroyCalls, "Old invalidated session must be disposed deterministically");
            Assert.IsTrue(ScreenBrightnessService.HasActiveSessionForTesting(hMon));
            Assert.AreEqual(1, ScreenBrightnessService.ActiveSessionCountForTesting, "Exactly one active session must remain");
        }

        [TestMethod]
        public void GetOrCreateSession_ZeroHandleReturned_CleansUpHandlesAndDoesNotStore()
        {
            var hMon = new IntPtr(0x8888);
            int destroyCalls = 0;

            ScreenBrightnessService.PhysicalMonitorsProviderForTesting = mon =>
            {
                var arr = new ScreenBrightnessService.PHYSICAL_MONITOR[1];
                arr[0].hPhysicalMonitor = IntPtr.Zero;
                return (true, 1, arr);
            };

            ScreenBrightnessService.DestroyPhysicalMonitorsHookForTesting = (cnt, arr) =>
            {
                Interlocked.Increment(ref destroyCalls);
            };

            var session = ScreenBrightnessService.GetOrCreateSessionForTesting(hMon);
            Assert.IsNull(session, "GetOrCreateSession must return null when hPhysicalMonitor is IntPtr.Zero");
            Assert.AreEqual(1, destroyCalls, "Zero-handle edge case must call DestroyPhysicalMonitors immediately");
            Assert.IsFalse(ScreenBrightnessService.HasActiveSessionForTesting(hMon));
            Assert.AreEqual(0, ScreenBrightnessService.ActiveSessionCountForTesting, "No session should be cached for zero handle");
        }

        [TestMethod]
        public void GetOrCreateSession_NullHandle_ReturnsNull()
        {
            var session = ScreenBrightnessService.GetOrCreateSessionForTesting(IntPtr.Zero);
            Assert.IsNull(session, "IntPtr.Zero must immediately return null without locking or allocating");
        }
    }
}
