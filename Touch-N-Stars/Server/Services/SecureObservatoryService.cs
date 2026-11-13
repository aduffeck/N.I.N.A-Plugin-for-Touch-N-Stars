using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TouchNStars.Server.Services;

/// <summary>What the end-of-night shutdown does besides stopping the sequence, the exposure and guiding.</summary>
public sealed class SecureObservatoryOptions
{
    public bool Park { get; set; } = true;
    public bool CloseDome { get; set; } = true;
    public bool CloseCover { get; set; } = true;
    public bool WarmCamera { get; set; } = true;
    public bool Disconnect { get; set; } = true;

    /// <summary>How long the camera warms up, in minutes (NINA's warm-up ramp).</summary>
    public double WarmMinutes { get; set; } = 10;
}

public static class SecureStepState
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

public static class SecureRunState
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed class SecureStepStatus
{
    public string Id { get; init; }
    public string State { get; set; } = SecureStepState.Pending;
    public string Message { get; set; } = string.Empty;
}

public sealed class SecureObservatoryStatus
{
    public string State { get; init; } = SecureRunState.Idle;
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public SecureObservatoryOptions Options { get; init; }
    public List<SecureStepStatus> Steps { get; init; } = [];
}

/// <summary>
/// The end-of-night shutdown behind Touch-N-Stars' "End night" button. It runs on the server, so that a phone that
/// loses Wi-Fi or goes to sleep halfway does not leave the mount unparked with the roof open.
///
/// In this order: stop the advanced sequence (and wait for it to end), abort the exposure, stop guiding, park the
/// mount, close the dome shutter (only over a parked mount), close the flat panel's cover and switch its light off,
/// warm the camera, disconnect every device. Steps for devices that are not connected are skipped. Devices stay
/// connected when an earlier step failed, so the user can still fix it from the app.
/// </summary>
public sealed class SecureObservatoryService
{
    public static SecureObservatoryService Instance { get; } = new(new MediatorSecureObservatoryRig());

    public const string StopSequence = "stop_sequence";
    public const string AbortExposure = "abort_exposure";
    public const string StopGuiding = "stop_guiding";
    public const string ParkMount = "park_mount";
    public const string CloseDome = "close_dome";
    public const string CloseCover = "close_cover";
    public const string WarmCamera = "warm_camera";
    public const string Disconnect = "disconnect";

    private readonly ISecureObservatoryRig rig;
    private readonly object gate = new();

    private SecureObservatoryOptions options;
    private List<SecureStepStatus> steps = [];
    private string state = SecureRunState.Idle;
    private DateTime? startedAt;
    private DateTime? finishedAt;
    private CancellationTokenSource cancel;

    internal SecureObservatoryService(ISecureObservatoryRig rig)
    {
        this.rig = rig;
    }

    /// <summary>How long the stopped sequence gets to end before hardware moves.</summary>
    internal TimeSpan SequenceStopGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound for closing the dome, which a cancel never interrupts (a half-open roof is worse).</summary>
    internal TimeSpan DomeCloseTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a device gets to report the state a command asked for.</summary>
    internal TimeSpan StateSettleTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The run that is in progress or ended last.</summary>
    internal Task Run { get; private set; } = Task.CompletedTask;

    public SecureObservatoryStatus GetStatus()
    {
        lock (gate)
        {
            return new SecureObservatoryStatus
            {
                State = state,
                StartedAt = startedAt,
                FinishedAt = finishedAt,
                Options = options,
                Steps = steps.Select(step => new SecureStepStatus { Id = step.Id, State = step.State, Message = step.Message }).ToList(),
            };
        }
    }

    /// <summary>
    /// Starts the shutdown. While one runs, returns that one instead of starting a second (its options stay): a
    /// retry from a reconnecting phone must not cancel a park halfway.
    /// </summary>
    public SecureObservatoryStatus Start(SecureObservatoryOptions requested)
    {
        lock (gate)
        {
            if (state == SecureRunState.Running)
            {
                return GetStatus();
            }

            options = requested ?? new SecureObservatoryOptions();
            steps = PlanSteps(options);
            state = SecureRunState.Running;
            startedAt = DateTime.UtcNow;
            finishedAt = null;
            cancel?.Dispose();
            cancel = new CancellationTokenSource();

            Logger.Info($"TNS: securing the observatory (park {options.Park}, close dome {options.CloseDome}, close cover {options.CloseCover}, warm camera {options.WarmCamera}, disconnect {options.Disconnect})");

            CancellationToken token = cancel.Token;
            Run = Task.Run(() => RunAsync(token));
        }

        return GetStatus();
    }

    /// <summary>Stops the remaining steps. A dome that is closing keeps closing.</summary>
    public SecureObservatoryStatus Cancel()
    {
        lock (gate)
        {
            if (state == SecureRunState.Running)
            {
                Logger.Info("TNS: securing the observatory was cancelled");
                cancel?.Cancel();
            }
        }

        return GetStatus();
    }

    private static List<SecureStepStatus> PlanSteps(SecureObservatoryOptions options)
    {
        var planned = new List<string> { StopSequence, AbortExposure, StopGuiding };
        if (options.Park) planned.Add(ParkMount);
        if (options.CloseDome) planned.Add(CloseDome);
        if (options.CloseCover) planned.Add(CloseCover);
        if (options.WarmCamera) planned.Add(WarmCamera);
        if (options.Disconnect) planned.Add(Disconnect);

        return planned.Select(id => new SecureStepStatus { Id = id }).ToList();
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Step(StopSequence, () => StopSequenceAsync(token), token);
            await Step(AbortExposure, () => Task.FromResult(AbortExposureStep()), token);
            await Step(StopGuiding, () => StopGuidingAsync(token), token);

            // A disconnected mount counts as not parked: where it points is unknown (as NINA's DomeVM sees it).
            bool parked = rig.MountParked();
            await Step(ParkMount, async () =>
            {
                var result = await ParkAsync(token);
                parked = rig.MountParked();
                return result;
            }, token);
            await Step(CloseDome, () => CloseDomeAsync(parked), token);
            await Step(CloseCover, () => CloseCoverAsync(token), token);
            await Step(WarmCamera, () => WarmCameraAsync(token), token);
            await Step(Disconnect, DisconnectAsync, token);

            Finish(token.IsCancellationRequested ? SecureRunState.Cancelled : null);
        }
        catch (Exception ex)
        {
            Logger.Error($"TNS: securing the observatory failed: {ex}");
            Finish(token.IsCancellationRequested ? SecureRunState.Cancelled : SecureRunState.Failed);
        }
    }

    private void Finish(string forced)
    {
        lock (gate)
        {
            foreach (var step in steps.Where(step => step.State is SecureStepState.Pending or SecureStepState.Running))
            {
                step.State = SecureStepState.Skipped;
                step.Message = "Cancelled.";
            }

            state = forced ?? (steps.Any(step => step.State == SecureStepState.Failed) ? SecureRunState.Failed : SecureRunState.Done);
            finishedAt = DateTime.UtcNow;
        }

        Logger.Info($"TNS: securing the observatory ended: {state} ({string.Join(", ", steps.Select(step => $"{step.Id} {step.State}"))})");
    }

    private record struct StepResult(string State, string Message)
    {
        public static StepResult Ok(string message) => new(SecureStepState.Done, message);
        public static StepResult Fail(string message) => new(SecureStepState.Failed, message);
        public static StepResult Skip(string message) => new(SecureStepState.Skipped, message);
    }

    private async Task Step(string id, Func<Task<StepResult>> action, CancellationToken token)
    {
        SecureStepStatus step;
        lock (gate)
        {
            step = steps.FirstOrDefault(candidate => candidate.Id == id);
            if (step == null) return;
            if (token.IsCancellationRequested)
            {
                step.State = SecureStepState.Skipped;
                step.Message = "Cancelled.";
                return;
            }

            step.State = SecureStepState.Running;
        }

        StepResult result;
        try
        {
            result = await action();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = StepResult.Skip("Cancelled.");
        }
        catch (Exception ex)
        {
            Logger.Error($"TNS: secure observatory step {id} failed: {ex}");
            result = StepResult.Fail(ex.Message);
        }

        lock (gate)
        {
            step.State = result.State;
            step.Message = result.Message;
        }
    }

    private async Task<StepResult> StopSequenceAsync(CancellationToken token)
    {
        if (!rig.IsSequenceRunning())
        {
            return StepResult.Skip("No sequence is running.");
        }

        rig.StopSequence();

        // A start that was still being prepared can begin running after the first stop: repeat it while it runs.
        DateTime deadline = DateTime.UtcNow + SequenceStopGrace;
        DateTime nextStop = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (rig.IsSequenceRunning())
        {
            if (DateTime.UtcNow >= deadline)
            {
                // Not a failure that should stop the shutdown: it goes on, the mount park ends any slew anyway.
                return StepResult.Fail($"The sequence was still running {SequenceStopGrace.TotalSeconds:0} s after the stop; the shutdown went on.");
            }

            if (DateTime.UtcNow >= nextStop)
            {
                rig.StopSequence();
                nextStop = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            }

            await Task.Delay(PollInterval, token);
        }

        return StepResult.Ok("Sequence stopped.");
    }

    private StepResult AbortExposureStep()
    {
        if (!rig.CameraExposing())
        {
            return StepResult.Skip(rig.CameraConnected() ? "No exposure running." : "Camera not connected.");
        }

        rig.AbortExposure();
        return StepResult.Ok("Exposure aborted.");
    }

    private async Task<StepResult> StopGuidingAsync(CancellationToken token)
    {
        if (!rig.GuiderConnected())
        {
            return StepResult.Skip("Guider not connected.");
        }

        // PHD2 returns false when it was not guiding; nothing guides either way.
        bool stopped = await rig.StopGuiding(token);
        return StepResult.Ok(stopped ? "Guiding stopped." : "Guiding was not running.");
    }

    private async Task<StepResult> ParkAsync(CancellationToken token)
    {
        if (!rig.MountConnected())
        {
            return StepResult.Skip("Mount not connected.");
        }

        if (rig.MountParked())
        {
            return StepResult.Ok("Mount was already parked.");
        }

        if (!await rig.ParkMount(token))
        {
            return StepResult.Fail("The mount could not be parked; the log has details.");
        }

        return await WaitFor(rig.MountParked, token)
            ? StepResult.Ok("Mount parked.")
            : StepResult.Fail("The park command ended, but the mount does not report parked.");
    }

    /// <summary>Never cancelled: a cancel during the close must not leave the roof half open.</summary>
    private async Task<StepResult> CloseDomeAsync(bool mountParked)
    {
        if (!rig.DomeConnected())
        {
            return StepResult.Skip("Dome not connected.");
        }

        if (!rig.DomeCanSetShutter())
        {
            return StepResult.Skip("The dome driver cannot operate the shutter.");
        }

        if (rig.DomeShutter() == ShutterState.ShutterClosed)
        {
            return StepResult.Ok("Shutter was already closed.");
        }

        if (!mountParked)
        {
            return StepResult.Fail(rig.MountConnected()
                ? "Not closed: the mount is not parked, and closing the dome over an unparked telescope can damage it. Park the mount, then close the dome."
                : "Not closed: no mount is connected, so it is unknown whether the telescope is clear. Check it, then close the dome by hand.");
        }

        using var timeout = new CancellationTokenSource(DomeCloseTimeout);
        if (!await rig.CloseDome(timeout.Token))
        {
            return StepResult.Fail($"The shutter did not close (state {rig.DomeShutter()}); the log has details.");
        }

        await WaitFor(() => rig.DomeShutter() == ShutterState.ShutterClosed, CancellationToken.None);
        ShutterState shutter = rig.DomeShutter();

        // Only a dome that reports closed counts: never say "secured" over an open or jammed roof.
        return shutter == ShutterState.ShutterClosed
            ? StepResult.Ok("Shutter closed.")
            : StepResult.Fail($"The close command ended, but the dome reports {shutter}. Check the dome.");
    }

    private async Task<StepResult> CloseCoverAsync(CancellationToken token)
    {
        if (!rig.FlatDeviceConnected())
        {
            return StepResult.Skip("Flat panel not connected.");
        }

        var done = new List<string>();

        if (rig.FlatDeviceHasLight() && rig.FlatDeviceLightOn())
        {
            await rig.LightOff(token);
            done.Add("light off");
        }

        if (rig.FlatDeviceHasCover())
        {
            if (!rig.FlatDeviceCoverClosed())
            {
                await rig.CloseCover(token);

                if (!await WaitFor(rig.FlatDeviceCoverClosed, token))
                {
                    return StepResult.Fail("The cover was told to close, but does not report closed.");
                }
            }

            done.Add("cover closed");
        }

        if (done.Count == 0)
        {
            return StepResult.Skip("The flat panel has no cover and its light is off.");
        }

        string text = string.Join(", ", done);
        return StepResult.Ok(char.ToUpperInvariant(text[0]) + text[1..] + ".");
    }

    private async Task<StepResult> WarmCameraAsync(CancellationToken token)
    {
        if (!rig.CameraConnected())
        {
            return StepResult.Skip("Camera not connected.");
        }

        if (!rig.CameraCoolerOn())
        {
            return StepResult.Skip("The cooler is off.");
        }

        if (rig.CameraTemperatureRampRunning())
        {
            return StepResult.Fail("A cooling or warming ramp is already running; stop it on the camera page.");
        }

        double minutes = Math.Clamp(options?.WarmMinutes ?? 10, 0, 60);
        return await rig.WarmCamera(TimeSpan.FromMinutes(minutes), token)
            ? StepResult.Ok("Camera warmed up, cooler off.")
            : StepResult.Fail("Warming did not complete; the log has details.");
    }

    private async Task<StepResult> DisconnectAsync()
    {
        List<string> failedBefore;
        lock (gate)
        {
            failedBefore = steps.Where(step => step.State == SecureStepState.Failed).Select(step => step.Id).ToList();
        }

        if (failedBefore.Count > 0)
        {
            return StepResult.Skip("Kept connected because a step above failed, so you can fix it from the app.");
        }

        var devices = rig.ConnectedDevices();
        if (devices.Count == 0)
        {
            return StepResult.Skip("Nothing is connected.");
        }

        var failed = new List<string>();
        foreach (var device in devices)
        {
            try
            {
                await device.Disconnect();
            }
            catch (Exception ex)
            {
                Logger.Error($"TNS: disconnecting {device.Name} failed: {ex}");
                failed.Add(device.Name);
            }
        }

        return failed.Count == 0
            ? StepResult.Ok($"Disconnected {string.Join(", ", devices.Select(device => device.Name))}.")
            : StepResult.Fail($"Could not disconnect {string.Join(", ", failed)}.");
    }

    /// <summary>Waits until a device reports the state a command asked for (GetInfo refreshes once per poll).</summary>
    private async Task<bool> WaitFor(Func<bool> reached, CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow + StateSettleTimeout;
        while (!reached())
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(PollInterval, token);
        }

        return true;
    }
}
