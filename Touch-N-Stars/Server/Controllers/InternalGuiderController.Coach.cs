using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers;

public partial class InternalGuiderController
{
    private const int DefaultHistory = 20;
    private const int MaxHistory = 200;

    /// <summary>GET /api/internal-guider/coach - the AdvancedCoachStatus of the current or last session (phase Idle before the first).</summary>
    [Route(HttpVerbs.Get, "/internal-guider/coach")]
    public Task GetCoach()
    {
        return WithCoachAsync("coach", coach => SendOk(InternalGuiderService.GetCoachStatus(coach)));
    }

    /// <summary>POST /api/internal-guider/coach/start - body AdvancedCoachOptions: start a Guiding Coach session.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/coach/start")]
    public Task StartCoach()
    {
        return WithCoachAsync("coach/start", async coach =>
        {
            string body = await HttpContext.GetRequestBodyAsStringAsync();
            if (!InternalGuiderRequest.TryParseCoachOptions(body, out AdvancedCoachOptions options, out string parseError))
            {
                await SendError(400, "InvalidRequest", parseError);
                return;
            }

            (GuiderCallOutcome outcome, AdvancedCoachStartResult result) = await Service.StartCoachAsync(coach, options);
            if (outcome.State == GuiderCallState.Completed && !outcome.Success && !outcome.Cancelled && !outcome.Faulted)
            {
                // Rejected: the reason comes with the start result (the session status is left alone).
                await SendError(409, "Rejected",
                    !string.IsNullOrWhiteSpace(result?.Message) ? result.Message : $"The guider did not start the Guiding Coach (state {coach.State ?? "unknown"}).",
                    result?.MessageCode,
                    result?.MessageParameters);
                return;
            }

            await SendCallOutcome("coach-start", outcome,
                () => result?.Status ?? InternalGuiderService.GetCoachStatus(coach),
                "A Guiding Coach session is already starting.",
                () => "The guider did not start the Guiding Coach.");
        });
    }

    /// <summary>POST /api/internal-guider/coach/skip - skip the running step (its usable partial results are kept).</summary>
    [Route(HttpVerbs.Post, "/internal-guider/coach/skip")]
    public Task SkipCoachStep()
    {
        return WithCoachAsync("coach/skip", async coach =>
        {
            GuiderCallOutcome outcome = await Service.SkipCoachStepAsync(coach);
            await SendCallOutcome("coach-skip", outcome,
                () => InternalGuiderService.GetCoachStatus(coach),
                null,
                () => InternalGuiderStates.IsCoachRunning(InternalGuiderService.GetCoachStatus(coach))
                    ? "The running coach step cannot be skipped."
                    : "No Guiding Coach session is running.");
        });
    }

    /// <summary>POST /api/internal-guider/coach/cancel - cancel the session; temporary settings are restored.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/coach/cancel")]
    public Task CancelCoach()
    {
        return WithCoachAsync("coach/cancel", async coach =>
        {
            bool wasRunning = InternalGuiderStates.IsCoachRunning(InternalGuiderService.GetCoachStatus(coach));
            GuiderCallOutcome outcome = await Service.CancelCoachAsync(coach);
            await SendCallOutcome("coach-cancel", outcome,
                () => InternalGuiderService.GetCoachStatus(coach),
                null,
                () => wasRunning ? "The guider could not cancel the Guiding Coach." : "No Guiding Coach session is running.");
        });
    }

    /// <summary>POST /api/internal-guider/coach/apply - body { ids }: apply the setting changes of findings, trials or live hints.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/coach/apply")]
    public Task ApplyCoachActions()
    {
        return WithCoachAsync("coach/apply", async coach =>
        {
            string body = await HttpContext.GetRequestBodyAsStringAsync();
            if (!InternalGuiderRequest.TryParseIdsBody(body, out List<string> ids, out string parseError))
            {
                await SendError(400, "InvalidRequest", parseError);
                return;
            }

            AdvancedCoachStatus status = InternalGuiderService.GetCoachStatus(coach);
            Dictionary<string, int> applicable = ApplicableCoachIds(status, coach.GetStatus()?.Hints, out bool anyKnown);
            if (!anyKnown)
            {
                await SendError(409, "Rejected", "There is nothing to apply. Run the Guiding Coach first.");
                return;
            }

            var resolved = new List<string>();
            foreach (string id in ids)
            {
                string match = applicable.Keys.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    await SendError(400, "InvalidRequest", $"Unknown finding or trial id: {id}.");
                    return;
                }
                if (applicable[match] == 0)
                {
                    await SendError(400, "InvalidRequest", $"'{match}' carries no setting changes.");
                    return;
                }
                resolved.Add(match);
            }

            GuiderCallOutcome outcome = await Service.ApplyCoachActionsAsync(coach, resolved);
            await SendCallOutcome("coach-apply", outcome,
                () => InternalGuiderService.GetCoachStatus(coach),
                null,
                () => "The guider could not apply the selected recommendations.",
                "applied", resolved);
        });
    }

    /// <summary>
    /// Ids that coach/apply accepts, with their number of setting changes: findings of the status and of its
    /// report, "trial:&lt;id&gt;" for every trial, and the active live hints.
    /// </summary>
    internal static Dictionary<string, int> ApplicableCoachIds(AdvancedCoachStatus status, IEnumerable<AdvancedCoachFinding> hints, out bool anyKnown)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        IEnumerable<AdvancedCoachFinding> findings = (status?.Findings ?? new List<AdvancedCoachFinding>())
            .Concat(status?.Report?.Findings ?? new List<AdvancedCoachFinding>())
            .Concat(hints ?? Enumerable.Empty<AdvancedCoachFinding>());
        foreach (AdvancedCoachFinding finding in findings)
        {
            if (finding == null || string.IsNullOrWhiteSpace(finding.Id)) continue;
            int changes = finding.Changes?.Count ?? 0;
            result[finding.Id] = Math.Max(changes, result.TryGetValue(finding.Id, out int known) ? known : 0);
        }
        IEnumerable<AdvancedCoachTrial> trials = (status?.Trials?.Count ?? 0) > 0
            ? status.Trials
            : status?.Report?.Trials ?? new List<AdvancedCoachTrial>();
        foreach (AdvancedCoachTrial trial in trials)
        {
            if (trial == null || string.IsNullOrWhiteSpace(trial.Id)) continue;
            result[InternalGuiderStates.TrialId(trial)] = trial.Settings?.Count ?? 0;
        }
        anyKnown = result.Count > 0;
        return result;
    }

    /// <summary>GET /api/internal-guider/coach/history?max=20 - stored reports, newest first, without raw samples.</summary>
    [Route(HttpVerbs.Get, "/internal-guider/coach/history")]
    public Task GetCoachHistory()
    {
        return WithCoachAsync("coach/history", coach =>
        {
            int max = QueryInt("max", DefaultHistory, 1, MaxHistory);
            return SendOk(coach.GetCoachHistory(max) ?? Array.Empty<AdvancedCoachReport>());
        });
    }

    /// <summary>POST /api/internal-guider/hints/dismiss - body { id }: hide a live hint for the rest of the guiding session.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/hints/dismiss")]
    public Task DismissHint()
    {
        return WithCoachAsync("hints/dismiss", async coach =>
        {
            string body = await HttpContext.GetRequestBodyAsStringAsync();
            if (!InternalGuiderRequest.TryParseIdBody(body, out string id, out string parseError))
            {
                await SendError(400, "InvalidRequest", parseError);
                return;
            }
            if (!coach.DismissHint(id))
            {
                await SendError(409, "Rejected", "The hint is no longer active.");
                return;
            }
            await SendOk(new { dismissed = id, hints = coach.GetStatus()?.Hints ?? new List<AdvancedCoachFinding>() });
        });
    }

    /// <summary>The coach routes: the connected internal guider, 501 when it has no <see cref="IGuidingCoach"/>.</summary>
    private Task WithCoachAsync(string what, Func<IGuidingCoach, Task> handler)
    {
        return WithConnectedGuiderAsync(what, guider => guider is IGuidingCoach coach
            ? handler(coach)
            : SendNotSupported("the Guiding Coach"));
    }
}
