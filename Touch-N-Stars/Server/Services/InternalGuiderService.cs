using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;

namespace TouchNStars.Server.Services;

/// <summary>An event of the internal guider, as handed to the WebSocket fan-out.</summary>
public sealed class InternalGuiderEvent
{
    public InternalGuiderEvent(string type, DateTime timestamp, object payload)
    {
        Type = type;
        Timestamp = timestamp;
        Payload = payload;
    }

    public string Type { get; }
    public DateTime Timestamp { get; }
    public object Payload { get; }
}

/// <summary>Connection summary of the guider device, used by /status and the WebSocket hello, device and heartbeat messages.</summary>
public sealed class InternalGuiderDeviceSummary
{
    /// <summary>True when the connected guider is an <see cref="IAdvancedGuider"/>.</summary>
    public bool Available { get; init; }
    public bool Connected { get; init; }
    public string DeviceId { get; init; }
    public string DeviceName { get; init; }
    public bool IsNative { get; init; }
}

/// <summary>
/// Glue between the Touch-N-Stars API and a guider that implements <see cref="IAdvancedGuider"/>
/// (the internal guider). It takes the guider pins exports to plugins and asks NINA's guider mediator
/// whether it is the connected guider; it keeps the
/// subscription to <see cref="IAdvancedGuider.AdvancedGuiderEvent"/> in sync with the connected
/// device (polled every 2 s, since a (re)connect replaces or re-activates the instance), renders
/// guide frames to JPEG and runs the long-running guider actions in the background.
/// </summary>
public sealed class InternalGuiderService
{
    private const int FrameRingSize = 3;
    private const int JpegCacheSize = 4;
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(2);

    public static InternalGuiderService Instance { get; } = new(() => TouchNStars.Mediators?.Guider, () => TouchNStars.Mediators?.InternalGuider);

    private readonly Func<IGuiderMediator> mediatorProvider;
    private readonly Func<IAdvancedGuider> internalGuiderProvider;
    private readonly object sync = new();
    private readonly LinkedList<AdvancedGuiderFrame> recentFrames = new();
    private readonly LinkedList<(string Key, ushort[] Pixels, byte[] Jpeg)> jpegCache = new();

    private IAdvancedGuider subscribedGuider;
    private bool subscribedConnected;
    private Timer watchTimer;
    private CancellationTokenSource guidingStartCts;
    private CancellationTokenSource darksCts;
    private int ditherRunning;

    /// <param name="internalGuiderProvider">The internal guider, also while it is not connected (pins exports it to plugins).</param>
    public InternalGuiderService(Func<IGuiderMediator> mediatorProvider, Func<IAdvancedGuider> internalGuiderProvider = null)
    {
        this.mediatorProvider = mediatorProvider;
        this.internalGuiderProvider = internalGuiderProvider;
    }

    /// <summary>
    /// Raised on the guider's own thread for every guider event plus the service's own
    /// 'device' and 'action' notifications. Handlers must return immediately (queue, never send).
    /// </summary>
    public event Action<InternalGuiderEvent> EventReceived;

    public IGuiderMediator Mediator => mediatorProvider();

    #region Guider resolution

    /// <summary>The connected advanced guider, or null with a user-facing reason.</summary>
    public IAdvancedGuider GetConnectedGuider(out string reason)
    {
        reason = null;
        IGuiderMediator mediator = Mediator;
        if (mediator == null)
        {
            reason = "The guider mediator is not available yet.";
            return null;
        }

        IDevice device = SafeGetDevice(mediator);
        if (device == null || !SafeConnected(device))
        {
            reason = "No guider is connected. Select and connect the Internal Guider first.";
            return null;
        }

        if (device is IAdvancedGuider advanced)
        {
            return advanced;
        }

        reason = $"The connected guider '{device.Name}' is not the Internal Guider.";
        return null;
    }

    /// <summary>
    /// The advanced guider for configuration: the connected one, else the (not connected) internal
    /// guider, so that it can be configured before the first connect.
    /// </summary>
    public IAdvancedGuider GetConfigurableGuider(out bool connected, out string reason)
    {
        connected = false;
        IAdvancedGuider guider = GetConnectedGuider(out reason);
        if (guider != null)
        {
            connected = true;
            return guider;
        }

        IGuiderMediator mediator = Mediator;
        IDevice device = mediator == null ? null : SafeGetDevice(mediator);
        if (device != null && SafeConnected(device) && device is not IAdvancedGuider)
        {
            // A different guider (e.g. PHD2) is connected: do not edit a device that is not in use.
            return null;
        }

        IAdvancedGuider internalGuider = internalGuiderProvider?.Invoke();
        if (internalGuider != null)
        {
            reason = null;
            return internalGuider;
        }

        reason ??= "The Internal Guider is not available.";
        return null;
    }

    private static IDevice SafeGetDevice(IGuiderMediator mediator)
    {
        try
        {
            return mediator.GetDevice();
        }
        catch (Exception ex)
        {
            Logger.Debug($"InternalGuider: GetDevice failed: {ex.Message}");
            return null;
        }
    }

    private static bool SafeConnected(IDevice device)
    {
        try
        {
            return device.Connected;
        }
        catch
        {
            return false;
        }
    }

    public InternalGuiderDeviceSummary GetDeviceSummary()
    {
        IGuiderMediator mediator = Mediator;
        IDevice device = mediator == null ? null : SafeGetDevice(mediator);
        bool connected = device != null && SafeConnected(device);
        return new InternalGuiderDeviceSummary
        {
            Available = connected && device is IAdvancedGuider,
            Connected = connected,
            DeviceId = device?.Id,
            DeviceName = device?.Name,
            IsNative = device is IAdvancedGuider
        };
    }

    #endregion

    #region Event subscription

    /// <summary>Starts the 2 s device watch (idempotent) and syncs the subscription right away.</summary>
    public void EnsureWatching()
    {
        lock (sync)
        {
            watchTimer ??= new Timer(_ => SyncSubscription(), null, TimeSpan.Zero, WatchInterval);
        }
    }

    public void StopWatching()
    {
        lock (sync)
        {
            watchTimer?.Dispose();
            watchTimer = null;
            if (subscribedGuider != null)
            {
                subscribedGuider.AdvancedGuiderEvent -= OnGuiderEvent;
                subscribedGuider = null;
            }
        }
    }

    /// <summary>
    /// Follows the device: the connected advanced guider if any, else the chooser's internal guider
    /// (so alerts raised while connecting reach the page). Emits a 'device' event on changes.
    /// </summary>
    internal void SyncSubscription()
    {
        IAdvancedGuider target;
        bool connected;
        try
        {
            target = GetConfigurableGuider(out connected, out _);
        }
        catch (Exception ex)
        {
            Logger.Debug($"InternalGuider: subscription sync failed: {ex.Message}");
            return;
        }

        bool changed = false;
        lock (sync)
        {
            if (!ReferenceEquals(target, subscribedGuider))
            {
                if (subscribedGuider != null)
                {
                    try { subscribedGuider.AdvancedGuiderEvent -= OnGuiderEvent; } catch { }
                }
                subscribedGuider = target;
                if (target != null)
                {
                    try { target.AdvancedGuiderEvent += OnGuiderEvent; } catch (Exception ex) { Logger.Warning($"InternalGuider: subscribe failed: {ex.Message}"); }
                }
                recentFrames.Clear();
                jpegCache.Clear();
                changed = true;
            }
            if (connected != subscribedConnected)
            {
                subscribedConnected = connected;
                changed = true;
            }
        }

        if (changed)
        {
            Publish(new InternalGuiderEvent("device", DateTime.UtcNow, GetDeviceSummary()));
        }
    }

    private void OnGuiderEvent(object sender, AdvancedGuiderEventArgs e)
    {
        if (e == null) return;
        DateTime timestamp = e.Timestamp == default ? DateTime.UtcNow : e.Timestamp;
        Publish(new InternalGuiderEvent(e.Type ?? "unknown", timestamp, e.Payload));
    }

    private void Publish(InternalGuiderEvent evt)
    {
        Action<InternalGuiderEvent> handler = EventReceived;
        if (handler == null) return;
        foreach (Action<InternalGuiderEvent> single in handler.GetInvocationList().Cast<Action<InternalGuiderEvent>>())
        {
            try
            {
                single(evt);
            }
            catch (Exception ex)
            {
                // Never let a consumer break the guider thread.
                Logger.Debug($"InternalGuider: event consumer failed: {ex.Message}");
            }
        }
    }

    #endregion

    #region Frames

    /// <summary>
    /// The latest frame, or - when <paramref name="frameNumber"/> is given and still among the
    /// last few frames served - that exact frame, so that /frame-info and /image of one refresh
    /// always describe the same frame even if a new one arrived in between.
    /// </summary>
    public AdvancedGuiderFrame GetFrame(IAdvancedGuider guider, long? frameNumber)
    {
        AdvancedGuiderFrame latest = guider.GetLatestFrame();
        lock (sync)
        {
            // GetLatestFrame() returns a new DTO per call; the pixel buffer identifies the frame.
            if (latest != null && !recentFrames.Any(f => IsSameFrame(f, latest)))
            {
                recentFrames.AddFirst(latest);
                while (recentFrames.Count > FrameRingSize) recentFrames.RemoveLast();
            }

            if (frameNumber.HasValue)
            {
                AdvancedGuiderFrame match = recentFrames.FirstOrDefault(f => f.FrameNumber == frameNumber.Value);
                if (match != null) return match;
            }
        }
        return latest;
    }

    private static bool IsSameFrame(AdvancedGuiderFrame a, AdvancedGuiderFrame b)
    {
        return ReferenceEquals(a, b)
            || (a.FrameNumber == b.FrameNumber && (ReferenceEquals(a.Pixels, b.Pixels) || a.Timestamp == b.Timestamp));
    }

    public byte[] RenderFrameJpeg(AdvancedGuiderFrame frame, GuideFrameRenderer.RenderOptions options)
    {
        string key = string.Join("|",
            frame.FrameNumber,
            options.MaxWidth,
            options.TargetBackground.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            options.ShadowsClipSigma.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            options.Gamma.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            options.Quality);

        lock (sync)
        {
            foreach ((string Key, ushort[] Pixels, byte[] Jpeg) entry in jpegCache)
            {
                if (entry.Key == key && ReferenceEquals(entry.Pixels, frame.Pixels)) return entry.Jpeg;
            }
        }

        byte[] jpeg = GuideFrameRenderer.RenderJpeg(frame.Pixels, frame.Width, frame.Height, options, frame.BitDepth);

        lock (sync)
        {
            jpegCache.AddFirst((key, frame.Pixels, jpeg));
            while (jpegCache.Count > JpegCacheSize) jpegCache.RemoveLast();
        }
        return jpeg;
    }

    /// <summary>
    /// Overlay data for the frame view, plus pixel crops around the primary star (profile inset) and
    /// around the <paramref name="secondaries"/> strongest secondaries (star peeper).
    /// </summary>
    public object BuildFrameInfo(AdvancedGuiderFrame frame, int cropSize, int secondaries = 0)
    {
        bool canCrop = cropSize > 0 && frame.Pixels != null && frame.Pixels.Length >= (long)frame.Width * frame.Height;
        AdvancedGuideStar primary = frame.Stars?.FirstOrDefault(s => s != null && s.IsPrimary);
        object crop = canCrop && primary != null ? CropAround(frame, primary, cropSize) : null;
        List<object> secondaryCrops = canCrop && secondaries > 0 && frame.Stars != null
            ? frame.Stars.Where(s => InternalGuiderStates.IsStar(s) && !s.IsPrimary)
                .OrderByDescending(s => s.Snr)
                .Take(secondaries)
                .Select(s => (object)new { star = s, crop = CropAround(frame, s, cropSize) })
                .ToList()
            : new List<object>();

        return new
        {
            frameNumber = frame.FrameNumber,
            timestamp = frame.Timestamp,
            width = frame.Width,
            height = frame.Height,
            bitDepth = frame.BitDepth,
            lockX = frame.LockX,
            lockY = frame.LockY,
            stars = frame.Stars ?? new List<AdvancedGuideStar>(),
            primaryCrop = crop,
            secondaryCrops,
            levels = frame.Pixels != null && frame.Pixels.Length > 0 ? GuideFrameRenderer.ComputeLevels(frame.Pixels, frame.BitDepth) : null
        };
    }

    private static object CropAround(AdvancedGuiderFrame frame, AdvancedGuideStar star, int cropSize)
    {
        ushort[] pixels = GuideFrameRenderer.Crop(frame.Pixels, frame.Width, frame.Height, star.X, star.Y, cropSize,
            out int x0, out int y0, out int w, out int h);
        return new { x0, y0, width = w, height = h, pixels };
    }

    #endregion

    #region Incidents

    /// <summary>
    /// Renders a stored incident image (a binned context image or a full-resolution key frame) to a JPEG like the
    /// live frames. Binning is in sensor pixels per JPEG pixel: the stored binning times the binning to the width
    /// limit; Width and Height are the size of the JPEG.
    /// </summary>
    public static (byte[] Jpeg, int Binning, int Width, int Height) RenderIncidentImage(AdvancedIncidentImage image, GuideFrameRenderer.RenderOptions options)
    {
        byte[] jpeg = GuideFrameRenderer.RenderJpeg(image.Pixels, image.Width, image.Height, options, image.BitDepth, out int factor, out int width, out int height);
        return (jpeg, Math.Max(1, image.Binning) * factor, width, height);
    }

    /// <summary>The star crops of an incident frame as { frame, crops: [{ star, x0, y0, width, height, pixels }] } (raw, like frame-info's).</summary>
    public static object BuildIncidentCrops(long frame, IEnumerable<AdvancedIncidentImage> crops)
    {
        return new
        {
            frame,
            crops = (crops ?? Enumerable.Empty<AdvancedIncidentImage>())
                .Where(c => c != null)
                .Select(c => new { star = c.Star, x0 = c.X0, y0 = c.Y0, width = c.Width, height = c.Height, pixels = c.Pixels ?? Array.Empty<ushort>() })
                .ToList()
        };
    }

    #endregion

    #region Background actions

    /// <summary>
    /// Starts guiding through NINA's guider mediator (so the GuiderVM raises GuidingStarted and
    /// its state stays consistent) without waiting for calibration and settling to finish. The
    /// outcome is published as an 'action' event.
    /// </summary>
    public void StartGuidingInBackground(bool forceCalibration)
    {
        IGuiderMediator mediator = Mediator;
        var cts = new CancellationTokenSource();
        CancellationTokenSource previous = Interlocked.Exchange(ref guidingStartCts, cts);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }

        _ = Task.Run(async () =>
        {
            bool ok = false;
            string error = null;
            try
            {
                ok = await mediator.StartGuiding(forceCalibration, null, cts.Token).ConfigureAwait(false);
                if (!ok) error = "The guider did not start guiding. Check the event log for the reason.";
            }
            catch (OperationCanceledException)
            {
                error = "Start of guiding was cancelled.";
            }
            catch (Exception ex)
            {
                Logger.Error($"InternalGuider: start guiding failed: {ex}");
                error = ex.Message;
            }
            finally
            {
                Interlocked.CompareExchange(ref guidingStartCts, null, cts);
            }
            PublishAction(forceCalibration ? "calibrate" : "start-guiding", ok, error);
        });
    }

    /// <summary>Cancels a pending start (calibration/settling) issued through this API.</summary>
    public void CancelGuidingStart()
    {
        CancellationTokenSource cts = Interlocked.Exchange(ref guidingStartCts, null);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Dithers in the background. With <paramref name="pixels"/> the native DitherBy is used,
    /// otherwise NINA's dither (profile dither settings) through the mediator.
    /// Returns false when a dither started here is still running.
    /// </summary>
    public bool TryDitherInBackground(IAdvancedGuider guider, double? pixels, bool raOnly)
    {
        if (Interlocked.CompareExchange(ref ditherRunning, 1, 0) != 0) return false;
        IGuiderMediator mediator = Mediator;
        _ = Task.Run(async () =>
        {
            bool ok = false;
            string error = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                ok = pixels.HasValue
                    ? await guider.DitherBy(pixels.Value, raOnly, cts.Token).ConfigureAwait(false)
                    : await mediator.Dither(cts.Token).ConfigureAwait(false);
                if (!ok) error = "Dither failed or did not settle.";
            }
            catch (OperationCanceledException)
            {
                error = "Dither was cancelled.";
            }
            catch (Exception ex)
            {
                Logger.Error($"InternalGuider: dither failed: {ex}");
                error = ex.Message;
            }
            finally
            {
                Interlocked.Exchange(ref ditherRunning, 0);
            }
            PublishAction("dither", ok, error);
        });
        return true;
    }

    /// <summary>
    /// Builds the dark library in the background (progress arrives as 'darks' guider events).
    /// Returns false when a build started here is still running.
    /// </summary>
    public bool TryBuildDarksInBackground(IAdvancedGuider guider, double minExposure, double maxExposure, int frames)
    {
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref darksCts, cts, null) != null)
        {
            cts.Dispose();
            return false;
        }

        _ = Task.Run(async () =>
        {
            bool ok = false;
            string error = null;
            try
            {
                ok = await guider.BuildDarkLibrary(minExposure, maxExposure, frames, cts.Token).ConfigureAwait(false);
                if (!ok) error = "Building the dark library failed. Check the event log for the reason.";
            }
            catch (OperationCanceledException)
            {
                error = "Building the dark library was cancelled.";
            }
            catch (Exception ex)
            {
                Logger.Error($"InternalGuider: dark library build failed: {ex}");
                error = ex.Message;
            }
            finally
            {
                Interlocked.CompareExchange(ref darksCts, null, cts);
                cts.Dispose();
            }
            PublishAction("darks", ok, error);
        });
        return true;
    }

    /// <summary>Cancels a dark library build started through this API. Returns false when none runs.</summary>
    public bool CancelDarks()
    {
        CancellationTokenSource cts = Volatile.Read(ref darksCts);
        if (cts == null) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    private void PublishAction(string action, bool success, string error)
    {
        Publish(new InternalGuiderEvent("action", DateTime.UtcNow, new { action, success, error }));
    }

    #endregion

    #region Guiding Coach

    private readonly OperationSlot coachStartSlot = new();

    /// <summary>The coach status, never null (Idle when the guider has none).</summary>
    public static AdvancedCoachStatus GetCoachStatus(IGuidingCoach coach)
    {
        return coach?.GetCoachStatus() ?? new AdvancedCoachStatus { Phase = AdvancedCoachPhases.Idle };
    }

    /// <summary>
    /// Starts a session. The outcome is Completed with Success = accepted; the guider's result (rejection
    /// reason, message code and parameters, status) comes along when the call returned in time.
    /// </summary>
    public async Task<(GuiderCallOutcome Outcome, AdvancedCoachStartResult Result)> StartCoachAsync(IGuidingCoach coach, AdvancedCoachOptions options)
    {
        AdvancedCoachStartResult result = null;
        GuiderCallOutcome outcome = await RunGuiderCallAsync("coach-start", async ct =>
        {
            AdvancedCoachStartResult r = await coach.StartCoach(options, ct).ConfigureAwait(false);
            Volatile.Write(ref result, r);
            return r?.Accepted == true;
        }, () => Volatile.Read(ref result)?.Message ?? "The Guiding Coach did not start.", coachStartSlot).ConfigureAwait(false);
        return (outcome, Volatile.Read(ref result));
    }

    public Task<GuiderCallOutcome> SkipCoachStepAsync(IGuidingCoach coach)
        => RunGuiderCallAsync("coach-skip", ct => coach.SkipCoachStep(ct), () => "The coach step could not be skipped.");

    /// <summary>Cancels the session: the token of a start call still running here, then the guider's own cancel.</summary>
    public async Task<GuiderCallOutcome> CancelCoachAsync(IGuidingCoach coach)
    {
        bool cancelledHere = CancelSlot(coachStartSlot);
        GuiderCallOutcome outcome = await RunGuiderCallAsync("coach-cancel", ct => coach.CancelCoach(ct),
            () => "The Guiding Coach could not be cancelled.").ConfigureAwait(false);
        return cancelledHere && outcome.State == GuiderCallState.Completed && !outcome.Success && !outcome.Faulted
            ? GuiderCallOutcome.Done(true)
            : outcome;
    }

    public Task<GuiderCallOutcome> ApplyCoachActionsAsync(IGuidingCoach coach, IReadOnlyCollection<string> ids)
        => RunGuiderCallAsync("coach-apply", ct => coach.ApplyCoachActions(ids, ct), () => "The coach recommendations could not be applied.");

    #endregion

    #region Guider calls

    /// <summary>A guider call that may still be running, with the token its cancel endpoint cancels.</summary>
    internal sealed class OperationSlot
    {
        public CancellationTokenSource Cts;
    }

    /// <summary>
    /// How long a guider call is awaited before the HTTP request answers 202 (the call continues in the
    /// background and its outcome is published as an 'action' event). StartCoach returns on acceptance,
    /// so this only matters for a misbehaving or very slow guider.
    /// </summary>
    public TimeSpan CallTimeout { get; internal set; } = TimeSpan.FromSeconds(15);

    private static bool CancelSlot(OperationSlot slot)
    {
        CancellationTokenSource cts = Volatile.Read(ref slot.Cts);
        if (cts == null) return false;
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a guider call and waits up to <see cref="CallTimeout"/> for it. A call that finished by then
    /// yields its result; one still running continues in the background and publishes its outcome as an
    /// 'action' event (no error text when it was cancelled on purpose). With a <paramref name="slot"/>
    /// only one such call runs at a time (Busy otherwise) and the slot's token can be cancelled.
    /// <paramref name="describeFailure"/> words a late 'false' result for the 'action' event.
    /// </summary>
    internal async Task<GuiderCallOutcome> RunGuiderCallAsync(string action, Func<CancellationToken, Task<bool>> call, Func<string> describeFailure,
        OperationSlot slot = null)
    {
        var cts = new CancellationTokenSource();
        if (slot != null && Interlocked.CompareExchange(ref slot.Cts, cts, null) != null)
        {
            cts.Dispose();
            return GuiderCallOutcome.Busy();
        }

        Task<GuiderCallOutcome> running = RunToOutcomeAsync(action, call, cts, slot);
        Task finished = await Task.WhenAny(running, Task.Delay(CallTimeout)).ConfigureAwait(false);
        if (finished == running)
        {
            return await running.ConfigureAwait(false);
        }

        _ = running.ContinueWith(t =>
        {
            GuiderCallOutcome outcome = t.Result;
            string error = null;
            if (!outcome.Success && !outcome.Cancelled)
            {
                try
                {
                    error = outcome.Error ?? describeFailure?.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.Debug($"InternalGuider: describing the failure of '{action}' failed: {ex.Message}");
                }
                error ??= $"'{action}' failed.";
            }
            PublishAction(action, outcome.Success, error);
        }, TaskScheduler.Default);
        return GuiderCallOutcome.Pending();
    }

    private static async Task<GuiderCallOutcome> RunToOutcomeAsync(string action, Func<CancellationToken, Task<bool>> call, CancellationTokenSource cts, OperationSlot slot)
    {
        try
        {
            bool ok = await Task.Run(() => call(cts.Token)).ConfigureAwait(false);
            return GuiderCallOutcome.Done(ok);
        }
        catch (OperationCanceledException)
        {
            return GuiderCallOutcome.WasCancelled();
        }
        catch (Exception ex)
        {
            Logger.Error($"InternalGuider: '{action}' failed: {ex}");
            return GuiderCallOutcome.Fault(ex.Message);
        }
        finally
        {
            if (slot != null) Interlocked.CompareExchange(ref slot.Cts, null, cts);
            cts.Dispose();
        }
    }

    #endregion
}

public enum GuiderCallState
{
    /// <summary>The call returned within the wait; see Success/Cancelled/Faulted.</summary>
    Completed,

    /// <summary>Still running in the background; the outcome follows as an 'action' event.</summary>
    Pending,

    /// <summary>The same kind of call started through this API is still running.</summary>
    Busy
}

/// <summary>Outcome of a guider call run through <see cref="InternalGuiderService"/>.</summary>
public sealed class GuiderCallOutcome
{
    private GuiderCallOutcome(GuiderCallState state, bool success, string error = null, bool cancelled = false, bool faulted = false)
    {
        State = state;
        Success = success;
        Error = error;
        Cancelled = cancelled;
        Faulted = faulted;
    }

    public GuiderCallState State { get; }
    public bool Success { get; }
    public string Error { get; }
    public bool Cancelled { get; }
    public bool Faulted { get; }

    public static GuiderCallOutcome Done(bool success) => new(GuiderCallState.Completed, success);
    public static GuiderCallOutcome WasCancelled() => new(GuiderCallState.Completed, false, "The operation was cancelled.", cancelled: true);
    public static GuiderCallOutcome Fault(string error) => new(GuiderCallState.Completed, false, error, faulted: true);
    public static GuiderCallOutcome Pending() => new(GuiderCallState.Pending, false);
    public static GuiderCallOutcome Busy() => new(GuiderCallState.Busy, false);
}
