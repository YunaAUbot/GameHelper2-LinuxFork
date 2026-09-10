# Root plugin layout and offline verification

`NinjaPricer.csproj` and production source live at repository root. Tests are in
`test/NinjaPricer.Tests.csproj`; all future auxiliary projects/tools must live under
`test/` (tools under `test/tools/`). Assembly name and root namespace remain
`NinjaPricer` and are now explicit.

Production source remains at root. Explicit root C# compilation is preserved,
and SDK resource/None globs now exclude auxiliary and nested build files. LICENSE
and README output copying and existing conditional vendoring behavior are retained.

There was only one README in the original checkout, at root. Its existing content
is retained with updated paths and a verification link; no README collision occurred.
There were no CI workflows, solutions, or auxiliary tool projects to relocate.
All original C# source is unchanged, including test source. Runtime behavior and
existing UI/performance acceptance gates are unchanged.

## Authoritative importer contract

Read-only host: `/root/workspaces/gamehelper2-linux-fork`, importer revision
`3fa782645c681737fe75a4f6fb6c45d74280d5d7` (`feat/plugin-git-install`). The supplied
host checkout predates the importer, so read its source using `git show`, without
checking out or modifying host files.

In `scripts/git-plugin-worker.py`, `Worker.install` recursively finds `*.csproj`,
excluding relative path parts named obj/bin or containing "test", case-insensitively.
It requires exactly one production project. Root placement is this repository's
layout requirement; the importer itself also accepts nested production projects.
A second project under plain `tools/` would be discovered and rejected.

The importer reads AssemblyName (falling back to project filename), replaces
ProjectReference entries ending in `/GameHelper/GameHelper.csproj` with installed
managed host references, and removes ValidateGameHelperHost/CopyFiles targets in
its temporary clone. It builds Release with Windows targeting and checks the named
DLL's MZ header and assembly metadata. The root project's host reference keeps that
recognized suffix. The host plugin manager discovers DLLs at plugin-directory top
level, so preserving assembly identity preserves its filename match.

## Regression and real builds

`test/check_import_layout.py` is adapted from the verified
`/root/workspaces/ange-reforging-ev/test/check_import_layout.py`. It extracts the
actual pinned importer's discovery expression via AST, checks root production and
auxiliary project placement, exercises ignored synthetic projects and a misplaced
tool, and evaluates real MSBuild Compile/EmbeddedResource/None/Content globs in a
temporary source copy. Sentinels include C#, resx, text, JSON and PNG under test,
tools and nested obj/bin (including asset directories). It verifies all production
C# sources, preserved assembly/namespace identity, recognized host reference, and
copy metadata for required runtime assets.

Against the supplied already-built host, run from this repository:

```bash
export GAMEHELPER2_HOST_ROOT=/root/workspaces/gamehelper2-linux-fork
export DOTNET=/root/.dotnet/dotnet
python3 test/check_import_layout.py
"$DOTNET" test test/NinjaPricer.Tests.csproj -c Release
"$DOTNET" restore NinjaPricer.csproj --no-dependencies -p:EnableWindowsTargeting=true -p:RuntimeIdentifier=win-x64
"$DOTNET" build NinjaPricer.csproj -c Release --no-restore -p:EnableWindowsTargeting=true -p:RuntimeIdentifier=win-x64 -p:BuildProjectReferences=false
python3 test/check_import_layout.py
git diff --check
```

Restore excludes dependencies and build disables project-reference builds, so the
host is only read. Output: `bin/Release/net10.0-windows/win-x64/NinjaPricer.dll`.
Ordinary builds using a different host may need that host built first.

## Verification scope

The new regression failed on the original layout before migration. Original and
moved test suites and real production builds passed against the supplied host.
Final regression passes include asset-copy and SDK isolation assertions. Full
commands, exact outputs, file/hash inventory and limitations are recorded in
`/root/plugin-root-layout-batch.md` for this batch.

These are offline discovery, SDK evaluation, tests and compilation checks. They do
not claim a GUI Git installation or in-game runtime comparison. No plugin runtime,
installation, deployment, commit or push is part of this verification.
