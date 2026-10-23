using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace KeyPulse
{
    /// <summary>
    /// TASK 10: browser discovery moved out of MainWindow. Each browser is probed in a fixed order:
    /// HKCU App Paths, HKLM App Paths, HKLM WOW6432Node App Paths, then the well-known install
    /// folders. The first candidate that exists on disk wins.
    /// </summary>
    internal static class BrowserDetectionService
    {
        internal enum RegistryHive { CurrentUser, LocalMachine }

        internal readonly record struct RegistryProbe(RegistryHive Hive, string SubKey);

        /// <summary>Test seam: returns the default value of a key, or null when it is absent.</summary>
        internal static Func<RegistryHive, string, string?>? RegistryReadOverrideForTesting;

        /// <summary>Test seam: replaces File.Exists for candidate paths.</summary>
        internal static Func<string, bool>? FileExistsOverrideForTesting;

        private static readonly IReadOnlyDictionary<string, string> AppPathExecutables =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["chrome"] = "chrome.exe",
                ["edge"] = "msedge.exe",
                ["firefox"] = "firefox.exe",
                ["brave"] = "brave.exe"
            };

        /// <summary>The browser a URL should open in: PreferredBrowser, else the legacy Chrome flag.</summary>
        internal static string ResolvePreference(AppConfig config)
            => config.PreferredBrowser ?? (config.UseGoogleChromeForUrls ? "Chrome" : "System");

        internal static bool IsSystemDefault(string preference)
            => string.Equals(preference, "System", StringComparison.OrdinalIgnoreCase);

        internal static IReadOnlyList<RegistryProbe> GetRegistryProbes(string browserKey)
        {
            if (!AppPathExecutables.TryGetValue(browserKey ?? string.Empty, out var exe)) return Array.Empty<RegistryProbe>();

            return new[]
            {
                new RegistryProbe(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe),
                new RegistryProbe(RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe),
                new RegistryProbe(RegistryHive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exe)
            };
        }

        internal static IReadOnlyList<string> GetInstallFolderCandidates(string browserKey)
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            switch ((browserKey ?? string.Empty).ToLowerInvariant())
            {
                case "chrome":
                    return new[]
                    {
                        Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                        Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                        Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")
                    };
                case "edge":
                    return new[]
                    {
                        Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                        Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe")
                    };
                case "firefox":
                    return new[]
                    {
                        Path.Combine(programFiles, "Mozilla Firefox", "firefox.exe"),
                        Path.Combine(programFilesX86, "Mozilla Firefox", "firefox.exe")
                    };
                case "brave":
                    return new[]
                    {
                        Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                        Path.Combine(programFilesX86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                        Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
                    };
                default:
                    return Array.Empty<string>();
            }
        }

        /// <summary>Every candidate in probe order: registry App Paths first, then install folders.</summary>
        internal static List<string> GetCandidates(string browserKey)
        {
            var candidates = new List<string>();
            foreach (var probe in GetRegistryProbes(browserKey))
            {
                var path = ReadRegistryDefault(probe);
                if (path != null) candidates.Add(path.Trim('"'));
            }
            candidates.AddRange(GetInstallFolderCandidates(browserKey));
            return candidates;
        }

        public static bool TryFindBrowser(string browserKey, out string browserPath)
        {
            var exists = FileExistsOverrideForTesting ?? File.Exists;
            foreach (var candidate in GetCandidates(browserKey).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (exists(candidate))
                {
                    browserPath = candidate;
                    return true;
                }
            }

            browserPath = string.Empty;
            return false;
        }

        private static string? ReadRegistryDefault(RegistryProbe probe)
        {
            var overrideRead = RegistryReadOverrideForTesting;
            if (overrideRead != null) return overrideRead(probe.Hive, probe.SubKey);

            try
            {
                var root = probe.Hive == RegistryHive.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine;
                using var key = root.OpenSubKey(probe.SubKey, false);
                return key?.GetValue(null) as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
