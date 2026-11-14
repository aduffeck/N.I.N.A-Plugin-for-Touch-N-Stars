using NINA.Core.Enum;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Utility;
using TouchNStars.Server.Services;
using Xunit;

namespace TouchNStars.Tests;

public class SequenceResetGuardTests
{
    private static ISequenceItem Item(SequenceEntityStatus status)
    {
        ISequenceItem item = new Annotation();
        item.Status = status;
        return item;
    }

    private static SequentialContainer Container(SequenceEntityStatus status, params ISequenceItem[] items)
    {
        var container = new SequentialContainer();
        foreach (var item in items)
        {
            container.Add(item);
        }
        container.Status = status;
        return container;
    }

    [Fact]
    public void RunningItem_IsRunning()
    {
        Assert.True(SequenceResetGuard.IsRunning(Item(SequenceEntityStatus.RUNNING)));
    }

    [Fact]
    public void RunningContainer_IsRunning()
    {
        var container = Container(SequenceEntityStatus.RUNNING, Item(SequenceEntityStatus.RUNNING));

        Assert.True(SequenceResetGuard.IsRunning(container));
    }

    [Fact]
    public void ContainerHoldingARunningItem_IsRunning_EvenIfItsOwnStatusSaysCreated()
    {
        var inner = Container(SequenceEntityStatus.CREATED, Item(SequenceEntityStatus.RUNNING));
        var outer = Container(SequenceEntityStatus.CREATED, Item(SequenceEntityStatus.FINISHED), inner);

        Assert.True(SequenceResetGuard.IsRunning(outer));
    }

    [Theory]
    [InlineData(SequenceEntityStatus.CREATED)]
    [InlineData(SequenceEntityStatus.FINISHED)]
    [InlineData(SequenceEntityStatus.FAILED)]
    [InlineData(SequenceEntityStatus.SKIPPED)]
    public void IdleItemsAndContainers_AreNotRunning(SequenceEntityStatus status)
    {
        Assert.False(SequenceResetGuard.IsRunning(Item(status)));
        Assert.False(SequenceResetGuard.IsRunning(
            Container(status, Item(SequenceEntityStatus.FINISHED), Item(SequenceEntityStatus.CREATED))));
    }
}
