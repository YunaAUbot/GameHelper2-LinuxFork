# NinjaPricer

NinjaPricer is the reusable shared price provider for GameHelper2 plugins. Consumer plugins query
`GameHelper.Plugin.Price.PriceProviderRegistry.Current`; they do not need to copy or maintain their
own pricing fetcher.

The plugin is passive and read-only. It does not inspect game memory, automate input, or communicate
outward except for bounded public price and league `GET` requests to poe.ninja and poe2scout.

It supports poe2scout and poe.ninja pricing, public league discovery, automatic/manual refresh, and
a local `price_cache.json` inside the NinjaPricer plugin directory. Defaults match the live Ritual
configuration: poe2scout (`PriceSource=1`), `Runes of Aldur`, and a five-minute refresh interval.

GameHelper allows one shared price provider at a time. NinjaPricer never replaces another owner's
registration. If a competing provider enabled first, NinjaPricer remains inactive and reports which
provider owns the registry; disable the competitor and enable NinjaPricer first. On disable,
NinjaPricer unregisters only the registration owned by its own plugin instance.

## Repository layout verification

Production source and the plugin project live at repository root; tests and any
auxiliary tools belong under `test/`. See [ROOT_LAYOUT.md](ROOT_LAYOUT.md) for the
verified Git importer contract, test commands, and builds against an already-built
GameHelper2 host without modifying it.
