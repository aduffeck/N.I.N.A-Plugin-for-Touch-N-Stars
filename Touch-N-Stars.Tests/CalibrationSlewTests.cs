using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using Newtonsoft.Json.Linq;
using TouchNStars.Server.Services;
using Xunit;

namespace TouchNStars.Tests;

public class CalibrationSlewTests
{
    [Theory]
    [InlineData("stay", CalibrationSlewSide.Stay)]
    [InlineData("East", CalibrationSlewSide.East)]
    [InlineData(" WEST ", CalibrationSlewSide.West)]
    public void TryParseSide_AcceptsTheSides(string value, CalibrationSlewSide expected)
    {
        Assert.True(CalibrationSlew.TryParseSide(value, out CalibrationSlewSide side));
        Assert.Equal(expected, side);
    }

    [Theory]
    [InlineData("north")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseSide_RejectsOthers(string? value)
    {
        Assert.False(CalibrationSlew.TryParseSide(value!, out _));
    }

    [Fact]
    public void PointsEast_EastAndWestAreSidesOfTheSky()
    {
        var info = new TelescopeInfo { SideOfPier = PierSide.pierEast };

        Assert.True(CalibrationSlew.PointsEast(CalibrationSlewSide.East, info));
        Assert.False(CalibrationSlew.PointsEast(CalibrationSlewSide.West, info));
    }

    [Theory]
    // West of the pier points east, east of the pier points west; also past the meridian (positive hour angle on pierWest).
    [InlineData(PierSide.pierWest, 10.0, 10.5, true)]
    [InlineData(PierSide.pierWest, 10.5, 10.0, true)]
    [InlineData(PierSide.pierEast, 10.5, 10.0, false)]
    [InlineData(PierSide.pierEast, 10.0, 10.5, false)]
    // Side of pier unknown: the side of the sky it points at now.
    [InlineData(PierSide.pierUnknown, 10.0, 10.5, true)]
    [InlineData(PierSide.pierUnknown, 10.5, 10.0, false)]
    [InlineData(PierSide.pierUnknown, 23.5, 0.5, true)]
    public void PointsEast_StayKeepsTheMountsSide(PierSide pier, double lst, double ra, bool expected)
    {
        var info = new TelescopeInfo { SideOfPier = pier, SiderealTime = lst, RightAscension = ra };

        Assert.Equal(expected, CalibrationSlew.PointsEast(CalibrationSlewSide.Stay, info));
    }

    [Fact]
    public void Target_IsFiveDegreesFromTheMeridianOnTheEquator()
    {
        Coordinates east = CalibrationSlew.Target(10.0, pointEast: true);
        Coordinates west = CalibrationSlew.Target(10.0, pointEast: false);

        Assert.Equal(10.0 + 1.0 / 3.0, east.RA, 6);
        Assert.Equal(10.0 - 1.0 / 3.0, west.RA, 6);
        Assert.Equal(0.0, east.Dec, 6);
        Assert.Equal(-5.0 / 15.0, CalibrationSlew.HourAngleHours(10.0, east.RA), 6);
        Assert.Equal(Epoch.JNOW, east.Epoch);
    }

    [Fact]
    public void Target_WrapsAroundZeroHours()
    {
        Assert.Equal(0.1 + 1.0 / 3.0, CalibrationSlew.Target(24.1, pointEast: true).RA, 6);
        Assert.Equal(24.0 - 1.0 / 3.0 + 0.1, CalibrationSlew.Target(0.1, pointEast: false).RA, 6);
    }

    [Theory]
    [InlineData(false, false, "Connect the mount")]
    [InlineData(true, true, "parked")]
    public void Blocker_NeedsAConnectedUnparkedMount(bool connected, bool atPark, string expected)
    {
        Assert.Contains(expected, CalibrationSlew.Blocker(new TelescopeInfo { Connected = connected, AtPark = atPark }));
        Assert.Null(CalibrationSlew.Blocker(new TelescopeInfo { Connected = true }));
        Assert.NotNull(CalibrationSlew.Blocker(null!));
    }
}

public class CalibrationSlewApiTests
{
    /// <summary>A mount and guider mediator that log their calls (slews with their coordinates) in one list.</summary>
    private sealed class Rig
    {
        public Rig(FakeInternalGuider guider, TelescopeInfo info)
        {
            Info = info;
            Mediator = InterfaceFake.Create<IGuiderMediator>((method, args) =>
            {
                if (method.Name == nameof(IGuiderMediator.GetDevice)) return guider.Guider;
                if (method.Name is nameof(IGuiderMediator.StopGuiding) or nameof(IGuiderMediator.StartGuiding))
                {
                    Log(method.Name + (method.Name == nameof(IGuiderMediator.StartGuiding) ? $"({args![0]})" : ""));
                }
                return null;
            });
            Telescope = InterfaceFake.Create<ITelescopeMediator>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(ITelescopeMediator.GetInfo):
                        return Info;
                    case nameof(ITelescopeMediator.SlewToCoordinatesAsync) when args![0] is Coordinates c:
                        Log($"Slew({c.RA:F4},{c.Dec:F1})");
                        return Slew?.Invoke((CancellationToken)args[1]!) ?? Task.FromResult(true);
                    case nameof(ITelescopeMediator.StopSlew):
                        Log("StopSlew");
                        return null;
                    default:
                        return null;
                }
            });
        }

        public TelescopeInfo Info { get; }
        public IGuiderMediator Mediator { get; }
        public ITelescopeMediator Telescope { get; }
        public Func<CancellationToken, Task<bool>>? Slew { get; set; }
        private readonly List<string> calls = new();

        public List<string> Calls { get { lock (calls) return calls.ToList(); } }

        private void Log(string call) { lock (calls) calls.Add(call); }
    }

    private static TelescopeInfo Mount(PierSide pier = PierSide.pierWest)
        => new() { Connected = true, SiderealTime = 10.0, RightAscension = 10.5, SideOfPier = pier };

    [Fact]
    public async Task StartGuiding_WithSlew_SlewsFromTheSouthThenCalibrates()
    {
        var rig = new Rig(new FakeInternalGuider(), Mount(PierSide.pierWest));
        using var host = new InternalGuiderApiHost(rig.Mediator, telescope: rig.Telescope);
        var waitFor = EventWaiter.Collect(host.Service);

        (int status, JObject json) = await host.Post("start-guiding?calibrate=true&slew=stay");

        Assert.Equal(202, status);
        Assert.Equal("calibrate", (string?)json["response"]!["action"]);
        Assert.Equal("stay", (string?)json["response"]!["slew"]);
        InternalGuiderEvent done = await waitFor(e => e.Type == "action");
        Assert.True((bool)EventWaiter.PayloadJson(done)["success"]!);
        // pierWest points east: RA = LST + 5 degrees.
        Assert.Equal(new[] { "StopGuiding", "Slew(10.3333,-1.0)", "Slew(10.3333,0.0)", "StartGuiding(True)" }, rig.Calls);
    }

    [Fact]
    public async Task StartGuiding_WithSlewWest_PointsWest()
    {
        var rig = new Rig(new FakeInternalGuider(), Mount(PierSide.pierWest));
        using var host = new InternalGuiderApiHost(rig.Mediator, telescope: rig.Telescope);
        var waitFor = EventWaiter.Collect(host.Service);

        (int status, _) = await host.Post("start-guiding?calibrate=true&slew=west");

        Assert.Equal(202, status);
        await waitFor(e => e.Type == "action");
        Assert.Contains("Slew(9.6667,0.0)", rig.Calls);
    }

    [Fact]
    public async Task StartGuiding_SlewFails_StartsNoCalibration()
    {
        var rig = new Rig(new FakeInternalGuider(), Mount()) { Slew = _ => Task.FromResult(false) };
        using var host = new InternalGuiderApiHost(rig.Mediator, telescope: rig.Telescope);
        var waitFor = EventWaiter.Collect(host.Service);

        (int status, _) = await host.Post("start-guiding?calibrate=true&slew=east");

        Assert.Equal(202, status);
        JObject payload = EventWaiter.PayloadJson(await waitFor(e => e.Type == "action"));
        Assert.False((bool)payload["success"]!);
        Assert.Contains("did not reach the calibration position", (string?)payload["error"]);
        Assert.DoesNotContain(rig.Calls, c => c.StartsWith("StartGuiding"));
        Assert.False(host.Service.CalibrationSlewInProgress);
    }

    [Fact]
    public async Task StartGuiding_DuringTheSlew_IsRejectedAndStopCancelsIt()
    {
        var slewStarted = new TaskCompletionSource();
        var rig = new Rig(new FakeInternalGuider { State = "Stopped" }, Mount());
        rig.Slew = async ct =>
        {
            slewStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        };
        using var host = new InternalGuiderApiHost(rig.Mediator, telescope: rig.Telescope);
        var waitFor = EventWaiter.Collect(host.Service);

        Assert.Equal(202, (await host.Post("start-guiding?calibrate=true&slew=stay")).Status);
        await slewStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        (int status, JObject statusJson) = await host.Get("status");
        Assert.Equal(200, status);
        Assert.True((bool)statusJson["response"]!["calibrationSlew"]!);
        (int again, JObject json) = await host.Post("start-guiding?calibrate=true&slew=stay");
        Assert.Equal(409, again);
        Assert.Contains("slewing to the calibration position", (string?)json["error"]);
        Assert.Equal(409, (await host.Post("start-guiding")).Status);

        await host.Post("stop");

        JObject payload = EventWaiter.PayloadJson(await waitFor(e => e.Type == "action" && (string?)EventWaiter.PayloadJson(e)["action"] == "calibrate"));
        Assert.False((bool)payload["success"]!);
        Assert.Contains("cancelled", (string?)payload["error"]);
        Assert.Contains("StopSlew", rig.Calls);
        Assert.DoesNotContain(rig.Calls, c => c.StartsWith("StartGuiding"));
        Assert.False(host.Service.CalibrationSlewInProgress);
    }

    [Theory]
    [InlineData("start-guiding?calibrate=true&slew=north", 400, "stay, east or west")]
    [InlineData("start-guiding?slew=east", 400, "calibrate=true")]
    public async Task StartGuiding_InvalidSlew_Answers400(string path, int expected, string message)
    {
        var rig = new Rig(new FakeInternalGuider(), Mount());
        using var host = new InternalGuiderApiHost(rig.Mediator, telescope: rig.Telescope);

        (int status, JObject json) = await host.Post(path);

        Assert.Equal(expected, status);
        Assert.Contains(message, (string?)json["error"]);
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task StartGuiding_SlewWithParkedMount_Answers409()
    {
        TelescopeInfo info = Mount();
        info.AtPark = true;
        var rig = new Rig(new FakeInternalGuider(), info);
        using var host = new InternalGuiderApiHost(rig.Mediator, telescope: rig.Telescope);

        (int status, JObject json) = await host.Post("start-guiding?calibrate=true&slew=stay");

        Assert.Equal(409, status);
        Assert.Equal("Rejected", (string?)json["code"]);
        Assert.Contains("parked", (string?)json["error"]);
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task StartGuiding_SlewWithoutMount_Answers409()
    {
        var fake = new FakeInternalGuider();
        using var host = new InternalGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("start-guiding?calibrate=true&slew=east");

        Assert.Equal(409, status);
        Assert.Contains("Connect the mount", (string?)json["error"]);
    }

    /// <summary>A fake guider whose StartCoach accepts and records what the mount had done by then.</summary>
    private static FakeInternalGuider CoachGuider(Func<List<string>> callsSoFar, List<string> seenAtStart)
    {
        var fake = new FakeInternalGuider { State = "Stopped" };
        fake.OnCall = (name, _) =>
        {
            if (name != nameof(IGuidingCoach.StartCoach)) return null;
            lock (seenAtStart) seenAtStart.AddRange(callsSoFar());
            return Task.FromResult(new AdvancedCoachStartResult { Accepted = true, Status = new AdvancedCoachStatus { Phase = "Running" } });
        };
        return fake;
    }

    [Fact]
    public async Task CoachStart_WithSlew_SlewsThenStartsTheCoach()
    {
        var seenAtStart = new List<string>();
        Rig? rig = null;
        FakeInternalGuider fake = CoachGuider(() => rig!.Calls, seenAtStart);
        rig = new Rig(fake, Mount(PierSide.pierWest));
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Slew = _ => release.Task;
        using var host = new InternalGuiderApiHost(rig.Mediator, telescope: rig.Telescope);
        var waitFor = EventWaiter.Collect(host.Service);

        (int status, JObject json) = await host.Post("coach/start?slew=stay", "{}");
        Assert.Equal(202, status);
        Assert.True((bool)json["response"]!["pending"]!);
        Assert.True(host.Service.CalibrationSlewInProgress);
        Assert.Equal(409, (await host.Post("start-guiding?calibrate=true&slew=east")).Status);

        release.SetResult(true);
        JObject payload = EventWaiter.PayloadJson(await waitFor(e => e.Type == "action"));
        Assert.Equal("coach-start", (string?)payload["action"]);
        Assert.True((bool)payload["success"]!);
        Assert.Equal(new[] { "StopGuiding", "Slew(10.3333,-1.0)", "Slew(10.3333,0.0)" }, seenAtStart);
        Assert.False(host.Service.CalibrationSlewInProgress);
    }

    [Fact]
    public async Task CoachStart_SlewFails_StartsNoSession()
    {
        var fake = new FakeInternalGuider { State = "Stopped" };
        var rig = new Rig(fake, Mount()) { Slew = _ => Task.FromResult(false) };
        using var host = new InternalGuiderApiHost(rig.Mediator, telescope: rig.Telescope);
        var waitFor = EventWaiter.Collect(host.Service);

        (int status, JObject json) = await host.Post("coach/start?slew=east", "{}");

        // A mount that does not get there is a refusal, not a server error.
        Assert.Equal(409, status);
        Assert.Equal("Rejected", (string?)json["code"]);
        Assert.Contains("the Guiding Coach did not start", (string?)json["error"]);
        Assert.Empty(fake.CallsOf(nameof(IGuidingCoach.StartCoach)));
        Assert.False(host.Service.CalibrationSlewInProgress);
    }

    [Fact]
    public async Task CoachStart_StopCancelsTheSlew()
    {
        var slewStarted = new TaskCompletionSource();
        var fake = new FakeInternalGuider { State = "Stopped" };
        var rig = new Rig(fake, Mount());
        rig.Slew = async ct =>
        {
            slewStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        };
        using var host = new InternalGuiderApiHost(rig.Mediator, telescope: rig.Telescope);
        var waitFor = EventWaiter.Collect(host.Service);

        Assert.Equal(202, (await host.Post("coach/start?slew=stay", "{}")).Status);
        await slewStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (_, JObject statusJson) = await host.Get("status");
        Assert.True((bool)statusJson["response"]!["calibrationSlew"]!);

        await host.Post("stop");

        JObject payload = EventWaiter.PayloadJson(await waitFor(e => e.Type == "action" && (string?)EventWaiter.PayloadJson(e)["action"] == "coach-start"));
        Assert.False((bool)payload["success"]!);
        Assert.Equal("The slew to the calibration position was cancelled.", (string?)payload["error"]);
        Assert.Contains("StopSlew", rig.Calls);
        Assert.Empty(fake.CallsOf(nameof(IGuidingCoach.StartCoach)));
        Assert.False(host.Service.CalibrationSlewInProgress);
    }

    [Theory]
    [InlineData("Running", "Stopped", "already running")]
    [InlineData("Idle", "Calibrating", "calibrating")]
    public async Task CoachStart_WithSlew_WhileBusy_IsRefusedBeforeAnythingMoves(string phase, string state, string message)
    {
        var fake = new FakeInternalGuider { State = state, CoachStatus = new AdvancedCoachStatus { Phase = phase } };
        var rig = new Rig(fake, Mount());
        using var host = new InternalGuiderApiHost(rig.Mediator, telescope: rig.Telescope);

        (int status, JObject json) = await host.Post("coach/start?slew=stay", "{}");

        Assert.Equal(409, status);
        Assert.Equal("coach.busy", (string?)json["messageCode"]);
        Assert.Contains(message, (string?)json["error"]);
        Assert.Empty(rig.Calls);
        Assert.Empty(fake.CallsOf(nameof(IGuidingCoach.StartCoach)));
        Assert.False(host.Service.CalibrationSlewInProgress);
    }

    [Fact]
    public async Task CoachStart_RefusedAfterALongSlew_ActionCarriesTheMessageCode()
    {
        var fake = new FakeInternalGuider { State = "Stopped" };
        fake.OnCall = (name, _) => name == nameof(IGuidingCoach.StartCoach)
            ? Task.FromResult(new AdvancedCoachStartResult
            {
                Accepted = false,
                Message = "The guide camera is not connected.",
                MessageCode = "coach.notConnected",
            })
            : null;
        var rig = new Rig(fake, Mount());
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Slew = _ => release.Task;
        using var host = new InternalGuiderApiHost(rig.Mediator, telescope: rig.Telescope);
        var waitFor = EventWaiter.Collect(host.Service);

        Assert.Equal(202, (await host.Post("coach/start?slew=stay", "{}")).Status);
        release.SetResult(true);

        JObject payload = EventWaiter.PayloadJson(await waitFor(e => e.Type == "action"));
        Assert.Equal("coach-start", (string?)payload["action"]);
        Assert.False((bool)payload["success"]!);
        Assert.Equal("coach.notConnected", (string?)payload["messageCode"]);
        Assert.Equal("The guide camera is not connected.", (string?)payload["error"]);
        Assert.False(host.Service.CalibrationSlewInProgress);
    }

    [Theory]
    [InlineData("coach/start?slew=north", false, 400, "stay, east or west")]
    [InlineData("coach/start?slew=stay", true, 409, "parked")]
    public async Task CoachStart_InvalidSlewOrParked_IsRefused(string path, bool parked, int expected, string message)
    {
        TelescopeInfo info = Mount();
        info.AtPark = parked;
        var fake = new FakeInternalGuider { State = "Stopped" };
        var rig = new Rig(fake, info);
        using var host = new InternalGuiderApiHost(rig.Mediator, telescope: rig.Telescope);

        (int status, JObject json) = await host.Post(path, "{}");

        Assert.Equal(expected, status);
        Assert.Contains(message, (string?)json["error"]);
        Assert.Empty(rig.Calls);
        Assert.Empty(fake.CallsOf(nameof(IGuidingCoach.StartCoach)));
    }
}
