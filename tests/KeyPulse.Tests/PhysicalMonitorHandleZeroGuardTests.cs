using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyPulse.Tests
{
    /// <summary>
    /// Regression guard (incident 2026-10-23): dxva2 physical monitor handle 0 is VALID. A process gets 0 whenever no
    /// other app (e.g. Twinkle Tray) holds a handle, and rejecting it silently disabled every brightness read and write.
    /// See project_structure.txt guard rail 9 and docs/architecture/screen-brightness.md "Known Pitfall".
    /// </summary>
    [TestClass]
    public partial class PhysicalMonitorHandleZeroGuardTests
    {
        [GeneratedRegex(@"(hPhys\w*|HPhysicalMonitor|hPhysicalMonitor)\s*[!=]=\s*IntPtr\.Zero|IntPtr\.Zero\s*[!=]=\s*(hPhys\w*|HPhysicalMonitor|hPhysicalMonitor)")]
        private static partial Regex PhysicalHandleZeroComparison();

        [TestMethod]
        public void ScreenBrightnessService_NeverComparesPhysicalHandleToZero()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "KeyPulse", "KeyPulse.csproj")))
            {
                dir = dir.Parent;
            }
            Assert.IsNotNull(dir, "Repository root must be found");

            var path = Path.Combine(dir.FullName, "src", "KeyPulse", "ScreenBrightnessService.cs");
            var offending = File.ReadAllLines(path)
                .Select((line, i) => (Line: line, No: i + 1))
                .Where(x => PhysicalHandleZeroComparison().IsMatch(x.Line))
                .Select(x => $"line {x.No}: {x.Line.Trim()}")
                .ToList();

            Assert.AreEqual(0, offending.Count,
                "Physical monitor handle 0 is valid; only GetPhysicalMonitorsFromHMONITOR's BOOL result decides success.\n" +
                string.Join("\n", offending));
        }
    }
}
