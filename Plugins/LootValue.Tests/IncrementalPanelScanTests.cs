namespace LootValue.Tests;

using Xunit;

public sealed class IncrementalPanelScanTests
{
    [Fact]
    public void AggregateScrollProbeBudgetCoversTraversalAndBothLivePanelStates()
    {
        Assert.Equal(196, ScrollProbePolicy.RequiredBudget(64, 3, 2, 2));
    }

    [Fact]
    public void ScrollProbeBudgetExhaustionIsExplicitlyUnavailable()
    {
        var budget = new ScrollRectangleProbeBudget(2);

        Assert.Equal(ScrollProbeStatus.Unavailable, budget.TryReserve(3));
        Assert.Equal(2, budget.Remaining);
        Assert.Equal(ScrollProbeStatus.Succeeded, budget.TryReserve(2));
    }

    [Fact]
    public void ScrollProbeReadFailureIsExplicitlyUnavailable()
    {
        Assert.Equal(ScrollProbeStatus.Succeeded, ScrollProbePolicy.FromReadResults(true, true, true));
        Assert.Equal(ScrollProbeStatus.Unavailable, ScrollProbePolicy.FromReadResults(true, false, true));
    }

    [Fact]
    public void DeferredTraversalIsRetriedAndCannotPublishAnIncompleteSnapshot()
    {
        var attempts = 0;
        var scan = CreatePhasedScan(
            node => ++attempts == 1
                ? PanelTraversalStep<int, int>.Deferred()
                : new PanelTraversalStep<int, int>(Array.Empty<int>(), node),
            candidate => PanelCandidateResult<int>.Accepted(candidate),
            _ => true,
            candidate => PanelCandidateResult<int>.Accepted(candidate));
        scan.Restart((nint)1, 42);

        var firstFrame = new PanelScanBudget(1, 1, 1, 1);
        scan.Advance(ref firstFrame);

        Assert.False(scan.IsComplete);
        Assert.Empty(scan.Snapshot);

        var secondFrame = new PanelScanBudget(1, 1, 1, 1);
        scan.Advance(ref secondFrame);

        Assert.True(scan.IsComplete);
        Assert.Equal(new[] { 42 }, scan.Snapshot);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void PersistentTraversalFailureStopsAfterFixedAttemptsWithoutPublishing()
    {
        var attempts = 0;
        var scan = CreatePhasedScan(
            _ =>
            {
                attempts++;
                return PanelTraversalStep<int, int>.Deferred();
            },
            candidate => PanelCandidateResult<int>.Accepted(candidate),
            _ => true,
            candidate => PanelCandidateResult<int>.Accepted(candidate));
        scan.Restart((nint)1, 42);

        for (var frame = 0; frame < 3; frame++)
        {
            var budget = new PanelScanBudget(64, 8, 8, 8);
            scan.Advance(ref budget);
        }

        Assert.True(scan.IsComplete);
        Assert.False(scan.LastRunSucceeded);
        Assert.Empty(scan.Snapshot);
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(false, (int)ScrollProbeStatus.Unavailable, true)]
    [InlineData(true, (int)ScrollProbeStatus.Unavailable, false)]
    [InlineData(true, (int)ScrollProbeStatus.Succeeded, true)]
    public void LiveScrollFailureRejectsOnlyScrollBoundLabels(
        bool isScrollBound,
        int status,
        bool expected)
    {
        Assert.Equal(expected, ScrollProbePolicy.CanUseLivePosition(isScrollBound, (ScrollProbeStatus)status));
    }

    [Fact]
    public void ScrollRectangleProbeBudgetIsAggregateAcrossCallbacksAndPanels()
    {
        var budget = new ScrollRectangleProbeBudget(7);
        var probes = 0;

        bool ProbeScrollContainer()
        {
            if (!budget.TryConsume(3)) return false;
            probes += 3;
            return true;
        }

        // Two callbacks from one panel and another callback from the other panel
        // all spend from the same per-frame allowance.
        Assert.True(ProbeScrollContainer());
        Assert.True(ProbeScrollContainer());
        Assert.False(ProbeScrollContainer());
        Assert.Equal(6, probes);
        Assert.Equal(1, budget.Remaining);
    }

    [Fact]
    public void ScheduledTickBoundsTraversalAndCandidateWorkAndPublishesOnlyCompleteSnapshot()
    {
        var lastNode = 0;
        var traversalCalls = 0;
        var candidateCalls = 0;
        var scan = new IncrementalPanelScan<int, int, int, int>(
            node =>
            {
                traversalCalls++;
                return new PanelTraversalStep<int, int>(
                    node < lastNode ? new[] { node + 1 } : Array.Empty<int>(),
                    node);
            },
            candidate => PanelCandidateResult<int>.Accepted(candidate),
            candidate => true,
            candidate =>
            {
                candidateCalls++;
                return PanelCandidateResult<int>.Accepted(candidate);
            },
            candidate => candidate,
            maxTraversalElements: 1000);

        scan.Restart((nint)1, 0);
        while (!scan.IsComplete)
        {
            var budget = new PanelScanBudget(7, 3, 3, 3);
            scan.Advance(ref budget);
        }
        Assert.Equal(new[] { 0 }, scan.Snapshot);

        lastNode = 99;
        traversalCalls = 0;
        candidateCalls = 0;
        scan.Restart((nint)1, 0);
        var initialBudget = new PanelScanBudget(7, 3, 3, 3);
        scan.Advance(ref initialBudget);

        Assert.Equal(7, traversalCalls);
        Assert.Equal(0, candidateCalls);
        Assert.Equal(new[] { 0 }, scan.Snapshot);

        while (!scan.IsComplete)
        {
            var traversalBefore = traversalCalls;
            var candidatesBefore = candidateCalls;
            var budget = new PanelScanBudget(7, 3, 3, 3);
            scan.Advance(ref budget);
            Assert.InRange(traversalCalls - traversalBefore, 0, 7);
            Assert.InRange(candidateCalls - candidatesBefore, 0, 3);
            if (!scan.IsComplete) Assert.Equal(new[] { 0 }, scan.Snapshot);
        }

        Assert.Equal(100, traversalCalls);
        Assert.Equal(100, candidateCalls);
        Assert.Equal(Enumerable.Range(0, 100).ToArray(), scan.Snapshot);

        scan.Restart((nint)2, 0);
        Assert.Empty(scan.Snapshot);
        var inFlightBudget = new PanelScanBudget(1, 0, 0, 0);
        scan.Advance(ref inFlightBudget);
        scan.Clear();
        Assert.True(scan.IsComplete);
        Assert.Equal((nint)0, scan.Identity);
        Assert.Empty(scan.Snapshot);
    }

    [Fact]
    public void DuplicateRectanglesEachConsumeOneBudgetUnitBeforeValidationAndPricing()
    {
        var rectangleCalls = 0;
        var validationCalls = 0;
        var pricingCalls = 0;
        var scan = CreatePhasedScan(
            node => new PanelTraversalStep<int, int>(node < 5 ? new[] { node + 1 } : Array.Empty<int>(), node),
            candidate =>
            {
                rectangleCalls++;
                return candidate == 5
                    ? PanelCandidateResult<int>.Accepted(candidate)
                    : PanelCandidateResult<int>.Rejected();
            },
            candidate => { validationCalls++; return true; },
            candidate => { pricingCalls++; return PanelCandidateResult<int>.Accepted(candidate); });

        scan.Restart((nint)1, 1);
        var traversal = new PanelScanBudget(5, 0, 0, 0);
        scan.Advance(ref traversal);

        var first = new PanelScanBudget(0, 2, 1, 1);
        scan.Advance(ref first);
        Assert.Equal(2, rectangleCalls);
        Assert.Equal(0, validationCalls);
        Assert.Equal(0, pricingCalls);

        var second = new PanelScanBudget(0, 3, 1, 1);
        scan.Advance(ref second);
        Assert.Equal(5, rectangleCalls);
        Assert.Equal(1, validationCalls);
        Assert.Equal(1, pricingCalls);
        Assert.Equal(new[] { 5 }, scan.Snapshot);
    }

    [Fact]
    public void TraversalLimitCapsQueuedAndVisitedNodesInsteadOfDrainingIgnoredChildren()
    {
        var calls = 0;
        var scan = new IncrementalPanelScan<int, int, int, int>(
            node =>
            {
                calls++;
                return new PanelTraversalStep<int, int>(Enumerable.Range(node + 1, 100).ToArray());
            },
            candidate => PanelCandidateResult<int>.Rejected(),
            candidate => true,
            candidate => PanelCandidateResult<int>.Rejected(),
            candidate => candidate,
            maxTraversalElements: 10);

        scan.Restart((nint)1, 0);
        var budget = new PanelScanBudget(100, 0, 0, 0);
        scan.Advance(ref budget);

        Assert.Equal(10, calls);
        Assert.True(scan.IsComplete);
    }

    [Fact]
    public void FairSchedulerSharesAggregatePhaseBudgetsAcrossPanels()
    {
        var leftCalls = 0;
        var rightCalls = 0;
        var left = CreateTraversalScan(() => leftCalls++);
        var right = CreateTraversalScan(() => rightCalls++);
        left.Restart((nint)1, 0);
        right.Restart((nint)2, 0);
        var scheduler = new IncrementalPanelScanScheduler<int, int, int, int>();

        var first = new PanelScanBudget(3, 0, 0, 0);
        scheduler.Advance(left, right, ref first);
        Assert.Equal(3, leftCalls + rightCalls);
        Assert.InRange(Math.Abs(leftCalls - rightCalls), 0, 1);

        var second = new PanelScanBudget(3, 0, 0, 0);
        scheduler.Advance(left, right, ref second);
        Assert.Equal(6, leftCalls + rightCalls);
        Assert.Equal(leftCalls, rightCalls);
    }

    [Fact]
    public void SchedulerNeverDoublesAnyPhaseBudgetAcrossPanels()
    {
        var traversals = 0;
        var rectangles = 0;
        var validations = 0;
        var prices = 0;
        IncrementalPanelScan<int, int, int, int> Create() => CreatePhasedScan(
            node =>
            {
                traversals++;
                return new PanelTraversalStep<int, int>(Array.Empty<int>(), node);
            },
            candidate =>
            {
                rectangles++;
                return PanelCandidateResult<int>.Accepted(candidate);
            },
            candidate => { validations++; return true; },
            candidate =>
            {
                prices++;
                return PanelCandidateResult<int>.Accepted(candidate);
            });

        var left = Create();
        var right = Create();
        left.Restart((nint)1, 1);
        right.Restart((nint)2, 2);
        var scheduler = new IncrementalPanelScanScheduler<int, int, int, int>();
        var budget = new PanelScanBudget(1, 1, 1, 1);

        scheduler.Advance(left, right, ref budget);

        Assert.Equal(1, traversals);
        Assert.Equal(1, rectangles);
        Assert.Equal(1, validations);
        Assert.Equal(1, prices);
    }

    private static IncrementalPanelScan<int, int, int, int> CreatePhasedScan(
        Func<int, PanelTraversalStep<int, int>> traverse,
        Func<int, PanelCandidateResult<int>> inspect,
        Func<int, bool> validate,
        Func<int, PanelCandidateResult<int>> price) =>
        new(traverse, inspect, validate, price, _ => 1, 100);

    private static IncrementalPanelScan<int, int, int, int> CreateTraversalScan(Action onTraverse) =>
        CreatePhasedScan(
            node =>
            {
                onTraverse();
                return new PanelTraversalStep<int, int>(new[] { node + 1 });
            },
            candidate => PanelCandidateResult<int>.Rejected(),
            candidate => false,
            candidate => PanelCandidateResult<int>.Rejected());
}
