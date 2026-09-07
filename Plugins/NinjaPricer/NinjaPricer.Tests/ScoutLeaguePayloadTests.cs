using Newtonsoft.Json.Linq;
using NinjaPricer;

namespace NinjaPricer.Tests;

public sealed class ScoutLeaguePayloadTests
{
    [Theory]
    [InlineData("[{\"Value\":\"Runes of Aldur\"}]", "Runes of Aldur")]
    [InlineData("{\"value\":[{\"Value\":\"Legacy League\"}]}", "Legacy League")]
    public void AcceptsCurrentArrayAndLegacyObjectRoots(string json, string expected)
    {
        var leagues = ScoutLeaguePayload.ParseLeagueArray(json, 100);

        Assert.Single(leagues);
        Assert.Equal(expected, leagues[0]!["Value"]!.ToString());
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[\"not an object\"]")]
    public void RejectsNonObjectLeagueEntries(string json)
    {
        Assert.Throws<InvalidDataException>(() => ScoutLeaguePayload.ParseLeagueArray(json, 100));
    }

    [Fact]
    public void ObjectArrayValidationRejectsScalarsAndExcessiveCounts()
    {
        var scalar = JArray.Parse("[{\"ok\":true},null]");
        var excessive = new JArray(Enumerable.Range(0, 251).Select(_ => new JObject()));

        Assert.Throws<InvalidDataException>(() => ScoutLeaguePayload.RequireObjectArray(scalar, 250, "items"));
        Assert.Throws<InvalidDataException>(() => ScoutLeaguePayload.RequireObjectArray(excessive, 250, "items"));
    }

    [Fact]
    public void RejectsUnsupportedRootShape()
    {
        Assert.Throws<InvalidDataException>(() => ScoutLeaguePayload.ParseLeagueArray("{}", 100));
    }
}
