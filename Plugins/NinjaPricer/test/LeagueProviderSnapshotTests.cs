using NinjaPricer;

namespace NinjaPricer.Tests;

public sealed class LeagueProviderSnapshotTests
{
    [Fact]
    public void PublishedLeagueListIsAnImmutableSnapshot()
    {
        LeagueProvider.SetLeaguesForTests(["Old"]);
        var snapshot = LeagueProvider.Leagues;

        LeagueProvider.SetLeaguesForTests(["New"]);

        Assert.Equal(["Old"], snapshot);
        Assert.Equal(["New"], LeagueProvider.Leagues);
    }
}
