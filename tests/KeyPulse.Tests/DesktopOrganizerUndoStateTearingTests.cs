using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class DesktopOrganizerUndoStateTearingTests
    {
        private const string NoUndoReport = "No recent restore to undo.";
        private const string NoIconsReport = "The selected profile contains no icon positions.";

        [TestInitialize]
        public void ResetUndoBuffer() => DesktopOrganizerService.TakeUndoSnapshot();

        [TestCleanup]
        public void ClearUndoBuffer() => DesktopOrganizerService.TakeUndoSnapshot();

        private static DesktopLayoutProfile MakeSnapshot(string name, int iconCount)
        {
            var profile = new DesktopLayoutProfile { Name = name };
            for (int i = 0; i < iconCount; i++)
            {
                profile.Icons.Add(new DesktopIconSnapshot { Name = name + "_" + i, AbsoluteX = i * 10, AbsoluteY = i * 20 });
            }
            return profile;
        }

        private static DesktopLayoutProfile MakeUnrestorableSnapshot(string name) => MakeSnapshot(name, 0);

        [TestMethod]
        public void HasUndo_ReflectsPublishAndTake()
        {
            Assert.IsFalse(DesktopOrganizerService.HasUndo, "Buffer should start empty.");

            var snapshot = MakeSnapshot("A", 3);
            DesktopOrganizerService.PublishUndoSnapshot(snapshot);
            Assert.IsTrue(DesktopOrganizerService.HasUndo);
            Assert.AreSame(snapshot, DesktopOrganizerService.PeekUndoSnapshot());

            Assert.AreSame(snapshot, DesktopOrganizerService.TakeUndoSnapshot());
            Assert.IsFalse(DesktopOrganizerService.HasUndo, "Take must clear the buffer.");
            Assert.IsNull(DesktopOrganizerService.TakeUndoSnapshot(), "Second take must observe the cleared buffer.");
        }

        [TestMethod]
        public void Publish_LastWriterWins()
        {
            var first = MakeSnapshot("first", 1);
            var second = MakeSnapshot("second", 2);
            DesktopOrganizerService.PublishUndoSnapshot(first);
            DesktopOrganizerService.PublishUndoSnapshot(second);

            Assert.AreSame(second, DesktopOrganizerService.PeekUndoSnapshot());
        }

        [TestMethod]
        public void TryReinstate_DoesNotOverwriteNewerSnapshot()
        {
            var claimed = MakeSnapshot("claimed", 1);
            var newer = MakeSnapshot("newer", 2);

            DesktopOrganizerService.PublishUndoSnapshot(newer);
            bool reinstated = DesktopOrganizerService.TryReinstateUndoSnapshot(claimed);

            Assert.IsFalse(reinstated, "A failed undo must not clobber a snapshot published by a newer restore.");
            Assert.AreSame(newer, DesktopOrganizerService.PeekUndoSnapshot());
        }

        [TestMethod]
        public void TryReinstate_RestoresIntoEmptyBuffer()
        {
            var claimed = MakeSnapshot("claimed", 1);

            Assert.IsTrue(DesktopOrganizerService.TryReinstateUndoSnapshot(claimed));
            Assert.AreSame(claimed, DesktopOrganizerService.PeekUndoSnapshot());
        }

        [TestMethod]
        public void UndoLastRestore_WithEmptyBuffer_ReportsNoUndo()
        {
            bool ok = DesktopOrganizerService.UndoLastRestore(out var report);

            Assert.IsFalse(ok);
            Assert.AreEqual(NoUndoReport, report);
            Assert.IsFalse(DesktopOrganizerService.HasUndo);
        }

        [TestMethod]
        public async Task UndoLastRestoreDetailedAsync_WithEmptyBuffer_ReportsNoUndo()
        {
            var (ok, report) = await DesktopOrganizerService.UndoLastRestoreDetailedAsync();

            Assert.IsFalse(ok);
            Assert.AreEqual(NoUndoReport, report);
            Assert.IsFalse(DesktopOrganizerService.HasUndo);
        }

        [TestMethod]
        public void UndoLastRestore_WhenRestoreFails_ReinstatesSameSnapshot()
        {
            var snapshot = MakeUnrestorableSnapshot("undo");
            DesktopOrganizerService.PublishUndoSnapshot(snapshot);

            bool ok = DesktopOrganizerService.UndoLastRestore(out var report);

            Assert.IsFalse(ok);
            Assert.AreEqual(NoIconsReport, report);
            Assert.IsTrue(DesktopOrganizerService.HasUndo, "A failed undo must keep the undo available.");
            Assert.AreSame(snapshot, DesktopOrganizerService.PeekUndoSnapshot());
        }

        [TestMethod]
        public async Task UndoLastRestoreDetailedAsync_WhenRestoreFails_ReinstatesSameSnapshot()
        {
            var snapshot = MakeUnrestorableSnapshot("undo");
            DesktopOrganizerService.PublishUndoSnapshot(snapshot);

            var (ok, report) = await DesktopOrganizerService.UndoLastRestoreDetailedAsync();

            Assert.IsFalse(ok);
            Assert.AreEqual(NoIconsReport, report);
            Assert.AreSame(snapshot, DesktopOrganizerService.PeekUndoSnapshot());
        }

        [TestMethod]
        public void UndoLastRestore_WhenCancelled_ReinstatesSnapshot()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var snapshot = MakeSnapshot("cancelled", 4);
            DesktopOrganizerService.PublishUndoSnapshot(snapshot);

            bool ok = DesktopOrganizerService.UndoLastRestore(out var report, cts.Token);

            Assert.IsFalse(ok);
            Assert.AreEqual("Restore cancelled.", report);
            Assert.AreSame(snapshot, DesktopOrganizerService.PeekUndoSnapshot());
        }

        [TestMethod]
        public async Task UndoLastRestoreDetailedAsync_WhenCancelled_ReinstatesSnapshot()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var snapshot = MakeSnapshot("cancelled", 4);
            DesktopOrganizerService.PublishUndoSnapshot(snapshot);

            var (ok, report) = await DesktopOrganizerService.UndoLastRestoreDetailedAsync(cts.Token);

            Assert.IsFalse(ok);
            Assert.AreEqual("Restore cancelled.", report);
            Assert.AreSame(snapshot, DesktopOrganizerService.PeekUndoSnapshot());
        }

        [TestMethod]
        public void UndoRestore_IsUndoOperation_NeverWritesUndoBuffer()
        {
            var unrelated = MakeSnapshot("unrelated", 2);
            DesktopOrganizerService.PublishUndoSnapshot(unrelated);

            DesktopOrganizerService.RestoreLayout(MakeUnrestorableSnapshot("x"), UnsavedIconBehavior.LeaveInPlace, out _, isUndoOperation: true);

            Assert.AreSame(unrelated, DesktopOrganizerService.PeekUndoSnapshot());
        }

        [TestMethod]
        public void ConcurrentTake_OnlyOneThreadClaimsSnapshot()
        {
            const int rounds = 200;
            const int threads = 8;

            for (int round = 0; round < rounds; round++)
            {
                var snapshot = MakeSnapshot("round" + round, 1);
                DesktopOrganizerService.PublishUndoSnapshot(snapshot);

                int claims = 0;
                using var barrier = new Barrier(threads);
                var workers = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    if (DesktopOrganizerService.TakeUndoSnapshot() != null)
                    {
                        Interlocked.Increment(ref claims);
                    }
                })).ToArray();
                Task.WaitAll(workers);

                Assert.AreEqual(1, claims, $"Round {round}: exactly one thread may claim the undo snapshot.");
                Assert.IsFalse(DesktopOrganizerService.HasUndo);
            }
        }

        [TestMethod]
        public void ConcurrentPublishAndTake_NoSnapshotClaimedTwiceAndNoForeignInstances()
        {
            const int publishers = 4;
            const int takers = 4;
            const int perPublisher = 5000;

            var published = new ConcurrentDictionary<DesktopLayoutProfile, byte>(ReferenceEqualityComparer.Instance);
            var taken = new ConcurrentBag<DesktopLayoutProfile>();
            using var stop = new CancellationTokenSource();
            using var start = new Barrier(publishers + takers);

            var publisherTasks = Enumerable.Range(0, publishers).Select(p => Task.Run(() =>
            {
                start.SignalAndWait();
                for (int i = 0; i < perPublisher; i++)
                {
                    var s = MakeSnapshot($"p{p}_{i}", 3);
                    published[s] = 0;
                    DesktopOrganizerService.PublishUndoSnapshot(s);
                }
            })).ToArray();

            var takerTasks = Enumerable.Range(0, takers).Select(t => Task.Run(() =>
            {
                start.SignalAndWait();
                while (!stop.IsCancellationRequested)
                {
                    _ = DesktopOrganizerService.HasUndo;
                    var s = DesktopOrganizerService.TakeUndoSnapshot();
                    if (s != null)
                    {
                        Assert.AreEqual(3, s.Icons.Count, "Claimed snapshot must be fully constructed.");
                        taken.Add(s);
                    }
                }
            })).ToArray();

            Task.WaitAll(publisherTasks);
            stop.Cancel();
            Task.WaitAll(takerTasks);

            var remaining = DesktopOrganizerService.TakeUndoSnapshot();
            if (remaining != null) taken.Add(remaining);

            var distinct = new HashSet<DesktopLayoutProfile>(ReferenceEqualityComparer.Instance);
            foreach (var s in taken)
            {
                Assert.IsTrue(published.ContainsKey(s), "Only published snapshots may ever be observed.");
                Assert.IsTrue(distinct.Add(s), "A snapshot must never be claimed by more than one undo.");
            }
        }

        [TestMethod]
        public async Task ConcurrentUndoAndBackgroundRestore_PreservesConsistentUndoState()
        {
            const int undoWorkers = 4;
            const int iterations = 300;

            var userSnapshot = MakeUnrestorableSnapshot("user");
            DesktopOrganizerService.PublishUndoSnapshot(userSnapshot);

            var published = new ConcurrentDictionary<DesktopLayoutProfile, byte>(ReferenceEqualityComparer.Instance);
            published[userSnapshot] = 0;
            var reports = new ConcurrentBag<string>();
            using var start = new Barrier(undoWorkers + 1);

            var background = Task.Run(() =>
            {
                start.SignalAndWait();
                for (int i = 0; i < iterations; i++)
                {
                    var s = MakeUnrestorableSnapshot("bg" + i);
                    published[s] = 0;
                    DesktopOrganizerService.PublishUndoSnapshot(s);
                }
            });

            var undos = Enumerable.Range(0, undoWorkers).Select(w => Task.Run(async () =>
            {
                start.SignalAndWait();
                for (int i = 0; i < iterations / 4; i++)
                {
                    if ((i & 1) == 0)
                    {
                        DesktopOrganizerService.UndoLastRestore(out var report);
                        reports.Add(report);
                    }
                    else
                    {
                        var (_, report) = await DesktopOrganizerService.UndoLastRestoreDetailedAsync();
                        reports.Add(report);
                    }
                }
            })).ToArray();

            await Task.WhenAll(undos.Append(background));

            foreach (var r in reports)
            {
                Assert.IsTrue(r == NoUndoReport || r == NoIconsReport, "Unexpected undo report: " + r);
            }

            var final = DesktopOrganizerService.PeekUndoSnapshot();
            Assert.IsNotNull(final, "Failed undos racing a background restore must not drop the undo buffer.");
            Assert.IsTrue(published.ContainsKey(final), "Undo buffer must only ever hold a published snapshot.");
        }

        [TestMethod]
        public async Task ConcurrentUndos_OnSingleSnapshot_NeverLoseIt()
        {
            const int workers = 8;
            var snapshot = MakeUnrestorableSnapshot("only");
            DesktopOrganizerService.PublishUndoSnapshot(snapshot);

            using var start = new Barrier(workers);
            var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
            {
                start.SignalAndWait();
                for (int i = 0; i < 25; i++)
                {
                    await DesktopOrganizerService.UndoLastRestoreDetailedAsync();
                }
            })).ToArray();

            await Task.WhenAll(tasks);

            Assert.AreSame(snapshot, DesktopOrganizerService.PeekUndoSnapshot(),
                "With no competing publisher, failed undos must always hand the same snapshot back.");
        }
    }
}
