using Atlas2;
using Newtonsoft.Json;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
var fresh = new Atlas2Settings();
Check(fresh.UseNinjaRitualWeights, "Automatic live pricing stays enabled");
Check(fresh.RitualRewardWeights["Mageblood"] > 0, "New configs receive upstream defaults");
foreach (var version in new[] { "", ",\"RitualRewardWeightsVersion\":1", ",\"RitualRewardWeightsVersion\":3" })
{
    var empty = JsonConvert.DeserializeObject<Atlas2Settings>("{\"RitualRewardWeights\":{}" + version + "}")!;
    Check(empty.RitualRewardWeights.Count == 0, "Explicit empty weights remain empty");
    var saved = JsonConvert.DeserializeObject<Atlas2Settings>("{\"UseNinjaRitualWeights\":false,\"RitualRewardWeights\":{\"Mageblood\":7,\"custom\":-3}" + version + "}")!;
    Check(!saved.UseNinjaRitualWeights, "Saved pricing toggle survives");
    Check(saved.RitualRewardWeights.Count == 2 && saved.RitualRewardWeights["Mageblood"] == 7 && saved.RitualRewardWeights["custom"] == -3, "Saved manual values survive without added defaults");
    var roundTrip = JsonConvert.DeserializeObject<Atlas2Settings>(JsonConvert.SerializeObject(saved))!;
    Check(roundTrip.RitualRewardWeights.Count == 2, "Reload does not repopulate defaults");
}
Console.WriteLine("PASS: fresh defaults, legacy/versioned empty/custom weights, pricing toggle, repeated reload.");
