using System.Linq;
using NINA.Core.Enum;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;

namespace TouchNStars.Server.Services;

/// <summary>
/// Whether a sequence entity may have its status written (reset, enable, disable). A running
/// item or trigger, or a container that holds one, may not: the write would replace Running
/// while the sequence still executes it.
/// </summary>
public static class SequenceResetGuard
{
    public static bool IsRunning(object entity)
    {
        return entity switch
        {
            ISequenceItem item => IsRunning(item),
            ISequenceTrigger trigger => IsRunning(trigger),
            _ => false,
        };
    }

    public static bool IsRunning(ISequenceItem item)
    {
        if (item == null) return false;
        if (item.Status == SequenceEntityStatus.RUNNING) return true;
        if (item is not ISequenceContainer container) return false;
        // A trigger runs between two items, when none of them is running.
        if (container is ITriggerable triggerable && triggerable.GetTriggersSnapshot().Any(IsRunning)) return true;
        return container.GetItemsSnapshot().Any(IsRunning);
    }

    public static bool IsRunning(ISequenceTrigger trigger)
    {
        if (trigger == null) return false;
        if (trigger.Status == SequenceEntityStatus.RUNNING) return true;
        return trigger is SequenceTrigger sequenceTrigger && IsRunning(sequenceTrigger.TriggerRunner);
    }
}
