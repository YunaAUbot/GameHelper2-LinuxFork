using Newtonsoft.Json.Linq;
using NinjaPricer;

namespace NinjaPricer.Tests;

public sealed class ScoutModifierPayloadTests
{
    [Fact]
    public void AcceptsMixedLegacyStringsAndCurrentDescriptionObjects()
    {
        var token = JArray.Parse("[\"legacy mod\",{\"description\":\"current mod\",\"hash\":\"stat.x\"},{\"description\":{\"unexpected\":\"shape\"}},null,{}]");

        var modifiers = PriceFetcher.ParseScoutModifiersForTests(token);

        Assert.Equal(new[] { "legacy mod", "current mod" }, modifiers);
    }

    [Fact]
    public void RejectsExcessiveModifierArrays()
    {
        var token = new JArray(Enumerable.Repeat<JToken>(new JValue("mod"), 101));

        Assert.Throws<InvalidDataException>(() => PriceFetcher.ParseScoutModifiersForTests(token));
    }
}
