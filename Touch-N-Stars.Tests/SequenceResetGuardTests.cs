using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Trigger;
using TouchNStars.Server.Services;
using Xunit;

namespace TouchNStars.Tests;

public class SequenceResetGuardTests
{
    private sealed class TestTrigger : SequenceTrigger
    {
        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => false;
        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) => Task.CompletedTask;
        public override object Clone() => new TestTrigger();
    }

    private static TestTrigger Trigger(SequenceEntityStatus status, params ISequenceItem[] runnerItems)
    {
        var trigger = new TestTrigger();
        foreach (var item in runnerItems)
        {
            trigger.TriggerRunner.Add(item);
        }
        trigger.Status = status;
        return trigger;
    }

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

    [Fact]
    public void RunningTrigger_IsRunning()
    {
        Assert.True(SequenceResetGuard.IsRunning((object)Trigger(SequenceEntityStatus.RUNNING)));
    }

    [Fact]
    public void TriggerWhoseRunnerRunsAnItem_IsRunning()
    {
        Assert.True(SequenceResetGuard.IsRunning((object)Trigger(SequenceEntityStatus.CREATED, Item(SequenceEntityStatus.RUNNING))));
    }

    [Fact]
    public void ContainerWithARunningTrigger_IsRunning_WhileNoneOfItsItemsRuns()
    {
        var container = Container(SequenceEntityStatus.CREATED, Item(SequenceEntityStatus.FINISHED), Item(SequenceEntityStatus.CREATED));
        container.Add(Trigger(SequenceEntityStatus.RUNNING, Item(SequenceEntityStatus.RUNNING)));

        Assert.True(SequenceResetGuard.IsRunning((object)container));
    }

    [Theory]
    [InlineData(SequenceEntityStatus.CREATED)]
    [InlineData(SequenceEntityStatus.FINISHED)]
    [InlineData(SequenceEntityStatus.DISABLED)]
    public void IdleTriggers_AreNotRunning(SequenceEntityStatus status)
    {
        var trigger = Trigger(status, Item(SequenceEntityStatus.FINISHED));
        var container = Container(SequenceEntityStatus.FINISHED, Item(SequenceEntityStatus.FINISHED));
        container.Add(Trigger(status));

        Assert.False(SequenceResetGuard.IsRunning((object)trigger));
        Assert.False(SequenceResetGuard.IsRunning((object)container));
    }

    [Fact]
    public void OtherObjects_AreNotRunning()
    {
        Assert.False(SequenceResetGuard.IsRunning((object)"not a sequence entity"));
        Assert.False(SequenceResetGuard.IsRunning((object)null!));
    }
}
