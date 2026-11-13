#nullable enable
using System;
using System.Diagnostics;

namespace TouchNStars.Server.Services.Alarm;

/// <summary>Alarm categories, each with a toggle in the settings.</summary>
public enum AlarmCategory { Errors, Guiding, Mount, Safety, PowerBox, Disk, Warnings }

/// <summary>Where an alarm came from.</summary>
public enum AlarmOrigin { Log, Event, Poll, Test }

/// <summary>What happened to an alarm (recorded in the history).</summary>
public enum AlarmOutcome {
    /// <summary>Handed to the beep worker, result not known yet.</summary>
    Pending,
    Beeped,
    /// <summary>Within the burst window of another alarm: its burst covers it.</summary>
    Coalesced,
    /// <summary>The same alarm beeped within the throttle time.</summary>
    Throttled,
    /// <summary>The same condition already beeped the maximum number of times in this episode.</summary>
    Repeated,
    Muted,
    /// <summary>Within the grace time after the start.</summary>
    Grace,
    /// <summary>N.I.N.A. or the system is shutting down.</summary>
    Shutdown,
    /// <summary>No sequence runs and the alarm is not always armed.</summary>
    NotArmed,
    CategoryOff,
    Excluded,
    Disabled,
    NoPowerBox,
    BeepFailed,
}

/// <summary>Result of one beep call.</summary>
public enum BeepResult { Ok, NoPowerBox, NotSupported, Failed }

/// <summary>
/// An alarm to decide on. <paramref name="Key"/> identifies "the same alarm" for the throttle;
/// <paramref name="AlwaysArmed"/> alarms also count without a running sequence (PowerBox over current, dew).
/// </summary>
public sealed record AlarmCandidate(
    AlarmCategory Category,
    string Key,
    string Message,
    AlarmOrigin Origin,
    bool AlwaysArmed = false,
    string Detail = "");

/// <summary>One entry of the alarm history.</summary>
public sealed class AlarmRecord {
    public long Id { get; init; }
    public DateTime TimeUtc { get; init; }
    public DateTime LastTimeUtc { get; set; }
    internal long LastMs { get; set; }
    public int Count { get; set; } = 1;
    public AlarmCategory Category { get; init; }
    public string Key { get; init; } = "";
    public string Message { get; init; } = "";
    public AlarmOrigin Origin { get; init; }
    public AlarmOutcome Outcome { get; set; }
    public string Reason { get; set; } = "";

    public AlarmRecord Copy() => (AlarmRecord)MemberwiseClone();
}

/// <summary>A burst for the beep worker: <paramref name="Tones"/> tones of <paramref name="ToneMs"/>, <paramref name="PauseMs"/> apart.</summary>
public sealed record BurstRequest(long AlarmId, int Volume, int ToneMs, int PauseMs, int Tones);

/// <summary>Clock of the alarm logic: a monotonic time for every interval (the Pi has no RTC, its wall clock jumps), UTC for display.</summary>
public interface IAlarmClock {
    long MonotonicMs { get; }
    DateTime UtcNow { get; }
}

public sealed class SystemAlarmClock : IAlarmClock {
    public static readonly SystemAlarmClock Instance = new();
    public long MonotonicMs => Environment.TickCount64;
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Plays a tone on the PowerBox buzzer; returns at once (the tone ends by itself, a new call replaces it).</summary>
public interface IBuzzer {
    BeepResult Beep(int volume, int durationMs);
}
