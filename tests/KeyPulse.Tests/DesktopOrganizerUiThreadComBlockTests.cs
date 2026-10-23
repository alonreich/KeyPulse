using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class DesktopOrganizerUiThreadComBlockTests
    {
        [TestMethod]
        public void RunInSta_WhenCalledFromStaThread_OffloadsToDedicatedWorkerThread()
        {
            Thread? callingThread = null;
            Thread? executedThread = null;
            ApartmentState executedApartment = ApartmentState.Unknown;

            var staCallerThread = new Thread(() =>
            {
                callingThread = Thread.CurrentThread;
                executedThread = DesktopOrganizerService.RunInSta(() =>
                {
                    executedApartment = Thread.CurrentThread.GetApartmentState();
                    return Thread.CurrentThread;
                });
            });
            staCallerThread.SetApartmentState(ApartmentState.STA);
            staCallerThread.Start();
            staCallerThread.Join(5000);

            Assert.IsNotNull(callingThread, "Calling STA thread should have started.");
            Assert.IsNotNull(executedThread, "Worker thread should have executed the action.");
            Assert.AreNotEqual(callingThread, executedThread,
                "Action must NOT execute on the calling STA thread (Avalonia UI dispatcher). It must be offloaded.");
            Assert.AreEqual(ApartmentState.STA, executedApartment,
                "Dedicated worker thread must be configured with ApartmentState.STA.");
            Assert.AreEqual(DesktopOrganizerService.StaWorkerThreadForTesting, executedThread,
                "Action must execute on the dedicated DesktopOrganizerService STA worker thread.");
        }

        [TestMethod]
        public async Task RunInStaAsync_WhenCalledFromStaThread_OffloadsAsynchronously()
        {
            Thread? callingThread = null;
            Thread? executedThread = null;

            var tcs = new TaskCompletionSource<bool>();

            var staCallerThread = new Thread(async () =>
            {
                try
                {
                    callingThread = Thread.CurrentThread;
                    executedThread = await DesktopOrganizerService.RunInStaAsync(() => Thread.CurrentThread);
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            staCallerThread.SetApartmentState(ApartmentState.STA);
            staCallerThread.Start();

            bool success = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(success, "Async operation should complete successfully.");
            Assert.IsNotNull(callingThread, "Calling STA thread should be known.");
            Assert.IsNotNull(executedThread, "Executed thread should be known.");
            Assert.AreNotEqual(callingThread, executedThread,
                "Async Shell COM execution must never execute on the calling STA dispatcher thread.");
            Assert.AreEqual(DesktopOrganizerService.StaWorkerThreadForTesting, executedThread,
                "Async operation must execute on the dedicated background STA worker thread.");
        }

        [TestMethod]
        public void RunInSta_NestedReentrantCallOnWorkerThread_DoesNotDeadlock()
        {
            bool innerExecuted = false;

            var result = DesktopOrganizerService.RunInSta(() =>
            {
                return DesktopOrganizerService.RunInSta(() =>
                {
                    innerExecuted = true;
                    return 42;
                });
            });

            Assert.IsTrue(innerExecuted, "Nested action should have executed.");
            Assert.AreEqual(42, result, "Nested action should return expected result without deadlocking.");
        }

        [TestMethod]
        public async Task RunInStaAsync_NestedReentrantCallOnWorkerThread_DoesNotDeadlock()
        {
            bool innerExecuted = false;

            var result = await DesktopOrganizerService.RunInStaAsync(async () =>
            {
                return await DesktopOrganizerService.RunInStaAsync(() =>
                {
                    innerExecuted = true;
                    return 99;
                });
            });

            var finalVal = await result;
            Assert.IsTrue(innerExecuted, "Nested async action should have executed.");
            Assert.AreEqual(99, finalVal, "Nested async action should return expected result without deadlocking.");
        }

        [TestMethod]
        public void DedicatedWorkerThread_IsBackgroundStaThread()
        {
            DesktopOrganizerService.RunInSta(() => 1);
            var worker = DesktopOrganizerService.StaWorkerThreadForTesting;

            Assert.IsNotNull(worker, "Dedicated STA worker thread should be running.");
            Assert.IsTrue(worker.IsBackground, "Dedicated STA worker must be a background thread so it does not block process shutdown.");
            Assert.AreEqual(ApartmentState.STA, worker.GetApartmentState(), "Dedicated worker must be ApartmentState.STA.");
        }

        [TestMethod]
        public async Task RunInStaAsync_WithCancelledToken_ReturnsCanceledTaskImmediately()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            bool actionExecuted = false;
            var task = DesktopOrganizerService.RunInStaAsync(() =>
            {
                actionExecuted = true;
                return 100;
            }, cts.Token);

            Assert.IsTrue(task.IsCanceled, "Task should immediately be marked as canceled.");
            Assert.IsFalse(actionExecuted, "Action should never execute when token is already canceled.");

            await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await task);
        }

        [TestMethod]
        public void RunInSta_ExceptionReThrowingSemantics_PreservesOriginalException()
        {
            var testEx = new InvalidOperationException("Simulated COM failure in test");

            var thrown = Assert.ThrowsException<InvalidOperationException>(() =>
            {
                DesktopOrganizerService.RunInSta<int>(() => throw testEx);
            });

            Assert.AreSame(testEx, thrown, "RunInSta must rethrow the exact original exception instance.");
        }

        [TestMethod]
        public async Task RunInStaAsync_ExceptionReThrowingSemantics_PropagatesToAwaiter()
        {
            var testEx = new ArgumentOutOfRangeException("Simulated parameter fault");

            var thrown = await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(async () =>
            {
                await DesktopOrganizerService.RunInStaAsync<int>(() => throw testEx);
            });

            Assert.AreEqual(testEx.Message, thrown.Message, "RunInStaAsync must propagate exact exception type and message.");
        }

        [TestMethod]
        public void CaptureCurrentLayout_WhenCancelled_ReturnsNullAndReportsCancelled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var profile = DesktopOrganizerService.CaptureCurrentLayout("TestProfile", out var error, cts.Token);

            Assert.IsNull(profile, "Cancelled capture must return null profile.");
            Assert.AreEqual("Operation cancelled.", error, "Cancelled capture must report 'Operation cancelled.'.");
        }

        [TestMethod]
        public async Task CaptureCurrentLayoutDetailedAsync_WhenCancelled_ReturnsNullAndReportsCancelled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var (profile, error) = await DesktopOrganizerService.CaptureCurrentLayoutDetailedAsync("TestProfile", cts.Token);

            Assert.IsNull(profile, "Cancelled async capture must return null profile.");
            Assert.AreEqual("Operation cancelled.", error, "Cancelled async capture must report 'Operation cancelled.'.");
        }

        [TestMethod]
        public void RestoreLayout_WhenCancelled_ReturnsFalseAndReportsCancelled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var dummyProfile = new DesktopLayoutProfile
            {
                Name = "Test",
                Icons = { new DesktopIconSnapshot { Name = "Icon1", AbsoluteX = 10, AbsoluteY = 10 } }
            };

            bool success = DesktopOrganizerService.RestoreLayout(dummyProfile, UnsavedIconBehavior.LeaveInPlace, out var report, cts.Token);

            Assert.IsFalse(success, "Cancelled restore must return false.");
            Assert.AreEqual("Restore cancelled.", report, "Cancelled restore must report 'Restore cancelled.'.");
        }

        [TestMethod]
        public async Task RestoreLayoutDetailedAsync_WhenCancelled_ReturnsFalseAndReportsCancelled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var dummyProfile = new DesktopLayoutProfile
            {
                Name = "Test",
                Icons = { new DesktopIconSnapshot { Name = "Icon1", AbsoluteX = 10, AbsoluteY = 10 } }
            };

            var (success, report) = await DesktopOrganizerService.RestoreLayoutDetailedAsync(dummyProfile, UnsavedIconBehavior.LeaveInPlace, cts.Token);

            Assert.IsFalse(success, "Cancelled async restore must return false.");
            Assert.AreEqual("Restore cancelled.", report, "Cancelled async restore must report 'Restore cancelled.'.");
        }

        [TestMethod]
        public async Task RestoreLayoutAsync_PublicInterfaceParity_ReturnsBool()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var dummyProfile = new DesktopLayoutProfile
            {
                Name = "Test",
                Icons = { new DesktopIconSnapshot { Name = "Icon1", AbsoluteX = 10, AbsoluteY = 10 } }
            };

            bool success = await DesktopOrganizerService.RestoreLayoutAsync(dummyProfile, UnsavedIconBehavior.LeaveInPlace, cts.Token);
            Assert.IsFalse(success, "RestoreLayoutAsync should return false when cancelled.");
        }

        [TestMethod]
        public async Task CaptureCurrentLayoutAsync_PublicInterfaceParity_ReturnsProfileOrNull()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var profile = await DesktopOrganizerService.CaptureCurrentLayoutAsync("TestProfile", cts.Token);
            Assert.IsNull(profile, "CaptureCurrentLayoutAsync should return null when cancelled.");
        }
    }
}
