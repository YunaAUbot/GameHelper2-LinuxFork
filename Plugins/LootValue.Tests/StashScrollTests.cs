using System.Numerics;
using Xunit;

namespace LootValue.Tests;

public sealed class StashScrollTests
{
    [Fact]
    public void OrdinaryPanelWithHiddenContentDoesNotDeferTheEntireStashScan()
    {
        int reads = 0;
        var status = ScrollContainerGeometry.Probe(address =>
        {
            reads++;
            Assert.Equal((nint)2, address); // Only the proposed track needs reading.
            return new ScrollRect(Vector2.Zero, new Vector2(600, 800));
        }, 1, 2, 3, out _);
        Assert.Equal(ScrollProbeStatus.NotApplicable, status);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void UnrelatedThumbIsRejectedBeforeReadingHiddenContents()
    {
        var status = ScrollContainerGeometry.Probe(address => address switch
        {
            2 => new ScrollRect(new(100, 10), new(12, 200)),
            3 => new ScrollRect(new(100, 10), new(300, 100)),
            _ => throw new Exception("An unrelated UI group must not probe its hidden content"),
        }, 1, 2, 3, out _);
        Assert.Equal(ScrollProbeStatus.NotApplicable, status);
    }

    [Fact]
    public void ActualScrollContainerPreservesOffsetAndClipping()
    {
        var status = ScrollContainerGeometry.Probe(address => address switch
        {
            1 => new ScrollRect(new(0, 10), new(100, 600)),
            2 => new ScrollRect(new(100, 10), new(12, 200)),
            3 => new ScrollRect(new(100, 85), new(10, 50)),
            _ => null,
        }, 1, 2, 3, out var geometry);
        Assert.Equal(ScrollProbeStatus.Succeeded, status);
        Assert.Equal(200f, geometry.OffsetY);
        Assert.Equal(10f, geometry.ClipTop);
        Assert.Equal(210f, geometry.ClipBottom);
    }

    [Fact]
    public void ConfirmedScrollbarWithUnreadableContentsRemainsUnavailable()
    {
        var status = ScrollContainerGeometry.Probe(address => address switch
        {
            2 => new ScrollRect(new(100, 10), new(12, 200)),
            3 => new ScrollRect(new(100, 85), new(10, 50)),
            _ => null,
        }, 1, 2, 3, out _);
        Assert.Equal(ScrollProbeStatus.Unavailable, status);
    }

    [Fact]
    public void StashTraversalStillPublishesSlotsPastUnrelatedUiGroups()
    {
        var scan = new IncrementalPanelScan<int, int, int, int>(node =>
        {
            if (node == 0)
            {
                var status = ScrollContainerGeometry.Probe(_ => new ScrollRect(Vector2.Zero, new(600, 800)), 1, 2, 3, out _);
                return status == ScrollProbeStatus.Unavailable
                    ? PanelTraversalStep<int, int>.Deferred()
                    : new PanelTraversalStep<int, int>(new[] { 1, 2 });
            }
            return new PanelTraversalStep<int, int>(Array.Empty<int>(), node);
        }, value => PanelCandidateResult<int>.Accepted(value), _ => true,
        value => PanelCandidateResult<int>.Accepted(value), value => value, 5000);
        scan.Restart(1, 0);
        for (int frame = 0; frame < 10 && !scan.IsComplete; frame++)
        {
            var budget = new PanelScanBudget(1, 1, 1, 1);
            scan.Advance(ref budget);
        }
        Assert.True(scan.IsComplete);
        Assert.True(scan.LastRunSucceeded);
        Assert.Equal(new[] { 1, 2 }, scan.Snapshot);
    }

    [Fact]
    public void RitualPathsAndUniqueThresholdsRemainAvailable()
    {
        Assert.Equal(new[] { 75, 13 }, RitualRewardGridPathPolicy.CandidatePaths[0]);
        Assert.Equal(new[] { 76, 13 }, RitualRewardGridPathPolicy.CandidatePaths[1]);
        Assert.False(RitualRewardGridPathPolicy.ShouldProbeScroll(true));
        Assert.Equal(0f, LootValuePricingPolicy.MinimumValueEx(true, 1.09f, 0f));
    }
}
