using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class DdcSessionBusCollisionTests
    {
        private string? _testSandboxDir;

        [TestInitialize]
        public void SetupTestSandbox()
        {
            _testSandboxDir = Path.Combine(Path.GetTempPath(), $"kp_test_ddc_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testSandboxDir);
            ConfigStore.ConfigDirectory = _testSandboxDir;
            ScreenBrightnessService.InvalidateAllSessions();
        }

        [TestCleanup]
        public void CleanupTestSandbox()
        {
            ScreenBrightnessService.InvalidateAllSessions();
            ConfigStore.ConfigDirectory = null!;
            if (!string.IsNullOrEmpty(_testSandboxDir) && Directory.Exists(_testSandboxDir))
            {
                try { Directory.Delete(_testSandboxDir, true); } catch { }
            }
        }

        [TestMethod]
        public void DdcMonitorSession_AtomicFlag_TracksInvalidationAndDisposal()
        {
            var hMon = new IntPtr(0x1000);
            var hPhys = new IntPtr(0x2000);
            var session = new ScreenBrightnessService.DdcMonitorSession(
                hMon,
                hPhys,
                "Test Screen",
                Array.Empty<ScreenBrightnessService.PHYSICAL_MONITOR>(),
                0);

            Assert.IsFalse(session.IsInvalidated, "Session must initially not be invalidated");
            Assert.IsFalse(session.IsDisposed, "Session must initially not be disposed");

            session.Invalidate();
            Assert.IsTrue(session.IsInvalidated, "Session must be marked invalidated after Invalidate()");
            Assert.IsFalse(session.IsDisposed, "Session should not yet be marked disposed if Dispose() was not called");

            session.Dispose();
            Assert.IsTrue(session.IsDisposed, "Session must be marked disposed after Dispose()");
            Assert.IsTrue(session.IsInvalidated, "Session must remain invalidated after Dispose()");
        }

        [TestMethod]
        public void DdcMonitorSession_Dispose_IsIdempotentAndThreadSafe()
        {
            var session = new ScreenBrightnessService.DdcMonitorSession(
                new IntPtr(0x1001),
                new IntPtr(0x2001),
                "Concurrent Dispose Screen",
                Array.Empty<ScreenBrightnessService.PHYSICAL_MONITOR>(),
                0);

            var exceptions = new ConcurrentBag<Exception>();

            Parallel.For(0, 20, _ =>
            {
                try
                {
                    session.Invalidate();
                    session.Dispose();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.AreEqual(0, exceptions.Count, "Concurrent Invalidate and Dispose calls must never throw: " + string.Join("; ", exceptions));
            Assert.IsTrue(session.IsInvalidated, "Session must be marked invalidated");
            Assert.IsTrue(session.IsDisposed, "Session must be marked disposed");
        }

        [TestMethod]
        public void InvalidateAllSessions_ClearsActiveSessions_AndIsIdempotent()
        {
            ScreenBrightnessService.InvalidateAllSessions();
            Assert.AreEqual(0, ScreenBrightnessService.ActiveSessionCountForTesting, "Active sessions must be 0 after InvalidateAllSessions");

            ScreenBrightnessService.InvalidateAllSessions();
            ScreenBrightnessService.InvalidateAllSessions();
            Assert.AreEqual(0, ScreenBrightnessService.ActiveSessionCountForTesting, "Active sessions must remain 0 after repeated invalidation");
        }

        [TestMethod]
        public void WakeDdcBus_WithZeroOrInvalidHandle_DoesNotThrow()
        {
            try
            {
                ScreenBrightnessService.WakeDdcBus(IntPtr.Zero);
                ScreenBrightnessService.WakeDdcBus(new IntPtr(0xDEAD));
            }
            catch (Exception ex)
            {
                Assert.Fail("WakeDdcBus must handle invalid handles gracefully without throwing: " + ex.Message);
            }
        }

        [TestMethod]
        public void Concurrent_InvalidateAllSessions_And_WakeDdcBus_NoDeadlockOrCrash()
        {
            var exceptions = new ConcurrentBag<Exception>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            Parallel.For(0, 40, i =>
            {
                try
                {
                    if (cts.Token.IsCancellationRequested) return;

                    if (i % 2 == 0)
                    {
                        ScreenBrightnessService.InvalidateAllSessions();
                    }
                    else
                    {
                        ScreenBrightnessService.WakeDdcBus(new IntPtr(0x5000 + (i % 5)));
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.AreEqual(0, exceptions.Count, "Concurrent InvalidateAllSessions and WakeDdcBus must never throw: " + string.Join("; ", exceptions));
        }

        [TestMethod]
        public void Concurrent_DdcWrites_During_InvalidateAllSessions_NoInvalidHandleCrash()
        {
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new List<Task>();

            for (int i = 0; i < 30; i++)
            {
                int index = i;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        if (index % 3 == 0)
                        {
                            ScreenBrightnessService.InvalidateAllSessions();
                        }
                        else if (index % 3 == 1)
                        {
                            ScreenBrightnessService.WakeDdcBus(new IntPtr(0x6000 + (index % 4)));
                            ScreenBrightnessService.TriggerBrightnessStep(index % 2 == 0 ? 5 : -5);
                        }
                        else
                        {
                            ScreenBrightnessService.TriggerBrightnessStep(10);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }));
            }

            bool completed = Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(15));
            Assert.IsTrue(completed, "All concurrent tasks must complete within bounded wait (no BusLock deadlocks)");
            Assert.AreEqual(0, exceptions.Count, "Concurrent DDC writes and InvalidateAllSessions must not throw: " + string.Join("; ", exceptions));
        }

        [TestMethod]
        public void BusLock_HighFrequencyContention_NoThreadStarvationOrDeadlock()
        {
            var exceptions = new ConcurrentBag<Exception>();
            int iterationCount = 60;
            var countdown = new CountdownEvent(iterationCount);

            for (int i = 0; i < iterationCount; i++)
            {
                int op = i;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        switch (op % 4)
                        {
                            case 0:
                                ScreenBrightnessService.InvalidateAllSessions();
                                break;
                            case 1:
                                ScreenBrightnessService.WakeDdcBus(new IntPtr(0x7000 + (op % 3)));
                                break;
                            case 2:
                                ScreenBrightnessService.TriggerBrightnessStep(5);
                                break;
                            case 3:
                                ScreenBrightnessService.InvalidateMonitorCache();
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    finally
                    {
                        countdown.Signal();
                    }
                });
            }

            bool finished = countdown.Wait(TimeSpan.FromSeconds(15));
            Assert.IsTrue(finished, "High-frequency contention test must complete promptly without deadlocks");
            Assert.AreEqual(0, exceptions.Count, "Contention operations must not throw: " + string.Join("; ", exceptions));
        }

        [TestMethod]
        public void InvalidateMonitorCache_CascadesTo_InvalidateAllSessions_Safely()
        {
            try
            {
                ScreenBrightnessService.InvalidateMonitorCache();
                Assert.AreEqual(0, ScreenBrightnessService.ActiveSessionCountForTesting);
            }
            catch (Exception ex)
            {
                Assert.Fail("InvalidateMonitorCache must cascade to InvalidateAllSessions safely: " + ex.Message);
            }
        }
    }
}
