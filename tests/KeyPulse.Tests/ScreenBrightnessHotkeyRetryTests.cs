using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    /// <summary>
    /// Brightness hotkeys held by another app at startup (e.g. Twinkle Tray) must be retried until they register,
    /// and the default StepUp combo "Ctrl+Alt+=" must be parseable.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class ScreenBrightnessHotkeyRetryTests
    {
        private string? _sandbox;
        private readonly HashSet<string> _heldByOtherApp = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _registered = new();
        private int _nextId;

        [TestInitialize]
        public void Setup()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), $"kp_test_hkretry_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_sandbox);
            ConfigStore.ConfigDirectory = _sandbox;
            ScreenBrightnessService.ResetToDefaults();
            _heldByOtherApp.Clear();
            _registered.Clear();
            _nextId = 1000;
            ScreenBrightnessService.BrightnessHotkeyRegisterOverrideForTesting = (combo, _) =>
            {
                if (_heldByOtherApp.Contains(combo)) return 0;
                _registered.Add(combo);
                return ++_nextId;
            };
            ScreenBrightnessService.BrightnessHotkeyUnregisterOverrideForTesting = _ => { };
        }

        [TestCleanup]
        public void Cleanup()
        {
            ScreenBrightnessService.UnregisterAllBrightnessHotkeys();
            ScreenBrightnessService.BrightnessHotkeyRegisterOverrideForTesting = null;
            ScreenBrightnessService.BrightnessHotkeyUnregisterOverrideForTesting = null;
            ConfigStore.ConfigDirectory = null!;
            if (!string.IsNullOrEmpty(_sandbox) && Directory.Exists(_sandbox))
            {
                try { Directory.Delete(_sandbox, true); } catch { }
            }
        }

        [TestMethod]
        public void HotkeyHeldByAnotherApp_IsRegisteredOnceReleased()
        {
            _heldByOtherApp.Add("Ctrl+Alt+=");
            _heldByOtherApp.Add("Ctrl+Alt+NumPadAdd");

            ScreenBrightnessService.RegisterBrightnessHotkeys();

            Assert.AreEqual(2, ScreenBrightnessService.FailedBrightnessHotkeyCountForTesting);
            Assert.IsTrue(ScreenBrightnessService.IsBrightnessHotkeyRetryScheduledForTesting, "A retry must be scheduled while hotkeys are missing.");
            CollectionAssert.DoesNotContain(_registered, "Ctrl+Alt+=");

            ScreenBrightnessService.RetryFailedBrightnessHotkeysForTesting();
            Assert.AreEqual(2, ScreenBrightnessService.FailedBrightnessHotkeyCountForTesting, "Still held: nothing should register yet.");

            _heldByOtherApp.Clear();
            ScreenBrightnessService.RetryFailedBrightnessHotkeysForTesting();

            Assert.AreEqual(0, ScreenBrightnessService.FailedBrightnessHotkeyCountForTesting);
            CollectionAssert.Contains(_registered, "Ctrl+Alt+=");
            CollectionAssert.Contains(_registered, "Ctrl+Alt+NumPadAdd");
            Assert.IsFalse(ScreenBrightnessService.IsBrightnessHotkeyRetryScheduledForTesting, "Retry timer must stop once all hotkeys are registered.");
        }

        [TestMethod]
        public void DefaultStepUpCombo_EqualsKey_Parses()
        {
            Assert.IsTrue(HotkeyManager.TryParseCombo("Ctrl+Alt+=", out uint mods, out uint vk), "Default StepUp hotkey must be parseable.");
            Assert.AreEqual(0xBBu, vk, "'=' is VK_OEM_PLUS.");
            Assert.AreNotEqual(0u, mods);
        }

        [TestMethod]
        public void UnregisterAll_ClearsPendingRetries()
        {
            _heldByOtherApp.Add("Ctrl+Alt+=");
            ScreenBrightnessService.RegisterBrightnessHotkeys();
            Assert.AreEqual(1, ScreenBrightnessService.FailedBrightnessHotkeyCountForTesting);

            ScreenBrightnessService.UnregisterAllBrightnessHotkeys();

            Assert.AreEqual(0, ScreenBrightnessService.FailedBrightnessHotkeyCountForTesting);
            Assert.IsFalse(ScreenBrightnessService.IsBrightnessHotkeyRetryScheduledForTesting);
        }

        [TestMethod]
        public void TakenHotkeys_AreDescribedInPlainWords_AndClearOnceRegistered()
        {
            _heldByOtherApp.Add("Ctrl+Alt+NumPadAdd");
            ScreenBrightnessService.RegisterBrightnessHotkeys();

            var text = ScreenBrightnessService.DescribeTakenBrightnessHotkeys();
            Assert.IsNotNull(text);
            StringAssert.Contains(text, "Ctrl+Alt+NumPadAdd (brighter)");

            _heldByOtherApp.Clear();
            ScreenBrightnessService.RetryFailedBrightnessHotkeysForTesting();
            Assert.IsNull(ScreenBrightnessService.DescribeTakenBrightnessHotkeys());
        }

        [TestMethod]
        public void WelcomeText_UsesConfiguredStepKeys()
        {
            var cfg = ScreenBrightnessService.CreateDefaultConfig();
            var text = ScreenBrightnessOverlayWindow.BuildWelcomeText(cfg);
            StringAssert.Contains(text, "Ctrl+Alt+= / Ctrl+Alt+- = brighter / darker");

            cfg.StepUpHotkey = string.Empty;
            cfg.StepDownHotkey = string.Empty;
            Assert.IsFalse(ScreenBrightnessOverlayWindow.BuildWelcomeText(cfg).Contains("brighter"));
        }
    }
}
