using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TouchNStars.Server.Services.Alarm;
using Xunit;

namespace TouchNStars.Tests;

internal sealed class FakeAlarmClock : IAlarmClock
{
    public long MonotonicMs { get; set; } = 1_000_000;
    public DateTime UtcNow => new DateTime(2026, 11, 12, 22, 0, 0, DateTimeKind.Utc).AddMilliseconds(MonotonicMs - 1_000_000);
    public void Advance(double seconds) => MonotonicMs += (long)(seconds * 1000);
}

public class LogRecordParserTests
{
    private const string Ts = "2026-11-12T22:01:02.1234";

    [Fact]
    public void SingleLineErrorCompletesOnNextHeaderOrFlush()
    {
        var p = new LogRecordParser();
        Assert.Null(p.Feed($"{Ts}|ERROR|CameraVM.cs|Capture|811|Download timed out"));
        Assert.True(p.HasPending);
        var r = p.Feed($"{Ts}|INFO|X.cs|Y|1|next");
        Assert.NotNull(r);
        Assert.Equal(("ERROR", "CameraVM.cs", "Capture", "Download timed out", ""), (r!.Level, r.Source, r.Member, r.Message, r.Detail));
        Assert.Equal("next", p.Flush()!.Message);
        Assert.Null(p.Flush());
    }

    [Fact]
    public void ErrorWithExceptionTakesTheFirstContinuationLine()
    {
        var p = new LogRecordParser();
        p.Feed($"{Ts}|ERROR|UtilityController.cs|GetAssemblyVersion|327\r");
        p.Feed("System.NullReferenceException: Object reference not set");
        p.Feed("   at Foo.Bar()");
        var r = p.Flush()!;
        Assert.Equal("", r.Message);
        Assert.Equal("System.NullReferenceException: Object reference not set", r.Detail);
    }

    [Fact]
    public void PipeInMessageAndBannerLines()
    {
        var p = new LogRecordParser();
        p.Feed("DATE|LEVEL|SOURCE|MEMBER|LINE|MESSAGE");
        p.Feed("-------------------------------------");
        Assert.False(p.HasPending);
        p.Feed($"{Ts}|WARNING|A.cs|B|2|a|b|c");
        p.Feed("--- banner ---");
        var r = p.Flush()!;
        Assert.Equal("a|b|c", r.Message);
        Assert.Equal("", r.Detail);
    }

    [Fact]
    public void RecordSplitAcrossBatches()
    {
        var p = new LogRecordParser();
        Assert.Null(p.Feed($"{Ts}|ERROR|ImageSaveController.cs|Save|142|Saving failed"));
        // the exception line comes with the next batch
        Assert.Null(p.Feed("System.IO.IOException: No space left on device"));
        var r = p.Flush()!;
        Assert.Equal("System.IO.IOException: No space left on device", r.Detail);
    }
}

public class AlarmLogClassifierTests
{
    private static readonly AlarmLogClassifier C = new(new[] { "UtilityController", "InternalGuiderController", "PinsAlarmController" });

    private static LogRecord Rec(string level, string source, string member, string message, string detail = "") =>
        new(level, source, member, message, detail);

    [Fact]
    public void ErrorIsErrorsAndNumbersShareAKey()
    {
        var a = C.Classify(Rec("ERROR", "FocuserVM.cs", "Move", "Move to 1234 failed after 5 s"), false)!;
        var b = C.Classify(Rec("ERROR", "FocuserVM.cs", "Move", "Move to 99 failed after 12 s"), false)!;
        Assert.Equal(AlarmCategory.Errors, a.Category);
        Assert.Equal(a.Key, b.Key);
        Assert.False(a.AlwaysArmed);
    }

    [Theory]
    [InlineData("PinsAlarmService.cs", "X", "x")]
    [InlineData("AlarmEngine.cs", "X", "x")]
    [InlineData("BackgroundWorker.cs", "X", "x")]
    [InlineData("PHD2Service.cs", "Connect", "x")]
    [InlineData("UtilityController.cs", "GetAssemblyVersion", "")]
    [InlineData("InternalGuiderController.Coach.cs", "Run", "x")]
    [InlineData("DllLoader.cs", "LoadDllFromAbsolutePath", "SOFAlib")]
    [InlineData("PluginLoader.cs", "AssignSequenceEntity", "Plugin Item failed to load")]
    [InlineData("ASTAPSolver.cs", "ReadResult", "ASTAP - Plate solve failed.")]
    [InlineData("JsonCreationConverter.cs", "ReadJson", "Deserialize failed")]
    [InlineData("MountVM.cs", "UpdateDeviceInfo", "Failed to query product firmware")]
    [InlineData("SequenceItemCreationConverter.cs", "Create", "x")]
    [InlineData("Program.cs", "OnUnobserved", "An unobserved task exception occurred")]
    [InlineData("TilterService.cs", "Connect", "Failed to connect to device 0: InvalidId")]
    [InlineData("HipsSurveyService.cs", "Download", "x")]
    [InlineData("AutoFocusEngine.cs", "Run|1234", "System.OperationCanceledException: The operation was canceled.")]
    [InlineData("SequenceItem.cs", "Run", "System.Threading.Tasks.TaskCanceledException: A task was canceled.")]
    public void BuiltInExclusions(string source, string member, string message)
    {
        Assert.Null(C.Classify(Rec("ERROR", source, member, message), true));
    }

    [Fact]
    public void NinaControllersAreNotExcluded()
    {
        // ImageSaveController.cs is N.I.N.A.'s, not a TNS API controller
        var c = C.Classify(Rec("ERROR", "ImageSaveController.cs", "Save", "Saving failed"), false);
        Assert.NotNull(c);
    }

    [Fact]
    public void TimeoutsAreNotTakenForCancellations()
    {
        var c = C.Classify(Rec("ERROR", "AlpacaCamera.cs", "Download", "System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."), false);
        Assert.NotNull(c);
    }

    [Fact]
    public void OwnFailedBeepWarningIsExcluded()
    {
        Assert.Null(C.Classify(Rec("WARNING", "PowerBoxDriver.cs", "Beep", "PowerBox beep failed: PB_ERROR_COMMUNICATION"), true));
    }

    [Fact]
    public void CriticalWarningsAlarmWithWarningsOff()
    {
        Assert.Equal(AlarmCategory.Mount, C.Classify(Rec("WARNING", "OnStepXTelescope.cs", "Poll", "OnStepX: controller reports error 4"), false)!.Category);
        Assert.Equal(AlarmCategory.Guiding, C.Classify(Rec("WARNING", "PHD2Guider.cs", "Process", "PHD2 - Alert (error): star lost"), false)!.Category);
        Assert.Null(C.Classify(Rec("WARNING", "FocuserVM.cs", "X", "something"), false));
        Assert.Equal(AlarmCategory.Warnings, C.Classify(Rec("WARNING", "FocuserVM.cs", "X", "something"), true)!.Category);
        Assert.Null(C.Classify(Rec("INFO", "FocuserVM.cs", "X", "something"), true));
    }

    [Fact]
    public void Recategorisation()
    {
        Assert.Equal(AlarmCategory.Mount, C.Classify(Rec("ERROR", "MeridianFlipVM.cs", "DoFlip", "Meridian Flip failed"), false)!.Category);
        Assert.Equal(AlarmCategory.Mount, C.Classify(Rec("ERROR", "SequenceTrigger.cs", "Run", "Failed: Trigger: MeridianFlipTrigger - Meridianflip fehlgeschlagen"), false)!.Category);
        Assert.Equal(AlarmCategory.Errors, C.Classify(Rec("ERROR", "SequenceTrigger.cs", "Run", "Failed: Trigger: AutofocusAfterFilterChange - x"), false)!.Category);
        Assert.Equal(AlarmCategory.Guiding, C.Classify(Rec("ERROR", "PHD2Guider.cs", "X", "y"), false)!.Category);
        Assert.Equal(AlarmCategory.Safety, C.Classify(Rec("ERROR", "SafetyMonitorVM.cs", "X", "y"), false)!.Category);
        Assert.Equal(AlarmCategory.Disk, C.Classify(Rec("ERROR", "ImageSaveController.cs", "Save", "", "System.IO.IOException: No space left on device"), false)!.Category);
    }

    [Fact]
    public void PowerBoxOverCurrentAndDewAreAlwaysArmedWithPortDigitsInTheKey()
    {
        var oc = C.Classify(Rec("ERROR", "PowerBoxSdkLog.cs", "Forward", "PowerBox SDK: USB port 2: over current, 2600 mA"), false)!;
        Assert.Equal(AlarmCategory.PowerBox, oc.Category);
        Assert.True(oc.AlwaysArmed);
        var dew1 = C.Classify(Rec("ERROR", "PowerBoxSdkLog.cs", "Forward", "PowerBox SDK: Dew port 1: no valid dew point, regulation stopped"), false)!;
        var dew2 = C.Classify(Rec("ERROR", "PowerBoxSdkLog.cs", "Forward", "PowerBox SDK: Dew port 2: no valid dew point, regulation stopped"), false)!;
        Assert.True(dew1.AlwaysArmed);
        Assert.NotEqual(dew1.Key, dew2.Key);
        var other = C.Classify(Rec("ERROR", "PowerBoxSdkLog.cs", "Forward", "PowerBox SDK: SPI bus not available"), false)!;
        Assert.False(other.AlwaysArmed);
        // measured values do not make another alarm, the port number does
        var dew1b = C.Classify(Rec("ERROR", "PowerBoxSdkLog.cs", "Forward", "PowerBox SDK: Dew port 1: no valid dew point, regulation stopped, keeping duty 87 (average)"), false)!;
        var dew1c = C.Classify(Rec("ERROR", "PowerBoxSdkLog.cs", "Forward", "PowerBox SDK: Dew port 1: no valid dew point, regulation stopped, keeping duty 120 (average)"), false)!;
        Assert.Equal(dew1b.Key, dew1c.Key);
        var p1 = C.Classify(Rec("ERROR", "PowerBoxPorts.cs", "Update", "Dew port 1 (Main): over current (2.31 A), switched off"), false)!;
        var p1b = C.Classify(Rec("ERROR", "PowerBoxPorts.cs", "Update", "Dew port 1 (Main): over current (3.05 A), switched off"), false)!;
        var p2 = C.Classify(Rec("ERROR", "PowerBoxPorts.cs", "Update", "Dew port 2 (Main): over current (2.31 A), switched off"), false)!;
        Assert.Equal(p1.Key, p1b.Key);
        Assert.NotEqual(p1.Key, p2.Key);
    }

    [Fact]
    public void ExceptionWithoutMessageUsesTheDetail()
    {
        var c = C.Classify(Rec("ERROR", "FocuserVM.cs", "X", "", "System.TimeoutException: timed out"), false)!;
        Assert.Equal("System.TimeoutException: timed out", c.Message);
    }

    [Fact]
    public void ShutdownLine()
    {
        Assert.True(AlarmLogClassifier.IsShutdownLine(Rec("INFO", "Program.cs", "Main", "Application shutting down...")));
        Assert.False(AlarmLogClassifier.IsShutdownLine(Rec("INFO", "Other.cs", "Main", "Application shutting down...")));
    }
}

public class AlarmSettingsTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void DefaultsAndTones()
    {
        var s = AlarmSettings.Defaults();
        Assert.True(s.Enabled);
        Assert.False(s.Categories.Warnings);
        Assert.True(s.Categories.PowerBox);
        Assert.Equal(50, s.Volume);
        Assert.Equal(5, s.Tones);
        s.BurstSeconds = 1;
        s.ToneMs = 2000;
        Assert.Equal(1, s.Tones);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{\"Volume\":500}")]
    public void InvalidStoredValueIsNull(string? raw)
    {
        Assert.Null(AlarmSettings.Parse(raw));
    }

    [Fact]
    public void PartialMergeKeepsOtherFieldsAndRoundTrips()
    {
        var current = AlarmSettings.Defaults();
        Assert.True(AlarmSettings.TryMerge(current, J("{\"volume\":20,\"categories\":{\"warnings\":true},\"unknown\":1}"), out var merged, out var error), error);
        Assert.Equal(20, merged.Volume);
        Assert.True(merged.Categories.Warnings);
        Assert.True(merged.Categories.Errors);
        Assert.Equal(400, merged.ToneMs);
        var again = AlarmSettings.Parse(merged.Serialize())!;
        Assert.Equal(20, again.Volume);
        Assert.True(again.Categories.Warnings);
    }

    [Theory]
    [InlineData("{\"Volume\":0}", "Volume")]
    [InlineData("{\"Volume\":101}", "Volume")]
    [InlineData("{\"ToneMs\":49}", "ToneMs")]
    [InlineData("{\"PauseMs\":5001}", "PauseMs")]
    [InlineData("{\"BurstSeconds\":31}", "BurstSeconds")]
    [InlineData("{\"GraceMinutes\":-1}", "GraceMinutes")]
    [InlineData("{\"ThrottleMinutes\":0}", "ThrottleMinutes")]
    [InlineData("{\"Volume\":\"loud\"}", "Invalid value")]
    [InlineData("{\"Exclusions\":[\"x\"]}", "Exclusions")]
    public void RangeChecks(string patch, string field)
    {
        Assert.False(AlarmSettings.TryMerge(AlarmSettings.Defaults(), J(patch), out _, out var error));
        Assert.Contains(field, error);
    }

    [Theory]
    [InlineData("{\"Volume\":1}")]
    [InlineData("{\"Volume\":100}")]
    [InlineData("{\"GraceMinutes\":0}")]
    [InlineData("{\"ThrottleMinutes\":240}")]
    public void RangeBoundariesAreAccepted(string patch)
    {
        Assert.True(AlarmSettings.TryMerge(AlarmSettings.Defaults(), J(patch), out _, out var error), error);
    }

    [Fact]
    public void ExclusionsAreTrimmedAndDeduplicated()
    {
        Assert.True(AlarmSettings.TryMerge(AlarmSettings.Defaults(), J("{\"Exclusions\":[\" Focuser \",\"focuser\",\"  \",\"Dome\"]}"), out var s, out var error), error);
        Assert.Equal(new[] { "Focuser", "Dome" }, s.Exclusions);
        var many = "{\"Exclusions\":[" + string.Join(",", Enumerable.Range(0, 51).Select(i => $"\"entry{i}\"")) + "]}";
        Assert.False(AlarmSettings.TryMerge(AlarmSettings.Defaults(), J(many), out _, out _));
    }

    [Fact]
    public void StoreUsesDefaultsUntilWrittenAndPicksUpExternalWrites()
    {
        string? raw = null;
        var store = new AlarmSettingsStore(() => raw, v => raw = v);
        Assert.False(store.Stored);
        Assert.Equal(50, store.Current.Volume);
        Assert.True(store.TryUpdate(J("{\"Volume\":30}"), out _, out _));
        Assert.True(store.Stored);
        Assert.Contains("\"Volume\":30", raw);
        raw = AlarmSettings.Defaults().Serialize().Replace("\"Volume\":50", "\"Volume\":70");
        store.Refresh();
        Assert.Equal(70, store.Current.Volume);
        raw = "garbage";
        store.Refresh();
        Assert.False(store.Stored);
        Assert.Equal(50, store.Current.Volume);
    }
}

public class AlarmEngineTests
{
    private readonly FakeAlarmClock clock = new();
    private AlarmSettings settings = AlarmSettings.Defaults();
    private readonly AlarmEngine engine;

    public AlarmEngineTests()
    {
        engine = new AlarmEngine(clock, () => settings, clock.MonotonicMs);
    }

    private static AlarmCandidate Cand(string key, AlarmCategory cat = AlarmCategory.Errors, bool always = false, AlarmOrigin origin = AlarmOrigin.Event) =>
        new(cat, key, "message " + key, origin, always);

    private void PastGrace() => clock.Advance(3 * 60 + 1);

    private AlarmOutcome Last() => engine.History(1)[0].Outcome;

    [Fact]
    public void GraceAndNotArmed()
    {
        engine.Submit(Cand("a"), armed: true);
        Assert.Empty(engine.ReleaseDue());
        Assert.Equal(AlarmOutcome.Grace, Last());

        PastGrace();
        engine.Submit(Cand("b"), armed: false);
        Assert.Empty(engine.ReleaseDue());
        Assert.Equal(AlarmOutcome.NotArmed, Last());

        engine.Submit(Cand("c", AlarmCategory.PowerBox, always: true), armed: false);
        var bursts = engine.ReleaseDue();
        Assert.Single(bursts);
        Assert.Equal(5, bursts[0].Tones);
        Assert.Equal(50, bursts[0].Volume);
    }

    [Fact]
    public void AlwaysArmedLogAlarmsAreHeldThroughTheGrace()
    {
        engine.Submit(Cand("dew", AlarmCategory.PowerBox, always: true, AlarmOrigin.Log), armed: false, AlarmEngine.LogDelayMs);
        clock.Advance(10);
        Assert.Empty(engine.ReleaseDue());
        Assert.Empty(engine.History(20));
        PastGrace();
        Assert.Single(engine.ReleaseDue());
    }

    [Fact]
    public void ThrottleCoalesceAndEpisodeLimit()
    {
        PastGrace();
        engine.Submit(Cand("a"), true);
        Assert.Single(engine.ReleaseDue());

        // another alarm within 30 s rides along
        clock.Advance(10);
        engine.Submit(Cand("b"), true);
        Assert.Empty(engine.ReleaseDue());
        Assert.Equal(AlarmOutcome.Coalesced, Last());

        // the same alarm within the throttle time
        clock.Advance(60);
        engine.Submit(Cand("a"), true);
        Assert.Empty(engine.ReleaseDue());
        Assert.Equal(AlarmOutcome.Throttled, Last());

        // a condition that persists (submitted every 5 s): 3 bursts, then Repeated
        int bursts = 1;
        for (int t = 0; t < 60 * 60 / 5; t++)
        {
            clock.Advance(5);
            engine.Submit(Cand("a"), true);
            bursts += engine.ReleaseDue().Count;
        }
        Assert.Equal(3, bursts);
        Assert.Contains(engine.History(20), r => r.Outcome == AlarmOutcome.Repeated);
        // the history is not flooded by the repeats
        Assert.True(engine.History(20).Count < 10);

        // two quiet throttle times end the episode (some faults are logged again exactly every throttle time)
        clock.Advance(11 * 60);
        engine.Submit(Cand("a"), true);
        Assert.Empty(engine.ReleaseDue());
        clock.Advance(21 * 60);
        engine.Submit(Cand("a"), true);
        Assert.Single(engine.ReleaseDue());
    }

    [Fact]
    public void FlappingDoesNotRestartTheEpisode()
    {
        PastGrace();
        int bursts = 0;
        // lost 70 s, fine 10 s, for two hours
        for (int cycle = 0; cycle < 90; cycle++)
        {
            for (int t = 0; t < 14; t++)
            {
                clock.Advance(5);
                engine.Submit(Cand("lostlock", AlarmCategory.Guiding), true);
                bursts += engine.ReleaseDue().Count;
            }
            clock.Advance(10);
        }
        Assert.Equal(AlarmEngine.MaxBurstsPerEpisode, bursts);
    }

    [Fact]
    public void NewSequenceResetsTheEpisodes()
    {
        PastGrace();
        int bursts = 0;
        for (int t = 0; t < 40 * 12; t++)
        {
            clock.Advance(5);
            engine.Submit(Cand("disk"), true);
            bursts += engine.ReleaseDue().Count;
        }
        Assert.Equal(3, bursts);
        engine.ResetEpisodes();
        clock.Advance(10 * 60);
        engine.Submit(Cand("disk"), true);
        Assert.Single(engine.ReleaseDue());
    }

    [Fact]
    public void LogDelayAndShutdown()
    {
        PastGrace();
        engine.Submit(Cand("log", origin: AlarmOrigin.Log), true, AlarmEngine.LogDelayMs);
        Assert.Empty(engine.ReleaseDue());
        Assert.Empty(engine.History(20));
        clock.Advance(2);
        engine.BeginShutdown();
        clock.Advance(4);
        Assert.Empty(engine.ReleaseDue());
        Assert.Equal(AlarmOutcome.Shutdown, Last());
        engine.ResetShutdown();
        engine.Submit(Cand("x"), true);
        Assert.Single(engine.ReleaseDue());
    }

    [Fact]
    public void SystemActionSuppressionExpires()
    {
        PastGrace();
        engine.SuppressFor(90_000);
        engine.Submit(Cand("a"), true);
        Assert.Empty(engine.ReleaseDue());
        Assert.Equal(AlarmOutcome.Shutdown, Last());
        clock.Advance(91);
        engine.Submit(Cand("b"), true);
        Assert.Single(engine.ReleaseDue());
    }

    [Fact]
    public void MuteAndUnmute()
    {
        PastGrace();
        engine.Mute(30);
        Assert.NotNull(engine.MutedUntilUtc);
        engine.Submit(Cand("a"), true);
        Assert.Empty(engine.ReleaseDue());
        Assert.Equal(AlarmOutcome.Muted, Last());
        engine.Mute(0);
        Assert.Null(engine.MutedUntilUtc);
        engine.Submit(Cand("b"), true);
        Assert.Single(engine.ReleaseDue());
    }

    [Fact]
    public void DisabledCategoryOffAndExclusion()
    {
        PastGrace();
        settings = AlarmSettings.Defaults();
        settings.Enabled = false;
        engine.Submit(Cand("a"), true);
        Assert.Empty(engine.ReleaseDue());
        Assert.Equal(AlarmOutcome.Disabled, Last());

        settings = AlarmSettings.Defaults();
        settings.Categories.Mount = false;
        engine.Submit(Cand("m", AlarmCategory.Mount), true);
        Assert.Empty(engine.ReleaseDue());
        Assert.Equal(AlarmOutcome.CategoryOff, Last());

        settings = AlarmSettings.Defaults();
        settings.Exclusions.Add("MESSAGE X");
        engine.Submit(Cand("x"), true);
        Assert.Empty(engine.ReleaseDue());
        Assert.Equal(AlarmOutcome.Excluded, Last());
    }

    [Fact]
    public void BurstResultUpdatesTheHistory()
    {
        PastGrace();
        engine.Submit(Cand("a"), true);
        var burst = engine.ReleaseDue().Single();
        Assert.Equal(AlarmOutcome.Pending, Last());
        engine.ReportBurstResult(burst.AlarmId, BeepResult.NoPowerBox);
        Assert.Equal(AlarmOutcome.NoPowerBox, Last());
        Assert.Equal(BeepResult.NoPowerBox, engine.LastBeepResult);
    }

    [Fact]
    public void MergedEntryMovesToTheFront()
    {
        PastGrace();
        engine.Submit(Cand("a"), false);
        engine.ReleaseDue();
        clock.Advance(40);
        engine.Submit(Cand("b"), false);
        engine.ReleaseDue();
        clock.Advance(40);
        engine.Submit(Cand("a"), false);
        engine.ReleaseDue();
        var h = engine.History(20);
        Assert.Equal(2, h.Count);
        Assert.Equal(("a", 2), (h[0].Key, h[0].Count));
    }

    [Fact]
    public void HistoryIsCappedNewestFirst()
    {
        PastGrace();
        for (int i = 0; i < 30; i++)
        {
            clock.Advance(31);
            engine.Submit(Cand("k" + i), true);
            engine.ReleaseDue();
        }
        var h = engine.History(20);
        Assert.Equal(20, h.Count);
        Assert.Equal("k29", h[0].Key);
    }
}

public class AlarmRuleTests
{
    [Fact]
    public void SequenceArmingTailAndPoll()
    {
        var a = new SequenceArming();
        Assert.False(a.IsArmed(0));
        a.Poll(advancedRunning: true, eventsSubscribed: true, 0);
        Assert.True(a.IsArmed(0));
        // the poll does not disarm a simple-sequencer run
        a.Poll(advancedRunning: false, eventsSubscribed: true, 1000);
        Assert.True(a.Running);
        a.OnFinished(2000);
        Assert.True(a.IsArmed(2000 + SequenceArming.TailMs));
        Assert.False(a.IsArmed(2001 + SequenceArming.TailMs));
        // without events the poll decides
        a.OnStarting();
        a.Poll(false, eventsSubscribed: false, 100_000);
        Assert.False(a.Running);
        // a (re)start takes the state as it is now
        a.OnStarting();
        a.Reset(false, 200_000);
        Assert.False(a.IsArmed(200_000));
    }

    private static LightFrameTrackingRule Samples(params (long Ms, bool Off)[] samples)
    {
        var r = new LightFrameTrackingRule();
        foreach (var s in samples) r.AddSample(s.Ms, s.Off);
        return r;
    }

    [Fact]
    public void LongLightFrameWithTrackingStoppedAlarms()
    {
        // 600 s frame, tracking stops at 400 s: 200 s off > 10 s
        var samples = new List<(long, bool)>();
        for (long t = 0; t <= 600_000; t += 5000) samples.Add((t, t >= 400_000));
        var r = Samples(samples.ToArray());
        Assert.True(r.ExposedWithoutTracking(0, 600_000, 605_000, offNow: true));
    }

    [Fact]
    public void FlipRightAfterTheLastShortFrameDoesNotAlarm()
    {
        // 2 s frame from 100.0 to 102.0 s, tracking stopped at 102.5 s, frame saved at 104 s
        var r = Samples((95_000, false), (100_000, false), (105_000, true), (110_000, true));
        Assert.False(r.ExposedWithoutTracking(100_000, 102_000, 104_000, offNow: true));
    }

    [Fact]
    public void ShortFrameBetweenTwoOffSamplesAlarms()
    {
        var r = Samples((95_000, true), (100_000, true), (105_000, true));
        Assert.True(r.ExposedWithoutTracking(101_000, 103_000, 104_000, offNow: true));
    }

    [Fact]
    public void TrackingSwitchedOnJustBeforeAShortFrameGetsTheBenefitOfTheDoubt()
    {
        // last sample "off", tracking on again at receipt: no off time after the last sample
        var r = Samples((95_000, true), (100_000, true));
        Assert.False(r.ExposedWithoutTracking(101_000, 103_000, 104_000, offNow: false));
        // a change between two samples counts as on
        var r2 = Samples((100_000, true), (105_000, false));
        Assert.False(r2.ExposedWithoutTracking(101_000, 103_000, 106_000, offNow: false));
    }

    [Fact]
    public void ExposureWindowFromMetadata()
    {
        var receiptUtc = new DateTime(2026, 11, 12, 22, 0, 10, DateTimeKind.Utc);
        // started 70 s before receipt, 60 s exposure (10 s save delay)
        var (s, e) = LightFrameTrackingRule.ExposureWindow(1_000_000, receiptUtc, receiptUtc.AddSeconds(-70), 60);
        Assert.Equal((930_000L, 990_000L), (s, e));
        // a driver time without kind counts as UTC
        var unspecified = DateTime.SpecifyKind(receiptUtc.AddSeconds(-70), DateTimeKind.Unspecified);
        Assert.Equal((930_000L, 990_000L), LightFrameTrackingRule.ExposureWindow(1_000_000, receiptUtc, unspecified, 60));
        // missing or implausible (an hour off): save time minus duration
        Assert.Equal((940_000L, 1_000_000L), LightFrameTrackingRule.ExposureWindow(1_000_000, receiptUtc, DateTime.MinValue, 60));
        Assert.Equal((940_000L, 1_000_000L), LightFrameTrackingRule.ExposureWindow(1_000_000, receiptUtc, receiptUtc.AddHours(-1), 60));
    }

    [Fact]
    public void LostLockAfterAMinuteNotDuringAutofocus()
    {
        var r = new LostLockRule();
        Assert.False(r.Update(true, false, 0));
        Assert.False(r.Update(true, false, 59_000));
        Assert.True(r.Update(true, false, 60_000));
        Assert.True(r.Update(true, false, 65_000));
        Assert.False(r.Update(true, true, 70_000));
        Assert.False(r.Update(false, false, 75_000));
        Assert.False(r.Update(true, false, 80_000));
    }

    [Fact]
    public void DiskHysteresis()
    {
        var r = new DiskSpaceRule();
        Assert.False(r.Update(3L << 30));
        Assert.True(r.Update((2L << 30) - 1));
        Assert.True(r.Update((2L << 30) + 100));
        Assert.False(r.Update((5L << 29) + 1));
    }

    [Fact]
    public void AutofocusPauseEndsWithTheSequenceOrAfterTenMinutes()
    {
        var p = new AutofocusPause();
        Assert.False(p.IsPaused(false, 0));
        Assert.True(p.IsPaused(true, 0));
        Assert.False(p.IsPaused(true, AutofocusPause.MaxMs));
        var q = new AutofocusPause();
        Assert.True(q.IsPaused(true, 0));
        q.OnSequenceFinished();
        Assert.False(q.IsPaused(true, 1000));
        Assert.False(q.IsPaused(false, 2000));
        Assert.True(q.IsPaused(true, 3000));
        // a flag left over from a cancelled run counts again in the next sequence
        q.OnSequenceFinished();
        Assert.False(q.IsPaused(true, 4000));
        q.OnSequenceStarting();
        Assert.True(q.IsPaused(true, 5000));
    }
}

public class DeviceConnectionTrackerTests
{
    [Fact]
    public void RequestedDisconnectIsNotLost()
    {
        var t = new DeviceConnectionTracker("camera", connected: true);
        t.DisconnectRequested(1000);
        var changes = t.Poll(false, 2000);
        Assert.Single(changes);
        Assert.False(changes[0].Lost);
    }

    [Fact]
    public void DisconnectWithoutRequestIsLostOnePollLater()
    {
        var t = new DeviceConnectionTracker("camera", connected: true);
        Assert.Empty(t.Poll(false, 1000));
        var changes = t.Poll(false, 6000);
        Assert.Single(changes);
        Assert.True(changes[0].Lost);
    }

    [Fact]
    public void LateRequestEventStillCounts()
    {
        var t = new DeviceConnectionTracker("mount", connected: true);
        Assert.Empty(t.Poll(false, 1000));
        t.DisconnectRequested(4000);
        Assert.False(t.Poll(false, 6000).Single().Lost);
    }

    [Fact]
    public void DropIsLostEvenWithTheVmsOwnDisconnectEvent()
    {
        var t = new DeviceConnectionTracker("guider", connected: true);
        t.ConnectionDropped(1000);
        t.DisconnectRequested(1500);
        Assert.True(t.Poll(false, 2000).Single().Lost);
    }

    [Fact]
    public void PortSwitchedOffOverridesADrop()
    {
        var t = new DeviceConnectionTracker("guider", connected: true);
        t.ConnectionDropped(1000);
        t.PortSwitchedOff(2000);
        Assert.False(t.Poll(false, 3000).Single().Lost);
        // a pending loss gets it too
        var u = new DeviceConnectionTracker("camera", connected: true);
        Assert.Empty(u.Poll(false, 1000));
        u.PortSwitchedOff(5000);
        Assert.False(u.Poll(false, 6000).Single().Lost);
    }

    [Fact]
    public void DisconnectEventsWhileDisconnectedAreIgnored()
    {
        var t = new DeviceConnectionTracker("focuser", connected: false);
        t.DisconnectRequested(1000);
        Assert.True(t.Poll(true, 2000).Single().Connected);
        Assert.Empty(t.Poll(false, 30_000));
        Assert.True(t.Poll(false, 35_000).Single().Lost);
    }
}
