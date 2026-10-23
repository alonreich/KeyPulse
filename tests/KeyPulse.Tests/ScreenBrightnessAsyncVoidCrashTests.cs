using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class ScreenBrightnessAsyncVoidCrashTests
    {
        private string? _sandbox;

        [TestInitialize]
        public void Setup()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), $"kp_test_sb_asyncvoid_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_sandbox);
            ConfigStore.ConfigDirectory = _sandbox;
            ScreenBrightnessService.LiveReadOverrideForTesting = null;
            ScreenBrightnessService.ResetToDefaults();
        }

        [TestCleanup]
        public void Cleanup()
        {
            ScreenBrightnessService.LiveReadOverrideForTesting = null;
            ConfigStore.ConfigDirectory = null!;
            if (!string.IsNullOrEmpty(_sandbox) && Directory.Exists(_sandbox))
            {
                try { Directory.Delete(_sandbox, true); } catch { }
            }
        }

        private static void SetOverride(Func<CancellationToken, Task<List<ScreenLiveState>>> reader) =>
            ScreenBrightnessService.LiveReadOverrideForTesting = reader;

        [TestMethod]
        public async Task InvalidOperationDuringLiveRead_FallsBackToCachedStates()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.Screens[0].LockRatio = false;
            cfg.Screens[0].LastBrightness = 61;
            cfg.Screens[0].LastContrast = 42;
            cfg.Screens[1].CustomNickname = "Desk Right";
            Assert.IsTrue(ScreenBrightnessService.SaveConfig(cfg));

            SetOverride(_ => throw new InvalidOperationException("Sequence contains no matching element"));

            var outcome = await ScreenBrightnessService.ReadAllScreensLiveOrCachedAsync(CancellationToken.None);

            Assert.IsTrue(outcome.IsFallback);
            Assert.IsInstanceOfType(outcome.Error, typeof(InvalidOperationException));
            Assert.AreEqual(ScreenBrightnessService.MaxScreens, outcome.States.Count);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, outcome.States.Select(s => s.SlotIndex).ToArray());
            Assert.AreEqual(61, outcome.States[0].Brightness);
            Assert.AreEqual(42, outcome.States[0].Contrast);
            Assert.AreEqual("Desk Right", outcome.States[1].Nickname);
        }

        [TestMethod]
        public async Task Win32ExceptionDuringEnumeration_FallsBackToCachedStates()
        {
            SetOverride(_ => throw new Win32Exception(6, "The handle is invalid."));

            var outcome = await ScreenBrightnessService.ReadAllScreensLiveOrCachedAsync(CancellationToken.None);

            Assert.IsTrue(outcome.IsFallback);
            Assert.IsInstanceOfType(outcome.Error, typeof(Win32Exception));
            Assert.AreEqual(ScreenBrightnessService.MaxScreens, outcome.States.Count);
        }

        [TestMethod]
        public async Task FaultAfterAwait_IsStillCaught()
        {
            SetOverride(async ct =>
            {
                await Task.Delay(20, ct);
                throw new IndexOutOfRangeException("slot unmapped");
            });

            var outcome = await ScreenBrightnessService.ReadAllScreensLiveOrCachedAsync(CancellationToken.None);

            Assert.IsTrue(outcome.IsFallback);
            Assert.IsInstanceOfType(outcome.Error, typeof(IndexOutOfRangeException));
            Assert.AreEqual(ScreenBrightnessService.MaxScreens, outcome.States.Count);
        }

        [TestMethod]
        public async Task SuccessfulLiveRead_IsPassedThroughUntouched()
        {
            var live = new List<ScreenLiveState>
            {
                new ScreenLiveState { SlotIndex = 0, Brightness = 77, Contrast = 33, IsConnected = true }
            };
            SetOverride(_ => Task.FromResult(live));

            var outcome = await ScreenBrightnessService.ReadAllScreensLiveOrCachedAsync(CancellationToken.None);

            Assert.IsFalse(outcome.IsFallback);
            Assert.IsNull(outcome.Error);
            Assert.AreSame(live, outcome.States);
        }

        [TestMethod]
        public async Task NullLiveResult_BecomesEmptyList()
        {
            SetOverride(_ => Task.FromResult<List<ScreenLiveState>>(null!));

            var outcome = await ScreenBrightnessService.ReadAllScreensLiveOrCachedAsync(CancellationToken.None);

            Assert.IsFalse(outcome.IsFallback);
            Assert.IsNotNull(outcome.States);
            Assert.AreEqual(0, outcome.States.Count);
        }

        [TestMethod]
        public async Task Cancellation_StillPropagatesAsOperationCanceled()
        {
            using var cts = new CancellationTokenSource();
            SetOverride(async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new List<ScreenLiveState>();
            });

            var pending = ScreenBrightnessService.ReadAllScreensLiveOrCachedAsync(cts.Token);
            cts.Cancel();

            bool canceled = false;
            try { await pending; }
            catch (OperationCanceledException) { canceled = true; }
            Assert.IsTrue(canceled, "Cooperative cancellation must abort cleanly, not fall back.");
        }

        [TestMethod]
        public async Task FatalException_IsNotSwallowed()
        {
            SetOverride(_ => throw new OutOfMemoryException());

            bool thrown = false;
            try { await ScreenBrightnessService.ReadAllScreensLiveOrCachedAsync(CancellationToken.None); }
            catch (OutOfMemoryException) { thrown = true; }
            Assert.IsTrue(thrown);
        }

        [TestMethod]
        public void IsFatalException_ClassifiesCorrectly()
        {
            Assert.IsTrue(ScreenBrightnessService.IsFatalException(new OutOfMemoryException()));
            Assert.IsTrue(ScreenBrightnessService.IsFatalException(new InsufficientExecutionStackException()));
            Assert.IsFalse(ScreenBrightnessService.IsFatalException(new InvalidOperationException()));
            Assert.IsFalse(ScreenBrightnessService.IsFatalException(new Win32Exception()));
            Assert.IsFalse(ScreenBrightnessService.IsFatalException(new OperationCanceledException()));
        }

        [TestMethod]
        public void GetSlotSettingOrDefault_MissingSlot_ReturnsDefaultInsteadOfThrowing()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.Screens.RemoveAll(s => s.SlotIndex == 2);
            cfg.Screens.Insert(0, null!);

            var found = ScreenBrightnessService.GetSlotSettingOrDefault(cfg, 1);
            Assert.AreSame(cfg.Screens.First(s => s != null && s.SlotIndex == 1), found);

            var fallback = ScreenBrightnessService.GetSlotSettingOrDefault(cfg, 2);
            Assert.IsNotNull(fallback);
            Assert.AreEqual(2, fallback.SlotIndex);
            Assert.AreEqual(ScreenBrightnessService.DefaultSlotName(2), fallback.CustomNickname);

            cfg.Screens = null!;
            var fromNull = ScreenBrightnessService.GetSlotSettingOrDefault(cfg, 0);
            Assert.AreEqual(0, fromNull.SlotIndex);
        }

        [TestMethod]
        public void GetCachedStatesSafe_AlwaysReturnsEverySlot()
        {
            var states = ScreenBrightnessService.GetCachedStatesSafe();
            Assert.AreEqual(ScreenBrightnessService.MaxScreens, states.Count);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, states.Select(s => s.SlotIndex).ToArray());
        }

        [TestMethod]
        public async Task LiveRead_DuringConcurrentConfigMutation_NeverEscapes()
        {
            var errors = new ConcurrentBag<Exception>();
            using var cts = new CancellationTokenSource();

            var writer = Task.Run(() =>
            {
                int n = 0;
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var cfg = ScreenBrightnessService.LoadConfig();
                        cfg.Screens.RemoveAll(s => s.SlotIndex == (n % 3));
                        cfg.Screens.Reverse();
                        ScreenBrightnessService.SaveConfig(cfg);
                        n++;
                    }
                    catch (Exception ex) { errors.Add(ex); }
                }
            });

            int reads = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(1200);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var outcome = await ScreenBrightnessService.ReadAllScreensLiveOrCachedAsync(CancellationToken.None);
                    Assert.IsNotNull(outcome.States);
                    var cached = ScreenBrightnessService.GetImmediateCachedStates();
                    Assert.AreEqual(ScreenBrightnessService.MaxScreens, cached.Count);
                    reads++;
                }
                catch (Exception ex) { errors.Add(ex); }
            }

            cts.Cancel();
            await writer;

            Assert.AreEqual(0, errors.Count, "Escaped: " + string.Join(" | ", errors.Select(e => e.GetType().Name + ": " + e.Message)));
            Assert.IsTrue(reads > 0);
        }

        [TestMethod]
        public void AsyncVoidHandlerPattern_ThrowingRead_DoesNotReachSynchronizationContext()
        {
            SetOverride(_ => throw new InvalidOperationException("display enumeration torn"));

            var ctx = new RecordingSynchronizationContext();
            var previous = SynchronizationContext.Current;
            var done = new ManualResetEventSlim(false);
            string? status = null;
            List<ScreenLiveState>? rendered = null;

            try
            {
                SynchronizationContext.SetSynchronizationContext(ctx);
                SimulatedOnWindowOpened(s => status = s, st => rendered = st, done);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            Assert.IsTrue(ctx.Pump(done, TimeSpan.FromSeconds(10)), "Handler never completed.");
            Assert.AreEqual(0, ctx.Unhandled.Count, "Exception escaped async void: " + string.Join(" | ", ctx.Unhandled.Select(e => e.Message)));
            Assert.AreEqual("⚠️ Hardware read failed, using cached settings.", status);
            Assert.IsNotNull(rendered);
            Assert.AreEqual(ScreenBrightnessService.MaxScreens, rendered!.Count);
        }

        private static async void SimulatedOnWindowOpened(Action<string> setStatus, Action<List<ScreenLiveState>> render, ManualResetEventSlim done)
        {
            try
            {
                using var cts = new CancellationTokenSource();
                var outcome = await ScreenBrightnessService.ReadAllScreensLiveOrCachedAsync(cts.Token);
                render(outcome.States);
                setStatus(outcome.IsFallback
                    ? "⚠️ Hardware read failed, using cached settings."
                    : $"Live hardware read complete ({outcome.States.Count(s => s.IsConnected)} displays connected).");
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                done.Set();
            }
        }

        private sealed class RecordingSynchronizationContext : SynchronizationContext
        {
            private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
            public List<Exception> Unhandled { get; } = new();

            public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

            public bool Pump(ManualResetEventSlim done, TimeSpan timeout)
            {
                var end = DateTime.UtcNow + timeout;
                var previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    while (!done.IsSet && DateTime.UtcNow < end)
                    {
                        if (_queue.TryTake(out var item, 20))
                        {
                            try { item.Item1(item.Item2); }
                            catch (Exception ex) { Unhandled.Add(ex); }
                        }
                    }
                    while (_queue.TryTake(out var rest))
                    {
                        try { rest.Item1(rest.Item2); }
                        catch (Exception ex) { Unhandled.Add(ex); }
                    }
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
                return done.IsSet;
            }
        }
    }
}
