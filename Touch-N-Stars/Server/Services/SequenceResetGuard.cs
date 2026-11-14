using System.Linq;
using NINA.Core.Enum;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;

namespace TouchNStars.Server.Services;

/// <summary>
/// Whether a sequence item may have its status reset. A running item, or a container that
/// holds one, may not: the reset would mark what the sequence executes as Created.
/// </summary>
public static class SequenceResetGuard
{
    public static bool IsRunning(ISequenceItem item)
    {
        if (item == null) return false;
        if (item.Status == SequenceEntityStatus.RUNNING) return true;
        return item is ISequenceContainer container
            && container.GetItemsSnapshot().Any(IsRunning);
    }
}
