using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using TouchNStars.ConeError.Domain;

namespace TouchNStars.ConeError.Session;

public sealed class AutoPlanRequest
{
    public double? DovetailMm { get; set; }
    public int? Repetitions { get; set; }
    public double? Declination { get; set; }
    public double? ExposureSeconds { get; set; }

    /// <summary>The phone's clock, for the server-side clock check.</summary>
    public DateTime? ClientTimeUtc { get; set; }
}

public sealed class AutoPlanResponse
{
    public string PlanId { get; init; }
    public DateTime ExpiresUtc { get; init; }

    /// <summary>Seconds the plan is valid from its creation, so that clients need not compare clocks.</summary>
    public double ExpiresInSeconds { get; init; }

    public AutoPlan Plan { get; init; }
    public PairTiming Timing { get; init; }
    public double ExposureSeconds { get; init; }
    public double DovetailMm { get; init; }
    public int Repetitions { get; init; }
    public bool AskPierLabels { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class TimeCheck
{
    public DateTime SystemUtc { get; init; }
    public DateTime? ClientUtc { get; init; }
    public double? SkewSeconds { get; init; }
    public bool LooksUnset { get; init; }
    public double? MountLstHours { get; init; }
    public double PiLstHours { get; init; }

    /// <summary>Mount sidereal time minus the Pi's (from its clock and the profile longitude), minutes.</summary>
    public double? LstSkewMinutes { get; init; }
}

public sealed class Preconditions
{
    public MountSnapshot Mount { get; init; }
    public string DriverKind { get; init; }
    public bool PierSideSupported { get; init; }
    public string PierSideDescription { get; init; }
    public string PierSideReason { get; init; }
    public MountLimits MountLimits { get; init; }
    public PointingModelInfo PointingModel { get; init; }
    public ProfileFacts Profile { get; init; }
    public ObservatoryState Observatory { get; init; }
    public TimeCheck Time { get; init; }
    public bool RestorePending { get; init; }
    public RestoreJournal Journal { get; init; }
    public IReadOnlyList<string> AutoBlockers { get; init; }
    public IReadOnlyList<string> ManualBlockers { get; init; }
    public IReadOnlyList<string> Warnings { get; init; }
    public bool AutoReady => AutoBlockers.Count == 0;
    public bool ManualReady => ManualBlockers.Count == 0;
}

/// <summary>
/// Entry point of the cone error wizard for the API: preconditions, plans, the one running session
/// (automated or daytime), the persisted settings, results and restore journal.
/// </summary>
public sealed class ConeErrorService
{
    public static readonly TimeSpan AutoPlanLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Clock differences that warn and that block, seconds.</summary>
    public const double TimeSkewWarnSeconds = 120.0;
    public const double TimeSkewBlockSeconds = 600.0;

    /// <summary>A mount sidereal time this far off the Pi's means the mount's clock or site is wrong, minutes.</summary>
    public const double LstSkewWarnMinutes = 1.0;
    public const double LstSkewBlockMinutes = 8.0;

    /// <summary>Planning assumptions on top of the profile settle time, seconds.</summary>
    public const double SlewSeconds = 120.0;
    public const double FlipSeconds = 90.0;
    public const double ExtraSettleSeconds = 2.0;
    public const double SolveSeconds = 30.0;

    private static ConeErrorService instance;

    private readonly Func<IConeEnvironment> environment;
    private readonly bool backgroundLoops;
    private readonly object sync = new();
    private readonly Dictionary<string, (AutoPlanRequest Request, AutoPlanResponse Response)> autoPlans = new();
    private AutoMeasurementSession auto;
    private ManualFlipSession manual;
    private bool starting;
    private bool abortWhileStarting;

    public ConeErrorService(Func<IConeEnvironment> environment, ConeErrorStore store, bool backgroundLoops = true)
    {
        this.environment = environment;
        Store = store;
        this.backgroundLoops = backgroundLoops;
    }

    /// <summary>The service the API uses, bound to NINA through <see cref="Adapters.NinaConeEnvironment"/>.</summary>
    public static ConeErrorService Instance
    {
        get
        {
            lock (typeof(ConeErrorService))
            {
                return instance ??= new ConeErrorService(
                    () => new Adapters.NinaConeEnvironment(TouchNStars.Mediators),
                    new ConeErrorStore(ConeErrorStore.DefaultDirectory));
            }
        }
    }

    public ConeErrorStore Store { get; }

    public ConeErrorSettings GetSettings() => Store.LoadSettings();

    public ConeErrorSettings SaveSettings(ConeErrorSettings settings)
    {
        ConeErrorSettings current = Store.LoadSettings();
        settings.VerifiedPierLabels = current.VerifiedPierLabels;
        return Store.SaveSettings(settings);
    }

    /// <summary>Applies only the fields a client sent (JSON object) on top of the stored settings.</summary>
    public ConeErrorSettings PatchSettings(string json)
    {
        ConeErrorSettings current = Store.LoadSettings();
        // a copy: PopulateObject would append a sent list to the stored one in place
        List<string> verified = current.VerifiedPierLabels.ToList();
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                Newtonsoft.Json.JsonConvert.PopulateObject(json, current);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                throw new ConeErrorException("BadRequest", $"The settings are not valid: {ex.Message}");
            }
        }

        current.VerifiedPierLabels = verified;
        return Store.SaveSettings(current);
    }

    public IReadOnlyList<ConeResultRecord> Results() => Store.LoadResults().OrderByDescending(r => r.TimestampUtc).ToList();

    #region Preconditions

    public Preconditions GetPreconditions(DateTime? clientUtc)
    {
        IConeEnvironment env = environment();
        MountSnapshot mount = SafeSnapshot(env.Mount);
        MountDriverInfo driver = env.Driver.Inspect();
        ProfileFacts profile = env.Observatory.Profile();
        ObservatoryState state = env.Observatory.State();
        TimeCheck time = CheckTime(env.Clock.UtcNow, clientUtc, mount, profile);
        RestoreJournal journal = Store.LoadJournal();
        bool sessionActive = IsSessionActive();

        var common = new List<string>();
        var auto = new List<string>();
        var manualBlockers = new List<string>();
        var warnings = new List<string>();

        if (mount == null || !mount.Connected)
        {
            common.Add("Connect the mount.");
        }
        else if (mount.AtPark)
        {
            common.Add("Unpark the mount.");
        }

        if (!driver.PierSide.Supported)
        {
            common.Add(driver.PierSide.UnsupportedReason);
        }

        if (profile.Latitude == 0.0 && profile.Longitude == 0.0)
        {
            common.Add("Set the site location in the profile (latitude and longitude are 0).");
        }

        if (state.SequenceRunning)
        {
            common.Add("A sequence is running; stop it first.");
        }

        if (state.InternalGuiderGuiding)
        {
            common.Add("The internal guider is guiding; stop guiding first.");
        }

        if (journal != null && !sessionActive)
        {
            common.Add("A previous cone error session did not finish; restore the mount settings first.");
        }

        if (time.LooksUnset)
        {
            common.Add($"The Pi's clock looks unset ({time.SystemUtc:yyyy-MM-dd HH:mm} UTC). Set the time; the sun check depends on it.");
        }
        else if (time.SkewSeconds is { } skew && Math.Abs(skew) > TimeSkewBlockSeconds)
        {
            common.Add($"The Pi's clock is {Math.Abs(skew) / 60.0:F0} minutes off this device's clock. Set the time first.");
        }
        else if (time.SkewSeconds is { } small && Math.Abs(small) > TimeSkewWarnSeconds)
        {
            warnings.Add($"The Pi's clock is {Math.Abs(small) / 60.0:F1} minutes off this device's clock.");
        }
        else if (time.SkewSeconds == null)
        {
            warnings.Add("The Pi's clock could not be compared with this device's clock.");
        }

        if (time.LstSkewMinutes is { } lstSkew && Math.Abs(lstSkew) > LstSkewBlockMinutes)
        {
            common.Add($"The mount's sidereal time is {Math.Abs(lstSkew):F0} minutes off the Pi's: the mount's time or site is wrong.");
        }
        else if (time.LstSkewMinutes is { } lstSmall && Math.Abs(lstSmall) > LstSkewWarnMinutes)
        {
            warnings.Add($"The mount's sidereal time is {Math.Abs(lstSmall):F1} minutes off the Pi's; check the mount's time and site.");
        }

        auto.AddRange(common);
        manualBlockers.AddRange(common);

        if (mount is { Connected: true, AtPark: false, Tracking: false })
        {
            auto.Add("Turn tracking on.");
        }

        if (!state.CameraConnected)
        {
            auto.Add("Connect the camera.");
        }
        else if (state.CameraExposing)
        {
            auto.Add("The camera is exposing; stop the exposure or loop first.");
        }

        if (profile.SolverProblem != null)
        {
            auto.Add(profile.SolverProblem);
        }

        if (!(profile.FocalLength > 0.0))
        {
            auto.Add("Set the telescope focal length in the profile; the plate solver needs it.");
        }

        switch (driver.PointingModel.State)
        {
            case PointingModelState.Active:
                warnings.Add($"The mount has an active pointing model ({driver.PointingModel.Detail}). It compensates cone error, so the measurement shows only what is left. Clear the model to measure the real cone error.");
                break;
            case PointingModelState.Unknown:
                warnings.Add("Whether the mount runs a pointing model cannot be read. An active model hides cone error; make sure none is active.");
                break;
        }

        warnings.Add("Stop PHD2 guiding if it runs; it cannot be detected here.");

        return new Preconditions
        {
            Mount = mount,
            DriverKind = driver.Kind,
            PierSideSupported = driver.PierSide.Supported,
            PierSideDescription = driver.PierSide.Description,
            PierSideReason = driver.PierSide.UnsupportedReason,
            MountLimits = driver.Limits,
            PointingModel = driver.PointingModel,
            Profile = profile,
            Observatory = state,
            Time = time,
            RestorePending = journal != null && !sessionActive,
            Journal = journal,
            AutoBlockers = auto.Distinct().ToList(),
            ManualBlockers = manualBlockers.Distinct().ToList(),
            Warnings = warnings
        };
    }

    public static TimeCheck CheckTime(DateTime systemUtc, DateTime? clientUtc, MountSnapshot mount, ProfileFacts profile)
    {
        double piLst = SunEphemeris.LocalSiderealTimeHours(systemUtc, profile.Longitude);
        double? lstSkew = mount is { Connected: true }
            ? Spherical.WrapHours12(mount.LocalSiderealTimeHours - piLst) * 60.0
            : null;
        return new TimeCheck
        {
            SystemUtc = systemUtc,
            ClientUtc = clientUtc,
            SkewSeconds = clientUtc.HasValue ? (systemUtc - clientUtc.Value).TotalSeconds : null,
            LooksUnset = systemUtc.Year < 2025,
            MountLstHours = mount?.LocalSiderealTimeHours,
            PiLstHours = piLst,
            LstSkewMinutes = lstSkew
        };
    }

    private static MountSnapshot SafeSnapshot(IConeMount mount)
    {
        try
        {
            return mount.Snapshot();
        }
        catch (Exception ex)
        {
            Logger.Warning($"ConeError: reading the mount failed: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Automated measurement

    private static string MountKey(ProfileFacts profile, MountSnapshot mount) => $"{profile.ProfileId}|{mount?.Name}";

    public AutoPlanResponse PlanAuto(AutoPlanRequest request)
    {
        IConeEnvironment env = environment();
        Preconditions pre = GetPreconditions(request.ClientTimeUtc);
        if (pre.AutoBlockers.Count > 0)
        {
            throw new ConeErrorException("PreconditionsNotMet", string.Join(" ", pre.AutoBlockers));
        }

        if (request.DovetailMm is not > 0)
        {
            throw new ConeErrorException("DovetailRequired", "Enter the distance between the two mounting points on the dovetail (mm).");
        }

        ConeErrorSettings settings = Store.LoadSettings();
        AutoPlanResponse response = BuildAutoPlan(env, request, settings, pre);

        lock (sync)
        {
            autoPlans.Clear();
            autoPlans[response.PlanId] = (request, response);
        }

        return response;
    }

    private AutoPlanResponse BuildAutoPlan(IConeEnvironment env, AutoPlanRequest request, ConeErrorSettings settings, Preconditions pre)
    {
        ProfileFacts profile = pre.Profile;
        MountSnapshot mount = pre.Mount;
        double exposure = request.ExposureSeconds is > 0 and <= 120 ? request.ExposureSeconds.Value : settings.ExposureSeconds ?? profile.PlateSolveExposureSeconds;
        var timing = new PairTiming(
            SlewSeconds + profile.SettleSeconds,
            FlipSeconds + profile.SettleSeconds,
            ExtraSettleSeconds,
            exposure,
            SolveSeconds);
        LimitSet limits = Limits(settings, profile, pre.MountLimits, settings.AutoMinAltitudeDegrees);
        DateTime now = env.Clock.UtcNow;
        int repetitions = request.Repetitions ?? settings.Repetitions;

        AutoPlan plan = TargetPlanner.Plan(new PlannerInput
        {
            NowUtc = now,
            LocalSiderealTimeHours = mount.LocalSiderealTimeHours,
            LatitudeDegrees = profile.Latitude,
            // at home OnStepX picks the side from the sign of the hour angle: West for the first target
            CurrentSide = mount.AtHome ? PierSideLabel.West : mount.Side,
            Repetitions = repetitions,
            Declination = request.Declination,
            Timing = timing,
            Limits = limits,
            SunAt = env.Sun.SunAt
        });

        string key = MountKey(profile, mount);
        return new AutoPlanResponse
        {
            PlanId = Guid.NewGuid().ToString("N"),
            ExpiresUtc = now + AutoPlanLifetime,
            ExpiresInSeconds = AutoPlanLifetime.TotalSeconds,
            Plan = plan,
            Timing = timing,
            ExposureSeconds = exposure,
            DovetailMm = request.DovetailMm ?? 0.0,
            Repetitions = repetitions,
            AskPierLabels = !settings.VerifiedPierLabels.Contains(key),
            Warnings = pre.Warnings
        };
    }

    public static LimitSet Limits(ConeErrorSettings settings, ProfileFacts profile, MountLimits mount, double minAltitude) => new()
    {
        MinAltitudeDegrees = minAltitude,
        HorizonAltitudeAt = profile.HorizonAltitudeAt,
        PinsPastMeridianDegrees = profile.MaxMinutesAfterMeridian * 0.25,
        MountHorizonDegrees = mount?.HorizonDegrees,
        MountOverheadDegrees = mount?.OverheadDegrees,
        MountPastMeridianEastDegrees = mount?.PastMeridianEastDegrees,
        MountPastMeridianWestDegrees = mount?.PastMeridianWestDegrees,
        MountSource = mount?.Source,
        SunExclusionDegrees = Math.Max(settings.SunExclusionDegrees, ConeErrorSettings.MinSunExclusionDegrees),
        // a pointing below the horizon (a dipped horizon from a height) can see a Sun below it too
        SunSetAltitudeDegrees = Math.Min(LimitValidator.SunSetAltitudeDegrees, minAltitude - 2.0)
    };

    /// <summary>Starts a confirmed plan after planning it again with the current time (the sky has moved since).</summary>
    public SessionStatus StartAuto(string planId, bool acknowledgePointingModel)
    {
        IConeEnvironment env = environment();
        (AutoPlanRequest Request, AutoPlanResponse Response) planned;
        lock (sync)
        {
            if (planId == null || !autoPlans.TryGetValue(planId, out planned))
            {
                throw new ConeErrorException("PlanUnknown", "This plan is unknown or was replaced; plan again.");
            }

            autoPlans.Remove(planId);
            if (env.Clock.UtcNow > planned.Response.ExpiresUtc)
            {
                throw new ConeErrorException("PlanExpired", "The confirmation expired; plan again.");
            }

            if (IsSessionActive())
            {
                throw new ConeErrorException("Busy", "A cone error session is already running.");
            }
        }

        Preconditions pre = GetPreconditions(null);
        if (pre.AutoBlockers.Count > 0)
        {
            throw new ConeErrorException("PreconditionsNotMet", string.Join(" ", pre.AutoBlockers));
        }

        MountDriverInfo driver = env.Driver.Inspect();
        if (driver.PointingModel.State != PointingModelState.None && !acknowledgePointingModel)
        {
            throw new ConeErrorException("PointingModelNotAcknowledged", "Confirm the pointing model warning first.");
        }

        ConeErrorSettings settings = Store.LoadSettings();
        AutoPlanRequest request = planned.Request;
        request.Declination = planned.Response.Plan.DeclinationDegrees;
        AutoPlanResponse fresh = BuildAutoPlan(env, request, settings, pre);
        if (!fresh.Plan.Ok)
        {
            throw new ConeErrorException("PlanRefused", fresh.Plan.Refusal ?? LimitValidator.Describe(fresh.Plan.Violations));
        }

        // the confirmed envelope is never widened silently
        if (fresh.Plan.MaxAbsHourAngleDegrees > planned.Response.Plan.MaxAbsHourAngleDegrees + 1e-6
            || fresh.Plan.NormalSidesOnly != planned.Response.Plan.NormalSidesOnly
            || fresh.Repetitions != planned.Response.Repetitions)
        {
            throw new ConeErrorException("PlanChanged", "The plan changed since it was confirmed; plan again.");
        }

        string key = MountKey(pre.Profile, pre.Mount);
        var session = new AutoMeasurementSession(
            env.Mount,
            driver.PierSide,
            env.Imager,
            env.Clock,
            env.Sun,
            Store,
            new AutoRunOptions
            {
                Repetitions = fresh.Repetitions,
                DeclinationDegrees = fresh.Plan.DeclinationDegrees,
                ExposureSeconds = fresh.ExposureSeconds,
                DovetailMm = fresh.DovetailMm,
                Timing = fresh.Timing,
                Limits = Limits(settings, pre.Profile, driver.Limits, settings.AutoMinAltitudeDegrees),
                LatitudeDegrees = pre.Profile.Latitude,
                MaxAbsHourAngleDegrees = planned.Response.Plan.MaxAbsHourAngleDegrees,
                AskPierLabels = fresh.AskPierLabels,
                NormalSidesOnly = fresh.Plan.NormalSidesOnly,
                MountKey = key,
                Profile = pre.Profile
            },
            backgroundLoops);
        session.PierLabelsConfirmed += () => RememberPierLabels(key);

        lock (sync)
        {
            if (IsSessionActive())
            {
                throw new ConeErrorException("Busy", "A cone error session is already running.");
            }

            auto = session;
            manual = null;
        }

        session.Start();
        return session.GetStatus();
    }

    // Called from a running measurement's pier label check: a settings file that cannot be read or written only
    // means the labels are asked for again next time, it does not end the measurement.
    private void RememberPierLabels(string key)
    {
        try
        {
            ConeErrorSettings settings = Store.LoadSettings();
            if (!settings.VerifiedPierLabels.Contains(key))
            {
                settings.VerifiedPierLabels.Add(key);
                Store.SaveSettings(settings);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"ConeError: could not remember the confirmed pier side labels: {ex.Message}");
        }
    }

    public bool Answer(int id, bool yes)
    {
        AutoMeasurementSession session;
        lock (sync)
        {
            session = auto;
        }

        return session != null && session.Answer(id, yes);
    }

    #endregion

    #region Status and abort

    private bool IsSessionActive() => starting || (auto != null && !auto.Finished) || (manual != null && !manual.Finished);

    public SessionStatus Status()
    {
        AutoMeasurementSession a;
        ManualFlipSession m;
        lock (sync)
        {
            a = auto;
            m = manual;
        }

        if (m != null)
        {
            m.Heartbeat();
            return m.GetStatus();
        }

        if (a != null)
        {
            return a.GetStatus();
        }

        return new SessionStatus { RestorePending = Store.LoadJournal() != null };
    }

    /// <summary>Stops the mount at once and ends whatever runs (restores on the way out).</summary>
    public SessionStatus Abort()
    {
        AutoMeasurementSession a;
        ManualFlipSession m;
        lock (sync)
        {
            a = auto;
            m = manual;
        }

        if (a != null && !a.Finished)
        {
            a.Abort();
        }

        if (m != null && !m.Finished)
        {
            Task end = m.AbortAsync();
            end.ContinueWith(t => Logger.Error($"ConeError: ending the daytime check failed: {t.Exception}"), TaskContinuationOptions.OnlyOnFaulted);
        }

        lock (sync)
        {
            if (starting)
            {
                // the daytime check is just beginning: it is ended as soon as it has begun
                abortWhileStarting = true;
            }
        }

        if ((a == null || a.Finished) && (m == null || m.Finished))
        {
            // nothing of ours runs, but the user asked the mount to stop
            try
            {
                environment().Mount.Stop();
            }
            catch (Exception ex)
            {
                Logger.Warning($"ConeError: stop failed: {ex.Message}");
            }
        }

        return Status();
    }

    /// <summary>Applies a leftover restore journal: settings only (preferred pier side, tracking), never a move.</summary>
    public async Task<SessionStatus> RestoreAsync()
    {
        if (IsSessionActive())
        {
            throw new ConeErrorException("Busy", "A cone error session is running.");
        }

        RestoreJournal journal = Store.LoadJournal();
        if (journal == null)
        {
            return Status();
        }

        if (journal.OriginalUnknown)
        {
            // nothing to put back: a guessed preferred pier side could leave the mount worse off than it is
            Store.ClearJournal();
            throw new ConeErrorException("OriginalUnknown",
                "The restore journal could not be read, so the mount's settings from before the session are unknown. Check the mount's preferred pier side and tracking.");
        }

        IConeEnvironment env = environment();
        MountSnapshot mount = SafeSnapshot(env.Mount);
        if (mount == null || !mount.Connected)
        {
            throw new ConeErrorException("MountNotConnected", "Connect the mount to restore its settings.");
        }

        // the journal holds another mount's settings (another profile or mount connected since)
        string key = MountKey(env.Observatory.Profile(), mount);
        if (journal.MountName != null && journal.MountName != key)
        {
            throw new ConeErrorException("OtherMount",
                $"The settings to restore belong to another mount or profile ({journal.MountName}). Connect that mount to restore them.");
        }

        MountDriverInfo driver = env.Driver.Inspect();
        if (journal.PreferredPierSide != null)
        {
            await driver.PierSide.RestoreToAsync(journal.PreferredPierSide);
        }

        if (journal.Tracking.HasValue && !env.Mount.SetTracking(journal.Tracking.Value))
        {
            throw new ConeErrorException("RestoreFailed", "The mount refused to restore tracking.");
        }

        Store.ClearJournal();
        return Status();
    }

    /// <summary>
    /// At plugin start: a session that did not end (e.g. the battery died during it) left the mount with its preferred
    /// pier side forced, and only the cone error panel showed it. A warning tells about it at once.
    /// </summary>
    public void WarnAboutOpenRestore()
    {
        try
        {
            if (Store.LoadJournal() != null)
            {
                Notification.ShowWarning("A cone error measurement did not finish: the mount may still have its preferred pier side forced. Open Cone Error to restore its settings.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"ConeError: could not check for an open restore: {ex.Message}");
        }
    }

    /// <summary>Plugin teardown: stop and restore whatever runs.</summary>
    public void Shutdown()
    {
        try
        {
            AutoMeasurementSession a;
            ManualFlipSession m;
            lock (sync)
            {
                a = auto;
                m = manual;
            }

            // one budget, long enough for a running move to end (up to 15 s) and the INDI confirmation (up to 10 s)
            a?.Abort();
            var pending = new List<Task>();
            if (a != null)
            {
                pending.Add(a.Completion);
            }

            if (m != null)
            {
                pending.Add(m.AbortAsync());
            }

            Task.WaitAll(pending.ToArray(), TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            Logger.Warning($"ConeError: shutdown failed: {ex.Message}");
        }
    }

    #endregion

    #region Daytime check

    public SessionStatus BeginManual(DateTime? clientUtc = null)
    {
        IConeEnvironment env = environment();
        Preconditions pre = GetPreconditions(clientUtc);
        if (pre.ManualBlockers.Count > 0)
        {
            throw new ConeErrorException("PreconditionsNotMet", string.Join(" ", pre.ManualBlockers));
        }

        ConeErrorSettings settings = Store.LoadSettings();
        MountDriverInfo driver = env.Driver.Inspect();
        var session = new ManualFlipSession(
            env.Mount,
            driver.PierSide,
            env.Clock,
            env.Sun,
            Store,
            new ManualFlipOptions
            {
                Limits = Limits(settings, pre.Profile, driver.Limits, settings.ManualMinAltitudeDegrees),
                LatitudeDegrees = pre.Profile.Latitude,
                StrictPath = settings.StrictPath,
                DovetailMm = settings.DovetailMm,
                MountKey = MountKey(pre.Profile, pre.Mount),
                Profile = pre.Profile,
                SettleSeconds = pre.Profile.SettleSeconds,
                PierLabelsVerified = settings.VerifiedPierLabels.Contains(MountKey(pre.Profile, pre.Mount))
            },
            backgroundLoops);
        string key = MountKey(pre.Profile, pre.Mount);
        session.PierLabelsConfirmed += () => RememberPierLabels(key);

        lock (sync)
        {
            if (IsSessionActive())
            {
                throw new ConeErrorException("Busy", "A cone error session is already running.");
            }

            starting = true;
            abortWhileStarting = false;
        }

        bool aborted;
        try
        {
            // only a session that has begun is kept; a failed start leaves nothing behind
            session.Begin();
            lock (sync)
            {
                manual = session;
                auto = null;
            }
        }
        finally
        {
            lock (sync)
            {
                starting = false;
                aborted = abortWhileStarting;
                abortWhileStarting = false;
            }
        }

        if (aborted)
        {
            session.AbortAsync().Wait(TimeSpan.FromSeconds(30));
            throw new ConeErrorException("Aborted", "Aborted while the daytime check was starting; tracking was restored.");
        }

        return session.GetStatus();
    }

    /// <summary>The one-time counterweight check of the daytime check (on the east pier side).</summary>
    public SessionStatus ManualPierLabels(bool counterweightWest)
    {
        ManualFlipSession m = ActiveManual();
        m.ConfirmPierLabels(counterweightWest);
        return m.GetStatus();
    }

    private ManualFlipSession ActiveManual()
    {
        lock (sync)
        {
            if (manual == null || manual.Finished)
            {
                throw new ConeErrorException("NotActive", "Start the daytime check first.");
            }

            return manual;
        }
    }

    public SessionStatus Capture()
    {
        ManualFlipSession m = ActiveManual();
        m.Capture();
        return m.GetStatus();
    }

    public ManualMovePlan PlanManualMove(PierSideLabel side, bool overrideSunPath = false) => ActiveManual().PlanMove(side, overrideSunPath);

    /// <summary>Starts a confirmed move; plan problems are thrown here, the move itself reports through the status.</summary>
    public SessionStatus StartManualMove(string planId)
    {
        ManualFlipSession m = ActiveManual();
        Task move = m.MoveAsync(planId);
        if (move.IsFaulted)
        {
            Exception inner = move.Exception?.GetBaseException();
            if (inner is ConeErrorException cone)
            {
                throw cone;
            }

            throw new ConeErrorException("MoveFailed", inner?.Message ?? "The move failed.");
        }

        move.ContinueWith(t => Logger.Warning($"ConeError: daytime move ended with: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
        return m.GetStatus();
    }

    public SessionStatus StopManualMove()
    {
        ManualFlipSession m = ActiveManual();
        m.StopMove();
        return m.GetStatus();
    }

    public ManualEstimate RecenterDone(double? axisOffsetMm, double? landmarkDistanceMeters)
    {
        if (axisOffsetMm is > 0)
        {
            ConeErrorSettings settings = Store.LoadSettings();
            settings.AxisOffsetMm = axisOffsetMm;
            Store.SaveSettings(settings);
        }

        return ActiveManual().RecenterDone(axisOffsetMm, landmarkDistanceMeters);
    }

    public async Task<SessionStatus> EndManualAsync()
    {
        ManualFlipSession m = ActiveManual();
        await m.EndAsync();
        return m.GetStatus();
    }

    #endregion
}
