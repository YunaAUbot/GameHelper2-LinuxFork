# NinjaPricer Agent Guide

## Project

NinjaPricer is the shared GameHelper2 price provider. It owns bounded public league/price HTTP GETs, normalization, caching, refresh policy, and registration in `PriceProviderRegistry`.

## Host boundary

- Standalone builds use `GAMEHELPER2_HOST_ROOT` or `-p:GameHelperHostRoot=...`.
- When vendored under `GameHelper2/Plugins/NinjaPricer`, the host root resolves automatically.
- Consumers must use the shared provider contract instead of adding another price-network lifecycle.

## Vendoring

- Canonical source: `https://github.com/YunaAUbot/GameHelper2-NinjaPricer`.
- The copy in `YunaAUbot/GameHelper2-LinuxFork/Plugins/NinjaPricer` must remain byte-identical to this repository, excluding Git/build/runtime state.
- Sync only after standalone tests pass; then rerun the Linux-fork plugin tests and package build.

## Verification

```bash
export GAMEHELPER2_HOST_ROOT=/path/to/GameHelper2
dotnet test test/NinjaPricer.Tests.csproj -c Release
dotnet build NinjaPricer.csproj -c Release -p:EnableWindowsTargeting=true
git diff --check
```

## Safety

No game input, process writes, injection, packet manipulation, or unrelated networking. Keep external calls bounded, public, read-only, and fail-closed. Never commit runtime cache or configuration.

## Git importer layout

Keep the production project and source at repository root. Every auxiliary project
and tool belongs under `test/` (tools under `test/tools/`). Preserve assembly identity
and exclude test/tools and nested obj/bin from all production SDK items.
Run `python3 test/check_import_layout.py` with `GAMEHELPER2_HOST_ROOT` and `DOTNET` set.
See [ROOT_LAYOUT.md](ROOT_LAYOUT.md) for the pinned importer contract and verification
commands that use existing host artifacts without rebuilding or modifying the host.
