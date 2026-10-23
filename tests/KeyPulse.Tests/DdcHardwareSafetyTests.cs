using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class DdcHardwareSafetyTests
    {
        private string? _sandbox;
        private ConcurrentQueue<(int Slot, int B, int C, bool WB, bool WC)> _writes = new();
        private volatile bool _writeSucceeds = true;

        [TestInitialize]
        public void Setup()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), $"kp_test_ddc_safety_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_sandbox);
            ConfigStore.ConfigDirectory = _sandbox;
            ScreenBrightnessService.ResetToDefaults();
            ScreenBrightnessService.ResetWriteSchedulerForTesting();
            _writes = new ConcurrentQueue<(int, int, int, bool, bool)>();
            _writeSucceeds = true;
            ScreenBrightnessService.HardwareWriteOverrideForTesting = (slot, b, c, wb, wc) =>
            {
                _writes.Enqueue((slot, b, c, wb, wc));
                return _writeSucceeds;
            };
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
            ScreenBrightnessService.HardwareWriteOverrideForTesting = null;
            ConfigStore.ConfigDirectory = null!;
            if (!string.IsNullOrEmpty(_sandbox) && Directory.Exists(_sandbox))
            {
                try { Directory.Delete(_sandbox, true); } catch { }
            }
        }


        private sealed class FakeMonitor
        {
            public uint Current;
            public uint Min;
            public uint Max = 100;
            public bool ReadHighOk = true;
            public bool ReadVcpOk = true;
            public bool HighWriteOk = true;
            public bool VcpWriteOk = true;
            public int HighWrites;
            public int VcpWrites;
            public int Reads;
            public readonly List<string> Log = new();

            public DdcFeatureReading ReadHigh()
            {
                Reads++;
                Log.Add("read");
                return new DdcFeatureReading(ReadHighOk, Current, Min, Max);
            }

            public DdcFeatureReading ReadVcp()
            {
                Reads++;
                Log.Add("read");
                return new DdcFeatureReading(ReadVcpOk, Current, 0, Max);
            }

            public DdcWriteOutcome Write(DdcFeature feature, int percent) =>
                DdcWriteSafety.WriteFeature(
                    feature,
                    percent,
                    () => DdcWriteSafety.ReadFeature(ReadHigh, ReadVcp, Pause),
                    v => { HighWrites++; Log.Add("write"); if (HighWriteOk) Current = v; return HighWriteOk; },
                    v => { VcpWrites++; Log.Add("write"); if (VcpWriteOk) Current = v; return VcpWriteOk; },
                    Pause);

            public void Pause(int ms)
            {
                Assert.IsTrue(ms >= 50, $"Inter-command pause {ms} ms is below the 50 ms DDC/CI spacing.");
                Log.Add("pause");
            }
        }


        [TestMethod]
        public void UnchangedValue_IsNeverWritten()
        {
            var m = new FakeMonitor { Current = 40 };
            Assert.AreEqual(DdcWriteOutcome.Unchanged, m.Write(DdcFeature.Brightness, 40));
            Assert.AreEqual(0, m.HighWrites + m.VcpWrites);
        }

        [TestMethod]
        public void RepeatedSameTarget_WritesOnlyOnce()
        {
            var m = new FakeMonitor { Current = 10 };
            for (int i = 0; i < 50; i++) m.Write(DdcFeature.Brightness, 70);
            Assert.AreEqual(1, m.HighWrites + m.VcpWrites, "Only the first apply may reach the monitor's NVRAM.");
            Assert.AreEqual(70u, m.Current);
        }

        [TestMethod]
        public void LockedContrastEqualToCurrent_IsSkipped()
        {
            var m = new FakeMonitor { Current = DdcWriteSafety.ToRaw(DdcFeature.Contrast, 60, 0, 100) };
            Assert.AreEqual(DdcWriteOutcome.Unchanged, m.Write(DdcFeature.Contrast, 60));
            Assert.AreEqual(0, m.HighWrites + m.VcpWrites);
        }

        [TestMethod]
        public void ChangedValue_WrittenWithSingleCommand()
        {
            var m = new FakeMonitor { Current = 10 };
            Assert.AreEqual(DdcWriteOutcome.Written, m.Write(DdcFeature.Brightness, 55));
            Assert.AreEqual(1, m.HighWrites);
            Assert.AreEqual(0, m.VcpWrites);
            Assert.AreEqual(55u, m.Current);
        }


        [TestMethod]
        public void RefusingMonitor_GetsAtMostTwoWriteCommands()
        {
            var m = new FakeMonitor { Current = 10, HighWriteOk = false, VcpWriteOk = false };
            Assert.AreEqual(DdcWriteOutcome.Failed, m.Write(DdcFeature.Brightness, 80));
            Assert.AreEqual(1, m.HighWrites);
            Assert.AreEqual(1, m.VcpWrites);
        }

        [TestMethod]
        public void HighLevelFailure_FallsBackToVcpOnce()
        {
            var m = new FakeMonitor { Current = 10, HighWriteOk = false };
            Assert.AreEqual(DdcWriteOutcome.Written, m.Write(DdcFeature.Brightness, 80));
            Assert.AreEqual(1, m.HighWrites);
            Assert.AreEqual(1, m.VcpWrites);
        }

        [TestMethod]
        public void EveryCommandIsSeparatedByAPause()
        {
            var m = new FakeMonitor { Current = 10, ReadHighOk = false, HighWriteOk = false, VcpWriteOk = false };
            m.Write(DdcFeature.Brightness, 80);
            var commands = m.Log;
            for (int i = 1; i < commands.Count; i++)
            {
                Assert.IsFalse(commands[i] != "pause" && commands[i - 1] != "pause",
                    $"Commands '{commands[i - 1]}' and '{commands[i]}' were sent back to back: {string.Join(",", commands)}");
            }
        }


        [TestMethod]
        public void UnreadableMonitor_IsNeverWritten()
        {
            var m = new FakeMonitor { ReadHighOk = false, ReadVcpOk = false };
            Assert.AreEqual(DdcWriteOutcome.UnknownRange, m.Write(DdcFeature.Brightness, 80));
            Assert.AreEqual(0, m.HighWrites + m.VcpWrites);
        }

        [TestMethod]
        public void BogusRange_IsTreatedAsUnreadable()
        {
            Assert.AreEqual(DdcWriteOutcome.UnknownRange, new FakeMonitor { Min = 0, Max = 0, ReadVcpOk = false }.Write(DdcFeature.Brightness, 50));
            Assert.AreEqual(DdcWriteOutcome.UnknownRange, new FakeMonitor { Current = 300, Max = 100, ReadVcpOk = false }.Write(DdcFeature.Brightness, 50));
            Assert.AreEqual(DdcWriteOutcome.UnknownRange, new FakeMonitor { Min = 50, Max = 50, ReadVcpOk = false }.Write(DdcFeature.Brightness, 50));
        }

        [TestMethod]
        public void RawValuesStayInsideReportedRange()
        {
            for (int p = -50; p <= 150; p += 5)
            {
                uint b = DdcWriteSafety.ToRaw(DdcFeature.Brightness, p, 20, 80);
                uint c = DdcWriteSafety.ToRaw(DdcFeature.Contrast, p, 20, 80);
                Assert.IsTrue(b >= 20 && b <= 80, $"brightness raw {b} for {p}%");
                Assert.IsTrue(c >= 20 && c <= 80, $"contrast raw {c} for {p}%");
            }
            Assert.AreEqual(255u, DdcWriteSafety.ToRaw(DdcFeature.Brightness, 100, 0, 255));
            Assert.AreEqual(0u, DdcWriteSafety.ToRaw(DdcFeature.Brightness, 0, 0, 255), "Brightness keeps its full range (0% = hardware minimum).");
        }


        [TestMethod]
        public void ZeroContrast_NeverGoesBelowFloor()
        {
            Assert.AreEqual(5u, DdcWriteSafety.ToRaw(DdcFeature.Contrast, 0, 0, 100));
            Assert.AreEqual(13u, DdcWriteSafety.ToRaw(DdcFeature.Contrast, 0, 0, 255));
            Assert.AreEqual(100u, DdcWriteSafety.ToRaw(DdcFeature.Contrast, 100, 0, 100));
        }

        [TestMethod]
        public void ContrastPercent_RoundTripsThroughFloorMapping()
        {
            for (int p = 0; p <= 100; p++)
            {
                uint raw = DdcWriteSafety.ToRaw(DdcFeature.Contrast, p, 0, 100);
                Assert.AreEqual(p, DdcWriteSafety.ToPercent(DdcFeature.Contrast, raw, 0, 100), 1, $"{p}%");
            }
            Assert.AreEqual(0, DdcWriteSafety.ToPercent(DdcFeature.Contrast, 0, 0, 100), "Below-floor hardware value set via OSD reads as 0%.");
        }


        [TestMethod]
        public void Schedule_IsolatedChangeWritesNow_BurstWaits()
        {
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(DdcScheduleDecision.WriteNow, DdcWriteSafety.DecideSchedule(now, DateTime.MinValue, false));
            Assert.AreEqual(DdcScheduleDecision.WriteNow, DdcWriteSafety.DecideSchedule(now, now.AddMilliseconds(-DdcWriteSafety.SettleMs), false));
            Assert.AreEqual(DdcScheduleDecision.StartSettleTimer, DdcWriteSafety.DecideSchedule(now, now.AddMilliseconds(-100), false));
            Assert.AreEqual(DdcScheduleDecision.AlreadyScheduled, DdcWriteSafety.DecideSchedule(now, DateTime.MinValue, true));
        }

        [TestMethod]
        public void FlushDelay_WaitsForSettle_ButCapsAtMaxLatency()
        {
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(DdcWriteSafety.SettleMs, DdcWriteSafety.ComputeFlushDelayMs(t0, t0, t0));
            Assert.AreEqual(0, DdcWriteSafety.ComputeFlushDelayMs(t0.AddMilliseconds(DdcWriteSafety.SettleMs), t0, t0));
            var now = t0.AddMilliseconds(DdcWriteSafety.MaxLatencyMs - 100);
            Assert.AreEqual(100, DdcWriteSafety.ComputeFlushDelayMs(now, now, t0), "Input still arriving: flush at the max-latency deadline.");
            Assert.AreEqual(0, DdcWriteSafety.ComputeFlushDelayMs(t0.AddMilliseconds(DdcWriteSafety.MaxLatencyMs), now, t0));
        }


        private int WriteCount(int slot) => _writes.Count(w => w.Slot == slot);

        [TestMethod]
        public void SinglePress_WritesImmediatelyOnce()
        {
            ScreenBrightnessService.RequestScreenLevelChange(0, 70, 70);
            Thread.Sleep(DdcWriteSafety.SettleMs + 400);
            Assert.AreEqual(1, WriteCount(0));
            Assert.AreEqual(70, _writes.Last(w => w.Slot == 0).B);
        }

        [TestMethod]
        public void HeldHotkeyBurst_WritesFirstAndFinalValueOnly()
        {
            for (int i = 0; i < 20; i++)
            {
                ScreenBrightnessService.RequestScreenLevelChange(0, 30 + i, 30 + i);
                Thread.Sleep(30);
            }
            Thread.Sleep(DdcWriteSafety.SettleMs + 500);

            Assert.IsTrue(WriteCount(0) <= 2, $"20 rapid steps produced {WriteCount(0)} monitor writes (old limiter: ~3-4).");
            Assert.AreEqual(49, _writes.Last(w => w.Slot == 0).B, "The final value must always reach the monitor.");
        }

        [TestMethod]
        public void ContinuousDrag_IsBoundedByMaxLatency()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int v = 0;
            while (sw.ElapsedMilliseconds < 3000)
            {
                ScreenBrightnessService.RequestScreenLevelChange(1, v % 101, v % 101);
                v++;
                Thread.Sleep(20);
            }
            int last = (v - 1) % 101;
            Thread.Sleep(DdcWriteSafety.SettleMs + 500);

            int writes = WriteCount(1);
            int ceiling = 1 + (3000 / DdcWriteSafety.MaxLatencyMs) + 2;
            Assert.IsTrue(writes <= ceiling, $"3 s continuous drag produced {writes} writes (ceiling {ceiling}; old limiter: ~12).");
            Assert.IsTrue(writes >= 2, "User must still see progress during a long drag.");
            Assert.AreEqual(last, _writes.Last(w => w.Slot == 1).B);
        }

        [TestMethod]
        public void FailedWrite_IsNotRetriedAutomatically()
        {
            _writeSucceeds = false;
            ScreenBrightnessService.RequestScreenLevelChange(2, 80, 80);
            Thread.Sleep(DdcWriteSafety.MaxLatencyMs + 1000);
            Assert.AreEqual(1, WriteCount(2), "A refusing monitor must not be hammered with automatic retries.");

            _writeSucceeds = true;
            ScreenBrightnessService.RequestScreenLevelChange(2, 81, 81);
            Thread.Sleep(DdcWriteSafety.SettleMs + 400);
            Assert.AreEqual(2, WriteCount(2), "The next user action re-arms writing.");
            Assert.AreEqual(81, _writes.Last(w => w.Slot == 2).B);
        }

        [TestMethod]
        public void IndependentScreens_HaveIndependentSchedules()
        {
            ScreenBrightnessService.RequestScreenLevelChange(0, 11, 11);
            ScreenBrightnessService.RequestScreenLevelChange(1, 22, 22);
            Thread.Sleep(DdcWriteSafety.SettleMs + 400);
            Assert.AreEqual(1, WriteCount(0));
            Assert.AreEqual(1, WriteCount(1));
        }
    }
}
