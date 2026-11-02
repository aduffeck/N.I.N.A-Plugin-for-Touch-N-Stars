using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.WPF.Base.Utility.AutoFocus;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TouchNStars.Server.Infrastructure;
using TouchNStars.Server.Models;
using TouchNStars.Utility;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Controller for the Filter Offset Calculator (DarksCustoms plugin) web interface.
/// Replicates the logic of FilterOffsetCalculator.Execute() without requiring WPF dialogs.
/// 
/// Workflow:
///   POST /api/filter-offset/start   → starts background calculation
///   GET  /api/filter-offset/status  → poll for progress, incl. the outcome of every filter × loop of
///                                     the current run (the measurement board)
///   GET  /api/filter-offset/stop    → cancel
///   GET  /api/filter-offset/filters → list profile filters
///   GET  /api/filter-offset/result  → fetch pending old/new offsets (state == PendingResult)
///   POST /api/filter-offset/apply   → write the chosen offsets to the profile
///   GET  /api/filter-offset/discard → restore old values and go back to Idle
/// </summary>
public partial class FilterOffsetController : WebApiController
{
    // ── Shared state ─────────────────────────────────────────────────────────
    // All of this is static and reachable from every HTTP entry point at once (several browser tabs,
    // a stale bundle, a script). Guards the state-machine transitions in start/apply/discard; the
    // background calculation itself runs outside it, so a running calculation never blocks polling.
    private static readonly object _stateLock = new object();

    private static Task _offsetTask;
    private static CancellationTokenSource _cts;

    // "Idle" | "Running" | "PendingResult" | "Error"
    private static string _state = "Idle";
    private static int _currentLoop;
    private static int _totalLoops;
    private static int _currentFilterIndex;
    private static int _totalFilters;
    private static string _currentFilterName = "";
    private static string _errorMessage = "";

    // Every AutoFocus attempt of the current run, one entry per filter per loop, in the order they
    // were measured. Written by the background calculation and read by /status while it is still
    // running, so it gets its own lock rather than _stateLock: the state-machine lock is held across
    // profile writes in /apply, and a status poll — the thing driving the live board — must not wait
    // on those. The state-machine paths take _measurementsLock while holding _stateLock, never the
    // other way round, so the nesting stays one-directional.
    private static readonly object _measurementsLock = new object();
    private static List<FilterOffsetMeasurement> _measurements = new();

    // Saved values for discard
    private static bool _oldUseOffsets;
    private static int? _oldDefaultFilterPosition;
    private static List<(int Position, string Name, int FocusOffset)> _oldOffsets = new();

    // Computed result waiting for user accept/discard
    private static FilterOffsetResult _result;

    // ── GET /api/filter-offset/filters ───────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/filters")]
    public ApiResponse GetFilters()
    {
        try
        {
            var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
            if (profile == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new ApiResponse { Success = false, Error = "Profile not available", StatusCode = 503, Type = "Error" };
            }

            var filters = profile.FilterWheelSettings.FilterWheelFilters
                .Select((f, idx) => new
                {
                    index = idx,
                    position = (int)f.Position,
                    name = f.Name,
                    focusOffset = f.FocusOffset,
                    autoFocusFilter = f.AutoFocusFilter,
                    autoFocusExposureTime = f.AutoFocusExposureTime,
                })
                .ToList();

            return new ApiResponse
            {
                Success = true,
                StatusCode = 200,
                Type = "FilterList",
                Response = new
                {
                    Filters = filters,
                    UseFilterWheelOffsets = profile.FocuserSettings.UseFilterWheelOffsets,
                    DefaultAutofocusExposureTime = profile.FocuserSettings.AutoFocusExposureTime,
                }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    // ── POST /api/filter-offset/start ────────────────────────────────────────
    [Route(HttpVerbs.Post, "/filter-offset/start")]
    public async Task<ApiResponse> StartCalculation()
    {
        if (_offsetTask != null && !_offsetTask.IsCompleted)
        {
            HttpContext.Response.StatusCode = 409;
            return new ApiResponse { Success = false, Error = "Calculation already running", StatusCode = 409, Type = "Error" };
        }

        // The task completes as soon as a result is ready, so IsCompleted alone does not mean the
        // controller is free. Restarting while a result is pending would capture the offsets this
        // run zeroed in the profile as the "old" values, making the user's real offsets
        // unrecoverable — /discard would then restore zeros. Force accept or discard first.
        if (_state == "PendingResult")
        {
            HttpContext.Response.StatusCode = 409;
            return new ApiResponse
            {
                Success = false,
                Error = "A calculated result is still pending — accept or discard it before starting a new calculation",
                StatusCode = 409,
                Type = "Error"
            };
        }

        FilterOffsetStartRequest payload;
        try
        {
            using var reader = new StreamReader(HttpContext.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            payload = JsonSerializer.Deserialize<FilterOffsetStartRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = $"Invalid request body: {ex.Message}", StatusCode = 400, Type = "Error" };
        }

        if (payload?.FilterPositions == null || payload.FilterPositions.Count == 0)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = "No filters specified", StatusCode = 400, Type = "Error" };
        }

        if (payload.Loops < 1)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = "Loops must be >= 1", StatusCode = 400, Type = "Error" };
        }

        var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
        if (profile == null)
        {
            HttpContext.Response.StatusCode = 503;
            return new ApiResponse { Success = false, Error = "Profile not available", StatusCode = 503, Type = "Error" };
        }

        var selectedFilters = profile.FilterWheelSettings.FilterWheelFilters
            .Where(f => payload.FilterPositions.Contains((int)f.Position))
            .OrderBy(f => f.Position)
            .ToList();

        if (selectedFilters.Count == 0)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = "No matching filters found in profile", StatusCode = 400, Type = "Error" };
        }

        CancellationToken token;
        lock (_stateLock)
        {
            // Re-check under the lock: the guards above ran before the request body was read, so two
            // near-simultaneous starts could both have passed them. Go by _state rather than the
            // task handle — _state flips to Running here, while _offsetTask is only assigned after
            // the lock is released and would still look completed to a racing second start.
            if (_state == "Running" || _state == "PendingResult")
            {
                HttpContext.Response.StatusCode = 409;
                return new ApiResponse { Success = false, Error = "Calculation already running", StatusCode = 409, Type = "Error" };
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;

            // Before the state flip, not after: a status poll reads the state first and the board
            // second (see GetStatus), so dropping the previous run's board afterwards is exactly the
            // order that lets a poll pair the new run's "Running" with the old run's measurements.
            ClearMeasurements();

            _state = "Running";
            _currentLoop = 0;
            _totalLoops = payload.Loops;
            _currentFilterIndex = 0;
            _totalFilters = selectedFilters.Count;
            _currentFilterName = "";
            _errorMessage = "";
            _result = null;
        }

        // Deliberately NOT Task.Run(..., token): with a token, a cancel that lands before the
        // threadpool picks the delegate up completes the task as Canceled without ever running it,
        // so none of the recovery below happens and _state stays "Running" forever — which the
        // restart guard then reads as "busy" for the rest of the process's life. The delegate
        // observes cancellation through RunCalculation's own ThrowIfCancellationRequested instead.
        _offsetTask = Task.Run(async () =>
        {
            try
            {
                await RunCalculation(selectedFilters, payload.Loops, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Logger.Info("FilterOffset: calculation cancelled");
                // Dropped before the state flip, like every other transition: a poll reads state then
                // board, so this order can only ever pair a state with a board at least as new. No
                // result to read the board against here anyway — the per-loop positions taken so far
                // are in the NINA log.
                ClearMeasurements();
                lock (_stateLock)
                {
                    RestoreOldValues();
                    _state = "Idle";
                }
            }
            catch (Exception ex)
            {
                // Includes the TaskCanceledException HttpClient raises on its own timeout — that is
                // a failure, not a user cancellation, and must not be reported as one.
                Logger.Error($"FilterOffset: calculation failed: {ex}");
                lock (_stateLock)
                {
                    RestoreOldValues();
                    _state = "Error";
                    _errorMessage = ex.Message;
                }
                // The board is deliberately kept here, unlike on cancel: a run that died on loop 5 of
                // 6 is exactly when the measurements taken so far are worth looking at, and the error
                // screen shows them alongside the reason. The next start clears them.
            }
        });

        return new ApiResponse { Success = true, Response = "Filter offset calculation started", StatusCode = 200, Type = "Success" };
    }

    // ── GET /api/filter-offset/status ────────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/status")]
    public ApiResponse GetStatus()
    {
        // State first, board second, and never the other way round. Every writer does the mirror
        // image — the board is published or dropped before the state that describes it, in all of
        // start, apply, discard, cancel and the flip to PendingResult — so reading in this order can
        // only pair a state with a board at least as new as itself. Reverse either side and a
        // "PendingResult" can arrive next to an unmarked board, or a "Running" next to the previous
        // run's measurements.
        var state = _state;

        // Copy the list so serialisation can't run over it while the background run appends. The
        // entries themselves are shared, so a poll that lands exactly while outlier rejection is
        // marking them can show a half-marked board — self-correcting on the next poll, two seconds
        // later, and the alternative is holding this lock across serialisation.
        List<FilterOffsetMeasurement> measurements;
        lock (_measurementsLock)
            measurements = new List<FilterOffsetMeasurement>(_measurements);

        return new ApiResponse
        {
            Success = true,
            StatusCode = 200,
            Type = "Success",
            Response = new
            {
                State = state,
                CurrentLoop = _currentLoop,
                TotalLoops = _totalLoops,
                CurrentFilterIndex = _currentFilterIndex,
                TotalFilters = _totalFilters,
                CurrentFilterName = _currentFilterName,
                Error = _errorMessage,
                Measurements = measurements,
            }
        };
    }

    // ── GET /api/filter-offset/stop ──────────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/stop")]
    public ApiResponse StopCalculation()
    {
        try
        {
            // Under the lock so this can't land between StartCalculation disposing the old source
            // and installing the new one — cancelling a disposed source throws, and cancelling the
            // brand new one would abort a run that has not begun.
            lock (_stateLock)
            {
                _cts?.Cancel();
            }
            return new ApiResponse { Success = true, Response = "Stop requested", StatusCode = 200, Type = "Success" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    // ── GET /api/filter-offset/result ────────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/result")]
    public ApiResponse GetResult()
    {
        if (_state != "PendingResult" || _result == null)
        {
            HttpContext.Response.StatusCode = 404;
            return new ApiResponse { Success = false, Error = "No result pending", StatusCode = 404, Type = "Error" };
        }

        return new ApiResponse
        {
            Success = true,
            StatusCode = 200,
            Type = "Success",
            Response = _result
        };
    }

    // ── POST /api/filter-offset/apply ────────────────────────────────────────
    [Route(HttpVerbs.Post, "/filter-offset/apply")]
    public async Task<ApiResponse> ApplyResult()
    {
        if (_state != "PendingResult" || _result == null)
        {
            HttpContext.Response.StatusCode = 409;
            return new ApiResponse { Success = false, Error = "No result pending", StatusCode = 409, Type = "Error" };
        }

        FilterOffsetApplyRequest payload;
        try
        {
            using var reader = new StreamReader(HttpContext.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            payload = JsonSerializer.Deserialize<FilterOffsetApplyRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = $"Invalid request body: {ex.Message}", StatusCode = 400, Type = "Error" };
        }

        // Everything below mutates the shared state machine and the profile, so it runs under the
        // same lock as start/discard: two clients hitting Accept, or Accept racing Discard, must not
        // both get past the pending check and write the profile twice. Re-check inside the lock —
        // the check above happened before the request body was read.
        try
        {
            lock (_stateLock)
            {
                if (_state != "PendingResult" || _result == null)
                {
                    HttpContext.Response.StatusCode = 409;
                    return new ApiResponse { Success = false, Error = "No result pending", StatusCode = 409, Type = "Error" };
                }

                if (_result.BaseFilterUnmeasured)
                {
                    // every offset of such a result is 0: applying it would make all filters parfocal
                    HttpContext.Response.StatusCode = 409;
                    return new ApiResponse { Success = false, Error = "The base filter has no usable measurement, so the result can't be applied. Discard it and run the calibration again.", StatusCode = 409, Type = "Error" };
                }

                var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
                if (profile == null)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse { Success = false, Error = "Profile not available", StatusCode = 503, Type = "Error" };
                }

                // FocusOffset is a differential quantity — FilterWheelVM moves the focuser by
                // newFilter.FocusOffset - prevFilter.FocusOffset — so only the spacing between filters
                // has any effect, and the set is free to be zeroed wherever it reads best. Zero it on
                // the filter the user picks as the AutoFocus filter: that is the filter AutoFocus
                // actually runs through, so "0" there and everything else stated relative to it is the
                // reading that matches what the equipment does. It is also how NINA's own offset
                // calculator presents its results.
                //
                // Every filter in the profile is written, not just the calibrated ones. A filter left
                // out of the run keeps its previous distance to the base filter, so its focuser moves
                // are unchanged, but it moves onto the same zero point as the rest instead of being
                // stranded on the old one. That is what lets the reference be chosen freely, and it
                // also repairs a profile poisoned by older builds that stored absolute focuser
                // positions in FocusOffset: only differences of the old values are ever used, never an
                // old value itself.
                var newOffsets = BuildOffsetTable(
                    profile.FilterWheelSettings.FilterWheelFilters,
                    _result.NewOffsets,
                    _result.OldOffsets,
                    payload?.NewDefaultFilterPosition);

                foreach (var (Position, Name, FocusOffset) in newOffsets)
                    Logger.Info($"FilterOffset: filter '{Name}' (position {Position}) offset {FocusOffset}");

                // Write offsets to profile
                profile.FocuserSettings.UseFilterWheelOffsets = true;
                foreach (var (Position, Name, FocusOffset) in newOffsets)
                {
                    var f = profile.FilterWheelSettings.FilterWheelFilters
                        .FirstOrDefault(x => x.Position == Position);
                    if (f != null)
                        f.FocusOffset = FocusOffset;
                }

                // Set new AutoFocus filter
                foreach (var f in profile.FilterWheelSettings.FilterWheelFilters)
                    f.AutoFocusFilter = f.Position == (payload?.NewDefaultFilterPosition ?? -1);

                // Profile.Save() swallows its own IO exceptions and returns normally, so a successful
                // return here does not mean anything reached disk — don't claim it did. The in-memory
                // profile is authoritative either way, and ProfileService's save timer retries.
                profile.Save();

                // The board belongs to the run, and the run is over — drop it rather than keep
                // shipping it with every Idle status poll. Before the state flip, as everywhere else.
                ClearMeasurements();

                _result = null;
                _state = "Idle";

                return new ApiResponse { Success = true, Response = "Offsets applied", StatusCode = 200, Type = "Success" };
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    // ── GET /api/filter-offset/discard ───────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/discard")]
    public ApiResponse DiscardResult()
    {
        if (_state != "PendingResult")
        {
            HttpContext.Response.StatusCode = 409;
            return new ApiResponse { Success = false, Error = "No result pending", StatusCode = 409, Type = "Error" };
        }

        try
        {
            lock (_stateLock)
            {
                // Re-check under the lock: a concurrent /apply may already have consumed the result,
                // and restoring the old values on top of a completed apply would undo it.
                if (_state != "PendingResult")
                {
                    HttpContext.Response.StatusCode = 409;
                    return new ApiResponse { Success = false, Error = "No result pending", StatusCode = 409, Type = "Error" };
                }

                RestoreOldValues();
                ClearMeasurements();
                _result = null;
                _state = "Idle";
                return new ApiResponse { Success = true, Response = "Discarded; old values restored", StatusCode = 200, Type = "Success" };
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    // ── Measurement board ─────────────────────────────────────────────────────

    private static void AddMeasurement(FilterOffsetMeasurement measurement)
    {
        lock (_measurementsLock)
            _measurements.Add(measurement);
    }

    /// <summary>
    /// Flags the samples outlier rejection threw away, so the board can show them struck through
    /// instead of silently dropping them from a column that still has a number in every other row.
    /// <paramref name="acceptedLoops"/> holds the zero-based loop tags that survived; the board's own
    /// loop numbers are one-based.
    /// </summary>
    private static void MarkRejectedMeasurements(int position, IEnumerable<int> acceptedLoops)
    {
        var kept = new HashSet<int>(acceptedLoops);
        lock (_measurementsLock)
        {
            foreach (var m in _measurements)
            {
                if (m.Position != position || m.FocusPosition == null) continue;
                m.Rejected = !kept.Contains(m.Loop - 1);
            }
        }
    }

    private static void ClearMeasurements()
    {
        lock (_measurementsLock)
            _measurements = new List<FilterOffsetMeasurement>();
    }

    // ── Calculation ───────────────────────────────────────────────────────────

    private async Task RunCalculation(List<FilterInfo> selectedFilters, int loops, CancellationToken token)
    {
        var profile = TouchNStars.Mediators.Profile.ActiveProfile;

        // 1. Save old state. The flag also goes to a file: the profile is saved about a second after it is switched
        // off below, so a restart of pins mid-run would otherwise keep it off for good (RecoverInterruptedRun)
        _oldUseOffsets = profile.FocuserSettings.UseFilterWheelOffsets;
        WriteRunSnapshot(profile.Id, _oldUseOffsets);
        _oldDefaultFilterPosition = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(f => f.AutoFocusFilter)?.Position;
        _oldOffsets = selectedFilters
            .Select(f => ((int)f.Position, f.Name, f.FocusOffset))
            .ToList();

        // 2. Setup: stop offsets from perturbing the measurements.
        //
        // Turning UseFilterWheelOffsets off is enough on its own, and it is the only thing done here.
        // Every path that turns a FocusOffset into a focuser move sits behind that flag —
        // FilterWheelVM.ChangeFilter (the only one that moves the focuser at all), HocusFocus's
        // SetAutofocusFilter, and its star-detection optimiser — and the readers that are not behind it
        // (the Alpaca FocusOffsets property, the Oasis store/sync buttons, the filter-list import) never
        // move a focuser.
        //
        // This deliberately no longer zeroes FocusOffset or clears AutoFocusFilter. Both were redundant
        // given the flag, and both were destructive: ProfileService auto-saves about a second after any
        // profile change, so the wiped values were on disk immediately while the only copy that could
        // restore them lived in this class's static fields. A run that never reached Accept or Discard —
        // NINA restarted, the plugin reloaded, an apply that failed — left the user with every offset at
        // 0 and their AutoFocus filter cleared, with nothing left to recover from.
        profile.FocuserSettings.UseFilterWheelOffsets = false;

        // position → AF results for that filter, tagged with the zero-based loop they came from. A loop
        // can be missing when its AutoFocus failed, so the loop number has to travel with the sample
        // rather than being inferred from its index later.
        var calculatedPositions = new Dictionary<int, List<(int Loop, int Position)>>();
        foreach (var f in selectedFilters)
            calculatedPositions[(int)f.Position] = new List<(int Loop, int Position)>();

        var apiUrl = await CoreUtility.GetApiUrl();
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        // 3. Run loops × filters. The status field tracks the counter rather than being the counter,
        // so it never reports loops + 1 after the last iteration increments past the bound.
        for (int loop = 1; loop <= loops; loop++)
        {
            _currentLoop = loop;
            _currentFilterIndex = 0;

            foreach (var filter in selectedFilters)
            {
                token.ThrowIfCancellationRequested();

                _currentFilterIndex++;
                _currentFilterName = filter.Name;

                Logger.Info($"FilterOffset: loop {_currentLoop}/{loops} — switching to filter '{filter.Name}' (position {filter.Position})");

                // Re-assert the flag before each filter's AF attempts. Nothing else in NINA or its
                // plugins writes UseFilterWheelOffsets, so this should never be doing any work — but a
                // filter-list re-import can replace the profile's FilterInfo objects mid-run, and
                // re-asserting costs nothing next to an AutoFocus run.
                //
                // It used to clear AutoFocusFilter on every filter here as well, to stop HocusFocus's
                // SetAutofocusFilter from switching the wheel away from the target. That was aimed at
                // the wrong mechanism: SetAutofocusFilter returns immediately while this flag is false,
                // and the wheel actually moved because of the previous run's teardown restoring its
                // imaging filter — which is what WaitForHocusFocusCleanupAsync and the wrong-filter
                // check in MeasureFilterFocusPositionAsync handle. Clearing it only served to destroy
                // the user's AutoFocus filter selection on any run that never reached Accept.
                profile.FocuserSettings.UseFilterWheelOffsets = false;

                int? position;
                string failure;
                int attempts;
                string firstFailure;
                try
                {
                    (position, failure, attempts, firstFailure) =
                        await MeasureFilterFocusPositionAsync(filter, apiUrl, client, token);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
                {
                    // A measurement can also end by throwing — AutoFocus never started, it never
                    // finished, the focuser position could not be read. That aborts the whole run, and
                    // the cell that killed it has to say so: without this it stays blank and reads as
                    // "not measured yet" on the error screen, next to loops that genuinely were not
                    // reached. Recorded, then rethrown so the run still fails.
                    AddMeasurement(new FilterOffsetMeasurement
                    {
                        Position = (int)filter.Position,
                        Name = filter.Name,
                        Loop = loop,
                        FocusPosition = null,
                        Failure = ex.Message,
                    });
                    throw;
                }

                // Recorded either way, and before the continue below: the board's whole point is that a
                // loop which produced nothing is visible as such, with the reason, instead of leaving a
                // gap the user has to go to the NINA log to explain.
                AddMeasurement(new FilterOffsetMeasurement
                {
                    Position = (int)filter.Position,
                    Name = filter.Name,
                    Loop = loop,
                    FocusPosition = position,
                    Failure = failure,
                    Attempts = attempts,
                    FirstFailure = firstFailure,
                });

                if (position == null)
                {
                    // Already logged with the reason by the measurement itself. Leaving the sample out
                    // entirely is the point: recording a failed run's leftover focuser position is what
                    // put a 200-step error into a filter's offset before this was detected at all.
                    Logger.Info($"FilterOffset: no measurement for filter '{filter.Name}' in loop {_currentLoop}/{loops}");
                    continue;
                }

                Logger.Info($"FilterOffset: filter '{filter.Name}' settled at position {position.Value}");

                calculatedPositions[(int)filter.Position].Add((loop - 1, position.Value));
            }
        }

        // The measuring is done: offsets apply on filter changes again from here, whether or not anyone ever
        // accepts or discards the result (an apply sets the flag anyway)
        RestoreUseOffsets();

        // 4. Compute new offsets using the same algorithm as FilterOffsetCalculator.Execute()
        var newOffsets = ComputeOffsets(selectedFilters, calculatedPositions, profile,
            out var details, out var temperatureDrift, out var baseFilterUnmeasured);

        // A filter without a usable measurement keeps its previous distance to the base filter (the first selected
        // one): stated as 0 it would be written as parfocal with the base, e.g. Ha after it failed in the clouds
        var unmeasured = details.Where(d => d.EstimatedFocus == null).Select(d => d.Position).ToHashSet();
        int basePreviousOffset = _oldOffsets.Count > 0 ? _oldOffsets[0].FocusOffset : 0;

        // 5. Store result and move to PendingResult state. This is the only transition INTO the state
        // the HTTP handlers wait on, so it publishes under the same lock they read under — otherwise
        // the handlers hold a lock that the writer ignores, and the freshly built result has no
        // release barrier pairing with their acquire (which matters on the ARM targets).
        //
        // Keeping the existing AutoFocus filter is always a valid choice, including when it was not
        // one of the calibrated filters, so it is suggested unchanged — the apply step then anchors
        // on the base filter instead.
        lock (_stateLock)
        {
            _result = new FilterOffsetResult
            {
                OldOffsets = _oldOffsets
                    .Select(o => new FilterOffsetEntry { Position = o.Position, Name = o.Name, FocusOffset = o.FocusOffset })
                    .ToList(),
                NewOffsets = newOffsets
                    .Select(o => unmeasured.Contains(o.Position) && !baseFilterUnmeasured
                        ? new FilterOffsetEntry
                        {
                            Position = o.Position,
                            Name = o.Name,
                            FocusOffset = (_oldOffsets.FirstOrDefault(x => x.Position == o.Position).FocusOffset) - basePreviousOffset,
                            Measured = false,
                        }
                        : new FilterOffsetEntry { Position = o.Position, Name = o.Name, FocusOffset = o.FocusOffset, Measured = !unmeasured.Contains(o.Position) })
                    .ToList(),
                OldDefaultFilterPosition = _oldDefaultFilterPosition.HasValue ? (int?)_oldDefaultFilterPosition.Value : null,
                SuggestedDefaultFilterPosition = _oldDefaultFilterPosition.HasValue ? (int?)_oldDefaultFilterPosition.Value : null,
                Details = details,
                TemperatureDrift = temperatureDrift,
                BaseFilterUnmeasured = baseFilterUnmeasured,
            };

            _state = "PendingResult";
        }
    }

    // Runs AF for one filter and returns its settled focuser position, guaranteeing the wheel was
    // actually on the requested filter for the whole run — not just at the moment ChangeFilter was
    // first called.
    //
    // The race this guards against: DataContainer.afRun (mirroring HocusFocus's "AF completed" broadcast)
    // clears BEFORE HocusFocus's own post-run cleanup restores the filter that was active when THAT run
    // started. Reacting to that early signal and switching to the next filter lets the still-in-flight
    // cleanup silently move the wheel back afterward — right as the next AF trigger fires. That gets
    // rejected ("Another AutoFocus is already in progress"), and once the retry succeeds, the wheel is
    // sitting on the reverted (wrong) filter.
    //
    // The primary fix is WaitForHocusFocusCleanupAsync below: it doesn't let this method return — and so
    // doesn't let the caller switch to the next filter — until HocusFocus's OWN in-progress guard
    // (HocusFocusVM.AutoFocusInProgress) has cleared, which only happens after that filter-restore cleanup
    // has actually finished. Re-asserting ChangeFilter before every trigger attempt (including retries),
    // and verifying the actually-mounted filter right after completion, are kept as defense in depth for
    // anything else (a manual AF from another client, etc.) that might still move the wheel mid-run.
    /// <summary>
    /// Runs AF for one filter and returns the focus position it found, or a null position with the
    /// reason when no trustworthy measurement could be taken after <c>maxAttempts</c> tries.
    ///
    /// A null position is a dropped sample, not a failed calibration: one flaky filter must not throw
    /// away a run that can take the better part of an hour. ComputeOffsets copes with a filter that has
    /// fewer samples than the others, and with one that has none at all. The reason travels back with
    /// it so the board can say why a cell is empty; it is the last attempt's failure.
    ///
    /// The attempt count and the first attempt's failure travel back too, so a measurement the retry
    /// rescued can be shown as what it was rather than as a clean first-try result.
    /// </summary>
    private static async Task<(int? Position, string Failure, int Attempts, string FirstFailure)> MeasureFilterFocusPositionAsync(FilterInfo filter, string apiUrl, HttpClient client, CancellationToken token)
    {
        // One retry, not more: every attempt is a full AutoFocus run — minutes of exposures — and a
        // filter that fails twice in a row is not usually going to succeed on a third try. Dropping the
        // sample and moving on costs less than keeping the user waiting.
        const int maxAttempts = 2;

        // Kept so a cell the retry rescued does not look like a first-try success. Without it, the run
        // that took twice as long is indistinguishable on the board from the one that went smoothly,
        // which is exactly the question the board is there to answer.
        string firstFailure = null;

        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();

            // Snapshot before the run starts: HocusFocus only advances LastReport from its Completed
            // handler, which does not fire for a failed run, so comparing the two is what tells a real
            // measurement apart from a failure. Taken before StartAutofocusWithRetryAsync so a run that
            // finishes unusually fast cannot slip in between.
            bool reportReadable = TryGetHocusFocusLastReport(out var reportBeforeRun);

            await StartAutofocusWithRetryAsync(apiUrl, client, filter, token);

            // Wait until the AF file watcher (BackgroundWorker) signals completion
            await WaitForAutofocusAsync(token);

            string failure = null;
            int? position = null;

            if (DataContainer.afError)
            {
                failure = string.IsNullOrWhiteSpace(DataContainer.afErrorText)
                    ? "AutoFocus reported an error"
                    : DataContainer.afErrorText;
            }

            // The just-completed run's own filter-restore cleanup hasn't fired yet at this point (it only
            // runs after the completion signal we just waited on), so the wheel still reflects whatever
            // filter the run actually measured through.
            var actualFilter = TouchNStars.Mediators.FilterWheel.GetInfo()?.SelectedFilter;
            if (failure == null && actualFilter != null && actualFilter.Position != filter.Position)
                failure = $"AutoFocus measured through filter '{actualFilter.Name}' instead (race with the previous run's cleanup)";

            if (failure == null)
            {
                if (reportReadable)
                {
                    // The report is the authoritative answer, and reading it removes a race that silently
                    // corrupted results: the AF report file appears — which is what WaitForAutofocusAsync
                    // waits on — a fraction of a second BEFORE PerformPostAutoFocusActions restores the
                    // focuser, so asking the focuser where it is at this moment can return wherever a
                    // failed blind search happened to stop. CalculatedFocusPoint is also the fitted
                    // minimum rather than the position the focuser was quantised to, so it carries the
                    // precision AF actually achieved.
                    TryGetHocusFocusLastReport(out var reportAfterRun);
                    double focus = reportAfterRun?.CalculatedFocusPoint?.Position ?? double.NaN;

                    if (reportAfterRun == null || ReferenceEquals(reportAfterRun, reportBeforeRun))
                        failure = "AutoFocus produced no new report — the run did not complete successfully";
                    else if (double.IsNaN(focus) || double.IsInfinity(focus))
                        // A fit that produced no finite minimum. Casting that to int yields 0 or
                        // int.MinValue with no complaint, and a bogus 0 here would drag every other
                        // filter's offset with it — the whole point of this method is to not do that.
                        failure = $"AutoFocus reported a non-finite focus position ({focus})";
                    else
                        position = (int)Math.Round(focus, MidpointRounding.AwayFromZero);
                }
                else
                {
                    // No HocusFocus (or its shape changed): fall back to asking the focuser directly, as
                    // before. NINA's built-in AutoFocus does not restore the position on failure, and its
                    // failures are caught by the DataContainer.afError check above.
                    position = await GetFocuserPositionAsync(apiUrl, client, token);
                }
            }

            // Don't return — and so don't let the caller switch to the next filter — until HocusFocus has
            // actually finished tearing this run down (filter restored, guard released). This is what
            // prevents the filter race from happening in the first place, rather than just detecting it.
            await WaitForHocusFocusCleanupAsync(token);

            if (failure == null) return (position, null, attempt, firstFailure);

            if (attempt >= maxAttempts)
            {
                Logger.Warning($"FilterOffset: AutoFocus for filter '{filter.Name}' failed on all {attempt} attempt(s) " +
                               $"({failure}) — recording no measurement for this loop and continuing with the " +
                               "remaining filters.");
                return (null, failure, attempt, firstFailure);
            }

            firstFailure ??= failure;

            Logger.Warning($"FilterOffset: AutoFocus for filter '{filter.Name}' did not produce a usable measurement " +
                           $"({failure}) — re-measuring (attempt {attempt}/{maxAttempts})");
            await TouchNStars.Mediators.FilterWheel.ChangeFilter(filter, token);
        }
    }

    // Reads HocusFocus's last AutoFocus report (HocusFocusVM.Current.LastReport). It is assigned only in
    // AutoFocusEngine_Completed, which fires on success only — a run that ends in "Too many failed points"
    // leaves the previous report in place, which is what makes this usable as a success signal.
    //
    // Returns false when HocusFocus isn't loaded or the property can't be read, so callers can fall back
    // rather than treat an unavailable report as a failed AutoFocus. A true with a null report is normal:
    // no AF has completed since NINA started.
    private static bool TryGetHocusFocusLastReport(out AutoFocusReport report)
    {
        report = null;
        try
        {
            var hocusFocusVMType = Type.GetType("NINA.Joko.Plugins.HocusFocus.AutoFocus.HocusFocusVM, NINA.Joko.Plugins.HocusFocus");
            var currentVM = hocusFocusVMType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var property = currentVM?.GetType().GetProperty("LastReport");
            if (property == null) return false;

            report = property.GetValue(currentVM) as AutoFocusReport;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"FilterOffset: could not read HocusFocus LastReport via reflection: {ex.Message}");
            return false;
        }
    }

    // Reflects into HocusFocus's own in-progress guard (HocusFocusVM.Current.AutoFocusInProgress), which —
    // unlike DataContainer.afRun — only clears in the finally block of HocusFocusVM.StartAutoFocus, i.e.
    // after "await autoFocusEngine.Run(...)" has fully returned, including AutoFocusEngine.RunImpl's own
    // outer finally (PerformPostAutoFocusActions' filter restore, then ReleaseAutoFocusInProgress). That
    // makes it the one externally-observable signal that means "safe to switch to the next filter now".
    // Returns null (and doesn't block) if HocusFocus isn't loaded or the property can't be read, so this
    // degrades gracefully rather than hanging the calibration on an unrelated AF backend.
    private static bool? IsHocusFocusAutoFocusInProgress()
    {
        try
        {
            var hocusFocusVMType = Type.GetType("NINA.Joko.Plugins.HocusFocus.AutoFocus.HocusFocusVM, NINA.Joko.Plugins.HocusFocus");
            var currentVM = hocusFocusVMType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return currentVM?.GetType().GetProperty("AutoFocusInProgress")?.GetValue(currentVM) as bool?;
        }
        catch (Exception ex)
        {
            Logger.Warning($"FilterOffset: could not read HocusFocus AutoFocusInProgress via reflection: {ex.Message}");
            return null;
        }
    }

    private static async Task WaitForHocusFocusCleanupAsync(CancellationToken token)
    {
        // PerformPostAutoFocusActions' individual steps (filter restore, temp-comp restore, guiding
        // restart) each allow up to 1 minute, so give this generous headroom before giving up.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            if (IsHocusFocusAutoFocusInProgress() != true) return; // false (torn down) or null (unavailable) — either way, don't block

            await Task.Delay(250, token);
        }

        Logger.Warning("FilterOffset: HocusFocus AutoFocus cleanup did not clear within 90s — proceeding anyway");
    }

    private static async Task StartAutofocusWithRetryAsync(string apiUrl, HttpClient client, FilterInfo filter, CancellationToken token)
    {
        // The previous run's post-AF cleanup steps each time out after 1 minute (filter restore,
        // temp-comp restore, guiding restart), so allow up to 3 minutes of re-triggering before
        // giving up with a clear error instead of hanging.
        var deadline = DateTime.UtcNow.AddMinutes(3);

        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();

            // Re-assert the target filter right before every trigger attempt, including retries — a
            // previous run's delayed cleanup can revert the wheel between attempts (see
            // MeasureFilterFocusPositionAsync), and this is the last write before AF captures "current
            // filter" as the run's imaging filter.
            await TouchNStars.Mediators.FilterWheel.ChangeFilter(filter, token);

            // Reset AF tracking state and start AF (ninaAPI call is async: returns "started" immediately)
            lock (DataContainer.lockObj)
            {
                DataContainer.afRun = true;
                DataContainer.afError = false;
                DataContainer.afErrorText = string.Empty;
                DataContainer.newAfGraph = false;
                DataContainer.afStartConfirmed = false;
            }

            await client.GetAsync($"{apiUrl}/equipment/focuser/auto-focus", token);

            if (await WaitForAutofocusStartAsync(token)) return;

            if (DateTime.UtcNow >= deadline)
                throw new Exception($"AutoFocus did not start for filter '{filter.Name}' after {attempt} attempts — a previous AutoFocus run may be stuck (see NINA log)");

            Logger.Warning($"FilterOffset: AutoFocus for filter '{filter.Name}' did not start (attempt {attempt}, previous run likely still finishing) — retrying");
            await Task.Delay(5000, token);
        }
    }

    private static async Task<bool> WaitForAutofocusStartAsync(CancellationToken token)
    {
        // AutoFocusRunStarting is broadcast right after the AF run claims its in-progress guard,
        // well before any exposures, so a healthy start confirms within a few seconds.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            lock (DataContainer.lockObj)
            {
                if (DataContainer.afStartConfirmed) return true;
            }

            await Task.Delay(500, token);
        }
        return false;
    }

    private static async Task WaitForAutofocusAsync(CancellationToken token)
    {
        // Give NINA a moment to write its AF file / log before we start polling
        await Task.Delay(2000, token);

        var deadline = DateTime.UtcNow.AddMinutes(20);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            bool stillRunning;
            lock (DataContainer.lockObj)
                stillRunning = DataContainer.afRun;

            if (!stillRunning) return;

            await Task.Delay(1000, token);
        }

        throw new TimeoutException("AutoFocus did not complete within 20 minutes");
    }

    // Never returns a sentinel on failure. A position of 0 is indistinguishable from a real reading,
    // and every offset in the result is a difference against these numbers — one unreadable position
    // silently turns the whole calibration into garbage (a failed read for the base filter alone
    // would push every other filter's offset up to a near-absolute focuser position). Failing the
    // run instead costs the user a repeat, which is the cheaper of the two outcomes by far.
    private static async Task<int> GetFocuserPositionAsync(string apiUrl, HttpClient client, CancellationToken token)
    {
        const int maxAttempts = 3;
        string lastError = "unknown error";

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                var resp = await client.GetAsync($"{apiUrl}/equipment/focuser/info", token);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(token);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("Response", out var response) &&
                        response.TryGetProperty("Position", out var pos))
                        return pos.GetInt32();

                    lastError = "response contained no focuser position";
                }
                else
                {
                    lastError = $"HTTP {(int)resp.StatusCode}";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            Logger.Warning($"FilterOffset: could not read focuser position ({lastError}) — attempt {attempt}/{maxAttempts}");
            if (attempt < maxAttempts) await Task.Delay(1000, token);
        }

        throw new Exception($"Could not read the focuser position after {maxAttempts} attempts: {lastError}");
    }

    // Smallest deviation-from-median (in focuser steps) that may ever be treated as an outlier.
    // Scaled from the AutoFocus step size so it tracks the setup rather than assuming one focuser:
    // a run that lands within half an AF sampling step of the others is normal AF scatter, not a
    // bad measurement. The absolute floor only guards against a nonsensically small step size.
    private static double OutlierToleranceSteps(NINA.Profile.Interfaces.IProfile profile)
        => Math.Max(5.0, profile.FocuserSettings.AutoFocusStepSize / 2.0);

    private static double Median(IReadOnlyList<double> sorted)
        => sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

    /// <summary>
    /// Robustly aggregates one filter's per-loop AF positions into a single focus position.
    ///
    /// A plain mean lets a single bad loop drag the result by a fraction of its error, which is the
    /// opposite of what running multiple loops is for. Instead: take the median, drop samples that
    /// are both statistically extreme (&gt; 3 scaled MADs) and beyond the tolerance floor, then mean
    /// what survives — the median resists the outlier, the mean of the survivors keeps the precision.
    ///
    /// Note this can only reject numerically outlying samples. A loop that measured through the
    /// wrong filter still passes if that filter happens to be near-parfocal with the intended one.
    /// It also needs at least 3 loops to reject anything at all; with 2 it keeps both samples and
    /// reports the disagreement instead, since neither can be blamed for it.
    ///
    /// Samples arrive already tagged with the zero-based loop they came from, and survivors keep that
    /// tag. The drift estimate needs the true loop number, not a position in the list: a loop can be
    /// missing before this is ever called (an AutoFocus that failed twice records no sample) as well as
    /// dropped here, and either way the survivors' spacing has to reflect the loops that sat between
    /// them.
    ///
    /// <paramref name="cutoffUsed"/> returns the distance from the median a sample had to exceed to be
    /// discarded — the MAD-derived value, which is usually well above <paramref name="tolerance"/>.
    /// Null when there were too few samples for rejection to be possible at all.
    ///
    /// Rejections and low-confidence warnings are reported through <paramref name="reportRejections"/>
    /// rather than straight to Logger: touching NINA's Logger initialises it, which creates a log file
    /// and prunes the real user log directory — not something a unit test of this arithmetic should do.
    /// </summary>
    internal static List<(int Loop, int Position)> RejectOutliers(
        string filterName,
        IReadOnlyList<(int Loop, int Position)> samples,
        double tolerance,
        out double? cutoffUsed,
        Action<string> reportRejections = null)
    {
        var all = samples.ToList();
        cutoffUsed = null;

        if (samples.Count < 3)
        {
            // Under three samples there is no median to defend: with two, the "median" is their mean,
            // so one bad AutoFocus run pulls the result halfway towards itself at full weight, and
            // nothing here can tell which of the two to blame. Rejection is inert rather than wrong,
            // but the offset it produces is only as good as the worse sample — say so when the samples
            // visibly disagree, because the result looks just as confident either way.
            if (samples.Count == 2)
            {
                int spread = Math.Abs(samples[0].Position - samples[1].Position);
                if (spread > tolerance)
                {
                    reportRejections?.Invoke(
                        $"FilterOffset: the 2 measurements for filter '{filterName}' disagree by {spread} steps " +
                        $"({samples[0].Position} vs {samples[1].Position}), more than the {tolerance:0.#} step " +
                        "tolerance, and outlier rejection needs at least 3 loops to discard either one. The offset " +
                        "for this filter is the midpoint of both and may be off by roughly half that spread — " +
                        "re-run with 3 or more loops for a result that can reject a bad AutoFocus.");
                }
            }

            return all;
        }

        var sorted = samples.Select(s => (double)s.Position).OrderBy(p => p).ToList();
        double median = Median(sorted);

        var deviations = samples.Select(s => Math.Abs(s.Position - median)).OrderBy(d => d).ToList();
        // 1.4826 rescales the MAD to be comparable to a standard deviation for normal data. The MAD
        // term routinely dominates the tolerance floor, so this — not the floor — is the distance a
        // sample actually had to exceed to be discarded, and it is what the caller reports.
        double cutoff = Math.Max(tolerance, 3.0 * 1.4826 * Median(deviations));
        cutoffUsed = cutoff;

        var accepted = new List<(int Loop, int Position)>();
        var rejected = new List<string>();
        foreach (var (loop, position) in samples)
        {
            if (Math.Abs(position - median) > cutoff)
                rejected.Add($"loop {loop + 1}: {position} ({position - median:+0.#;-0.#;0} from median)");
            else
                accepted.Add((loop, position));
        }

        if (rejected.Count > 0)
        {
            reportRejections?.Invoke(
                $"FilterOffset: discarded {rejected.Count} outlying measurement(s) for filter '{filterName}' " +
                $"(median {median:0.#}, cutoff ±{cutoff:0.#} steps): {string.Join("; ", rejected)}. " +
                "A loop this far off usually means the AutoFocus ran through a different filter than intended " +
                "— check the filter wheel for slot slippage.");
        }

        // Defensive only: the median is at or between the samples, so at least one always survives
        // the cutoff. Kept so a future change to the cutoff can't start returning an empty set and
        // silently produce an offset out of no measurements at all.
        return accepted.Count > 0 ? accepted : all;
    }

    /// <summary>
    /// Mean per-loop focus drift of one filter's surviving samples, positive when the focus position
    /// moves down over the run.
    ///
    /// The divisor is the loop span, not the sample count: the samples are one per loop, but outlier
    /// rejection can punch a hole in the middle of the series, and a survivor pair that straddles a
    /// discarded loop is still separated by every loop that sat between them.
    /// </summary>
    internal static double EstimatePerLoopDrift(IReadOnlyList<(int Loop, int Position)> samples)
    {
        if (samples.Count < 2) return 0.0;

        int loopSpan = samples[^1].Loop - samples[0].Loop;
        return loopSpan > 0
            ? (samples[0].Position - samples[^1].Position) / (double)loopSpan
            : 0.0;
    }

    /// <summary>
    /// One filter's surviving samples, averaged and drift-corrected back to the start of the run so
    /// that filters measured at different times are directly comparable.
    ///
    /// Two corrections, both in units of loops:
    ///  * <paramref name="withinLoopRatio"/> is where in a loop this filter is measured — the later
    ///    its turn, the more the focus has already moved since that loop began.
    ///  * the mean loop index is which loops the samples actually came from. Outlier rejection runs
    ///    per filter, so one filter can lose an early loop that its neighbours kept, which drags its
    ///    mean later in time by an amount no within-loop term can see. With complete sample sets
    ///    this term is the same constant for every filter and cancels out of the offsets entirely,
    ///    which is why it was invisible before rejection existed.
    /// </summary>
    internal static double EstimateFocusAtRunStart(
        IReadOnlyList<(int Loop, int Position)> samples,
        double driftPerLoop,
        double withinLoopRatio)
        => samples.Average(p => p.Position)
           + ((samples.Average(p => p.Loop) + withinLoopRatio) * driftPerLoop);

    /// <summary>
    /// Aggregates the per-loop AF results into focus offsets.
    ///
    /// Based on the offset math from FilterOffsetCalculator.Execute(), with two deliberate changes:
    ///  * per-filter aggregation is a median with outlier rejection rather than a plain mean, so one
    ///    bad loop out of several no longer corrupts every filter's result;
    ///  * the values returned are offsets relative to the base filter — the lowest-position filter in
    ///    the selection, which is also the one measured first in every loop — not absolute focuser
    ///    positions. FocusOffset is a relative quantity everywhere else in NINA: it is displayed as
    ///    one, compared against the existing offsets in OldOffsets, and pushed to hardware verbatim
    ///    by some wheels (e.g. OasisFilterWheel.StoreFocusOffsets). The apply step can still
    ///    re-reference these onto whichever filter the user picks as the new AutoFocus filter.
    ///
    /// <paramref name="details"/> receives the per-filter working: how many loops produced a usable
    /// measurement (failed AutoFocus runs are not counted — they never reach
    /// <paramref name="calculatedPositions"/>), how many of those survived rejection, the median they
    /// were rejected against, the spread of the survivors, and the drift-corrected position the offset
    /// was derived from. It is what turns the board from a grid of numbers into something that
    /// explains the offset above it.
    /// </summary>
    private static List<(int Position, string Name, int FocusOffset)> ComputeOffsets(
        List<FilterInfo> selectedFilters,
        Dictionary<int, List<(int Loop, int Position)>> calculatedPositions,
        NINA.Profile.Interfaces.IProfile profile,
        out List<FilterOffsetDetail> details,
        out double temperatureDrift,
        out bool baseFilterUnmeasured)
    {
        baseFilterUnmeasured = false;

        double tolerance = OutlierToleranceSteps(profile);

        // Outlier-filtered samples per filter position, each tagged with the loop it came from.
        var accepted = new Dictionary<int, List<(int Loop, int Position)>>();
        var cutoffs = new Dictionary<int, double?>();
        foreach (var f in selectedFilters)
        {
            int pos = (int)f.Position;
            accepted[pos] = RejectOutliers(f.Name, calculatedPositions[pos], tolerance, out var cutoff, msg => Logger.Warning(msg));
            cutoffs[pos] = cutoff;
            MarkRejectedMeasurements(pos, accepted[pos].Select(s => s.Loop));
        }

        // Temperature drift is estimated from the first (base) filter, across surviving samples only
        // so a rejected loop can't masquerade as thermal movement.
        temperatureDrift = selectedFilters.Count > 0
            ? EstimatePerLoopDrift(accepted[(int)selectedFilters[0].Position])
            : 0.0;

        double defaultAfTime = profile.FocuserSettings.AutoFocusExposureTime;
        double defaultFilterAfTime = new FilterInfo().AutoFocusExposureTime;

        // Total exposure time across selected filters (used for ratio weighting). Kept as a double:
        // truncating it skews every within-loop ratio, and sub-second exposures would truncate the
        // whole sum to 0 and silently fall back to the guard below.
        double totalTime = selectedFilters.Sum(f =>
        {
            var pf = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(x => x.Position == f.Position);
            return pf?.AutoFocusExposureTime == defaultFilterAfTime ? defaultAfTime : (pf?.AutoFocusExposureTime ?? defaultAfTime);
        });
        if (totalTime <= 0.0) totalTime = 1.0;

        // Drift-corrected absolute focus position per filter, in selection order.
        var absolutePositions = new List<(int Position, string Name, double? Focus)>();
        double totalRatio = 0.0;

        details = new List<FilterOffsetDetail>();
        var detailByPosition = new Dictionary<int, FilterOffsetDetail>();

        foreach (var filter in selectedFilters)
        {
            var pf = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(x => x.Position == filter.Position);
            double filterTime = pf?.AutoFocusExposureTime == defaultFilterAfTime
                ? defaultAfTime
                : (pf?.AutoFocusExposureTime ?? defaultAfTime);

            var positions = accepted[(int)filter.Position];
            double? focus = positions.Count > 0
                ? EstimateFocusAtRunStart(positions, temperatureDrift, totalRatio)
                : null;

            var taken = calculatedPositions[(int)filter.Position];
            var detail = new FilterOffsetDetail
            {
                Position = (int)filter.Position,
                Name = filter.Name,
                SamplesTaken = taken.Count,
                SamplesUsed = positions.Count,
                // Over all samples, including the rejected ones — it is the value they were rejected
                // against, so the board can state a discarded cell's deviation from it.
                Median = taken.Count > 0
                    ? (double?)Median(taken.Select(s => (double)s.Position).OrderBy(p => p).ToList())
                    : null,
                // Of the surviving samples only, unlike the median: this is the disagreement the
                // offset actually carries, and a rejected sample is by definition not in it.
                Spread = positions.Count > 1
                    ? (int?)(positions.Max(s => s.Position) - positions.Min(s => s.Position))
                    : null,
                // The distance a sample had to exceed to be discarded. Usually set by this filter's own
                // scatter, falling back to the profile-wide floor when that scatter is tiny. Null when
                // there were too few samples to reject any.
                Cutoff = cutoffs[(int)filter.Position],
                EstimatedFocus = focus,
            };
            details.Add(detail);
            detailByPosition[(int)filter.Position] = detail;

            absolutePositions.Add(((int)filter.Position, filter.Name, focus));
            totalRatio += filterTime / totalTime;
        }

        // Convert to offsets relative to the base filter.
        double? baseFocus = absolutePositions.Count > 0 ? absolutePositions[0].Focus : null;
        if (baseFocus == null)
        {
            // Every detail's FocusOffset is still 0, which is exactly what is being reported here.
            // Flagged so the result says so on screen: a table of zeros next to filters that plainly
            // did measure something otherwise reads as a real, if surprising, calibration.
            baseFilterUnmeasured = true;
            Logger.Warning("FilterOffset: base filter has no usable measurement — reporting all offsets as 0.");
            return absolutePositions.Select(p => (p.Position, p.Name, 0)).ToList();
        }

        Logger.Info($"FilterOffset: computing offsets relative to base filter '{absolutePositions[0].Name}' " +
                    $"at position {baseFocus.Value:0.#} (temperature drift {temperatureDrift:+0.##;-0.##;0} steps/loop)");

        var result = new List<(int Position, string Name, int FocusOffset)>();
        foreach (var (Position, Name, Focus) in absolutePositions)
        {
            int focusOffset = Focus.HasValue
                ? (int)Math.Round(Focus.Value - baseFocus.Value, MidpointRounding.AwayFromZero)
                : 0;

            if (!Focus.HasValue)
                Logger.Warning($"FilterOffset: no usable measurement for filter '{Name}' — offset set to 0.");

            if (detailByPosition.TryGetValue(Position, out var detail))
                detail.FocusOffset = focusOffset;

            result.Add((Position, Name, focusOffset));
        }

        return result;
    }

    /// <summary>
    /// Builds the offsets to write for every filter in the profile, stated relative to the filter
    /// selected as the AutoFocus filter — that filter comes out at 0.
    ///
    /// Only spacing matters (FilterWheelVM moves by the difference between two filters' offsets), so
    /// zeroing the set is free: it changes every number on screen and no focuser move at all. Zeroing
    /// on the AutoFocus filter is the reading that matches the equipment, since that is the filter
    /// AutoFocus runs through and therefore the one the focuser is actually parked on when the offsets
    /// are applied.
    ///
    /// Filters that were not calibrated this run are carried across by their previous distance to the
    /// base filter, which is still valid and is the only thing the run learned nothing about. Note this
    /// only ever reads *differences* of previous offsets, never a previous offset on its own, so a
    /// profile left holding absolute focuser positions by an older build comes out clean.
    ///
    /// <paramref name="measuredOffsets"/> is ComputeOffsets' output, stated relative to its first
    /// entry — the base filter of the run.
    /// </summary>
    internal static List<(int Position, string Name, int FocusOffset)> BuildOffsetTable(
        IEnumerable<FilterInfo> profileFilters,
        IReadOnlyList<FilterOffsetEntry> measuredOffsets,
        IReadOnlyList<FilterOffsetEntry> previousOffsets,
        int? autoFocusFilterPosition)
    {
        var table = new List<(int Position, string Name, int FocusOffset)>();
        if (profileFilters == null || measuredOffsets == null || measuredOffsets.Count == 0) return table;

        // a filter of the run without a usable measurement is carried across like one that was not calibrated
        var measured = new Dictionary<int, int>();
        foreach (var o in measuredOffsets.Where(o => o.Measured)) measured[o.Position] = o.FocusOffset;

        int basePosition = measuredOffsets[0].Position;
        int baseMeasured = measuredOffsets[0].FocusOffset;
        int basePrevious = previousOffsets?.FirstOrDefault(o => o.Position == basePosition)?.FocusOffset ?? 0;

        foreach (var f in profileFilters)
        {
            int position = (int)f.Position;
            if (measured.TryGetValue(position, out int m))
            {
                table.Add((position, f.Name, m));
            }
            else
            {
                // A run only zeroes the FocusOffset of the filters it calibrates, so an uncalibrated
                // filter still holds the pre-run value that basePrevious was snapshotted alongside —
                // the two are comparable, and their difference is this filter's distance to the base.
                table.Add((position, f.Name, baseMeasured + (f.FocusOffset - basePrevious)));
            }
        }

        if (table.Count == 0) return table;

        // "None" (and a selection the profile no longer has) falls back to the base filter, which keeps
        // the result identical to what ComputeOffsets measured.
        int referenceIndex = autoFocusFilterPosition is int afPosition
            ? table.FindIndex(t => t.Position == afPosition)
            : -1;
        if (referenceIndex < 0) referenceIndex = table.FindIndex(t => t.Position == basePosition);
        if (referenceIndex < 0) referenceIndex = 0;

        int zero = table[referenceIndex].FocusOffset;
        if (zero != 0)
            table = table.Select(t => (t.Position, t.Name, t.FocusOffset - zero)).ToList();

        return table;
    }

    /// <summary>
    /// Puts the profile back the way the run found it, for a cancel, a discard or a failure.
    ///
    /// A run only changes UseFilterWheelOffsets now, so that is the only line here that normally has
    /// work to do. The offset and AutoFocus-filter restores are kept because they cost nothing and
    /// still cover the case where something outside this controller changed them mid-run — a
    /// filter-list re-import from the wheel being the realistic one.
    /// </summary>
    private static void RestoreOldValues()
    {
        try
        {
            var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
            if (profile == null) return;

            RestoreUseOffsets();

            foreach (var (Position, Name, FocusOffset) in _oldOffsets)
            {
                var f = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(x => x.Position == Position);
                if (f != null) f.FocusOffset = FocusOffset;
            }

            foreach (var f in profile.FilterWheelSettings.FilterWheelFilters)
                f.AutoFocusFilter = f.Position == (_oldDefaultFilterPosition ?? -1);
        }
        catch (Exception ex)
        {
            Logger.Error($"FilterOffset: failed to restore old values: {ex.Message}");
        }
    }
}

// ── Run snapshot: UseFilterWheelOffsets across a restart ────────────────────────

public partial class FilterOffsetController
{
    private sealed class RunSnapshot
    {
        public Guid ProfileId { get; set; }
        public bool UseFilterWheelOffsets { get; set; }
    }

    private static readonly string RunSnapshotPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NINA", "TnsCache", "filter-offset-run.json");

    private static void WriteRunSnapshot(Guid profileId, bool useFilterWheelOffsets)
    {
        try
        {
            JsonFileStore.Write(RunSnapshotPath, JsonSerializer.Serialize(new RunSnapshot
            {
                ProfileId = profileId,
                UseFilterWheelOffsets = useFilterWheelOffsets,
            }));
        }
        catch (Exception ex)
        {
            Logger.Warning($"FilterOffset: could not save the offsets setting for after a restart: {ex.Message}");
        }
    }

    private static void DeleteRunSnapshot()
    {
        try
        {
            File.Delete(RunSnapshotPath);
            File.Delete(JsonFileStore.BackupPath(RunSnapshotPath));
        }
        catch (Exception ex)
        {
            Logger.Warning($"FilterOffset: could not delete {RunSnapshotPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts UseFilterWheelOffsets back the way the run found it, once the measuring is over (done, failed or
    /// cancelled). Only the measuring needs the offsets off; left off until an accept or a discard that may never
    /// come, the sequences of every following night ran without them.
    /// </summary>
    private static void RestoreUseOffsets()
    {
        var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
        if (profile != null)
        {
            profile.FocuserSettings.UseFilterWheelOffsets = _oldUseOffsets;
        }

        DeleteRunSnapshot();
    }

    // Only an earlier pins process can have left the snapshot. The server starts again in this one when the app is
    // switched off and on, possibly while a calibration runs, whose snapshot has to stay.
    private static int _recoveryDone;

    /// <summary>
    /// At plugin start: a calibration that pins stopped in the middle of (restart, power cut) left the offsets
    /// switched off in the profile. Puts the setting back for that profile and drops the snapshot; once per process.
    /// </summary>
    internal static void RecoverInterruptedRun()
    {
        if (Interlocked.Exchange(ref _recoveryDone, 1) != 0)
        {
            return;
        }

        try
        {
            if (!File.Exists(RunSnapshotPath) && !File.Exists(JsonFileStore.BackupPath(RunSnapshotPath)))
            {
                return;
            }

            var snapshot = JsonFileStore.Read<RunSnapshot>(
                RunSnapshotPath,
                json => JsonSerializer.Deserialize<RunSnapshot>(json),
                () => null,
                "filter offset run snapshot");
            var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
            if (snapshot != null && profile != null && profile.Id == snapshot.ProfileId)
            {
                Logger.Warning($"FilterOffset: a filter offset calibration was interrupted; filter offsets are switched {(snapshot.UseFilterWheelOffsets ? "on" : "off")} again as before it");
                profile.FocuserSettings.UseFilterWheelOffsets = snapshot.UseFilterWheelOffsets;
            }
            else if (snapshot != null)
            {
                // kept, so a start of pins with that profile puts its setting back
                Logger.Warning("FilterOffset: a filter offset calibration of another profile was interrupted; \"Use filter offsets\" is switched back when pins starts with that profile");
                return;
            }

            DeleteRunSnapshot();
        }
        catch (Exception ex)
        {
            Logger.Warning($"FilterOffset: could not recover an interrupted calibration: {ex.Message}");
        }
    }
}

// ── Request / response models ─────────────────────────────────────────────────

public class FilterOffsetStartRequest
{
    public int Loops { get; set; } = 3;
    public List<int> FilterPositions { get; set; }
}

public class FilterOffsetApplyRequest
{
    /// <summary>
    /// Filter that becomes the AutoFocus filter, and the filter the new offsets are anchored on.
    /// Null leaves no AutoFocus filter set and anchors on the base filter instead.
    /// </summary>
    public int? NewDefaultFilterPosition { get; set; }
}

public class FilterOffsetEntry
{
    public int Position { get; set; }
    public string Name { get; set; }
    public int FocusOffset { get; set; }

    /// <summary>
    /// False for a filter of the run that produced no usable measurement. Its <see cref="FocusOffset"/> is then its
    /// previous distance to the base filter, which an apply keeps, instead of 0.
    /// </summary>
    public bool Measured { get; set; } = true;
}

/// <summary>
/// What one filter × loop produced. <see cref="FocusPosition"/> is null when the loop produced no
/// usable measurement, in which case <see cref="Failure"/> says why.
///
/// One entry covers the loop's AutoFocus attempts, not a single run: a loop that failed once and
/// succeeded on the retry is one entry, with the position it settled on, <see cref="Attempts"/> at 2
/// and the discarded attempt's reason in <see cref="FirstFailure"/>.
/// </summary>
public class FilterOffsetMeasurement
{
    public int Position { get; set; }
    public string Name { get; set; }

    /// <summary>One-based, matching the loop counter the run reports.</summary>
    public int Loop { get; set; }

    public int? FocusPosition { get; set; }

    /// <summary>Why this loop produced nothing. Null when it produced a position.</summary>
    public string Failure { get; set; }

    /// <summary>
    /// AutoFocus runs this loop cost. 0 when the attempt threw and took the whole calibration with it.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// The first attempt's failure when a later one succeeded — the cost the retry hid. Null when the
    /// loop needed only one attempt, or when it failed outright (<see cref="Failure"/> covers that).
    /// </summary>
    public string FirstFailure { get; set; }

    /// <summary>
    /// A measurement that was taken but left out of the offset. Set when outlier rejection runs, which
    /// is just before the result is published — so it is false for the whole run and, briefly, in the
    /// window between rejection and the flip to PendingResult. A client that has no aggregation to go
    /// with the board should not explain a rejection it cannot yet quantify.
    /// </summary>
    public bool Rejected { get; set; }
}

/// <summary>Per-filter working behind one offset, for the measurement board.</summary>
public class FilterOffsetDetail
{
    public int Position { get; set; }
    public string Name { get; set; }
    /// <summary>Loops that produced a usable measurement; a failed AutoFocus is not one.</summary>
    public int SamplesTaken { get; set; }

    /// <summary>Of those, the ones outlier rejection kept.</summary>
    public int SamplesUsed { get; set; }
    public double? Median { get; set; }
    public int? Spread { get; set; }

    /// <summary>
    /// Steps from the median a sample had to exceed to be rejected: the greater of the profile-wide
    /// tolerance floor and a term scaled from this filter's own scatter. The scatter term usually
    /// wins, which is what makes this per filter rather than one number for the run. Null when there
    /// were fewer than the three samples rejection needs.
    /// </summary>
    public double? Cutoff { get; set; }

    public double? EstimatedFocus { get; set; }
    public int FocusOffset { get; set; }
}

public class FilterOffsetResult
{
    public List<FilterOffsetEntry> OldOffsets { get; set; }
    public List<FilterOffsetEntry> NewOffsets { get; set; }
    public int? OldDefaultFilterPosition { get; set; }
    public int? SuggestedDefaultFilterPosition { get; set; }

    public List<FilterOffsetDetail> Details { get; set; }

    /// <summary>Focuser steps per loop, positive when the focus position moved down over the run.</summary>
    public double TemperatureDrift { get; set; }

    /// <summary>
    /// The run's base filter produced no usable measurement, so there was nothing to state the other
    /// filters against and every offset is reported as 0. The result is not usable.
    /// </summary>
    public bool BaseFilterUnmeasured { get; set; }
}
