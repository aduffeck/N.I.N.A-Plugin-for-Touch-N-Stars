using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces;
using Newtonsoft.Json.Linq;
using TouchNStars.Server;
using TouchNStars.Server.Controllers;
using TouchNStars.Server.Services;
using Xunit;

namespace TouchNStars.Tests;

public class InternalGuiderCoachApiTests
{
    private static AdvancedCoachStatus CompletedSession() => new()
    {
        Phase = "Complete",
        SessionId = "s1",
        GainMin = 0,
        GainMax = 400,
        Findings =
        {
            new AdvancedCoachFinding
            {
                Id = "drift.minMove",
                Code = "drift.minMove",
                Step = "Drift",
                Severity = "info",
                Parameters = new Dictionary<string, object?> { ["raPx"] = 0.21, ["decPx"] = double.NaN, ["level"] = "good" },
                Changes = { new AdvancedCoachSettingChange { Name = "RaMinMove", Value = "0.21", CurrentValue = "0" } }
            },
            new AdvancedCoachFinding { Id = "drift.seeing", Code = "drift.seeing", Step = "Drift", Severity = "good" }
        },
        Trials =
        {
            new AdvancedCoachTrial { Id = "A", Kind = "current", State = "Done", RmsTotalArcsec = 0.9 },
            new AdvancedCoachTrial
            {
                Id = "B",
                Kind = "suggestion",
                State = "Done",
                RmsTotalArcsec = 0.7,
                IsWinner = true,
                Settings = { new AdvancedCoachSettingChange { Name = "RaAggression", Value = "0.6", CurrentValue = "0.7" } }
            }
        },
        Report = new AdvancedCoachReport { Id = "r1", Grade = "good", GuidedRmsArcsec = 0.7, Actions = { "drift.minMove" } }
    };

    [Fact]
    public async Task GetCoach_ReturnsTheStatusInCamelCaseWithFindingParameters()
    {
        var fake = new FakeInternalGuider { CoachStatus = CompletedSession() };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get("coach");

        Assert.Equal(200, status);
        JToken response = json["response"]!;
        Assert.Equal("Complete", (string?)response["phase"]);
        Assert.Equal(400, (int)response["gainMax"]!);
        JToken finding = response["findings"]![0]!;
        Assert.Equal("drift.minMove", (string?)finding["code"]);
        Assert.Equal(0.21, (double)finding["parameters"]!["raPx"]!);
        // NaN inside the parameter dictionary is still written as null.
        Assert.Equal(JTokenType.Null, finding["parameters"]!["decPx"]!.Type);
        Assert.Equal("good", (string?)finding["parameters"]!["level"]);
        Assert.Equal("RaMinMove", (string?)finding["changes"]![0]!["name"]);
        Assert.Equal("good", (string?)response["report"]!["grade"]);
    }

    [Fact]
    public async Task GetCoach_WithoutStatus_IsIdle()
    {
        var fake = new FakeInternalGuider { CoachStatus = null };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get("coach");

        Assert.Equal(200, status);
        Assert.Equal("Idle", (string?)json["response"]!["phase"]);
    }

    [Theory]
    [InlineData("GET", "coach")]
    [InlineData("POST", "coach/start")]
    [InlineData("POST", "coach/skip")]
    [InlineData("POST", "coach/cancel")]
    [InlineData("POST", "coach/apply")]
    [InlineData("GET", "coach/history")]
    [InlineData("POST", "hints/dismiss")]
    public async Task Answers409_WhenTheConnectedGuiderIsNotNative(string method, string path)
    {
        using var host = new InternalGuiderApiHost(FakeInternalGuider.PlainGuiderMediator());

        (int status, JObject json) = method == "GET" ? await host.Get(path) : await host.Post(path, "{}");

        Assert.Equal(409, status);
        Assert.False((bool)json["success"]!);
        Assert.Equal("NotAvailable", (string?)json["code"]);
        Assert.Contains("PHD2", (string?)json["error"]);
    }

    [Fact]
    public async Task Start_PassesTheOptionsAndAnswersWithTheStatus()
    {
        var fake = new FakeInternalGuider { State = "Guiding" };
        fake.OnCall = (name, _) =>
        {
            if (name != nameof(IGuidingCoach.StartCoach)) return null;
            return Task.FromResult(new AdvancedCoachStartResult
            {
                Accepted = true,
                Status = new AdvancedCoachStatus { Phase = "Running", Step = "CameraCheck" }
            });
        };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("coach/start",
            "{\"steps\":[\"trials\",\"CameraCheck\",\"Drift\"],\"exposureSeconds\":[2,1,2],\"gains\":[300,100],\"framesPerCombination\":4," +
            "\"driftSeconds\":240,\"trialSeconds\":60,\"repeatBaseline\":false,\"allowCalibration\":false}");

        Assert.Equal(200, status);
        Assert.Equal("coach-start", (string?)json["response"]!["action"]);
        Assert.False((bool)json["response"]!["pending"]!);
        Assert.Equal("Running", (string?)json["response"]!["status"]!["phase"]);
        var options = (AdvancedCoachOptions)fake.CallsOf(nameof(IGuidingCoach.StartCoach)).Single()![0]!;
        // Steps are known names, in session order; the numbers are the guider's to check.
        Assert.Equal(new[] { "CameraCheck", "Drift", "Trials" }, options.Steps);
        Assert.Equal(new[] { 2.0, 1.0, 2.0 }, options.ExposureSeconds);
        Assert.Equal(new[] { 300, 100 }, options.Gains);
        Assert.Equal(4, options.FramesPerCombination);
        Assert.Equal(240, options.DriftSeconds);
        Assert.Equal(60, options.TrialSeconds);
        Assert.False(options.RepeatBaseline);
        Assert.False(options.AllowCalibration);
    }

    [Fact]
    public async Task Start_WithoutBody_UsesTheDefaults()
    {
        var fake = new FakeInternalGuider { State = "Stopped" };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, _) = await host.Post("coach/start");

        Assert.Equal(200, status);
        var options = (AdvancedCoachOptions)fake.CallsOf(nameof(IGuidingCoach.StartCoach)).Single()![0]!;
        Assert.Empty(options.Steps);
        Assert.Empty(options.ExposureSeconds);
        Assert.Empty(options.Gains);
        Assert.Equal(5, options.FramesPerCombination);
        Assert.Equal(180, options.DriftSeconds);
        Assert.Equal(120, options.TrialSeconds);
        Assert.True(options.RepeatBaseline);
        Assert.True(options.AllowCalibration);
    }

    [Fact]
    public async Task Start_RejectedByTheGuider_CarriesMessageCodeAndParameters()
    {
        var fake = new FakeInternalGuider { State = "Calibrating", CoachStatus = new AdvancedCoachStatus { Phase = "Running", SessionId = "s1" } };
        fake.OnCall = (name, _) =>
        {
            if (name != nameof(IGuidingCoach.StartCoach)) return null;
            return Task.FromResult(new AdvancedCoachStartResult
            {
                Accepted = false,
                Message = "Another coach session is running.",
                MessageCode = "coach.busy",
                MessageParameters = new Dictionary<string, object?> { ["sessionId"] = "s1", ["progressPercent"] = 40.0 },
                Status = fake.CoachStatus
            });
        };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("coach/start", "{}");

        Assert.Equal(409, status);
        Assert.Equal("Rejected", (string?)json["code"]);
        Assert.Equal("coach.busy", (string?)json["messageCode"]);
        Assert.Equal("s1", (string?)json["messageParameters"]!["sessionId"]);
        Assert.Equal(40.0, (double)json["messageParameters"]!["progressPercent"]!);
        Assert.Equal("Another coach session is running.", (string?)json["error"]);
        // The running session's status is untouched by the rejection.
        (int _, JObject coach) = await host.Get("coach");
        Assert.Equal("Running", (string?)coach["response"]!["phase"]);
    }

    [Fact]
    public async Task Start_ReturnsTheStatusOfTheStartResult()
    {
        var fake = new FakeInternalGuider { State = "Guiding" };
        fake.OnCall = (name, _) => name == nameof(IGuidingCoach.StartCoach)
            ? Task.FromResult(new AdvancedCoachStartResult
            {
                Accepted = true,
                Status = new AdvancedCoachStatus { Phase = "Running", SessionId = "s9", Step = "Drift" }
            })
            : null;
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("coach/start", "{\"steps\":[\"Drift\"]}");

        Assert.Equal(200, status);
        Assert.Equal("s9", (string?)json["response"]!["status"]!["sessionId"]);
    }

    [Fact]
    public async Task Start_RejectedWithoutAReason_StillExplains()
    {
        var fake = new FakeInternalGuider { State = "Looping" };
        fake.OnCall = (name, _) => name == nameof(IGuidingCoach.StartCoach)
            ? Task.FromResult<AdvancedCoachStartResult>(null!)
            : null;
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("coach/start", "{}");

        Assert.Equal(409, status);
        Assert.Null(json["messageCode"]);
        Assert.Contains("Looping", (string?)json["error"]);
    }

    [Theory]
    [InlineData("{\"steps\":[\"Report\"]}")]
    [InlineData("{\"steps\":\"Drift\"}")]
    [InlineData("{\"exposureSeconds\":[\"1\"]}")]
    [InlineData("{\"exposureSeconds\":2}")]
    [InlineData("{\"gains\":[1.5]}")]
    [InlineData("{\"gains\":[1e12]}")]
    [InlineData("{\"framesPerCombination\":2.5}")]
    [InlineData("{\"driftSeconds\":\"long\"}")]
    [InlineData("{\"repeatBaseline\":\"yes\"}")]
    [InlineData("nope")]
    public async Task Start_RejectsMalformedOptionsWith400(string body)
    {
        var fake = new FakeInternalGuider { State = "Stopped" };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("coach/start", body);

        Assert.Equal(400, status);
        Assert.Equal("InvalidRequest", (string?)json["code"]);
        Assert.Empty(fake.CallsOf(nameof(IGuidingCoach.StartCoach)));
    }

    [Theory]
    [InlineData("{\"exposureSeconds\":[0]}")]
    [InlineData("{\"exposureSeconds\":[31]}")]
    [InlineData("{\"gains\":[500]}")]
    [InlineData("{\"framesPerCombination\":2}")]
    [InlineData("{\"driftSeconds\":60}")]
    [InlineData("{\"trialSeconds\":10}")]
    [InlineData("{\"exposureSeconds\":[1,2,3,4,5,6],\"gains\":[0,10,20,30,40,50,60,70,80,90,100]}")]
    public async Task Start_LeavesTheValueRangesToTheGuider(string body)
    {
        var fake = new FakeInternalGuider { State = "Stopped" };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, _) = await host.Post("coach/start", body);

        Assert.Equal(200, status);
        Assert.Single(fake.CallsOf(nameof(IGuidingCoach.StartCoach)));
    }

    [Fact]
    public async Task Start_StillRunning_Answers202AndPublishesTheOutcomeLater()
    {
        var fake = new FakeInternalGuider { State = "Stopped" };
        var completion = new TaskCompletionSource<AdvancedCoachStartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OnCall = (name, _) =>
        {
            if (name != nameof(IGuidingCoach.StartCoach)) return null;
            return completion.Task;
        };
        using var host = new InternalGuiderApiHost(fake.Mediator);
        var waitFor = EventWaiter.Collect(host.Service);

        (int status, JObject json) = await host.Post("coach/start", "{}");
        Assert.Equal(202, status);
        Assert.True((bool)json["response"]!["pending"]!);

        (int second, JObject secondJson) = await host.Post("coach/start", "{}");
        Assert.Equal(409, second);
        Assert.Contains("already", (string?)secondJson["error"]);

        completion.SetResult(new AdvancedCoachStartResult { Accepted = false, Message = "No guide camera.", MessageCode = "coach.notConnected" });
        InternalGuiderEvent action = await waitFor(e => e.Type == "action");
        JObject payload = EventWaiter.PayloadJson(action);
        Assert.Equal("coach-start", (string?)payload["action"]);
        Assert.False((bool)payload["success"]!);
        Assert.Equal("No guide camera.", (string?)payload["error"]);
    }

    [Fact]
    public async Task SkipAndCancel_CallTheGuider()
    {
        var fake = new FakeInternalGuider { CoachStatus = new AdvancedCoachStatus { Phase = "Running", Step = "Drift" } };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int skip, JObject skipJson) = await host.Post("coach/skip");
        Assert.Equal(200, skip);
        Assert.Equal("coach-skip", (string?)skipJson["response"]!["action"]);
        Assert.Single(fake.CallsOf(nameof(IGuidingCoach.SkipCoachStep)));

        (int cancel, _) = await host.Post("coach/cancel");
        Assert.Equal(200, cancel);
        Assert.Single(fake.CallsOf(nameof(IGuidingCoach.CancelCoach)));
    }

    [Fact]
    public async Task SkipAndCancel_WithoutASession_Answer409()
    {
        var fake = new FakeInternalGuider();
        fake.OnCall = (name, _) => name is nameof(IGuidingCoach.SkipCoachStep) or nameof(IGuidingCoach.CancelCoach) ? Task.FromResult(false) : null;
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int skip, JObject skipJson) = await host.Post("coach/skip");
        Assert.Equal(409, skip);
        Assert.Contains("No Guiding Coach session", (string?)skipJson["error"]);

        (int cancel, JObject cancelJson) = await host.Post("coach/cancel");
        Assert.Equal(409, cancel);
        Assert.Contains("No Guiding Coach session", (string?)cancelJson["error"]);
    }

    [Fact]
    public async Task Cancel_CancelsAStartStillRunningWithoutAnError()
    {
        var fake = new FakeInternalGuider { State = "Stopped" };
        fake.OnCall = (name, args) =>
        {
            if (name == nameof(IGuidingCoach.StartCoach))
            {
                var ct = (CancellationToken)args![1]!;
                return Task.Run(async () =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return new AdvancedCoachStartResult { Accepted = true };
                });
            }
            return name == nameof(IGuidingCoach.CancelCoach) ? Task.FromResult(false) : null;
        };
        using var host = new InternalGuiderApiHost(fake.Mediator);
        var waitFor = EventWaiter.Collect(host.Service);

        (int started, _) = await host.Post("coach/start", "{}");
        Assert.Equal(202, started);

        (int status, _) = await host.Post("coach/cancel");
        Assert.Equal(200, status);

        InternalGuiderEvent action = await waitFor(e => e.Type == "action");
        JObject payload = EventWaiter.PayloadJson(action);
        Assert.Equal("coach-start", (string?)payload["action"]);
        Assert.Equal(JTokenType.Null, payload["error"]!.Type);
    }

    [Fact]
    public async Task Apply_AcceptsFindingsAndTrialsWithChanges()
    {
        var fake = new FakeInternalGuider { CoachStatus = CompletedSession() };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("coach/apply", "{\"ids\":[\"DRIFT.minmove\",\"trial:b\"]}");

        Assert.Equal(200, status);
        Assert.Equal(new[] { "drift.minMove", "trial:B" }, json["response"]!["applied"]!.Select(t => (string)t!));
        var ids = (IList<string>)fake.CallsOf(nameof(IGuidingCoach.ApplyCoachActions)).Single()![0]!;
        Assert.Equal(new[] { "drift.minMove", "trial:B" }, ids);
    }

    [Theory]
    [InlineData("{\"ids\":[\"nope\"]}", "Unknown")]
    [InlineData("{\"ids\":[\"drift.seeing\"]}", "no setting changes")]
    [InlineData("{\"ids\":[\"trial:A\"]}", "no setting changes")]
    [InlineData("{\"ids\":[]}", "non-empty")]
    [InlineData("{}", "non-empty")]
    public async Task Apply_RejectsIdsThatCannotBeApplied(string body, string hint)
    {
        var fake = new FakeInternalGuider { CoachStatus = CompletedSession() };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("coach/apply", body);

        Assert.Equal(400, status);
        Assert.Contains(hint, (string?)json["error"]);
        Assert.Empty(fake.CallsOf(nameof(IGuidingCoach.ApplyCoachActions)));
    }

    [Fact]
    public async Task Apply_AcceptsActiveHintsWithoutASession()
    {
        var fake = new FakeInternalGuider
        {
            State = "Guiding",
            Hints =
            {
                new AdvancedCoachFinding
                {
                    Id = "hint.raOscillation",
                    Code = "hint.raOscillation",
                    Step = "Live",
                    Severity = "warning",
                    Changes = { new AdvancedCoachSettingChange { Name = "RaAggression", Value = "0.6", CurrentValue = "0.7" } }
                },
                new AdvancedCoachFinding { Id = "hint.seeingBound", Code = "hint.seeingBound", Step = "Live", Severity = "good" }
            }
        };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("coach/apply", "{\"ids\":[\"hint.raOscillation\"]}");
        Assert.Equal(200, status);
        Assert.Equal("hint.raOscillation", (string?)json["response"]!["applied"]![0]);
        Assert.Equal(new[] { "hint.raOscillation" }, (IList<string>)fake.CallsOf(nameof(IGuidingCoach.ApplyCoachActions)).Single()![0]!);

        (int adviceOnly, JObject adviceJson) = await host.Post("coach/apply", "{\"ids\":[\"hint.seeingBound\"]}");
        Assert.Equal(400, adviceOnly);
        Assert.Contains("no setting changes", (string?)adviceJson["error"]);
    }

    [Fact]
    public async Task Apply_BeforeTheFirstSession_Answers409()
    {
        var fake = new FakeInternalGuider();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("coach/apply", "{\"ids\":[\"drift.minMove\"]}");

        Assert.Equal(409, status);
        Assert.Contains("Run the Guiding Coach first", (string?)json["error"]);
    }

    [Fact]
    public async Task Apply_RejectedByTheGuider_Answers409()
    {
        var fake = new FakeInternalGuider { CoachStatus = CompletedSession() };
        fake.OnCall = (name, _) => name == nameof(IGuidingCoach.ApplyCoachActions) ? Task.FromResult(false) : null;
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("coach/apply", "{\"ids\":[\"drift.minMove\"]}");

        Assert.Equal(409, status);
        Assert.Equal("Rejected", (string?)json["code"]);
    }

    [Fact]
    public async Task History_ClampsMaxAndReturnsNewestFirst()
    {
        var fake = new FakeInternalGuider
        {
            CoachHistory = Enumerable.Range(0, 30)
                .Select(i => new AdvancedCoachReport { Id = $"r{i}", Night = $"2026-09-{(i % 28) + 1:00}", Grade = "fair", GuidedRmsArcsec = 1 - i * 0.01 })
                .ToList()
        };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get("coach/history?max=5");
        Assert.Equal(200, status);
        Assert.Equal(5, json["response"]!.Count());
        Assert.Equal("r0", (string?)json["response"]![0]!["id"]);
        Assert.Equal(5, (int)fake.CallsOf(nameof(IGuidingCoach.GetCoachHistory)).Last()![0]!);

        await host.Get("coach/history");
        Assert.Equal(20, (int)fake.CallsOf(nameof(IGuidingCoach.GetCoachHistory)).Last()![0]!);

        await host.Get("coach/history?max=100000");
        Assert.Equal(200, (int)fake.CallsOf(nameof(IGuidingCoach.GetCoachHistory)).Last()![0]!);
    }

    [Fact]
    public async Task History_WithoutReports_IsAnEmptyList()
    {
        var fake = new FakeInternalGuider { CoachHistory = null };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get("coach/history");

        Assert.Equal(200, status);
        Assert.Empty(json["response"]!);
    }

    [Fact]
    public async Task DismissHint_ReturnsTheRemainingHints()
    {
        var fake = new FakeInternalGuider
        {
            State = "Guiding",
            Hints =
            {
                new AdvancedCoachFinding { Id = "hint.raOscillation", Code = "hint.raOscillation", Step = "Live", Severity = "warning" },
                new AdvancedCoachFinding { Id = "hint.lowSnr", Code = "hint.lowSnr", Step = "Live", Severity = "warning" }
            }
        };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("hints/dismiss", "{\"id\":\"hint.raOscillation\"}");
        Assert.Equal(200, status);
        Assert.Equal("hint.raOscillation", (string?)json["response"]!["dismissed"]);
        Assert.Equal("hint.lowSnr", (string?)json["response"]!["hints"]!.Single()["id"]);

        (int again, JObject againJson) = await host.Post("hints/dismiss", "{\"id\":\"hint.raOscillation\"}");
        Assert.Equal(409, again);
        Assert.Contains("no longer active", (string?)againJson["error"]);

        (int invalid, _) = await host.Post("hints/dismiss", "{}");
        Assert.Equal(400, invalid);
    }

    [Fact]
    public async Task Status_CarriesHintsAndCoachRunning()
    {
        var fake = new FakeInternalGuider
        {
            CoachRunning = true,
            Hints = { new AdvancedCoachFinding { Id = "hint.seeingBound", Code = "hint.seeingBound", Severity = "good", Parameters = new Dictionary<string, object?> { ["rmsArcsec"] = 0.8 } } }
        };
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get("status");

        Assert.Equal(200, status);
        JToken guiderStatus = json["response"]!["status"]!;
        Assert.True((bool)guiderStatus["coachRunning"]!);
        Assert.Equal("hint.seeingBound", (string?)guiderStatus["hints"]![0]!["code"]);
        Assert.Equal(0.8, (double)guiderStatus["hints"]![0]!["parameters"]!["rmsArcsec"]!);
    }

    [Theory]
    [InlineData("GET", "coach")]
    [InlineData("POST", "coach/start")]
    [InlineData("POST", "coach/skip")]
    [InlineData("POST", "coach/cancel")]
    [InlineData("POST", "coach/apply")]
    [InlineData("GET", "coach/history")]
    [InlineData("POST", "hints/dismiss")]
    public async Task GuiderWithoutACoach_Answers501(string method, string path)
    {
        var fake = new FakeInternalGuider(optionalParts: false);
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = method == "GET" ? await host.Get(path) : await host.Post(path, "{\"ids\":[\"a\"],\"id\":\"a\"}");

        Assert.Equal(501, status);
        Assert.Equal("NotSupported", (string?)json["code"]);
        Assert.Contains("Guiding Coach", (string?)json["error"]);
    }

    [Fact]
    public async Task ExceptionOfTheCoach_Answers500()
    {
        var fake = new FakeInternalGuider();
        fake.OnCall = (name, _) => name == nameof(IGuidingCoach.GetCoachHistory) ? throw new NotSupportedException("broken") : null;
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get("coach/history");

        Assert.Equal(500, status);
        Assert.Equal("broken", (string?)json["error"]);
    }
}

public class InternalGuiderCoachBodyTests
{
    [Fact]
    public void Options_EmptyBodyMeansAllStepsWithDefaults()
    {
        Assert.True(InternalGuiderRequest.TryParseCoachOptions("", out AdvancedCoachOptions options, out string error), error);
        Assert.Empty(options.Steps);
        Assert.Equal(180, options.DriftSeconds);
    }

    [Fact]
    public void Options_GainsAreWholeNumbers()
    {
        Assert.True(InternalGuiderRequest.TryParseCoachOptions("{\"gains\":[0,5000.0,-1]}", out AdvancedCoachOptions options, out _));
        Assert.Equal(new[] { 0, 5000, -1 }, options.Gains);
        Assert.False(InternalGuiderRequest.TryParseCoachOptions("{\"gains\":[0.5]}", out _, out string error));
        Assert.Contains("whole numbers", error);
    }

    [Fact]
    public void Options_StepsKeepSessionOrder()
    {
        Assert.True(InternalGuiderRequest.TryParseCoachOptions("{\"steps\":[\"Trials\",\"mountresponse\",\"Trials\"]}", out AdvancedCoachOptions options, out _));
        Assert.Equal(new[] { "MountResponse", "Trials" }, options.Steps);
    }

    [Fact]
    public void Ids_AreTrimmedAndDistinct()
    {
        Assert.True(InternalGuiderRequest.TryParseIdsBody("{\"ids\":[\" trial:B \",\"TRIAL:b\",\"drift.minMove\"]}", out List<string> ids, out _));
        Assert.Equal(new[] { "trial:B", "drift.minMove" }, ids);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"ids\":\"x\"}")]
    [InlineData("{\"ids\":[\"\"]}")]
    [InlineData("{\"ids\":[1]}")]
    public void Ids_RejectsBadBodies(string body)
    {
        Assert.False(InternalGuiderRequest.TryParseIdsBody(body, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("{\"id\":\" hint.lowSnr \"}", "hint.lowSnr")]
    [InlineData("{\"Id\":\"hint.decDrift\"}", "hint.decDrift")]
    public void Id_IsParsed(string body, string expected)
    {
        Assert.True(InternalGuiderRequest.TryParseIdBody(body, out string id, out _));
        Assert.Equal(expected, id);
    }

    [Fact]
    public void ApplicableIds_CoverStatusReportAndTrials()
    {
        var status = new AdvancedCoachStatus
        {
            Findings = { new AdvancedCoachFinding { Id = "a", Changes = { new AdvancedCoachSettingChange { Name = "Gain", Value = "1" } } } },
            Report = new AdvancedCoachReport
            {
                Findings = { new AdvancedCoachFinding { Id = "b" } },
                Trials = { new AdvancedCoachTrial { Id = "C", Settings = { new AdvancedCoachSettingChange { Name = "Gain", Value = "2" } } } }
            }
        };

        var hints = new List<AdvancedCoachFinding>
        {
            new() { Id = "hint.raOscillation", Changes = { new AdvancedCoachSettingChange { Name = "RaAggression", Value = "0.6" } } }
        };

        Dictionary<string, int> ids = InternalGuiderController.ApplicableCoachIds(status, hints, out bool anyKnown);

        Assert.True(anyKnown);
        Assert.Equal(1, ids["a"]);
        Assert.Equal(0, ids["b"]);
        Assert.Equal(1, ids["trial:C"]);
        Assert.Equal(1, ids["hint.raOscillation"]);
        InternalGuiderController.ApplicableCoachIds(new AdvancedCoachStatus(), null!, out bool none);
        Assert.False(none);
    }
}

public class InternalGuiderCoachSocketTests
{
    private static async Task<JObject> ReceiveUntil(ClientWebSocket socket, string type, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var builder = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            JObject message = JObject.Parse(builder.ToString());
            if ((string?)message["type"] == type) return message;
        }
    }

    [Fact]
    public async Task ForwardsCoachAndHintEvents()
    {
        var fake = new FakeInternalGuider();
        var service = new InternalGuiderService(() => fake.Mediator);
        int port = InternalGuiderApiHost.FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = new WebServer(o => o.WithUrlPrefix($"http://127.0.0.1:{port}/").WithMode(HttpListenerMode.EmbedIO))
            .WithModule(new InternalGuiderSocket("/ws/internal-guider", service));
        _ = server.RunAsync(cts.Token);

        using var client = new ClientWebSocket();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/internal-guider"), cts.Token);
                break;
            }
            catch when (attempt < 20)
            {
                await Task.Delay(100, cts.Token);
            }
        }
        await ReceiveUntil(client, "hello", cts.Token);
        for (int i = 0; i < 50 && fake.SubscriberCount == 0; i++) await Task.Delay(50, cts.Token);
        Assert.Equal(1, fake.SubscriberCount);

        fake.Raise("coach", new AdvancedCoachStatus
        {
            Phase = "Running",
            Step = "Drift",
            Progress = 0.4,
            Steps =
            {
                new AdvancedCoachStepStatus
                {
                    Name = "Drift",
                    State = "Running",
                    Progress = 0.5,
                    DetailCode = "camera.combination",
                    DetailParameters = new Dictionary<string, object?> { ["exposureSeconds"] = 2.0, ["gain"] = 120, ["index"] = 3, ["total"] = 9 }
                }
            },
            MessageParameters = new Dictionary<string, object?> { ["reason"] = "slew" },
            Drift = new AdvancedCoachDrift
            {
                Samples = { new AdvancedCoachSample { T = 1.5, Ra = 0.2, Dec = double.NaN } },
                PeriodicErrorOffsetArcsec = -0.4,
                GustPercent = 12.5
            }
        });
        JObject coach = await ReceiveUntil(client, "coach", cts.Token);
        Assert.Equal("Drift", (string?)coach["payload"]!["step"]);
        Assert.Equal(0.4, (double)coach["payload"]!["progress"]!);
        Assert.Equal("Running", (string?)coach["payload"]!["steps"]![0]!["state"]);
        Assert.Equal(1.5, (double)coach["payload"]!["drift"]!["samples"]![0]!["t"]!);
        Assert.Equal(JTokenType.Null, coach["payload"]!["drift"]!["samples"]![0]!["dec"]!.Type);
        Assert.Equal("camera.combination", (string?)coach["payload"]!["steps"]![0]!["detailCode"]);
        Assert.Equal(9, (int)coach["payload"]!["steps"]![0]!["detailParameters"]!["total"]!);
        Assert.Equal("slew", (string?)coach["payload"]!["messageParameters"]!["reason"]);
        Assert.Equal(-0.4, (double)coach["payload"]!["drift"]!["periodicErrorOffsetArcsec"]!);
        Assert.Equal(12.5, (double)coach["payload"]!["drift"]!["gustPercent"]!);

        fake.Raise("hint", new AdvancedCoachFinding
        {
            Id = "hint.raOscillation",
            Code = "hint.raOscillation",
            Step = "Live",
            Severity = "warning",
            Parameters = new Dictionary<string, object?> { ["index"] = 0.72 },
            Changes = { new AdvancedCoachSettingChange { Name = "RaAggression", Value = "0.6", CurrentValue = "0.7" } }
        });
        JObject hint = await ReceiveUntil(client, "hint", cts.Token);
        Assert.Equal("hint.raOscillation", (string?)hint["payload"]!["code"]);
        Assert.Equal(0.72, (double)hint["payload"]!["parameters"]!["index"]!);
        Assert.Equal("RaAggression", (string?)hint["payload"]!["changes"]![0]!["name"]);

        service.StopWatching();
    }
}
