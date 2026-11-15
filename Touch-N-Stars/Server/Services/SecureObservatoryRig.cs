using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Interfaces.Mediator;
using TouchNStars.ConeError.Session;
using TouchNStars.Server.Controllers;
using TouchNStars.Server.Infrastructure;
#if PINS
using NINA.Equipment.Equipment.MyGuider.Advanced;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TouchNStars.Server.Services;

/// <summary>A device the shutdown can disconnect at the end.</summary>
internal sealed record RigDevice(string Name, Func<Task> Disconnect);

/// <summary>
/// A job besides the advanced sequence that holds the camera, focuser, flat panel or mount (autofocus, flats, ...).
/// The shutdown stops it before it moves hardware, so it does not take a device away in the middle of a run.
/// </summary>
internal sealed record RigActivity(string Name, Func<bool> IsRunning, Func<Task> Stop);

/// <summary>
/// The parts of the rig the end-of-night shutdown reads and moves. An interface so that the order of the steps and
/// the rules between them (no dome close over an unparked mount, no disconnect after a failed step) can be tested
/// without NINA.
/// </summary>
internal interface ISecureObservatoryRig
{
    bool IsSequenceRunning();
    void StopSequence();

    /// <summary>The jobs besides the sequence that run now.</summary>
    IReadOnlyList<RigActivity> RunningActivities();

    bool CameraConnected();
    bool CameraExposing();
    void AbortExposure();

    /// <summary>Connected, has a regulated cooler and the cooler is on.</summary>
    bool CameraCoolerOn();
    bool CameraTemperatureRampRunning();
    Task<bool> WarmCamera(TimeSpan duration, CancellationToken token);

    bool GuiderConnected();
    Task<bool> StopGuiding(CancellationToken token);

    bool MountConnected();
    bool MountParked();

    /// <summary>
    /// False for mounts without a park position: NINA's park then slews to the pole and stops tracking, and the
    /// mount never reports parked.
    /// </summary>
    bool MountCanPark();
    bool MountTracking();
    Task<bool> ParkMount(CancellationToken token);

    bool DomeConnected();
    bool DomeCanSetShutter();
    ShutterState DomeShutter();
    Task<bool> CloseDome(CancellationToken token);

    bool FlatDeviceConnected();
    bool FlatDeviceHasCover();
    bool FlatDeviceCoverClosed();
    bool FlatDeviceHasLight();
    bool FlatDeviceLightOn();
    Task CloseCover(CancellationToken token);
    Task LightOff(CancellationToken token);

    /// <summary>The connected devices in the order they are disconnected (the mount last).</summary>
    IReadOnlyList<RigDevice> ConnectedDevices();
}

/// <summary>The rig behind the NINA mediators the plugin imports.</summary>
internal sealed class MediatorSecureObservatoryRig : ISecureObservatoryRig
{
    private static Mediators M => TouchNStars.Mediators;

    public bool IsSequenceRunning() => M.Sequence?.IsAdvancedSequenceRunning() == true;

    public void StopSequence()
    {
        ISequenceMediator sequence = M.Sequence;
        if (sequence == null) return;

        // As the ninaAPI and the pins MCP do: the sequencer view model expects its own thread.
        System.Windows.Threading.Dispatcher dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            sequence.CancelAdvancedSequence();
        }
        else
        {
            dispatcher.Invoke(sequence.CancelAdvancedSequence);
        }
    }

    public IReadOnlyList<RigActivity> RunningActivities()
    {
        var activities = new List<RigActivity>
        {
            // The filter offset calibration first: it would start the next filter's autofocus after this one ends.
            new("filter offset calibration", () => FilterOffsetController.IsCalculationRunning, () => { FilterOffsetController.RequestStop(); return Task.CompletedTask; }),
            new("autofocus", AutofocusController.IsAutofocusRunning, AutofocusController.CancelAutofocusAsync),
            new("flats", () => FlatWizardService.IsRunning, () => { FlatWizardService.Stop(); return Task.CompletedTask; }),
            new("cone error measurement", () => ConeErrorService.Instance.IsActive, () => { ConeErrorService.Instance.Abort(); return Task.CompletedTask; }),
        };
#if PINS
        // A calibration slew, or the Guiding Coach's start slew, would run alongside the park and then start guiding
        // on the parked mount; stopping guiding does not end them.
        activities.Add(new("guiding start", () => InternalGuiderService.Instance.GuidingStartInProgress,
            () => { InternalGuiderService.Instance.CancelGuidingStart(); return Task.CompletedTask; }));
        activities.Add(new("guider dark library", () => InternalGuiderService.Instance.DarksInProgress,
            () => { InternalGuiderService.Instance.CancelDarks(); return Task.CompletedTask; }));
        if (InternalGuiderService.Instance.GetConnectedGuider(out _) is IGuidingCoach coach)
        {
            activities.Add(new("guiding coach",
                () => InternalGuiderStates.IsCoachRunning(InternalGuiderService.GetCoachStatus(coach)),
                () => InternalGuiderService.Instance.CancelCoachAsync(coach)));
        }
#endif
        return activities.Where(activity => activity.IsRunning()).ToList();
    }

    public bool CameraConnected() => M.Camera?.GetInfo()?.Connected == true;

    public bool CameraExposing() => M.Camera?.GetInfo() is { Connected: true, IsExposing: true };

    public void AbortExposure() => M.Camera.AbortExposure();

    public bool CameraCoolerOn() => M.Camera?.GetInfo() is { Connected: true, CanSetTemperature: true, CoolerOn: true };

#if PINS
    // The camera view model's flag (a pins addition to ICameraMediator): CameraInfo.TempChangeRunning is never set.
    public bool CameraTemperatureRampRunning() => M.Camera?.TempChangeRunning == true;
#else
    // NINA 3.1 has no way to tell; WarmCamera then takes over the running ramp.
    public bool CameraTemperatureRampRunning() => false;
#endif

    public Task<bool> WarmCamera(TimeSpan duration, CancellationToken token) =>
        M.Camera.WarmCamera(duration, new Progress<NINA.Core.Model.ApplicationStatus>(), token);

    public bool GuiderConnected() => M.Guider?.GetInfo()?.Connected == true;

    public Task<bool> StopGuiding(CancellationToken token) => M.Guider.StopGuiding(token);

    public bool MountConnected() => M.Telescope?.GetInfo()?.Connected == true;

    public bool MountParked() => M.Telescope?.GetInfo() is { Connected: true, AtPark: true };

    public bool MountCanPark() => M.Telescope?.GetInfo() is { Connected: true, CanPark: true };

    public bool MountTracking() => M.Telescope?.GetInfo() is { Connected: true, TrackingEnabled: true };

    public Task<bool> ParkMount(CancellationToken token) =>
        M.Telescope.ParkTelescope(new Progress<NINA.Core.Model.ApplicationStatus>(), token);

    public bool DomeConnected() => M.Dome?.GetInfo()?.Connected == true;

    public bool DomeCanSetShutter() => M.Dome?.GetInfo() is { Connected: true, CanSetShutter: true };

    public ShutterState DomeShutter() => M.Dome?.GetInfo()?.ShutterStatus ?? ShutterState.ShutterNone;

    public Task<bool> CloseDome(CancellationToken token) => M.Dome.CloseShutter(token);

    public bool FlatDeviceConnected() => M.FlatDevice?.GetInfo()?.Connected == true;

    public bool FlatDeviceHasCover() => M.FlatDevice?.GetInfo() is { Connected: true, SupportsOpenClose: true };

    // By name: the enum lives in different assemblies in NINA and in pins.
    public bool FlatDeviceCoverClosed() => M.FlatDevice?.GetInfo()?.CoverState.ToString() == "Closed";

    public bool FlatDeviceHasLight() => M.FlatDevice?.GetInfo() is { Connected: true, SupportsOnOff: true };

    public bool FlatDeviceLightOn() => M.FlatDevice?.GetInfo() is { Connected: true, LightOn: true };

    public Task CloseCover(CancellationToken token) =>
        M.FlatDevice.CloseCover(new Progress<NINA.Core.Model.ApplicationStatus>(), token);

    public Task LightOff(CancellationToken token) =>
        M.FlatDevice.ToggleLight(false, new Progress<NINA.Core.Model.ApplicationStatus>(), token);

    public IReadOnlyList<RigDevice> ConnectedDevices()
    {
        var devices = new List<RigDevice>();

        void Add(string name, bool? connected, Func<Task> disconnect)
        {
            if (connected == true) devices.Add(new RigDevice(name, disconnect));
        }

        // The guider before its camera and the mount last, as NINA's own "disconnect all" does.
        Add("guider", M.Guider?.GetInfo()?.Connected, () => M.Guider.Disconnect());
#if PINS
        // pins' guide camera slot: the guider's camera, which stays connected when the guider disconnects.
        Add("guidecamera", M.GuideCamera?.GetInfo()?.Connected, () => M.GuideCamera.Disconnect());
#endif
        Add("camera", M.Camera?.GetInfo()?.Connected, () => M.Camera.Disconnect());
        Add("focuser", M.Focuser?.GetInfo()?.Connected, () => M.Focuser.Disconnect());
        Add("filterwheel", M.FilterWheel?.GetInfo()?.Connected, () => M.FilterWheel.Disconnect());
        Add("rotator", M.Rotator?.GetInfo()?.Connected, () => M.Rotator.Disconnect());
        Add("flatdevice", M.FlatDevice?.GetInfo()?.Connected, () => M.FlatDevice.Disconnect());
        Add("dome", M.Dome?.GetInfo()?.Connected, () => M.Dome.Disconnect());
        Add("switch", M.Switch?.GetInfo()?.Connected, () => M.Switch.Disconnect());
        Add("weather", M.WeatherData?.GetInfo()?.Connected, () => M.WeatherData.Disconnect());
        Add("safetymonitor", M.SafetyMonitor?.GetInfo()?.Connected, () => M.SafetyMonitor.Disconnect());
        Add("mount", M.Telescope?.GetInfo()?.Connected, () => M.Telescope.Disconnect());

        return devices;
    }
}
