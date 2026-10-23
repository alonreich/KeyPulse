using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    public class HardwareConsentAndPanelGuardTests
    {
        [TestMethod]
        public void Restore_WithoutConsentOnThisPc_DisablesFeatureAndAsksAgain()
        {
            var restored = new ScreenBrightnessConfig { Enabled = true, DisclaimerAccepted = true };

            bool needsConsent = BackupService.PrepareScreenBrightnessForRestore(restored, disclaimerAcceptedOnThisPc: false);

            Assert.IsTrue(needsConsent);
            Assert.IsFalse(restored.Enabled);
            Assert.IsFalse(restored.DisclaimerAccepted);
            Assert.IsFalse(restored.IsFeatureActive);
        }

        [TestMethod]
        public void Restore_WithConsentOnThisPc_KeepsFeatureStateAndDoesNotAsk()
        {
            var restored = new ScreenBrightnessConfig { Enabled = true, DisclaimerAccepted = false };

            bool needsConsent = BackupService.PrepareScreenBrightnessForRestore(restored, disclaimerAcceptedOnThisPc: true);

            Assert.IsFalse(needsConsent);
            Assert.IsTrue(restored.Enabled);
            Assert.IsTrue(restored.DisclaimerAccepted);
        }

        [TestMethod]
        public void Restore_FeatureOffInBackup_DoesNotAsk()
        {
            var restored = new ScreenBrightnessConfig { Enabled = false, DisclaimerAccepted = true };

            bool needsConsent = BackupService.PrepareScreenBrightnessForRestore(restored, disclaimerAcceptedOnThisPc: false);

            Assert.IsFalse(needsConsent);
            Assert.IsFalse(restored.Enabled);
            Assert.IsFalse(restored.DisclaimerAccepted);
        }

        [DataTestMethod]
        [DataRow(0x80000000u, true)]  // INTERNAL
        [DataRow(11u, true)]          // DISPLAYPORT_EMBEDDED (eDP)
        [DataRow(13u, true)]          // UDI_EMBEDDED
        [DataRow(5u, false)]          // HDMI
        [DataRow(10u, false)]         // DISPLAYPORT_EXTERNAL
        [DataRow(4u, false)]          // DVI
        [DataRow(0u, false)]          // HD15 (VGA)
        [DataRow(0xFFFFFFFFu, false)] // OTHER
        public void OnlyBuiltInConnectorsCountAsLaptopPanels(uint outputTechnology, bool expected)
        {
            Assert.AreEqual(expected, ScreenBrightnessService.IsInternalOutputTechnology(outputTechnology));
        }
    }
}
