#nullable enable
using System;
using System.Collections.Generic;

namespace TouchNStars.Server.Services.Alarm;

/// <summary>
/// Whether a sequence runs. SequenceStarting/SequenceFinished come for both sequencers; the poll can only see the
/// advanced one, so with the events subscribed it only arms (a missed start event), never disarms.
/// </summary>
public sealed class SequenceArming {
    /// <summary>Alarms right after the end of a sequence still count (the failure that ended it).</summary>
    public const long TailMs = 15000;

    private readonly object gate = new();
    private bool running;
    private long endedMs = long.MinValue / 2;

    public bool Running { get { lock (gate) return running; } }

    public void OnStarting() {
        lock (gate) running = true;
    }

    public void OnFinished(long nowMs) {
        lock (gate) {
            running = false;
            endedMs = nowMs;
        }
    }

    /// <summary>Sets the state at a (re)start: the events of the time before may have been missed.</summary>
    public void Reset(bool advancedRunning, long nowMs) {
        lock (gate) {
            running = advancedRunning;
            endedMs = long.MinValue / 2;
        }
    }

    /// <param name="eventsSubscribed">With the events the poll only arms; without them it follows the poll.</param>
    public void Poll(bool advancedRunning, bool eventsSubscribed, long nowMs) {
        lock (gate) {
            if (advancedRunning) {
                running = true;
            } else if (!eventsSubscribed && running) {
                running = false;
                endedMs = nowMs;
            }
        }
    }

    public bool IsArmed(long nowMs) {
        lock (gate) return running || nowMs - endedMs <= TailMs;
    }
}

/// <summary>
/// A LIGHT frame exposed while the mount did not track. The poll samples the tracking state every few seconds; a saved
/// LIGHT frame alarms if tracking was off for more than min(10 s, half the exposure) within its exposure. Off time
/// counts only between two "off" samples in a row (and after the last one if tracking is still off): a change between
/// two samples gets the benefit of the doubt. Flips, warm-up and calibration frames never alarm (no LIGHT frame).
/// </summary>
public sealed class LightFrameTrackingRule {
    public const long MaxOffMs = 10_000;
    private const int MaxSamples = 2000;

    private readonly object gate = new();
    private readonly LinkedList<(long Ms, bool Off)> samples = new();

    /// <param name="off">Connected mount not tracking, parked or slewing. A mount that is not connected is "on" (no mount, no alarm here).</param>
    public void AddSample(long nowMs, bool off) {
        lock (gate) {
            samples.AddLast((nowMs, off));
            while (samples.Count > MaxSamples) {
                samples.RemoveFirst();
            }
        }
    }

    /// <summary>
    /// The exposure window on the monotonic clock. <paramref name="exposureStartUtc"/> comes from the image metadata
    /// (DATE-OBS); a driver time without a kind is taken as UTC. If it is missing or implausible (more than the
    /// duration + 120 s off from the save time minus the duration), the save time minus the duration is used.
    /// </summary>
    public static (long StartMs, long EndMs) ExposureWindow(long receiptMs, DateTime receiptUtc, DateTime exposureStartUtc, double exposureSeconds) {
        long durationMs = double.IsFinite(exposureSeconds) && exposureSeconds > 0 ? (long)(exposureSeconds * 1000) : 0;
        long fallbackStart = receiptMs - durationMs;
        if (exposureStartUtc == DateTime.MinValue) {
            return (fallbackStart, receiptMs);
        }
        var start = exposureStartUtc.Kind switch {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(exposureStartUtc, DateTimeKind.Utc),
            DateTimeKind.Local => exposureStartUtc.ToUniversalTime(),
            _ => exposureStartUtc,
        };
        double expectedOffsetMs = (receiptUtc - start).TotalMilliseconds;
        if (Math.Abs(expectedOffsetMs - durationMs) > durationMs + 120_000) {
            return (fallbackStart, receiptMs);
        }
        long startMs = receiptMs - (long)expectedOffsetMs;
        return (startMs, startMs + durationMs);
    }

    /// <summary>True if the frame from <paramref name="startMs"/> to <paramref name="endMs"/> was exposed without tracking.</summary>
    public bool ExposedWithoutTracking(long startMs, long endMs, long receiptMs, bool offNow) {
        if (endMs <= startMs) {
            return false;
        }
        long offMs = 0;
        lock (gate) {
            (long Ms, bool Off)? previous = null;
            foreach (var s in samples) {
                if (previous is { } p && p.Off && s.Off) {
                    offMs += Overlap(p.Ms, s.Ms, startMs, endMs);
                }
                previous = s;
            }
            if (previous is { } last && last.Off && offNow) {
                offMs += Overlap(last.Ms, Math.Max(last.Ms, receiptMs), startMs, endMs);
            }
        }
        long threshold = Math.Min(MaxOffMs, (endMs - startMs) / 2);
        return offMs > threshold;
    }

    private static long Overlap(long a0, long a1, long b0, long b1) => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));
}

/// <summary>PHD2 in LostLock for more than a minute (not during an autofocus run). True on every poll while it holds.</summary>
public sealed class LostLockRule {
    public const long LostForMs = 60_000;
    private long? lostSinceMs;

    public bool Update(bool lostLock, bool paused, long nowMs) {
        if (!lostLock) {
            lostSinceMs = null;
            return false;
        }
        lostSinceMs ??= nowMs;
        return !paused && nowMs - lostSinceMs.Value >= LostForMs;
    }
}

/// <summary>Free space of the image folder below 2 GiB; clears above 2.5 GiB. True on every poll while low.</summary>
public sealed class DiskSpaceRule {
    public const long LowBytes = 2L << 30;
    public const long ClearBytes = 5L << 29;
    private bool low;

    public bool Update(long freeBytes) {
        if (freeBytes < LowBytes) {
            low = true;
        } else if (freeBytes > ClearBytes) {
            low = false;
        }
        return low;
    }
}

/// <summary>
/// The internal guider's autofocus pause: while an autofocus runs, guide star alarms that only report searching wait.
/// TNS's autofocus flag is cleared only by a successful run or a logged failure, so a cancelled run is also treated
/// as over at the end of the sequence, and a run counts for at most 10 minutes.
/// </summary>
public sealed class AutofocusPause {
    public const long MaxMs = 10 * 60_000;
    private long? sinceMs;
    private bool stale;

    public void OnSequenceFinished() => stale = sinceMs != null;

    /// <summary>A new sequence: an autofocus flag left over from a cancelled run counts again.</summary>
    public void OnSequenceStarting() {
        stale = false;
        sinceMs = null;
    }

    public bool IsPaused(bool afRun, long nowMs) {
        if (!afRun) {
            sinceMs = null;
            stale = false;
            return false;
        }
        sinceMs ??= nowMs;
        return !stale && nowMs - sinceMs.Value < MaxMs;
    }
}
