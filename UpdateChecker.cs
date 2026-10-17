using System;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace KeyPulse
{
    /// <summary>
    /// ISSUE_15: KeyPulse used to report version 1.0.0.0 for every build ever released and never
    /// looked for a newer one, so a user had no way to tell whether a fix had shipped. This asks
    /// GitHub once, on demand, and never blocks the UI thread.
    /// </summary>
    public sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }
    }

    public readonly struct UpdateCheckResult
    {
        public bool Succeeded { get; init; }
        public bool UpdateAvailable { get; init; }
        public string LatestVersion { get; init; }
        public string Message { get; init; }
        public string? LocalInstallerPath { get; init; }
    }

    public static class UpdateChecker
    {
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.Add("User-Agent", "KeyPulse-UpdateCheck");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return client;
        }

        public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // DEVUPDATE_01: In dev mode, inspect local .\compiled\KeyPulse.exe instead of querying GitHub.
                var devUpdateSource = Environment.GetEnvironmentVariable("KEYPULSE_DEV_UPDATE_SOURCE");
                if (!string.IsNullOrWhiteSpace(devUpdateSource))
                {
                    var localExe = System.IO.Path.Combine(devUpdateSource.Trim(), "compiled", "KeyPulse.exe");
                    if (!System.IO.File.Exists(localExe))
                    {
                        return new UpdateCheckResult
                        {
                            Succeeded = false,
                            Message = $"Dev update source active, but '{localExe}' does not exist yet. Run .\\dev_build.cmd first."
                        };
                    }

                    var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(localExe);
                    var rawVer = info.FileVersion ?? "unknown";
                    var latestLocal = rawVer.EndsWith(".0", StringComparison.Ordinal) && System.Linq.Enumerable.Count(rawVer, c => c == '.') == 3
                        ? rawVer.Substring(0, rawVer.Length - 2)
                        : rawVer;
                    var currentVer = Program.AppVersion;

                    return new UpdateCheckResult
                    {
                        Succeeded = true,
                        UpdateAvailable = CompareVersions(latestLocal, currentVer) > 0,
                        LatestVersion = latestLocal,
                        LocalInstallerPath = localExe,
                        Message = string.Empty
                    };
                }

                using var response = await Client.GetAsync(Program.LatestReleaseApiUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new UpdateCheckResult
                    {
                        Succeeded = false,
                        Message = $"GitHub answered {(int)response.StatusCode}."
                    };
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var release = System.Text.Json.JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.GitHubRelease);

                if (release == null || string.IsNullOrWhiteSpace(release.TagName))
                {
                    return new UpdateCheckResult { Succeeded = false, Message = "GitHub did not name a latest release." };
                }

                var latest = release.TagName.TrimStart('v', 'V');
                var current = Program.AppVersion;

                return new UpdateCheckResult
                {
                    Succeeded = true,
                    UpdateAvailable = CompareVersions(latest, current) > 0,
                    LatestVersion = latest,
                    Message = string.Empty
                };
            }
            catch (Exception ex)
            {
                Program.LogDebug("Update check failed: " + ex.Message);
                return new UpdateCheckResult { Succeeded = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// Compares dotted numeric versions part by part. Returns >0 when left is newer.
        /// Anything unparseable compares as equal rather than as "an update is available", so a
        /// malformed tag never nags the user forever.
        /// </summary>
        internal static int CompareVersions(string left, string right)
        {
            var leftParts = Split(left);
            var rightParts = Split(right);
            if (leftParts == null || rightParts == null) return 0;

            var length = Math.Max(leftParts.Length, rightParts.Length);
            for (var i = 0; i < length; i++)
            {
                var l = i < leftParts.Length ? leftParts[i] : 0;
                var r = i < rightParts.Length ? rightParts[i] : 0;
                if (l != r) return l.CompareTo(r);
            }

            return 0;
        }

        private static long[]? Split(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;

            var clean = version.Trim().TrimStart('v', 'V');
            var metaIdx = clean.IndexOfAny(new[] { '-', '+' });
            if (metaIdx >= 0)
            {
                clean = clean.Substring(0, metaIdx);
            }

            var raw = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (raw.Length == 0) return null;

            var parts = new long[raw.Length];
            for (var i = 0; i < raw.Length; i++)
            {
                if (!long.TryParse(raw[i], out parts[i])) return null;
            }

            return parts;
        }
    }
}
