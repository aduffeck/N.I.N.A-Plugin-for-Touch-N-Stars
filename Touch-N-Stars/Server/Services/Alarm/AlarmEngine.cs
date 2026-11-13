#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TouchNStars.Server.Services.Alarm;

/// <summary>
/// Decides which alarms beep. Pure (no N.I.N.A. access) and thread safe: event handlers, the poll and the API call it.
/// Decision order at release, the first match is recorded: disabled, user exclusion, category off, not armed, grace,
/// shutdown, muted, throttled (same key within the throttle time), repeated (the maximum bursts of this episode),
/// coalesced (another burst started within <see cref="BurstWindowMs"/>), else a burst.
/// </summary>
public sealed class AlarmEngine {
    /// <summary>Log alarms wait this long, so that a shutdown that follows can still cancel them.</summary>
    public const long LogDelayMs = 5000;
    public const long BurstWindowMs = 30000;
    public const int HistorySize = 20;
    /// <summary>Bursts per key and episode; an episode ends after two throttle times without the alarm.</summary>
    public const int MaxBurstsPerEpisode = 3;

    private sealed class Pending {
        public AlarmCandidate Candidate = null!;
        public bool Armed;
        public long DueMs;
    }

    private sealed class Episode {
        public int Bursts;
        public long LastSeenMs;
    }

    private readonly object gate = new();
    private readonly IAlarmClock clock;
    private readonly Func<AlarmSettings> settings;
    private readonly long startMs;

    private readonly List<Pending> pending = new();
    private readonly Dictionary<string, long> lastAccepted = new();
    private readonly Dictionary<string, Episode> episodes = new();
    private readonly LinkedList<AlarmRecord> history = new();
    private long lastBurstMs = long.MinValue;
    private long mutedUntilMs = long.MinValue;
    private long suppressedUntilMs = long.MinValue;
    private bool shuttingDown;
    private long nextId = 1;
    private BeepResult? lastBeepResult;
    private DateTime? lastBurstUtc;

    /// <param name="startMs">Monotonic time of the start; the grace time counts from it.</param>
    public AlarmEngine(IAlarmClock clock, Func<AlarmSettings> settings, long startMs) {
        this.clock = clock;
        this.settings = settings;
        this.startMs = startMs;
    }

    /// <summary>
    /// An alarm candidate. <paramref name="armed"/>: a sequence runs (or just ended) at its arrival.
    /// <paramref name="delayMs"/>: <see cref="LogDelayMs"/> for log lines, 0 otherwise. Conditions that persist are
    /// submitted again on every poll; the throttle and the episode limit keep them to a few bursts.
    /// </summary>
    public void Submit(AlarmCandidate candidate, bool armed, long delayMs = 0) {
        lock (gate) {
            long now = clock.MonotonicMs;
            long throttle = ThrottleMs(settings());
            if (!episodes.TryGetValue(candidate.Key, out var episode)) {
                episode = new Episode { LastSeenMs = now };
                episodes[candidate.Key] = episode;
            } else if (now - episode.LastSeenMs >= EpisodeQuietMs(throttle)) {
                episode.Bursts = 0;
            }
            episode.LastSeenMs = now;

            pending.Add(new Pending { Candidate = candidate, Armed = armed, DueMs = now + Math.Max(0, delayMs) });
        }
    }

    /// <summary>Decides the candidates that are due; returns the bursts to play.</summary>
    public IReadOnlyList<BurstRequest> ReleaseDue() {
        var bursts = new List<BurstRequest>();
        lock (gate) {
            long now = clock.MonotonicMs;
            var s = settings();
            for (int i = 0; i < pending.Count;) {
                var p = pending[i];
                if (p.DueMs > now) {
                    i++;
                    continue;
                }

                var (outcome, reason) = Decide(p, s, now);
                if (outcome == null) {
                    // Held until the grace time ends, then decided again
                    p.DueMs = GraceEndMs(s);
                    i++;
                    continue;
                }

                pending.RemoveAt(i);
                var record = Record(p.Candidate, outcome.Value, reason, now, s);
                if (outcome == AlarmOutcome.Pending) {
                    bursts.Add(new BurstRequest(record.Id, s.Volume, s.ToneMs, s.PauseMs, s.Tones));
                }
            }
        }
        return bursts;
    }

    // Null: hold the candidate until the grace time ends
    private (AlarmOutcome?, string) Decide(Pending p, AlarmSettings s, long now) {
        var c = p.Candidate;
        if (!s.Enabled) {
            return (AlarmOutcome.Disabled, "alarms are off");
        }
        string text = $"{c.Category}|{c.Key}|{c.Message}|{c.Detail}";
        string? exclusion = s.Exclusions.FirstOrDefault(e => text.Contains(e, StringComparison.OrdinalIgnoreCase));
        if (exclusion != null) {
            return (AlarmOutcome.Excluded, $"matches \"{exclusion}\"");
        }
        if (!s.Categories.IsOn(c.Category)) {
            return (AlarmOutcome.CategoryOff, $"category {c.Category} is off");
        }
        if (!p.Armed && !c.AlwaysArmed) {
            return (AlarmOutcome.NotArmed, "no sequence running");
        }
        if (now < GraceEndMs(s)) {
            // An always-armed fault logged once (dew) must not be lost to the grace time
            if (c.AlwaysArmed && c.Origin == AlarmOrigin.Log) {
                return (null, "");
            }
            return (AlarmOutcome.Grace, "start-up grace time");
        }
        if (shuttingDown || now < suppressedUntilMs) {
            return (AlarmOutcome.Shutdown, "shutting down");
        }
        if (now < mutedUntilMs) {
            return (AlarmOutcome.Muted, "silenced");
        }
        long throttle = ThrottleMs(s);
        if (lastAccepted.TryGetValue(c.Key, out long last) && now - last < throttle) {
            return (AlarmOutcome.Throttled, $"same alarm within {s.ThrottleMinutes} min");
        }
        var episode = episodes.TryGetValue(c.Key, out var e2) ? e2 : null;
        if (episode != null && episode.Bursts >= MaxBurstsPerEpisode) {
            return (AlarmOutcome.Repeated, $"already beeped {MaxBurstsPerEpisode} times");
        }
        if (lastBurstMs != long.MinValue && now - lastBurstMs < BurstWindowMs) {
            lastAccepted[c.Key] = now;
            return (AlarmOutcome.Coalesced, "covered by the burst of another alarm");
        }

        lastAccepted[c.Key] = now;
        lastBurstMs = now;
        lastBurstUtc = clock.UtcNow;
        if (episode != null) {
            episode.Bursts++;
        }
        return (AlarmOutcome.Pending, "");
    }

    private long GraceEndMs(AlarmSettings s) => startMs + s.GraceMinutes * 60_000L;

    private static long ThrottleMs(AlarmSettings s) => s.ThrottleMinutes * 60_000L;

    // An episode ends after twice the throttle time without the alarm: some faults are logged again exactly every
    // throttle time (the SDK repeats a lasting over current every 600 s), which must not start a new episode.
    private static long EpisodeQuietMs(long throttleMs) => Math.Max(2 * throttleMs, 11 * 60_000L);

    // Callers hold the lock. The same key and outcome within the throttle time merge into one entry (Count), so
    // conditions submitted on every poll do not push other alarms out of the history.
    private AlarmRecord Record(AlarmCandidate c, AlarmOutcome outcome, string reason, long now, AlarmSettings s) {
        var utc = clock.UtcNow;
        if (outcome != AlarmOutcome.Pending) {
            var node = history.First;
            while (node != null && !(node.Value.Key == c.Key && node.Value.Outcome == outcome && now - node.Value.LastMs < ThrottleMs(s))) {
                node = node.Next;
            }
            if (node != null) {
                var existing = node.Value;
                existing.Count++;
                existing.LastTimeUtc = utc;
                existing.LastMs = now;
                // Newest first by the last occurrence
                history.Remove(node);
                history.AddFirst(existing);
                return existing;
            }
        }

        var record = new AlarmRecord {
            Id = nextId++,
            TimeUtc = utc,
            LastTimeUtc = utc,
            LastMs = now,
            Category = c.Category,
            Key = c.Key,
            Message = c.Message,
            Origin = c.Origin,
            Outcome = outcome,
            Reason = reason,
        };
        history.AddFirst(record);
        while (history.Count > HistorySize) {
            history.RemoveLast();
        }
        return record;
    }

    /// <summary>Records a test burst (ignores every rule); returns its id.</summary>
    public long RecordTest() {
        lock (gate) {
            long now = clock.MonotonicMs;
            var c = new AlarmCandidate(AlarmCategory.Errors, "test", "Test alarm", AlarmOrigin.Test);
            return Record(c, AlarmOutcome.Pending, "test", now, settings()).Id;
        }
    }

    /// <summary>The first beep of a burst was played (or not).</summary>
    public void ReportBurstResult(long id, BeepResult result) {
        lock (gate) {
            lastBeepResult = result;
            var record = history.FirstOrDefault(r => r.Id == id);
            if (record == null) {
                return;
            }
            (record.Outcome, record.Reason) = result switch {
                BeepResult.Ok => (AlarmOutcome.Beeped, ""),
                BeepResult.NoPowerBox => (AlarmOutcome.NoPowerBox, "no PowerBox connected"),
                BeepResult.NotSupported => (AlarmOutcome.BeepFailed, "the PowerBox driver has no beep"),
                _ => (AlarmOutcome.BeepFailed, "the PowerBox did not beep (see the log)"),
            };
        }
    }

    /// <summary>Silence: alarms are muted for <paramref name="minutes"/>; 0 ends a mute.</summary>
    public void Mute(int minutes) {
        lock (gate) {
            mutedUntilMs = minutes > 0 ? clock.MonotonicMs + minutes * 60_000L : long.MinValue;
        }
    }

    /// <summary>N.I.N.A. or the plugin shuts down: no more bursts, pending alarms end as Shutdown.</summary>
    public void BeginShutdown() {
        lock (gate) {
            shuttingDown = true;
        }
    }

    public void ResetShutdown() {
        lock (gate) {
            shuttingDown = false;
            suppressedUntilMs = long.MinValue;
        }
    }

    /// <summary>A shutdown or restart of the system was requested: no bursts for a while (a failed one must not mute alarms for good).</summary>
    public void SuppressFor(long ms) {
        lock (gate) {
            suppressedUntilMs = clock.MonotonicMs + ms;
        }
    }

    /// <summary>A new sequence starts: every alarm may beep its full number of times again.</summary>
    public void ResetEpisodes() {
        lock (gate) {
            foreach (var e in episodes.Values) {
                e.Bursts = 0;
            }
        }
    }

    public bool ShuttingDown { get { lock (gate) return shuttingDown; } }

    public DateTime? MutedUntilUtc {
        get {
            lock (gate) {
                long now = clock.MonotonicMs;
                return now < mutedUntilMs ? clock.UtcNow.AddMilliseconds(mutedUntilMs - now) : null;
            }
        }
    }

    public long GraceRemainingMs {
        get {
            lock (gate) {
                return Math.Max(0, GraceEndMs(settings()) - clock.MonotonicMs);
            }
        }
    }

    public DateTime? LastBurstUtc { get { lock (gate) return lastBurstUtc; } }

    public BeepResult? LastBeepResult { get { lock (gate) return lastBeepResult; } }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<AlarmRecord> History(int limit) {
        lock (gate) {
            return history.Take(Math.Clamp(limit, 1, HistorySize)).Select(r => r.Copy()).ToList();
        }
    }
}
