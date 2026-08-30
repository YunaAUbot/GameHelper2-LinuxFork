using GameHelper.Settings;
using Newtonsoft.Json;

var json = """
{
  "SpecialMiscObjPaths": [
    { "Item1": "Metadata/Test/Object", "Item2": 123 },
    { "Item1": "Metadata/Test/Object", "Item2": 123 },
    { "Item1": "Metadata/Custom/Object", "Item2": 456 }
  ],
  "MonstersPathsToIgnore": [
    "Metadata/Test/Monster",
    "Metadata/Test/Monster",
    "Metadata/Custom/Monster"
  ]
}
""";

var loaded = JsonConvert.DeserializeObject<State>(json)
    ?? throw new InvalidOperationException("state deserialized to null");
if (loaded.SpecialMiscObjPaths.Count != 2 ||
    loaded.SpecialMiscObjPaths[0] != ("Metadata/Test/Object", 123) ||
    loaded.SpecialMiscObjPaths[1] != ("Metadata/Custom/Object", 456))
    throw new InvalidOperationException($"special-object paths accumulated defaults/duplicates or reordered custom entries: {loaded.SpecialMiscObjPaths.Count}");
if (loaded.MonstersPathsToIgnore.Count != 2 ||
    loaded.MonstersPathsToIgnore[0] != "Metadata/Test/Monster" ||
    loaded.MonstersPathsToIgnore[1] != "Metadata/Custom/Monster")
    throw new InvalidOperationException($"ignored-monster paths accumulated defaults/duplicates or reordered custom entries: {loaded.MonstersPathsToIgnore.Count}");

var defaults = JsonConvert.DeserializeObject<State>("{}")
    ?? throw new InvalidOperationException("default state deserialized to null");
if (defaults.SpecialMiscObjPaths.Count != 9 || defaults.MonstersPathsToIgnore.Count != 12)
    throw new InvalidOperationException("missing JSON properties did not retain canonical defaults");

Console.WriteLine("PASS: state collections replace JSON values and deduplicate persisted entries");
