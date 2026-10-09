using System;
using System.Collections.Generic;
using NINA.Equipment.Equipment.MyGuider.Advanced;

namespace TouchNStars.Server.Services;

/// <summary>The words of the internal guider (wire values of the IAdvancedGuider contract) that this API acts on.</summary>
internal static class InternalGuiderStates
{
    /// <summary>Guider states in which NINA's guider mediator considers the guider guiding.</summary>
    private static readonly HashSet<string> GuidingStates = new(StringComparer.OrdinalIgnoreCase)
    {
        AdvancedGuiderStates.Calibrating,
        AdvancedGuiderStates.Guiding,
        AdvancedGuiderStates.LostLock,
        AdvancedGuiderStates.Reacquiring,
        AdvancedGuiderStates.Paused
    };

    /// <summary>Coach steps accepted in AdvancedCoachOptions.Steps, in session order.</summary>
    public static IReadOnlyList<string> CoachSteps => AdvancedCoachSteps.Selectable;

    /// <summary>Kinds of stored incident images: the binned whole field and the full-resolution key frames.</summary>
    public const string IncidentImageContext = "context";
    public const string IncidentImageKey = "key";
    public static readonly IReadOnlyList<string> IncidentImageKinds = new[] { IncidentImageContext, IncidentImageKey };

    /// <summary>
    /// Reject reasons of secondaries whose position in the frame is no star: not found in their search box, or evicted
    /// as hot pixels. Whether a secondary is Used changes from frame to frame (e.g. not while settling), so that doesn't count.
    /// </summary>
    private static readonly HashSet<string> NoStarReasons = new(StringComparer.Ordinal) { "Lost", "DroppedZero" };

    public static bool IsGuiding(string state) => state != null && GuidingStates.Contains(state);

    public static bool IsStopped(string state) => string.Equals(state, AdvancedGuiderStates.Stopped, StringComparison.OrdinalIgnoreCase);

    public static bool IsCoachRunning(AdvancedCoachStatus status)
        => string.Equals(status?.Phase, AdvancedCoachPhases.Running, StringComparison.OrdinalIgnoreCase);

    /// <summary>The coach/apply id of a trial.</summary>
    public static string TrialId(AdvancedCoachTrial trial) => AdvancedCoachActions.TrialPrefix + trial.Id;

    /// <summary>Whether a star of a frame is a star at its position (secondaries can be lost or hot pixels).</summary>
    public static bool IsStar(AdvancedGuideStar star) => star != null && (star.RejectReason == null || !NoStarReasons.Contains(star.RejectReason));
}
