using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using EmbedIO;
using EmbedIO.WebApi;
using Newtonsoft.Json.Linq;
using NINA.Equipment.Interfaces;
using TouchNStars.Server.Controllers;
using TouchNStars.Server.Services;
using Xunit;

namespace TouchNStars.Tests;

public class SecureObservatoryServiceTests
{
    private sealed class FakeRig : ISecureObservatoryRig
    {
        public readonly List<string> Calls = [];

        public bool SequenceRunning;
        public bool SequenceIgnoresStop;
        public bool CameraOn = true;
        public bool Exposing;
        public bool Cooling = true;
        public bool Ramp;
        public bool WarmResult = true;
        public bool GuiderOn = true;
        public bool MountOn = true;
        public bool Parked;
        public bool ParkResult = true;
        public bool ParkThrows;
        public TaskCompletionSource<bool>? ParkGate;
        public bool DomeOn = true;
        public bool DomeShutterControl = true;
        public ShutterState Shutter = ShutterState.ShutterOpen;
        public bool DomeCloseWorks = true;
        public bool FlatOn = true;
        public bool HasCover = true;
        public bool CoverClosed;
        public bool HasLight = true;
        public bool Light = true;

        public bool IsSequenceRunning() => SequenceRunning;
        public void StopSequence() { Calls.Add("stop_sequence"); if (!SequenceIgnoresStop) SequenceRunning = false; }
        public bool CameraConnected() => CameraOn;
        public bool CameraExposing() => CameraOn && Exposing;
        public void AbortExposure() { Calls.Add("abort_exposure"); Exposing = false; }
        public bool CameraCoolerOn() => CameraOn && Cooling;
        public bool CameraTemperatureRampRunning() => Ramp;
        public Task<bool> WarmCamera(TimeSpan duration, CancellationToken token) { Calls.Add($"warm {duration.TotalMinutes}"); if (WarmResult) Cooling = false; return Task.FromResult(WarmResult); }
        public bool GuiderConnected() => GuiderOn;
        public Task<bool> StopGuiding(CancellationToken token) { Calls.Add("stop_guiding"); return Task.FromResult(true); }
        public bool MountConnected() => MountOn;
        public bool MountParked() => MountOn && Parked;
        public async Task<bool> ParkMount(CancellationToken token)
        {
            Calls.Add("park");
            if (ParkThrows) throw new InvalidOperationException("driver error");
            if (ParkGate != null) await ParkGate.Task.WaitAsync(token);
            if (ParkResult) Parked = true;
            return ParkResult;
        }
        public bool DomeConnected() => DomeOn;
        public bool DomeCanSetShutter() => DomeOn && DomeShutterControl;
        public ShutterState DomeShutter() => Shutter;
        public Task<bool> CloseDome(CancellationToken token) { Calls.Add("close_dome"); if (DomeCloseWorks) Shutter = ShutterState.ShutterClosed; return Task.FromResult(true); }
        public bool FlatDeviceConnected() => FlatOn;
        public bool FlatDeviceHasCover() => FlatOn && HasCover;
        public bool FlatDeviceCoverClosed() => CoverClosed;
        public bool FlatDeviceHasLight() => FlatOn && HasLight;
        public bool FlatDeviceLightOn() => Light;
        public Task CloseCover(CancellationToken token) { Calls.Add("close_cover"); CoverClosed = true; return Task.CompletedTask; }
        public Task LightOff(CancellationToken token) { Calls.Add("light_off"); Light = false; return Task.CompletedTask; }
        public IReadOnlyList<RigDevice> ConnectedDevices()
        {
            var devices = new List<RigDevice>();
            if (GuiderOn) devices.Add(new RigDevice("guider", () => { Calls.Add("disconnect guider"); return Task.CompletedTask; }));
            if (CameraOn) devices.Add(new RigDevice("camera", () => { Calls.Add("disconnect camera"); return Task.CompletedTask; }));
            if (MountOn) devices.Add(new RigDevice("mount", () => { Calls.Add("disconnect mount"); return Task.CompletedTask; }));
            return devices;
        }
    }

    private static SecureObservatoryService Service(FakeRig rig) => new(rig)
    {
        SequenceStopGrace = TimeSpan.FromMilliseconds(300),
        StateSettleTimeout = TimeSpan.FromMilliseconds(100),
        PollInterval = TimeSpan.FromMilliseconds(5),
    };

    private static async Task<SecureObservatoryStatus> RunToEnd(SecureObservatoryService service, SecureObservatoryOptions? options = null)
    {
        service.Start(options ?? new SecureObservatoryOptions());
        await service.Run.WaitAsync(TimeSpan.FromSeconds(10));
        return service.GetStatus();
    }

    private static SecureStepStatus StepOf(SecureObservatoryStatus status, string id) => status.Steps.Single(step => step.Id == id);

    [Fact]
    public async Task FullShutdown_RunsEveryStepInOrder()
    {
        var rig = new FakeRig { SequenceRunning = true, Exposing = true };

        var status = await RunToEnd(Service(rig));

        Assert.Equal(SecureRunState.Done, status.State);
        Assert.All(status.Steps, step => Assert.Equal(SecureStepState.Done, step.State));
        Assert.Equal(
            ["stop_sequence", "abort_exposure", "stop_guiding", "park", "close_dome", "light_off", "close_cover", "warm 10", "disconnect guider", "disconnect camera", "disconnect mount"],
            rig.Calls);
        Assert.NotNull(status.StartedAt);
        Assert.NotNull(status.FinishedAt);
    }

    [Fact]
    public async Task UnselectedStepsAreNotPlannedOrRun()
    {
        var rig = new FakeRig();

        var status = await RunToEnd(Service(rig), new SecureObservatoryOptions { Park = false, CloseDome = false, CloseCover = false, WarmCamera = false, Disconnect = false });

        Assert.Equal([SecureObservatoryService.StopSequence, SecureObservatoryService.AbortExposure, SecureObservatoryService.StopGuiding], status.Steps.Select(step => step.Id));
        Assert.Equal(["stop_guiding"], rig.Calls);
        Assert.Equal(SecureRunState.Done, status.State);
    }

    [Fact]
    public async Task DevicesThatAreNotConnectedAreSkipped()
    {
        var rig = new FakeRig { CameraOn = false, GuiderOn = false, MountOn = false, DomeOn = false, FlatOn = false };

        var status = await RunToEnd(Service(rig), new SecureObservatoryOptions { Disconnect = false });

        Assert.Equal(SecureRunState.Done, status.State);
        Assert.All(status.Steps, step => Assert.Equal(SecureStepState.Skipped, step.State));
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task DomeStaysOpenWhenTheMountDidNotPark()
    {
        var rig = new FakeRig { ParkResult = false };

        var status = await RunToEnd(Service(rig));

        Assert.Equal(SecureRunState.Failed, status.State);
        Assert.Equal(SecureStepState.Failed, StepOf(status, SecureObservatoryService.ParkMount).State);
        Assert.Equal(SecureStepState.Failed, StepOf(status, SecureObservatoryService.CloseDome).State);
        Assert.Contains("not parked", StepOf(status, SecureObservatoryService.CloseDome).Message);
        Assert.DoesNotContain("close_dome", rig.Calls);
        // The rest still runs, but nothing is disconnected so the user can still fix it from the app.
        Assert.Contains("close_cover", rig.Calls);
        Assert.Equal(SecureStepState.Skipped, StepOf(status, SecureObservatoryService.Disconnect).State);
        Assert.DoesNotContain(rig.Calls, call => call.StartsWith("disconnect"));
    }

    [Fact]
    public async Task DomeStaysOpenWithoutAMount()
    {
        var rig = new FakeRig { MountOn = false };

        var status = await RunToEnd(Service(rig));

        Assert.Equal(SecureStepState.Skipped, StepOf(status, SecureObservatoryService.ParkMount).State);
        Assert.Equal(SecureStepState.Failed, StepOf(status, SecureObservatoryService.CloseDome).State);
        Assert.Contains("no mount", StepOf(status, SecureObservatoryService.CloseDome).Message);
    }

    [Fact]
    public async Task DomeIsClosedWhenTheMountWasAlreadyParkedAndParkIsNotSelected()
    {
        var rig = new FakeRig { Parked = true };

        var status = await RunToEnd(Service(rig), new SecureObservatoryOptions { Park = false });

        Assert.Equal(SecureStepState.Done, StepOf(status, SecureObservatoryService.CloseDome).State);
        Assert.DoesNotContain("park", rig.Calls);
    }

    [Fact]
    public async Task DomeThatDoesNotReportClosedFails()
    {
        var rig = new FakeRig { DomeCloseWorks = false };

        var status = await RunToEnd(Service(rig));

        Assert.Equal(SecureStepState.Failed, StepOf(status, SecureObservatoryService.CloseDome).State);
        Assert.Contains("ShutterOpen", StepOf(status, SecureObservatoryService.CloseDome).Message);
    }

    [Fact]
    public async Task AFailingStepDoesNotStopTheOthers()
    {
        var rig = new FakeRig { ParkThrows = true };

        var status = await RunToEnd(Service(rig));

        Assert.Equal("driver error", StepOf(status, SecureObservatoryService.ParkMount).Message);
        Assert.Contains("warm 10", rig.Calls);
        Assert.Equal(SecureRunState.Failed, status.State);
    }

    [Fact]
    public async Task SequenceThatKeepsRunningIsReportedAndTheShutdownGoesOn()
    {
        var rig = new FakeRig { SequenceRunning = true, SequenceIgnoresStop = true };

        var status = await RunToEnd(Service(rig));

        Assert.Equal(SecureStepState.Failed, StepOf(status, SecureObservatoryService.StopSequence).State);
        Assert.Contains("park", rig.Calls);
    }

    [Fact]
    public async Task CameraWithACoolerRampRunningIsNotWarmed()
    {
        var rig = new FakeRig { Ramp = true };

        var status = await RunToEnd(Service(rig));

        Assert.Equal(SecureStepState.Failed, StepOf(status, SecureObservatoryService.WarmCamera).State);
        Assert.DoesNotContain(rig.Calls, call => call.StartsWith("warm"));
    }

    [Fact]
    public async Task CameraWithTheCoolerOffIsSkipped()
    {
        var rig = new FakeRig { Cooling = false };

        var status = await RunToEnd(Service(rig));

        Assert.Equal(SecureStepState.Skipped, StepOf(status, SecureObservatoryService.WarmCamera).State);
        Assert.Equal(SecureRunState.Done, status.State);
    }

    [Fact]
    public async Task WarmMinutesArePassedOn()
    {
        var rig = new FakeRig();

        await RunToEnd(Service(rig), new SecureObservatoryOptions { WarmMinutes = 4 });

        Assert.Contains("warm 4", rig.Calls);
    }

    [Fact]
    public async Task SecondStartWhileRunningJoinsTheFirst()
    {
        var rig = new FakeRig { ParkGate = new TaskCompletionSource<bool>() };
        var service = Service(rig);

        var first = service.Start(new SecureObservatoryOptions());
        var second = service.Start(new SecureObservatoryOptions { Park = false, WarmCamera = false });

        Assert.Equal(SecureRunState.Running, second.State);
        Assert.Equal(first.StartedAt, second.StartedAt);
        Assert.Contains(second.Steps, step => step.Id == SecureObservatoryService.ParkMount);

        rig.ParkGate.SetResult(true);
        await service.Run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, rig.Calls.Count(call => call == "park"));
    }

    [Fact]
    public async Task CancelSkipsTheRemainingSteps()
    {
        var rig = new FakeRig { ParkGate = new TaskCompletionSource<bool>() };
        var service = Service(rig);

        service.Start(new SecureObservatoryOptions());
        await WaitUntil(() => rig.Calls.Contains("park"));
        service.Cancel();
        await service.Run.WaitAsync(TimeSpan.FromSeconds(10));

        var status = service.GetStatus();
        Assert.Equal(SecureRunState.Cancelled, status.State);
        Assert.Equal(SecureStepState.Skipped, StepOf(status, SecureObservatoryService.ParkMount).State);
        Assert.Equal(SecureStepState.Skipped, StepOf(status, SecureObservatoryService.Disconnect).State);
        Assert.DoesNotContain("close_dome", rig.Calls);
    }

    [Fact]
    public async Task CanRunAgainAfterTheEnd()
    {
        var rig = new FakeRig { ParkResult = false };
        var service = Service(rig);

        Assert.Equal(SecureRunState.Failed, (await RunToEnd(service)).State);

        rig.ParkResult = true;
        Assert.Equal(SecureRunState.Done, (await RunToEnd(service)).State);
    }

    [Fact]
    public void IdleBeforeTheFirstRun()
    {
        var status = Service(new FakeRig()).GetStatus();

        Assert.Equal(SecureRunState.Idle, status.State);
        Assert.Empty(status.Steps);
    }

    [Fact]
    public async Task Api_StartsWithTheBodysOptionsAndAnswersInCamelCase()
    {
        // Parked already, so that the dome may close without the park step.
        var rig = new FakeRig { Parked = true };
        var service = Service(rig);
        int port = InternalGuiderApiHost.FreePort();
        using var lifetime = new CancellationTokenSource();
        using var server = new WebServer(o => o.WithUrlPrefix($"http://127.0.0.1:{port}/").WithMode(HttpListenerMode.EmbedIO))
            .WithWebApi("/api", m => m.WithController(() => new SecureObservatoryController(service)));
        _ = server.RunAsync(lifetime.Token);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/") };

        JObject idle = JObject.Parse(await Retry(() => client.GetStringAsync("secure-observatory")));
        Assert.Equal("idle", (string?)idle["state"]);

        using var body = new StringContent("{\"park\":false,\"warmCamera\":false,\"disconnect\":false}", Encoding.UTF8, "application/json");
        using HttpResponseMessage started = await client.PostAsync("secure-observatory", body);
        JObject running = JObject.Parse(await started.Content.ReadAsStringAsync());
        Assert.Equal(200, (int)started.StatusCode);
        Assert.False((bool)running["options"]!["park"]!);
        Assert.Contains("stop_sequence", running["steps"]!.Select(step => (string?)step["id"]));
        Assert.DoesNotContain("park_mount", running["steps"]!.Select(step => (string?)step["id"]));

        await service.Run.WaitAsync(TimeSpan.FromSeconds(10));
        JObject done = JObject.Parse(await client.GetStringAsync("secure-observatory"));
        Assert.Equal("done", (string?)done["state"]);
        Assert.Equal("skipped", (string?)done["steps"]![0]!["state"]);
        Assert.Equal("No sequence is running.", (string?)done["steps"]![0]!["message"]);
        Assert.DoesNotContain("park", rig.Calls);

        using HttpResponseMessage bad = await client.PostAsync("secure-observatory", new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(400, (int)bad.StatusCode);

        lifetime.Cancel();
    }

    private static async Task<string> Retry(Func<Task<string>> call)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await call(); }
            catch (HttpRequestException) when (attempt < 30) { await Task.Delay(100); }
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(5);
        }
    }
}
