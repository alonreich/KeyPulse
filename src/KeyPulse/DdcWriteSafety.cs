using System;
using System.Threading.Tasks;

namespace KeyPulse
{
    internal enum DdcFeature
    {
        Brightness,
        Contrast
    }

    internal enum DdcWriteOutcome
    {
        /// <summary>The monitor already holds the target value; nothing was sent (no NVRAM write).</summary>
        Unchanged,
        /// <summary>The value was written with a single successful set command.</summary>
        Written,
        /// <summary>The monitor did not answer a read with a valid range; nothing was written.</summary>
        UnknownRange,
        /// <summary>Both write methods were refused; the caller must not retry on its own.</summary>
        Failed
    }

    internal enum DdcScheduleDecision
    {
        /// <summary>Isolated change after an idle period: write now so single presses feel instant.</summary>
        WriteNow,
        /// <summary>Part of a burst: start a settle timer and write the final value once input stops.</summary>
        StartSettleTimer,
        /// <summary>A settle timer is already running; it will pick up the newest value.</summary>
        AlreadyScheduled
    }

    internal readonly record struct DdcFeatureReading(bool Ok, uint Current, uint Min, uint Max);

    /// <summary>
    /// Hardware-safety policy for DDC/CI monitor writes. Monitors commonly persist brightness / contrast to
    /// NVRAM (EEPROM) with limited write endurance, and scalers can lock up when the I2C bus is flooded.
    /// Everything here is pure so it can be unit tested without a physical monitor.
    /// </summary>
    internal static class DdcWriteSafety
    {
        /// <summary>Minimum pause between any two DDC/CI commands (reads or writes) to the same monitor.</summary>
        public const int CommandSpacingMs = 50;

        /// <summary>Continuous input (held hotkey, slider drag) is written only once it has been still this long.</summary>
        public const int SettleMs = 500;

        /// <summary>During uninterrupted input, the newest value is still written at most this often for visible feedback.</summary>
        public const int MaxLatencyMs = 1500;

        /// <summary>0% contrast maps to this share of the hardware range so the picture never goes fully black.</summary>
        public const int MinContrastFloorPercent = 5;

        public static DdcScheduleDecision DecideSchedule(DateTime nowUtc, DateTime lastWriteUtc, bool flushScheduled)
        {
            if (flushScheduled) return DdcScheduleDecision.AlreadyScheduled;
            return (nowUtc - lastWriteUtc).TotalMilliseconds >= SettleMs
                ? DdcScheduleDecision.WriteNow
                : DdcScheduleDecision.StartSettleTimer;
        }

        /// <summary>
        /// Milliseconds to keep waiting before flushing a pending value: until input has been still for
        /// <see cref="SettleMs"/>, but never longer than <see cref="MaxLatencyMs"/> after the burst began. 0 = flush now.
        /// </summary>
        public static int ComputeFlushDelayMs(DateTime nowUtc, DateTime lastRequestUtc, DateTime firstPendingUtc)
        {
            double untilSettled = SettleMs - (nowUtc - lastRequestUtc).TotalMilliseconds;
            double untilDeadline = MaxLatencyMs - (nowUtc - firstPendingUtc).TotalMilliseconds;
            double wait = Math.Min(untilSettled, untilDeadline);
            return wait <= 0 ? 0 : (int)Math.Ceiling(wait);
        }

        /// <summary>The raw hardware range a percentage maps onto (contrast keeps a floor above the hardware minimum).</summary>
        public static (uint Min, uint Max) EffectiveRange(DdcFeature feature, uint min, uint max)
        {
            if (feature != DdcFeature.Contrast || max <= min) return (min, max);
            uint floor = (uint)Math.Ceiling((max - min) * MinContrastFloorPercent / 100.0);
            return (Math.Min(max, min + floor), max);
        }

        public static uint ToRaw(DdcFeature feature, int percent, uint min, uint max)
        {
            var (lo, hi) = EffectiveRange(feature, min, max);
            int p = Math.Clamp(percent, 0, 100);
            if (hi <= lo) return lo;
            double raw = lo + (p / 100.0) * (hi - lo);
            return (uint)Math.Clamp(Math.Round(raw), lo, hi);
        }

        public static int ToPercent(DdcFeature feature, uint current, uint min, uint max)
        {
            var (lo, hi) = EffectiveRange(feature, min, max);
            if (hi <= lo) return current >= hi ? 100 : 0;
            uint clamped = Math.Clamp(current, lo, hi);
            return (int)Math.Clamp(Math.Round((double)(clamped - lo) / (hi - lo) * 100.0), 0, 100);
        }

        /// <summary>
        /// Reads a feature with the high-level MCCS call, falling back to a raw VCP read after a spaced pause.
        /// A reading is only Ok when the monitor reports a real range (max above min, current within it).
        /// </summary>
        public static DdcFeatureReading ReadFeature(
            Func<DdcFeatureReading> readHighLevel,
            Func<DdcFeatureReading> readVcp,
            Action<int> pause)
        {
            var r = readHighLevel();
            if (IsValid(r)) return r;
            pause(CommandSpacingMs);
            r = readVcp();
            return IsValid(r) ? r : default;
        }

        private static bool IsValid(DdcFeatureReading r) => r.Ok && r.Max > r.Min && r.Current <= r.Max;

        /// <summary>
        /// Safe single-feature write: read first, never write without a confirmed range, skip if the monitor already
        /// holds the value, otherwise at most one high-level set plus one raw VCP set, each preceded by a spaced pause.
        /// </summary>
        public static DdcWriteOutcome WriteFeature(
            DdcFeature feature,
            int percent,
            Func<DdcFeatureReading> read,
            Func<uint, bool> writeHighLevel,
            Func<uint, bool> writeVcp,
            Action<int> pause)
        {
            var reading = read();
            if (!reading.Ok) return DdcWriteOutcome.UnknownRange;

            uint target = ToRaw(feature, percent, reading.Min, reading.Max);
            if (reading.Current == target) return DdcWriteOutcome.Unchanged;

            pause(CommandSpacingMs);
            if (writeHighLevel(target)) return DdcWriteOutcome.Written;

            pause(CommandSpacingMs);
            return writeVcp(target) ? DdcWriteOutcome.Written : DdcWriteOutcome.Failed;
        }

        /// <summary>
        /// <see cref="ReadFeature"/> with a non-blocking pause, for the asynchronous hardware write pipeline.
        /// </summary>
        public static async Task<DdcFeatureReading> ReadFeatureAsync(
            Func<DdcFeatureReading> readHighLevel,
            Func<DdcFeatureReading> readVcp,
            Func<int, Task> pause)
        {
            var r = readHighLevel();
            if (IsValid(r)) return r;
            await pause(CommandSpacingMs).ConfigureAwait(false);
            r = readVcp();
            return IsValid(r) ? r : default;
        }

        /// <summary>
        /// <see cref="WriteFeature"/> with non-blocking pauses: identical read-before-write, confirmed-range, skip-unchanged
        /// and bounded-retry policy, but the spacing between commands never parks a ThreadPool thread.
        /// </summary>
        public static async Task<DdcWriteOutcome> WriteFeatureAsync(
            DdcFeature feature,
            int percent,
            Func<Task<DdcFeatureReading>> read,
            Func<uint, bool> writeHighLevel,
            Func<uint, bool> writeVcp,
            Func<int, Task> pause)
        {
            var reading = await read().ConfigureAwait(false);
            if (!reading.Ok) return DdcWriteOutcome.UnknownRange;

            uint target = ToRaw(feature, percent, reading.Min, reading.Max);
            if (reading.Current == target) return DdcWriteOutcome.Unchanged;

            await pause(CommandSpacingMs).ConfigureAwait(false);
            if (writeHighLevel(target)) return DdcWriteOutcome.Written;

            await pause(CommandSpacingMs).ConfigureAwait(false);
            return writeVcp(target) ? DdcWriteOutcome.Written : DdcWriteOutcome.Failed;
        }

        public static bool IsSuccess(DdcWriteOutcome outcome) =>
            outcome == DdcWriteOutcome.Written || outcome == DdcWriteOutcome.Unchanged;
    }
}
