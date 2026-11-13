#nullable enable
using EmbedIO.WebApi;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TouchNStars.Server.Infrastructure;

namespace TouchNStars.Server.Services.Alarm;

/// <summary>
/// Server-side alarm: beeps the PowerBox buzzer on critical events, without an app connected. Sources: every ERROR of
/// the N.I.N.A. log (TNS's own log tail), critical WARNINGs, events (internal guider, safety monitor, image save,
/// camera download timeout) and a 5 s poll (device lost, PHD2 lost lock, disk space, PowerBox over current, LIGHT
/// frames without tracking). <see cref="AlarmEngine"/> decides; <see cref="AlarmBeepWorker"/> plays.
/// Started and stopped with the TNS web server (alarms exist only while it is enabled).
/// </summary>
public sealed class PinsAlarmService {
    public static PinsAlarmService Instance { get; } = new();

    private const int PollEveryTicks = 5;

    private readonly object gate = new();
    private readonly IAlarmClock clock;
    private readonly bool hookNina;

    private readonly SequenceArming arming = new();
    private readonly LightFrameTrackingRule lightRule = new();
    private readonly LostLockRule lostLock = new();
    private readonly DiskSpaceRule disk = new();
    private readonly AutofocusPause afPause = new();
    private readonly object afGate = new();
    private readonly LogRecordParser parser = new();
    private AlarmLogClassifier? classifier;

    private Channel<string[]>? logChannel;
    private CancellationTokenSource? loopCts;
    private Task? loopTask;
    private readonly List<Action> unsubscribers = new();
    private readonly Dictionary<string, bool> sources = new();
    private readonly List<DeviceWatch> devices = new();
    private readonly HashSet<object> guiderRefs = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenRegistration stoppingRegistration;
    private bool stoppingRegistered;
    private bool sequenceSubscribed;
    private bool? safetyPublished;
    // The mount reported tracking on since it connected: a driver that never reports tracking (some INDI drivers
    // fall back to "off") must not make every LIGHT frame an alarm. Known limit: a mount never switched to tracking
    // since it connected gives no alarm for trailed frames.
    private volatile bool trackingSeen;
    private Dictionary<string, bool> portsOn = new();
    private long portOffMs = long.MinValue / 2;  // Interlocked: also read by the guider drop callback
    // After a device-powering port was switched off, device losses count as disconnects for this long
    private const long PortOffWindowMs = 60_000;

    private PinsAlarmService() : this(SystemAlarmClock.Instance, AlarmSettingsStore.CreateDefault(), new PinsBuzzer(), hookNina: true) {
    }

    /// <summary>For tests: no N.I.N.A. hooks, injected clock, settings and buzzer.</summary>
    internal PinsAlarmService(IAlarmClock clock, AlarmSettingsStore settings, IBuzzer buzzer, bool hookNina = false) {
        this.clock = clock;
        this.hookNina = hookNina;
        Settings = settings;
        Engine = new AlarmEngine(clock, () => Settings.Current, clock.MonotonicMs);
        Worker = new AlarmBeepWorker(buzzer, Engine.ReportBurstResult);
    }

    public AlarmSettingsStore Settings { get; }
    public AlarmEngine Engine { get; }
    internal AlarmBeepWorker Worker { get; }
    public bool Running { get { lock (gate) return loopTask != null; } }

    // ----- Lifecycle -----

    public void Start() {
        lock (gate) {
            if (loopTask != null) {
                return;
            }
            Engine.ResetShutdown();
            // The sequence may have ended while the service was stopped (its events were not subscribed)
            arming.Reset(false, clock.MonotonicMs);
            logChannel = Channel.CreateBounded<string[]>(new BoundedChannelOptions(256) {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
            Worker.Start();
            loopCts = new CancellationTokenSource();
            var token = loopCts.Token;
            var reader = logChannel.Reader;
            if (hookNina) {
                Subscribe();
            }
            loopTask = Task.Run(() => LoopAsync(reader, token));
        }
    }

    /// <summary>Stops everything (re-entrant: the TNS server can be switched off and on at runtime).</summary>
    public void Stop() {
        Task? task;
        lock (gate) {
            if (loopTask == null) {
                return;
            }
            Unsubscribe();
            loopCts?.Cancel();
            logChannel?.Writer.TryComplete();
            logChannel = null;
            task = loopTask;
            loopTask = null;
            // Inside the lock: a Start right after must not find the old worker still running (it takes no lock of
            // this service, so this cannot deadlock)
            Worker.Stop();
        }
        try {
            task.Wait(TimeSpan.FromSeconds(3));
        } catch (AggregateException) {
        }
    }

    /// <summary>N.I.N.A. or the plugin shuts down: no more bursts (only sets the flag; safe in any callback).</summary>
    public void BeginShutdown() {
        Engine.BeginShutdown();
    }

    /// <summary>A system shutdown or restart was requested through the app: no bursts for 90 s.</summary>
    public void SuppressForSystemAction() {
        Engine.SuppressFor(90_000);
    }

    /// <summary>
    /// Called by the log tail for every batch of new lines, under its lock: only hands them over, never blocks.
    /// </summary>
    public void EnqueueLogLines(IReadOnlyList<string> lines) {
        if (lines.Count == 0) {
            return;
        }
        logChannel?.Writer.TryWrite(lines.ToArray());
    }

    // ----- API -----

    public (bool StoppedBurst, DateTime? MutedUntilUtc) Silence(int minutes) {
        bool stopped = Worker.StopCurrent();
        Engine.Mute(minutes);
        return (stopped, Engine.MutedUntilUtc);
    }

    /// <summary>A test burst with the settings (or overrides); ignores every rule.</summary>
    public async Task<(BeepResult Result, BurstRequest Request)> TestAsync(AlarmSettings settings) {
        var request = new BurstRequest(Engine.RecordTest(), settings.Volume, settings.ToneMs, settings.PauseMs, settings.Tones);
        var result = await Worker.RunTestAsync(request).ConfigureAwait(false);
        Engine.ReportBurstResult(request.AlarmId, result);
        return (result, request);
    }

    public object GetStatus() {
        long now = clock.MonotonicMs;
        Dictionary<string, bool> src;
        lock (gate) {
            src = new Dictionary<string, bool>(sources);
        }
        return new {
            Settings.Current.Enabled,
            Running,
            SequenceRunning = arming.Running,
            Armed = arming.IsArmed(now),
            GraceRemainingSeconds = (int)Math.Ceiling(Engine.GraceRemainingMs / 1000.0),
            Engine.ShuttingDown,
            Engine.MutedUntilUtc,
            Worker.Bursting,
            Engine.LastBurstUtc,
            LastBeepResult = Engine.LastBeepResult?.ToString(),
            PowerBoxConnected = hookNina && PinsPowerBoxAccess.GetConnectedPowerBox() != null,
            Sources = src,
        };
    }

    // ----- Loop -----

    private async Task LoopAsync(ChannelReader<string[]> reader, CancellationToken token) {
        long tick = 0;
        while (!token.IsCancellationRequested) {
            try {
                bool gotLines = false;
                while (reader.TryRead(out var batch)) {
                    gotLines = true;
                    foreach (var line in batch) {
                        Handle(parser.Feed(line));
                    }
                }
                // A record is complete once no line came for a second
                if (!gotLines && parser.HasPending) {
                    Handle(parser.Flush());
                }

                if (++tick % PollEveryTicks == 0 && hookNina) {
                    Poll();
                }

                // A shutdown ends a running burst (here, not in the shutdown callbacks)
                if (Engine.ShuttingDown && Worker.Bursting) {
                    Worker.StopCurrent();
                }

                foreach (var burst in Engine.ReleaseDue()) {
                    if (!Worker.TryRequest(burst)) {
                        Engine.ReportBurstResult(burst.AlarmId, BeepResult.Failed);
                    }
                }
            } catch (Exception ex) {
                // Not an error: the alarm service must not raise alarms about itself
                Logger.Warning($"Alarm service: {ex.Message}");
            }

            try {
                await Task.Delay(1000, token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                break;
            }
        }
    }

    private void Handle(LogRecord? record) {
        if (record == null) {
            return;
        }
        if (AlarmLogClassifier.IsShutdownLine(record)) {
            BeginShutdown();
            return;
        }
        // The internal guider logs its critical alerts as ERROR too; with its events watched they are alarmed from
        // there (Guiding category, autofocus pause), not a second time from the log
        if (record.Source == "InternalGuider.cs" && record.Member == "OnAlert" && SourceAvailable("internalGuider")) {
            return;
        }
        classifier ??= new AlarmLogClassifier(ControllerTypeNames());
        var candidate = classifier.Classify(record, Settings.Current.Categories.Warnings);
        if (candidate != null) {
            Engine.Submit(candidate, arming.IsArmed(clock.MonotonicMs), AlarmEngine.LogDelayMs);
        }
    }

    private static IEnumerable<string> ControllerTypeNames() {
        Type[] types;
        try {
            types = typeof(PinsAlarmService).Assembly.GetTypes();
        } catch (ReflectionTypeLoadException ex) {
            types = ex.Types.Where(t => t != null).ToArray()!;
        }
        return types.Where(t => typeof(WebApiController).IsAssignableFrom(t)).Select(t => t.Name).ToList();
    }

    private void Submit(AlarmCategory category, string key, string message, AlarmOrigin origin, bool alwaysArmed = false) {
        Engine.Submit(new AlarmCandidate(category, key, message, origin, alwaysArmed), arming.IsArmed(clock.MonotonicMs));
    }

    // ----- Subscriptions (N.I.N.A.) -----

    private sealed class DeviceWatch {
        public DeviceWatch(string name, AlarmCategory category, Func<bool> isConnected) {
            Name = name;
            Category = category;
            IsConnected = isConnected;
        }

        public string Name { get; }
        public AlarmCategory Category { get; }
        public Func<bool> IsConnected { get; }
        public DeviceConnectionTracker? Tracker { get; set; }
        public bool Lost { get; set; }
        public Action? OnConnected { get; set; }
    }

    private global::TouchNStars.Mediators? M => global::TouchNStars.TouchNStars.Mediators;

    // Callers hold the gate
    private void Subscribe() {
        var m = M;
        if (m == null) {
            return;
        }

        Watch("camera", AlarmCategory.Errors, m.Camera);
        Watch("mount", AlarmCategory.Mount, m.Telescope);
        Watch("focuser", AlarmCategory.Errors, m.Focuser);
        Watch("filter wheel", AlarmCategory.Errors, m.FilterWheel);
        var guiderWatch = Watch("guider", AlarmCategory.Guiding, m.Guider);
        Watch("rotator", AlarmCategory.Errors, m.Rotator);
        Watch("flat device", AlarmCategory.Errors, m.FlatDevice);
        Watch("dome", AlarmCategory.Errors, m.Dome);
        Watch("switch", AlarmCategory.Errors, m.Switch);
        Watch("weather", AlarmCategory.Errors, m.WeatherData);
        var safetyWatch = Watch("safety monitor", AlarmCategory.Safety, m.SafetyMonitor);

        // The guider VM disconnects by itself when its device drops: that is a lost connection, not a request
        if (m.Guider != null && guiderWatch != null) {
            Try("guider drop", () => {
                var consumer = new GuiderDropConsumer(m.Guider, () => {
                    long t = clock.MonotonicMs;
                    // A guide camera on a port switched off on purpose did not drop
                    if (!WithinPortOffWindow(t)) {
                        guiderWatch.Tracker?.ConnectionDropped(t);
                    }
                });
                m.Guider.RegisterConsumer(consumer);
                unsubscribers.Add(() => m.Guider.RemoveConsumer(consumer));
            });
        }

        if (m.SafetyMonitor != null && safetyWatch != null) {
            Try("safety", () => WatchSafety(m.SafetyMonitor, safetyWatch));
        }
        if (m.InternalGuider != null) {
            Try("internalGuider", () => WatchAdvancedGuider(m.InternalGuider));
        }
        if (m.ImageSaveMediator != null) {
            Try("imageSave", () => WatchImageSave(m.ImageSaveMediator));
        }
        if (m.Camera != null) {
            Try("cameraTimeout", () => WatchDownloadTimeout(m.Camera));
        }
        sources["log"] = true;
    }

    private void Unsubscribe() {
        foreach (var unsubscribe in unsubscribers) {
            try {
                unsubscribe();
            } catch (Exception) {
            }
        }
        unsubscribers.Clear();
        devices.Clear();
        guiderRefs.Clear();
        sources.Clear();
        sequenceSubscribed = false;
        safetyPublished = null;
        if (stoppingRegistered) {
            stoppingRegistration.Dispose();
            stoppingRegistered = false;
        }
    }

    private void Try(string name, Action subscribe) {
        try {
            subscribe();
            sources[name] = true;
        } catch (Exception ex) {
            sources[name] = false;
            Logger.Warning($"Alarm service: {name} events not available: {ex.Message}");
        }
    }

    private DeviceWatch? Watch<THandler, TConsumer, TInfo>(string name, AlarmCategory category, IDeviceMediator<THandler, TConsumer, TInfo>? mediator)
        where THandler : IDeviceVM<TInfo>
        where TConsumer : IDeviceConsumer<TInfo> {
        if (mediator == null) {
            sources["device:" + name] = false;
            return null;
        }
        var watch = new DeviceWatch(name, category, () => mediator.GetInfo() is DeviceInfo info && info.Connected);
        Func<object, EventArgs, Task> disconnected = (_, _) => {
            watch.Tracker?.DisconnectRequested(clock.MonotonicMs);
            return Task.CompletedTask;
        };
        mediator.Disconnected += disconnected;
        unsubscribers.Add(() => mediator.Disconnected -= disconnected);
        devices.Add(watch);
        sources["device:" + name] = true;
        return watch;
    }

    private void WatchSafety(ISafetyMonitorMediator mediator, DeviceWatch watch) {
        // Only changes of a connected monitor are real (the VM reports unsafe on disconnect); one burst per change
        // to unsafe. A monitor that is unsafe when it connects raises nothing: the poll checks it then.
        void Publish(bool isSafe) {
            lock (gate) {
                if (safetyPublished == isSafe) {
                    return;
                }
                safetyPublished = isSafe;
            }
            if (!isSafe) {
                Submit(AlarmCategory.Safety, "safety|unsafe", "The safety monitor reports UNSAFE", AlarmOrigin.Event);
            }
        }

        EventHandler<IsSafeEventArgs> changed = (_, args) => {
            if (mediator.GetInfo()?.Connected == true) {
                Publish(args.IsSafe);
            }
        };
        watch.OnConnected = () => {
            lock (gate) {
                safetyPublished = null;
            }
            var info = mediator.GetInfo();
            if (info?.Connected == true && !info.IsSafe) {
                Publish(false);
            }
        };
        mediator.IsSafeChanged += changed;
        unsubscribers.Add(() => mediator.IsSafeChanged -= changed);
    }

    private void WatchAdvancedGuider(IAdvancedGuider guider) {
        if (!guiderRefs.Add(guider)) {
            return;
        }
        EventHandler<AdvancedGuiderEventArgs> handler = (_, args) => {
            try {
                if (args?.Type != AdvancedGuiderEventTypes.Alert || args.Payload is not AdvancedGuiderAlert alert ||
                    alert.Severity != "Critical") {
                    return;
                }
                // "Still searching" after the star was lost: waits while an autofocus runs (the star is gone on purpose)
                bool searching = alert.CodeName == "StarReacquireTimeout" && (alert.Detail ?? "").Contains("still searching", StringComparison.OrdinalIgnoreCase);
                if (searching) {
                    lock (afGate) {
                        if (afPause.IsPaused(DataContainer.afRun, clock.MonotonicMs)) {
                            return;
                        }
                    }
                }
                string detail = string.IsNullOrWhiteSpace(alert.Detail) ? "" : $" ({alert.Detail})";
                Submit(AlarmCategory.Guiding, $"guider|internal|{alert.CodeName}", $"Internal guider: {alert.Title}{detail}", AlarmOrigin.Event);
            } catch (Exception) {
                // Never throw into the guider
            }
        };
        guider.AdvancedGuiderEvent += handler;
        unsubscribers.Add(() => guider.AdvancedGuiderEvent -= handler);
    }

    private void WatchImageSave(IImageSaveMediator mediator) {
        EventHandler<ImageSavedEventArgs> saved = (_, args) => {
            try {
                var image = args?.MetaData?.Image;
                if (image == null || !string.Equals(image.ImageType, "LIGHT", StringComparison.OrdinalIgnoreCase)) {
                    return;
                }
                long now = clock.MonotonicMs;
                var (start, end) = LightFrameTrackingRule.ExposureWindow(now, clock.UtcNow, image.ExposureStart, image.ExposureTime);
                if (lightRule.ExposedWithoutTracking(start, end, now, TrackingOff())) {
                    Submit(AlarmCategory.Mount, "mount|tracking-off", "A LIGHT frame was exposed while the mount was not tracking", AlarmOrigin.Event);
                }
            } catch (Exception) {
            }
        };
        mediator.ImageSaved += saved;
        unsubscribers.Add(() => mediator.ImageSaved -= saved);

#if PINS
        Func<object, ImageSaveFailedEventArgs, Task> failed = (_, args) => {
            try {
                bool full = args.IsDiskFull || IsNoSpaceLeft(args.Exception);
                if (full) {
                    Submit(AlarmCategory.Disk, "disk|full", "Image could not be saved: the disk is full", AlarmOrigin.Event);
                } else {
                    Submit(AlarmCategory.Errors, $"image|save-failed|{args.FailureStage}", $"Image could not be saved ({args.FailureStage}): {args.Exception?.Message}", AlarmOrigin.Event);
                }
            } catch (Exception) {
            }
            return Task.CompletedTask;
        };
        mediator.ImageSaveFailed += failed;
        unsubscribers.Add(() => mediator.ImageSaveFailed -= failed);
#endif
    }

    private void WatchDownloadTimeout(ICameraMediator camera) {
#if PINS
        Func<object, EventArgs, Task> timeout = (_, _) => {
            Submit(AlarmCategory.Errors, "camera|download-timeout", "Camera image download timed out", AlarmOrigin.Event);
            return Task.CompletedTask;
        };
        camera.DownloadTimeout += timeout;
        unsubscribers.Add(() => camera.DownloadTimeout -= timeout);
#endif
    }

    // On Linux an IOException's HResult is the errno, ENOSPC (28) for a full disk (IsDiskFull knows only Windows codes)
    private static bool IsNoSpaceLeft(Exception? exception) {
        const int NoSpaceLeftOnDevice = 28;
        return exception switch {
            null => false,
            _ when OperatingSystem.IsWindows() => false,
            IOException io when (io.HResult & 0xffff) == NoSpaceLeftOnDevice => true,
            AggregateException aggregate => aggregate.InnerExceptions.Any(IsNoSpaceLeft),
            _ => IsNoSpaceLeft(exception.InnerException),
        };
    }

    private sealed class GuiderDropConsumer : IGuiderConsumer {
        private readonly IGuiderMediator mediator;
        private readonly Action dropped;
        private bool connected;

        public GuiderDropConsumer(IGuiderMediator mediator, Action dropped) {
            this.mediator = mediator;
            this.dropped = dropped;
        }

        public void UpdateDeviceInfo(GuiderInfo deviceInfo) {
            bool wasConnected = connected;
            connected = deviceInfo?.Connected == true;
            if (wasConnected && !connected && HoldsDevice()) {
                dropped();
            }
        }

        public void Dispose() {
        }

        private bool HoldsDevice() {
            try {
                return mediator.GetDevice() != null;
            } catch (Exception) {
                return false;
            }
        }
    }

    // ----- Poll (every 5 s, on the loop) -----

    private bool TrackingOff() {
        var info = M?.Telescope?.GetInfo();
        if (info == null || !info.Connected) {
            trackingSeen = false;
            return false;
        }
        if (info.TrackingEnabled) {
            trackingSeen = true;
        }
        return trackingSeen && (!info.TrackingEnabled || info.AtPark || info.Slewing);
    }

    private void Poll() {
        var m = M;
        if (m == null) {
            return;
        }
        long now = clock.MonotonicMs;
        Settings.Refresh();
        BackgroundWorker.CatchUp();

        PollLifetime();
        PollSequence(m, now);
        PollPowerBox(now);
        PollDevices(now);

        lightRule.AddSample(now, TrackingOff());

        // An internal guider can be replaced on reconnect: watch the current one too
        try {
            if (m.Guider?.GetDevice() is IAdvancedGuider current) {
                lock (gate) {
                    if (loopTask != null) {
                        WatchAdvancedGuider(current);
                    }
                }
            }
        } catch (Exception) {
        }

        // PHD2 lost the star for more than a minute (not during an autofocus run)
        try {
            var device = m.Guider?.GetDevice();
            bool lost = device is IGuider guider && device.GetType().Name == "PHD2Guider" && guider.State == "LostLock";
            bool paused;
            lock (afGate) {
                paused = afPause.IsPaused(DataContainer.afRun, now);
            }
            if (lostLock.Update(lost, paused, now)) {
                Submit(AlarmCategory.Guiding, "guider|phd2|lostlock", "PHD2 lost the guide star for more than a minute", AlarmOrigin.Poll);
            }
        } catch (Exception) {
        }

        // Free space where the images go
        try {
            string? path = m.Profile?.ActiveProfile?.ImageFileSettings?.FilePath;
            string? dir = NearestExistingDirectory(path);
            if (dir != null && disk.Update(new DriveInfo(dir).AvailableFreeSpace)) {
                Submit(AlarmCategory.Disk, "disk|low", "Less than 2 GB free for images", AlarmOrigin.Poll);
            }
            SetSource("disk", dir != null);
        } catch (Exception) {
            SetSource("disk", false);
        }
    }

    // Before the device poll: a port switched off this poll must already count for the devices
    private void PollPowerBox(long now) {
        // PowerBox over current: a set flag is a fault until the port is switched on again (always armed)
        try {
            var ports = PinsPowerBoxAccess.ReadPorts();
            SetSource("powerBox", ports.Count > 0);
            foreach (var port in ports.Where(p => p.Overcurrent)) {
                Submit(AlarmCategory.PowerBox, $"powerbox|oc|{port.PortKey}", $"PowerBox {port.Label}: over current, switched off", AlarmOrigin.Poll, alwaysArmed: true);
            }

            // A port that powers devices (12 V, USB, adjustable; not the heaters, which switch themselves off at
            // duty 0) switched off on purpose (e.g. by the end of a sequence, not by over current): a device that
            // disappears within a minute was disconnected on purpose, not lost. Known limit: a real loss of another
            // device in that minute counts as a disconnect too.
            bool switchedOff = ports.Any(p => IsDevicePort(p.PortKey) && !p.Enabled && !p.Overcurrent &&
                                              portsOn.TryGetValue(p.PortKey, out bool wasOn) && wasOn);
            portsOn = ports.ToDictionary(p => p.PortKey, p => p.Enabled);
            if (switchedOff) {
                Interlocked.Exchange(ref portOffMs, now);
            }
        } catch (Exception) {
            SetSource("powerBox", false);
        }

        if (WithinPortOffWindow(now)) {
            List<DeviceWatch> watches;
            lock (gate) {
                watches = devices.ToList();
            }
            foreach (var w in watches) {
                w.Tracker?.PortSwitchedOff(now);
            }
        }
    }

    private bool WithinPortOffWindow(long now) => now - Interlocked.Read(ref portOffMs) <= PortOffWindowMs;

    private static bool IsDevicePort(string portKey) {
        return portKey.StartsWith("12v|", StringComparison.Ordinal) || portKey.StartsWith("usb|", StringComparison.Ordinal) ||
               portKey.StartsWith("buck|", StringComparison.Ordinal);
    }

    private void PollLifetime() {
#if PINS
        lock (gate) {
            if (stoppingRegistered || loopTask == null) {
                return;
            }
            var lifetime = System.Windows.Application.ApplicationLifetime;
            if (lifetime == null) {
                // Set once the host has started; TNS can load before
                return;
            }
            stoppingRegistration = lifetime.ApplicationStopping.Register(BeginShutdown);
            stoppingRegistered = true;
            sources["lifetime"] = true;
        }
#endif
    }

    private void PollSequence(global::TouchNStars.Mediators m, long now) {
        var mediator = m.Sequence;
        if (mediator == null) {
            return;
        }
        bool initialized;
        try {
            initialized = mediator.Initialized;
        } catch (Exception) {
            return;
        }
        if (!initialized) {
            return;
        }

        lock (gate) {
            if (!sequenceSubscribed && loopTask != null) {
                Func<object, EventArgs, Task> starting = (_, _) => {
                    arming.OnStarting();
                    Engine.ResetEpisodes();
                    lock (afGate) {
                        afPause.OnSequenceStarting();
                    }
                    // A device lost before the sequence and not reconnected is not part of it
                    lock (gate) {
                        foreach (var w in devices) {
                            w.Lost = false;
                        }
                    }
                    return Task.CompletedTask;
                };
                Func<object, EventArgs, Task> finished = (_, _) => {
                    arming.OnFinished(clock.MonotonicMs);
                    lock (afGate) {
                        afPause.OnSequenceFinished();
                    }
                    return Task.CompletedTask;
                };
                try {
                    mediator.SequenceStarting += starting;
                    mediator.SequenceFinished += finished;
                    unsubscribers.Add(() => {
                        mediator.SequenceStarting -= starting;
                        mediator.SequenceFinished -= finished;
                    });
                    sequenceSubscribed = true;
                    sources["sequence"] = true;
                    // Events from now on; the state until now from the poll
                    bool runningNow = false;
                    try {
                        runningNow = mediator.IsAdvancedSequenceRunning();
                    } catch (Exception) {
                    }
                    arming.Reset(runningNow, now);
                } catch (Exception) {
                    sources["sequence"] = false;
                }
            }
        }

        bool advanced = false;
        try {
            advanced = mediator.IsAdvancedSequenceRunning();
        } catch (Exception) {
        }
        arming.Poll(advanced, sequenceSubscribed, now);
    }

    private void PollDevices(long now) {
        List<DeviceWatch> watches;
        lock (gate) {
            watches = devices.ToList();
        }
        foreach (var watch in watches) {
            bool connected;
            try {
                connected = watch.IsConnected();
            } catch (Exception) {
                continue;
            }
            try {
                if (watch.Tracker == null) {
                    // Devices connected before the start are the baseline
                    watch.Tracker = new DeviceConnectionTracker(watch.Name, connected);
                    if (connected) {
                        watch.OnConnected?.Invoke();
                    }
                    continue;
                }
                foreach (var change in watch.Tracker.Poll(connected, now)) {
                    if (change.Connected) {
                        watch.Lost = false;
                        watch.OnConnected?.Invoke();
                    } else if (change.Lost) {
                        watch.Lost = true;
                    }
                }
                if (watch.Lost && !connected) {
                    Submit(watch.Category, $"device|{watch.Name}|lost", $"The {watch.Name} lost its connection (not disconnected by request)", AlarmOrigin.Poll);
                }
            } catch (Exception) {
                // One device must not stop the others' checks
            }
        }
    }

    private bool SourceAvailable(string name) {
        lock (gate) {
            return sources.TryGetValue(name, out bool available) && available;
        }
    }

    private void SetSource(string name, bool available) {
        lock (gate) {
            sources[name] = available;
        }
    }

    private static string? NearestExistingDirectory(string? path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return null;
        }
        try {
            var dir = new DirectoryInfo(path);
            while (dir != null && !dir.Exists) {
                dir = dir.Parent;
            }
            return dir?.FullName;
        } catch (Exception) {
            return null;
        }
    }
}
