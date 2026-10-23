using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace KeyPulse
{
    public partial class MainWindow
    {
        internal static bool TryResolveLaunchTarget(string rawTarget, out string fileName, out string arguments, out string error)
        {
            fileName = string.Empty;
            arguments = string.Empty;
            error = string.Empty;

            try
            {
                if (!TrySplitLaunchCommand(rawTarget, out fileName, out arguments, out error)) return false;

                if (File.Exists(fileName) || Directory.Exists(fileName)) return true;

                if (TryFindExecutableOnPath(fileName, out var resolvedPath))
                {
                    fileName = resolvedPath;
                    return true;
                }

                error = "Program not found";
                return false;
            }
            catch (Exception ex)
            {
                error = "Invalid program target: " + ex.Message;
                return false;
            }
        }

        private static bool TrySplitLaunchCommand(string rawTarget, out string fileName, out string arguments, out string error)
        {
            fileName = string.Empty;
            arguments = string.Empty;
            error = string.Empty;

            var target = rawTarget.Trim();
            if (target.Length == 0)
            {
                error = "Program required";
                return false;
            }

            if (target.StartsWith("\"", StringComparison.Ordinal))
            {
                var endQuote = target.IndexOf('"', 1);
                if (endQuote <= 1)
                {
                    error = "Closing quote missing";
                    return false;
                }

                fileName = target.Substring(1, endQuote - 1);
                arguments = target.Substring(endQuote + 1).Trim();
                return true;
            }

            if (File.Exists(target) || Directory.Exists(target))
            {
                fileName = target;
                return true;
            }

            var lastSpace = target.LastIndexOf(' ');
            while (lastSpace > 0)
            {
                var possiblePath = target.Substring(0, lastSpace);
                if (File.Exists(possiblePath) || Directory.Exists(possiblePath))
                {
                    fileName = possiblePath;
                    arguments = target.Substring(lastSpace + 1).Trim();
                    return true;
                }

                lastSpace = target.LastIndexOf(' ', lastSpace - 1);
            }

            var extensions = new[] { ".exe", ".bat", ".cmd", ".ps1", ".lnk", ".vbs", ".com" };
            foreach (var ext in extensions)
            {
                var extIdx = target.IndexOf(ext + " ", StringComparison.OrdinalIgnoreCase);
                if (extIdx > 0)
                {
                    fileName = target.Substring(0, extIdx + ext.Length);
                    arguments = target.Substring(extIdx + ext.Length + 1).Trim();
                    return true;
                }
                if (target.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    fileName = target;
                    arguments = string.Empty;
                    return true;
                }
            }

            var switchIndex = target.IndexOf(" -", StringComparison.Ordinal);
            if (switchIndex < 0) switchIndex = target.IndexOf(" /", StringComparison.Ordinal);
            if (switchIndex > 0)
            {
                fileName = target.Substring(0, switchIndex).Trim();
                arguments = target.Substring(switchIndex + 1).Trim();
                return true;
            }

            var firstSpace = target.IndexOf(' ');
            if (firstSpace > 0)
            {
                var candidate = target.Substring(0, firstSpace);
                if (Path.HasExtension(candidate) || candidate.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) < 0)
                {
                    fileName = candidate;
                    arguments = target.Substring(firstSpace + 1).Trim();
                }
                else
                {
                    fileName = target;
                    arguments = string.Empty;
                }
            }
            else
            {
                fileName = target;
                arguments = string.Empty;
            }

            return true;
        }

        private static bool TryFindExecutableOnPath(string fileName, out string resolvedPath)
        {
            resolvedPath = string.Empty;

            if (fileName.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
            {
                return false;
            }

            var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var pathExtValue = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD;.VBS;.PS1;.LNK";
            var extensions = Path.HasExtension(fileName)
                ? new[] { string.Empty }
                : pathExtValue.Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var extension in extensions)
                {
                    var candidate = Path.Combine(directory.Trim(), fileName + extension);
                    if (File.Exists(candidate))
                    {
                        resolvedPath = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        private static string FormatLaunchTarget(string fileName, string arguments)
        {
            var formattedFileName = fileName.Contains(' ') ? $"\"{fileName}\"" : fileName;
            return string.IsNullOrWhiteSpace(arguments) ? formattedFileName : formattedFileName + " " + arguments;
        }

        private void LaunchUrl(string target)
        {
            if (!TryNormalizeUrlTarget(target, out var normalizedUrl, out var error))
            {
                throw new InvalidOperationException(error);
            }

            string pref = _currentConfig.PreferredBrowser ?? (_currentConfig.UseGoogleChromeForUrls ? "Chrome" : "System");
            if (!string.Equals(pref, "System", StringComparison.OrdinalIgnoreCase) && TryFindBrowser(pref, out var browserPath))
            {
                using var proc = Process.Start(new ProcessStartInfo(browserPath, $"\"{normalizedUrl}\"") { UseShellExecute = true });
                return;
            }

            using var browser = Process.Start(new ProcessStartInfo(normalizedUrl) { UseShellExecute = true });
        }

        private static bool TryFindGoogleChrome(out string chromePath) => TryFindBrowser("Chrome", out chromePath);

        private static bool TryFindBrowser(string browserKey, out string browserPath)
        {
            var candidates = new List<string>();

            switch ((browserKey ?? string.Empty).ToLowerInvariant())
            {
                case "chrome":
                    AddBrowserCandidateFromRegistry(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", candidates);
                    AddBrowserCandidateFromRegistry(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", candidates);
                    AddBrowserCandidateFromRegistry(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", candidates);
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"));
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"));
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"));
                    break;
                case "edge":
                    AddBrowserCandidateFromRegistry(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", candidates);
                    AddBrowserCandidateFromRegistry(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", candidates);
                    AddBrowserCandidateFromRegistry(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", candidates);
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"));
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"));
                    break;
                case "firefox":
                    AddBrowserCandidateFromRegistry(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe", candidates);
                    AddBrowserCandidateFromRegistry(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe", candidates);
                    AddBrowserCandidateFromRegistry(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe", candidates);
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"));
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe"));
                    break;
                case "brave":
                    AddBrowserCandidateFromRegistry(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe", candidates);
                    AddBrowserCandidateFromRegistry(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe", candidates);
                    AddBrowserCandidateFromRegistry(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\brave.exe", candidates);
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
                    break;
            }

            foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(candidate))
                {
                    browserPath = candidate;
                    return true;
                }
            }

            browserPath = string.Empty;
            return false;
        }

        private static void AddBrowserCandidateFromRegistry(RegistryKey root, string subKey, List<string> candidates)
        {
            try
            {
                using var key = root.OpenSubKey(subKey, false);
                if (key?.GetValue(null) is string path)
                {
                    candidates.Add(path.Trim('"'));
                }
            }
            catch { }
        }

    }
}
