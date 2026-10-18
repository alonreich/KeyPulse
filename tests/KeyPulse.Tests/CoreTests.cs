using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    public class CoreTests
    {
        [TestMethod]
        public void CompareVersions_DetectsNewerVersionCorrectly()
        {
            Assert.IsTrue(UpdateChecker.CompareVersions("2026.10.18.0100", "2026.10.17.1515") > 0);
            Assert.IsTrue(UpdateChecker.CompareVersions("2026.10.17.1516", "2026.10.17.1515") > 0);
            Assert.IsTrue(UpdateChecker.CompareVersions("2026.10.17.1515", "2026.10.17.1515") == 0);
            Assert.IsTrue(UpdateChecker.CompareVersions("2026.10.17.1436", "2026.10.17.1515") < 0);
        }

        [TestMethod]
        public void CompareVersions_HandlesPrereleaseAndPrefixes()
        {
            Assert.IsTrue(UpdateChecker.CompareVersions("v2026.10.18.0100-beta", "2026.10.17.1515") > 0);
            Assert.IsTrue(UpdateChecker.CompareVersions("2026.10.17.1515-beta.1", "2026.10.17.1515") == 0);
            Assert.IsTrue(UpdateChecker.CompareVersions("invalid", "2026.10.17.1515") == 0);
        }

        [TestMethod]
        public void AtomicFileWriter_WritesAndReadsFileSuccessfully()
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"kp_test_{Guid.NewGuid():N}.txt");
            try
            {
                var content = "Atomic write test verification payload";
                var success = AtomicFileWriter.WriteAtomic(tempFile, content, out var error);
                Assert.IsTrue(success, "WriteAtomic should succeed: " + error);
                Assert.IsTrue(File.Exists(tempFile));
                Assert.AreEqual(content, File.ReadAllText(tempFile));
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        [TestMethod]
        public void Changelog_ExistsAndContainsUnreleasedSection()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "KeyPulse.csproj")))
            {
                dir = dir.Parent;
            }
            Assert.IsNotNull(dir, "Repository root containing KeyPulse.csproj must be found");

            var changelogPath = Path.Combine(dir.FullName, "docs", "CHANGELOG.md");
            Assert.IsTrue(File.Exists(changelogPath), $"docs/CHANGELOG.md must exist at {changelogPath}");

            var text = File.ReadAllText(changelogPath);
            Assert.IsTrue(text.Contains("## [Unreleased]"), "CHANGELOG.md must contain ## [Unreleased]");
            Assert.IsTrue(text.Contains("AI Agent Directive"), "CHANGELOG.md must contain agent directives");
        }

        [TestMethod]
        public void AppConfig_HasDefaultSchemaVersion()
        {
            var config = new AppConfig();
            Assert.AreEqual(1, config.SchemaVersion);
            Assert.IsTrue(config.AutoCheckForUpdates);
            Assert.AreEqual("Stable", config.UpdateChannel);
        }

        [TestMethod]
        public void ReleaseVersion_CleanTagParsesCorrectly()
        {
            var testTags = new[] { "v2026.10.18.0100", "2026.10.17.1515-beta.1", "v1.0.0-rc1" };
            foreach (var tag in testTags)
            {
                var rawVer = tag.TrimStart('v', 'V');
                var dashIdx = rawVer.IndexOf('-');
                var cleanVer = dashIdx >= 0 ? rawVer.Substring(0, dashIdx) : rawVer;
                var parsed = Version.TryParse(cleanVer, out var verObj);
                Assert.IsTrue(parsed, $"Tag '{tag}' should parse into a valid Version object");
                Assert.IsNotNull(verObj);
            }
        }

        [TestMethod]
        public void GitHubReleaseAsset_DigestCleanedSuccessfully()
        {
            var rawDigest = "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
            var clean = System.Text.RegularExpressions.Regex.Replace(rawDigest.Trim(), @"^(?i)sha256:\s*", string.Empty).ToLowerInvariant();
            Assert.AreEqual("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", clean);
            Assert.AreEqual(64, clean.Length);
        }

        [TestMethod]
        public void AppConfig_JsonExtensionData_PreservesFutureProperties()
        {
            var json = "{\"SchemaVersion\":1,\"AutoCheckForUpdates\":true,\"NewFutureFeatureEnabled\":true,\"FutureThreshold\":42}";
            var config = System.Text.Json.JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);

            Assert.IsNotNull(config);
            Assert.IsNotNull(config.ExtensionData);
            Assert.IsTrue(config.ExtensionData.ContainsKey("NewFutureFeatureEnabled"));
            Assert.AreEqual("True", config.ExtensionData["NewFutureFeatureEnabled"].ToString());
            Assert.IsTrue(config.ExtensionData.ContainsKey("FutureThreshold"));
            Assert.AreEqual(42, config.ExtensionData["FutureThreshold"].GetInt32());

            var reSerialized = System.Text.Json.JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
            Assert.IsTrue(reSerialized.Contains("NewFutureFeatureEnabled"));
            Assert.IsTrue(reSerialized.Contains("FutureThreshold"));
        }

        [TestMethod]
        public void ConfigStore_SaveAllRollbackCopies_ExecutesSafely()
        {
            var copies = ConfigStore.SaveAllRollbackCopies("unit-test");
            Assert.IsNotNull(copies);
            foreach (var copy in copies)
            {
                if (File.Exists(copy))
                {
                    try { File.Delete(copy); } catch { }
                }
            }
        }
    }
}
