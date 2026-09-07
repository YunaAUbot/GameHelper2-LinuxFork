using Newtonsoft.Json.Linq;

namespace NinjaPricer;

internal static class ScoutLeaguePayload
{
    internal static JArray ParseLeagueArray(string json, int maxLeagues)
    {
        var root = JToken.Parse(json);
        var leagues = root switch
        {
            JArray array => array,
            JObject obj => obj["value"] as JArray ?? obj["Value"] as JArray,
            _ => null,
        };

        return RequireObjectArray(leagues, maxLeagues, "leagues");
    }

    internal static JArray RequireObjectArray(JToken? token, int maxItems, string fieldName)
    {
        if (token is not JArray values)
            throw new InvalidDataException($"Missing or invalid Scout {fieldName} array.");
        if (values.Count > maxItems)
            throw new InvalidDataException($"Too many Scout {fieldName} entries.");
        if (values.Any(entry => entry is not JObject))
            throw new InvalidDataException($"Invalid Scout {fieldName} entry.");

        return values;
    }
}
