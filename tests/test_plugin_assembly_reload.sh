#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
probe="$repo_root/tests/PluginAssemblyReloadProbe"
probe_output=$(mktemp -d)
trap 'rm -rf -- "$probe_output"' EXIT
dotnet build "$probe/Fixture/Fixture.csproj" -c Release -o "$probe_output/first" -p:NuGetAudit=false -m:1
dotnet build "$probe/Fixture/Fixture.csproj" -c Release -o "$probe_output/second" -p:FixtureVersion=SECOND -p:NuGetAudit=false -m:1
dotnet build "$probe/PluginAssemblyReloadProbe.csproj" -c Release -o "$probe_output/runner" -p:NuGetAudit=false -m:1
dotnet "$probe_output/runner/PluginAssemblyReloadProbe.dll" "$probe_output/first/ReloadFixture.dll" "$probe_output/second/ReloadFixture.dll"
