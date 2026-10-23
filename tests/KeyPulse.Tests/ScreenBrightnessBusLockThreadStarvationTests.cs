using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    /// <summary>
    /// TASK 7 - SCREEN_BRIGHTNESS_BUSLOCK_THREAD_STARVATION: the DDC/CI write pipeline must arbitrate the I2C bus
    /// asynchronously (BusLock.WaitAsync + Task.Delay spacing) so waiting monitor slots never park ThreadPool threads,
    /// while keeping single-access bus exclusivity and the VESA inter-command spacing.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class ScreenBrightnessBusLockThreadStarvationTests
    {
        private string? _sandbox;
        private int _active;
        private int _maxActive;
        private ConcurrentQueue<(IntPtr HMon, long EnterMs, long ExitMs)> _busLog = new();
        private Stopwatch _clock = Stopwatch.StartNew();

        [TestInitialize]
        public void Setup()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), $"kp_test_buslock_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_sandbox);
            ConfigStore.ConfigDirectory = _sandbox;
            ScreenBrightnessService.ResetToDefaults();
            ScreenBrightnessService.ResetWriteSchedulerForTesting();
            _active = 0;
            _maxActive = 0;
            _busLog = new ConcurrentQueue<(IntPtr, long, long)>();
            _clock = Stopwatch.StartNew();
            Assert.AreEqual(1, ScreenBrightnessService.BusLockForTesting.CurrentCount, "BusLock leaked by an earlier test.");
        }

        [TestCleanup]
        public void Cleanup()
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(DdcWriteSafety.MaxLatencyMs * 3);
            while (ScreenBrightnessService.HasPendingHardwareWritesForTesting && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            Thread.Sleep(100);
            ScreenBrightnessService.ResetWriteSchedulerForTesting();
            ScreenBrightnessService.DdcBusWriteOverrideForTesting = null;
            ScreenBrightnessService.HardwareWriteOverrideForTesting = null;
            ConfigStore.ConfigDirectory = null!;
            if (!string.IsNullOrEmpty(_sandbox) && Directory.Exists(_sandbox))
            {
                try { Directory.Delete(_sandbox, true); } catch { }
            }
        }

        private bool RecordingBusWrite(IntPtr hMon, int holdMs)
        {
            long enter = _clock.ElapsedMilliseconds;
            int now = Interlocked.Increment(ref _active);
            int seen;
            while ((seen = Volatile.Read(ref _maxActive)) < now && Interlocked.CompareExchange(ref _maxActive, now, seen) != seen) { }
            if (holdMs > 0) Thread.Sleep(holdMs);
            Interlocked.Decrement(ref _active);
            _busLog.Enqueue((hMon, enter, _clock.ElapsedMilliseconds));
            return true;
        }


        [TestMethod]
        public async Task HeldBus_CallerIsNotBlocked_AndWriteRunsAfterRelease()
        {
            int calls = 0;
            ScreenBrightnessService.DdcBusWriteOverrideForTesting = (h, b, c, wb, wc) => { Interlocked.Increment(ref calls); return true; };
            var bus = ScreenBrightnessService.BusLockForTesting;

            bus.Wait();
            Task<bool> pending;
            try
            {
                var sw = Stopwatch.StartNew();
                pending = ScreenBrightnessService.TryWriteDdcMonitorForTestingAsync(new IntPtr(101), 40, 40, true, true);
                sw.Stop();

                Assert.IsTrue(sw.ElapsedMilliseconds < 200, $"Calling thread was blocked {sw.ElapsedMilliseconds} ms waiting for BusLock (synchronous Wait).");
                await Task.Delay(150);
                Assert.IsFalse(pending.IsCompleted, "Write must wait for the bus instead of bypassing exclusivity.");
                Assert.AreEqual(0, Volatile.Read(ref calls), "No bus command may run while another owner holds BusLock.");
            }
            finally
            {
                bus.Release();
            }

            Assert.IsTrue(await pending, "Write must proceed once the bus is free.");
            Assert.AreEqual(1, calls);
            Assert.AreEqual(1, bus.CurrentCount, "BusLock must be released after the write.");
        }

        [TestMethod]
        public async Task ConcurrentMonitorWrites_AreMutuallyExclusive_AndVesaSpaced()
        {
            ScreenBrightnessService.DdcBusWriteOverrideForTesting = (h, b, c, wb, wc) => RecordingBusWrite(h, 10);
            int spacing = ScreenBrightnessService.VesaInterCommandDelayMsForTesting;
            Assert.IsTrue(spacing >= 40, $"VESA inter-command delay {spacing} ms is below the 40 ms hardware minimum.");

            var tasks = Enumerable.Range(1, 4)
                .Select(i => Task.Run(() => ScreenBrightnessService.TryWriteDdcMonitorForTestingAsync(new IntPtr(200 + i), 50, 50, true, true)))
                .ToArray();
            var results = await Task.WhenAll(tasks);

            Assert.IsTrue(results.All(r => r), "Every monitor write must succeed.");
            Assert.AreEqual(1, _maxActive, "Two monitors were driven on the I2C bus at the same time.");

            var ordered = _busLog.OrderBy(e => e.EnterMs).ToList();
            Assert.AreEqual(4, ordered.Count);
            Assert.AreEqual(4, ordered.Select(e => e.HMon).Distinct().Count());
            for (int i = 1; i < ordered.Count; i++)
            {
                long gap = ordered[i].EnterMs - ordered[i - 1].ExitMs;
                Assert.IsTrue(gap >= spacing - 2, $"Bus commands {i - 1}->{i} only {gap} ms apart (VESA spacing {spacing} ms).");
            }
            Assert.AreEqual(1, ScreenBrightnessService.BusLockForTesting.CurrentCount);
        }

        [TestMethod]
        public async Task ManyQueuedWriters_DoNotStarveThreadPool()
        {
            int calls = 0;
            ScreenBrightnessService.DdcBusWriteOverrideForTesting = (h, b, c, wb, wc) => { Interlocked.Increment(ref calls); return true; };
            var bus = ScreenBrightnessService.BusLockForTesting;
            int writers = Math.Clamp(Environment.ProcessorCount * 2 + 8, 24, 48);

            bus.Wait();
            Task<bool>[] queued;
            try
            {
                queued = Enumerable.Range(0, writers)
                    .Select(i => Task.Run(() => ScreenBrightnessService.TryWriteDdcMonitorForTestingAsync(new IntPtr(1000 + i), 30, 30, true, false)))
                    .ToArray();

                await Task.Delay(300);

                var probe = Stopwatch.StartNew();
                var probes = Enumerable.Range(0, Environment.ProcessorCount * 2).Select(_ => Task.Run(() => 42)).ToArray();
                await Task.WhenAll(probes);
                probe.Stop();
                Assert.IsTrue(probe.ElapsedMilliseconds < 500, $"ThreadPool starved: probe work items took {probe.ElapsedMilliseconds} ms while {writers} writers waited for BusLock.");
                Assert.AreEqual(0, Volatile.Read(ref calls));
            }
            finally
            {
                bus.Release();
            }

            var all = await Task.WhenAll(queued);
            Assert.IsTrue(all.All(r => r), $"{all.Count(r => !r)} of {writers} queued writes timed out waiting for BusLock.");
            Assert.AreEqual(writers, calls);
            Assert.AreEqual(1, bus.CurrentCount);
        }

        [TestMethod]
        public async Task TrailingVesaSpacing_HeldBeforeBusRelease()
        {
            ScreenBrightnessService.DdcBusWriteOverrideForTesting = (h, b, c, wb, wc) => true;
            int spacing = ScreenBrightnessService.VesaInterCommandDelayMsForTesting;
            var bus = ScreenBrightnessService.BusLockForTesting;

            var write = ScreenBrightnessService.TryWriteDdcMonitorForTestingAsync(new IntPtr(301), 10, 10, true, false);
            var sw = Stopwatch.StartNew();
            await bus.WaitAsync();
            sw.Stop();
            bus.Release();

            Assert.IsTrue(await write);
            Assert.IsTrue(sw.ElapsedMilliseconds >= spacing - 20, $"Next bus owner got the bus after only {sw.ElapsedMilliseconds} ms (spacing {spacing} ms).");
        }


        [TestMethod]
        public async Task BusLock_ReleasedWhenBusCommandThrows()
        {
            ScreenBrightnessService.DdcBusWriteOverrideForTesting = (h, b, c, wb, wc) => throw new InvalidOperationException("simulated I2C fault");
            bool ok = await ScreenBrightnessService.TryWriteDdcMonitorForTestingAsync(new IntPtr(401), 60, 60, true, true);
            Assert.IsFalse(ok, "A faulting bus command is a failed write.");
            Assert.AreEqual(1, ScreenBrightnessService.BusLockForTesting.CurrentCount, "BusLock leaked after an exception.");

            ScreenBrightnessService.DdcBusWriteOverrideForTesting = (h, b, c, wb, wc) => true;
            Assert.IsTrue(await ScreenBrightnessService.TryWriteDdcMonitorForTestingAsync(new IntPtr(401), 60, 60, true, true), "Bus must stay usable after a fault.");
        }

        [TestMethod]
        public async Task BusLock_ReleasedWhenBusCommandFails()
        {
            ScreenBrightnessService.DdcBusWriteOverrideForTesting = (h, b, c, wb, wc) => false;
            Assert.IsFalse(await ScreenBrightnessService.TryWriteDdcMonitorForTestingAsync(new IntPtr(402), 60, 60, true, true));
            Assert.AreEqual(1, ScreenBrightnessService.BusLockForTesting.CurrentCount);
        }

        [TestMethod]
        public async Task InvalidArguments_ReturnWithoutTouchingBus()
        {
            int calls = 0;
            ScreenBrightnessService.DdcBusWriteOverrideForTesting = (h, b, c, wb, wc) => { calls++; return true; };
            var bus = ScreenBrightnessService.BusLockForTesting;

            bus.Wait();
            try
            {
                var sw = Stopwatch.StartNew();
                Assert.IsFalse(await ScreenBrightnessService.TryWriteDdcMonitorForTestingAsync(IntPtr.Zero, 50, 50, true, true));
                Assert.IsFalse(await ScreenBrightnessService.TryWriteDdcMonitorForTestingAsync(new IntPtr(5), 50, 50, false, false));
                Assert.IsTrue(sw.ElapsedMilliseconds < 200, "Argument validation must not wait for the bus.");
            }
            finally
            {
                bus.Release();
            }
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public async Task HungBusOwner_WriteTimesOutAndIsSkipped()
        {
            int calls = 0;
            ScreenBrightnessService.DdcBusWriteOverrideForTesting = (h, b, c, wb, wc) => { calls++; return true; };
            var bus = ScreenBrightnessService.BusLockForTesting;

            bus.Wait();
            try
            {
                var sw = Stopwatch.StartNew();
                bool ok = await ScreenBrightnessService.TryWriteDdcMonitorForTestingAsync(new IntPtr(501), 50, 50, true, true);
                sw.Stop();
                Assert.IsFalse(ok, "A write that never got the bus must report failure (no throttle-cache update).");
                Assert.AreEqual(0, calls, "No command may reach the bus without owning BusLock.");
                Assert.IsTrue(sw.ElapsedMilliseconds >= 4500 && sw.ElapsedMilliseconds < 9000, $"Bounded wait expected ~5000 ms, got {sw.ElapsedMilliseconds} ms.");
                Assert.AreEqual(0, bus.CurrentCount, "A timed-out waiter must not release a lock it never acquired.");
            }
            finally
            {
                bus.Release();
            }
            Assert.AreEqual(1, bus.CurrentCount);
        }


        [TestMethod]
        public async Task SlotPipelines_AreSerialised()
        {
            var order = new ConcurrentQueue<int>();
            using var start = new ManualResetEventSlim(false);
            ScreenBrightnessService.HardwareWriteOverrideForTesting = (slot, b, c, wb, wc) =>
            {
                RecordingBusWrite(new IntPtr(slot + 1), 25);
                order.Enqueue(slot);
                return true;
            };

            var tasks = Enumerable.Range(0, ScreenBrightnessService.MaxScreens)
                .Select(slot => Task.Run(async () =>
                {
                    start.Wait();
                    return await ScreenBrightnessService.WriteSingleScreenHardwareWithRetryForTestingAsync(slot, 70, 70, true, true);
                }))
                .ToArray();
            start.Set();
            var results = await Task.WhenAll(tasks);

            Assert.IsTrue(results.All(r => r));
            Assert.AreEqual(1, _maxActive, "Slot write pipelines overlapped on the hardware.");
            Assert.AreEqual(ScreenBrightnessService.MaxScreens, order.Distinct().Count());
        }

        [TestMethod]
        public async Task SlotPipeline_GateReleasedAfterOverrideThrows()
        {
            ScreenBrightnessService.HardwareWriteOverrideForTesting = (slot, b, c, wb, wc) => throw new InvalidOperationException("boom");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                ScreenBrightnessService.WriteSingleScreenHardwareWithRetryForTestingAsync(0, 10, 10, true, true));

            ScreenBrightnessService.HardwareWriteOverrideForTesting = (slot, b, c, wb, wc) => true;
            var next = ScreenBrightnessService.WriteSingleScreenHardwareWithRetryForTestingAsync(1, 10, 10, true, true);
            Assert.AreSame(next, await Task.WhenAny(next, Task.Delay(2000)), "Write gate stayed held after a faulting write (deadlock).");
            Assert.IsTrue(await next);
        }

        [TestMethod]
        public void MasterChange_AllSlotsWrittenOnce_ProbeStaysResponsive()
        {
            var writes = new ConcurrentQueue<int>();
            ScreenBrightnessService.HardwareWriteOverrideForTesting = (slot, b, c, wb, wc) =>
            {
                RecordingBusWrite(new IntPtr(slot + 1), 40);
                writes.Enqueue(slot);
                return true;
            };

            for (int slot = 0; slot < ScreenBrightnessService.MaxScreens; slot++)
            {
                ScreenBrightnessService.RequestScreenLevelChange(slot, 33, 33);
            }

            var probe = Stopwatch.StartNew();
            Task.Run(() => 1).Wait();
            probe.Stop();
            Assert.IsTrue(probe.ElapsedMilliseconds < 500, $"ThreadPool probe took {probe.ElapsedMilliseconds} ms during a multi-monitor change.");

            Thread.Sleep(DdcWriteSafety.SettleMs + 600);
            Assert.AreEqual(1, _maxActive, "Slot workers overlapped on the hardware.");
            for (int slot = 0; slot < ScreenBrightnessService.MaxScreens; slot++)
            {
                Assert.AreEqual(1, writes.Count(s => s == slot), $"Slot {slot} must be written exactly once (250 ms limiter / settle scheduler preserved).");
            }
        }

        [TestMethod]
        public void FailedWrite_StillNotRetried_AfterAsyncConversion()
        {
            int calls = 0;
            ScreenBrightnessService.HardwareWriteOverrideForTesting = (slot, b, c, wb, wc) => { Interlocked.Increment(ref calls); return false; };
            ScreenBrightnessService.RequestScreenLevelChange(2, 90, 90);
            Thread.Sleep(DdcWriteSafety.MaxLatencyMs + 800);
            Assert.AreEqual(1, calls, "A refusing monitor must not be hammered (bus-thrash guard preserved).");
        }


        private sealed class FakeMonitor
        {
            public uint Current;
            public uint Max = 100;
            public bool ReadHighOk = true;
            public bool ReadVcpOk = true;
            public bool HighWriteOk = true;
            public bool VcpWriteOk = true;
            public readonly List<string> Log = new();

            public DdcFeatureReading ReadHigh() { Log.Add("read"); return new DdcFeatureReading(ReadHighOk, Current, 0, Max); }
            public DdcFeatureReading ReadVcp() { Log.Add("read"); return new DdcFeatureReading(ReadVcpOk, Current, 0, Max); }
            public bool WriteHigh(uint v) { Log.Add("write"); if (HighWriteOk) Current = v; return HighWriteOk; }
            public bool WriteVcp(uint v) { Log.Add("write"); if (VcpWriteOk) Current = v; return VcpWriteOk; }

            public void Pause(int ms)
            {
                Assert.IsTrue(ms >= 40, $"Pause {ms} ms below VESA spacing.");
                Log.Add("pause");
            }

            public async Task PauseAsync(int ms)
            {
                Pause(ms);
                await Task.Yield();
            }

            public DdcWriteOutcome WriteSync(DdcFeature f, int p) =>
                DdcWriteSafety.WriteFeature(f, p, () => DdcWriteSafety.ReadFeature(ReadHigh, ReadVcp, Pause), WriteHigh, WriteVcp, Pause);

            public Task<DdcWriteOutcome> WriteAsync(DdcFeature f, int p) =>
                DdcWriteSafety.WriteFeatureAsync(f, p, () => DdcWriteSafety.ReadFeatureAsync(ReadHigh, ReadVcp, PauseAsync), WriteHigh, WriteVcp, PauseAsync);
        }

        private static IEnumerable<Func<FakeMonitor>> Scenarios() => new Func<FakeMonitor>[]
        {
            () => new FakeMonitor { Current = 40 },
            () => new FakeMonitor { Current = 10 },
            () => new FakeMonitor { Current = 10, HighWriteOk = false },
            () => new FakeMonitor { Current = 10, HighWriteOk = false, VcpWriteOk = false },
            () => new FakeMonitor { Current = 10, ReadHighOk = false },
            () => new FakeMonitor { ReadHighOk = false, ReadVcpOk = false },
            () => new FakeMonitor { Current = 300, ReadVcpOk = false },
        };

        [TestMethod]
        public async Task AsyncWritePolicy_MatchesSyncPolicyExactly()
        {
            foreach (var make in Scenarios())
            {
                foreach (var feature in new[] { DdcFeature.Brightness, DdcFeature.Contrast })
                {
                    var s = make();
                    var a = make();
                    var syncOutcome = s.WriteSync(feature, 40);
                    var asyncOutcome = await a.WriteAsync(feature, 40);
                    Assert.AreEqual(syncOutcome, asyncOutcome, $"{feature}: outcome diverged.");
                    CollectionAssert.AreEqual(s.Log, a.Log, $"{feature}: command/pause sequence diverged: sync={string.Join(",", s.Log)} async={string.Join(",", a.Log)}");
                    Assert.AreEqual(s.Current, a.Current);
                }
            }
        }

        [TestMethod]
        public async Task AsyncWritePolicy_NeverSendsBackToBackCommands()
        {
            var m = new FakeMonitor { Current = 10, ReadHighOk = false, HighWriteOk = false, VcpWriteOk = false };
            Assert.AreEqual(DdcWriteOutcome.Failed, await m.WriteAsync(DdcFeature.Brightness, 80));
            for (int i = 1; i < m.Log.Count; i++)
            {
                Assert.IsFalse(m.Log[i] != "pause" && m.Log[i - 1] != "pause", $"Back-to-back commands: {string.Join(",", m.Log)}");
            }
            Assert.AreEqual(2, m.Log.Count(x => x == "write"), "Bounded retry: at most one high-level and one VCP write.");
        }
    }
}
