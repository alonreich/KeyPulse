using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    /// <summary>
    /// TASK 9 - PROGRAM_AUDIO_STREAM_STARVATION: PlaySound must cache each WAV once in pinned memory and hand it to
    /// winmm with SND_ASYNC | SND_MEMORY, so playback never parks a ThreadPool worker. A 50 ms per-sound throttle
    /// drops redundant re-triggers, SoundEnabled still mutes, and missing assets / failing audio stay silent.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class ProgramAudioStreamStarvationTests
    {
        private bool _originalSoundEnabled;
        private long _fakeNow;
        private ConcurrentQueue<(byte[] Buffer, uint Flags, int ThreadId)> _calls = new();

        [TestInitialize]
        public void Setup()
        {
            _originalSoundEnabled = Program.SoundEnabled;
            Program.SoundEnabled = true;
            Program.ResetSoundStateForTesting();
            _calls = new ConcurrentQueue<(byte[], uint, int)>();
            _fakeNow = 1_000_000;
            Program.SoundClockOverrideForTesting = () => Interlocked.Read(ref _fakeNow);
            Program.PlaySoundNativeOverrideForTesting = (buffer, flags) =>
            {
                _calls.Enqueue((buffer, flags, Environment.CurrentManagedThreadId));
                return true;
            };
        }

        [TestCleanup]
        public void Cleanup()
        {
            Program.PlaySoundNativeOverrideForTesting = null;
            Program.SoundClockOverrideForTesting = null;
            Program.ResetSoundStateForTesting();
            Program.SoundEnabled = _originalSoundEnabled;
        }

        private void Advance(long ms) => Interlocked.Add(ref _fakeNow, ms);

        [TestMethod]
        public void SoundDisabled_NeverLoadsOrPlays()
        {
            Program.SoundEnabled = false;
            Program.PlaySound("click");
            Assert.AreEqual(0, _calls.Count);
            Assert.AreEqual(0, Program.SoundCacheCountForTesting, "Muted calls must not even touch the cache.");
        }

        [TestMethod]
        public void UsesAsyncMemoryFlags_WithoutDefaultBeep()
        {
            Program.PlaySound("click");
            Assert.AreEqual(1, _calls.Count);
            _calls.TryPeek(out var call);
            Assert.AreEqual(Program.SND_ASYNC | Program.SND_MEMORY | Program.SND_NODEFAULT, call.Flags);
            Assert.AreNotEqual(0u, call.Flags & Program.SND_ASYNC);
            Assert.AreNotEqual(0u, call.Flags & Program.SND_MEMORY);
        }

        [TestMethod]
        public void PlaysOnCallerThread_NoThreadPoolHop()
        {
            int caller = Environment.CurrentManagedThreadId;
            Program.PlaySound("click");
            _calls.TryPeek(out var call);
            Assert.AreEqual(caller, call.ThreadId, "Playback dispatch must not be queued to the ThreadPool.");
        }

        [TestMethod]
        public void LoadsManifestResourceExactlyOnce_AndBufferIsAValidWav()
        {
            using var expected = typeof(Program).Assembly.GetManifestResourceStream("KeyPulse.Assets.click.wav");
            Assert.IsNotNull(expected, "Resource naming convention KeyPulse.Assets.{name}.wav must be preserved.");

            Program.PlaySound("click");
            var first = Program.GetCachedSoundForTesting("click");
            Advance(1000);
            Program.PlaySound("click");
            var second = Program.GetCachedSoundForTesting("click");

            Assert.IsNotNull(first);
            Assert.AreSame(first, second, "The WAV must be read once and reused.");
            Assert.AreEqual(expected!.Length, first!.Length);
            Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(first, 0, 4));
            Assert.AreEqual("WAVE", System.Text.Encoding.ASCII.GetString(first, 8, 4));

            var played = _calls.Select(c => c.Buffer).ToArray();
            Assert.AreEqual(2, played.Length);
            Assert.IsTrue(played.All(b => ReferenceEquals(b, first)), "Every playback must hand winmm the cached buffer.");
        }

        [TestMethod]
        public void CachedBuffer_IsPinned_SoAsyncPlaybackCannotReadMovedMemory()
        {
            Program.PlaySound("error");
            var buffer = Program.GetCachedSoundForTesting("error")!;
            var before = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0);

            for (int i = 0; i < 3; i++)
            {
                _ = Enumerable.Range(0, 20_000).Select(n => new byte[64]).ToList();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }

            var after = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0);
            Assert.AreEqual(before, after, "A compacting GC moved the buffer winmm is still reading.");
        }

        [TestMethod]
        public void AllEmbeddedSounds_AreCachedIndependently()
        {
            foreach (var name in new[] { "click", "close", "error" })
            {
                Program.PlaySound(name);
            }
            Assert.AreEqual(3, Program.SoundCacheCountForTesting);
            Assert.AreEqual(3, _calls.Count, "Different sounds must not throttle each other.");
            Assert.AreEqual(3, _calls.Select(c => c.Buffer).Distinct().Count());
        }

        [TestMethod]
        public void SameSoundWithin50ms_IsThrottled_AndPlaysAgainAfterWindow()
        {
            Program.PlaySound("click");
            Advance(10);
            Program.PlaySound("click");
            Advance(Program.SoundThrottleWindowMs - 11);
            Program.PlaySound("click");
            Assert.AreEqual(1, _calls.Count, "Re-triggers inside the 50 ms window must be dropped.");

            Advance(1);
            Program.PlaySound("click");
            Assert.AreEqual(2, _calls.Count, "Exactly at the window boundary the sound must play again.");
        }

        [TestMethod]
        public void ConcurrentBurst_SameInstant_PlaysExactlyOnce()
        {
            using var start = new Barrier(16);
            var tasks = Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                for (int i = 0; i < 50; i++) Program.PlaySound("click");
            }, TaskCreationOptions.LongRunning)).ToArray();
            Assert.IsTrue(Task.WaitAll(tasks, 10_000));

            Assert.AreEqual(1, _calls.Count, "Lock-free throttle must admit exactly one caller per window.");
            Assert.AreEqual(1, Program.SoundCacheCountForTesting);
        }

        [TestMethod]
        public void MissingAsset_IsSilent_AndCachedAsMiss()
        {
            Program.PlaySound("does_not_exist");
            Advance(1000);
            Program.PlaySound("does_not_exist");

            Assert.AreEqual(0, _calls.Count);
            var cached = Program.GetCachedSoundForTesting("does_not_exist");
            Assert.IsNotNull(cached, "A miss is cached so the manifest is not re-scanned on every trigger.");
            Assert.AreEqual(0, cached!.Length);
        }

        [TestMethod]
        public void NativeFailureOrThrow_IsSwallowed()
        {
            Program.PlaySoundNativeOverrideForTesting = (_, _) => false;
            Program.PlaySound("click");

            Advance(1000);
            Program.PlaySoundNativeOverrideForTesting = (_, _) => throw new InvalidOperationException("no audio endpoint");
            Program.PlaySound("click");
        }

        [TestMethod]
        public void RealWinmm_RapidTriggers_DoNotThrow()
        {
            Program.PlaySoundNativeOverrideForTesting = null;
            Program.SoundClockOverrideForTesting = null;
            for (int i = 0; i < 100; i++)
            {
                Program.PlaySound(i % 3 == 0 ? "click" : i % 3 == 1 ? "close" : "error");
            }
            Thread.Sleep(200);
        }

        [TestMethod]
        public void HundredRapidPresses_WithRealWinmm_DoNotBlockThreadPool()
        {
            Program.PlaySoundNativeOverrideForTesting = null;
            Program.SoundClockOverrideForTesting = null;
            ThreadPool.GetAvailableThreads(out int availBefore, out _);
            long pendingBefore = ThreadPool.PendingWorkItemCount;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 100; i++)
            {
                Program.PlaySound("click");
                Thread.Sleep(1);
            }
            sw.Stop();

            ThreadPool.GetAvailableThreads(out int availAfter, out _);
            Assert.IsTrue(availAfter >= availBefore - 2, $"Available workers dropped {availBefore} -> {availAfter}.");
            Assert.IsTrue(ThreadPool.PendingWorkItemCount <= pendingBefore + 2, "Sound playback queued work to the ThreadPool.");
            Assert.IsTrue(sw.ElapsedMilliseconds < 5_000, $"100 async triggers took {sw.ElapsedMilliseconds} ms; the call is blocking.");

            var probe = Task.Run(() => 42);
            Assert.IsTrue(probe.Wait(1_000), "ThreadPool starved after a burst of sound triggers.");
        }

        [TestMethod]
        public void HundredRapidPresses_ViaSeam_QueueNoWorkItems()
        {
            long completedBefore = ThreadPool.CompletedWorkItemCount;
            int threadsBefore = ThreadPool.ThreadCount;
            for (int i = 0; i < 100; i++)
            {
                Advance(Program.SoundThrottleWindowMs);
                Program.PlaySound("click");
            }
            Assert.AreEqual(100, _calls.Count, "Spaced presses must each play.");
            Assert.IsTrue(_calls.All(c => c.ThreadId == Environment.CurrentManagedThreadId));
            Assert.IsTrue(ThreadPool.ThreadCount <= threadsBefore + 2, "ThreadPool grew during sound playback.");
            Assert.IsTrue(ThreadPool.CompletedWorkItemCount - completedBefore < 50, "PlaySound is dispatching through the ThreadPool.");
        }
    }
}
