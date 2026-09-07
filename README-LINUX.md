# GameHelper2 Linux GPU build

This fork runs the Windows x64 GameHelper2 process inside the existing Path of Exile 2 Steam/Proton prefix and renders its ImGui draw lists through a native Linux GPU compositor.

## Architecture

- `run-gamehelper2-linux.sh` identifies a running PoE2 process by executable name **and** Steam app ID `2694490`.
- Only GameHelper2 is launched in a dedicated process group. The group is terminated when PoE2 exits.
- The managed renderer is based on ClickableTransparentOverlay 11.1.0 and keeps its public API.
- Under Linux, compact ImGui vertices, indices, clipping rectangles and the font atlas are sent over a loopback-only connection to `gamehelper2-gpu-overlay`.
- The native helper creates an ARGB X11/GLX window above the `Path of Exile 2` XWayland window and renders with OpenGL. It does **not** copy a complete monitor-sized pixel surface back through the CPU.
- X Shape regions keep ordinary transparent/text-only areas click-through while interactive ImGui windows receive mouse events. Keyboard capture is limited to `WantTextInput`. During text entry, a transparent focus window lets KWin route characters even when a Wayland app previously held focus. Capture releases on app switching, field deactivation, F12 menu close, or disconnect; a fresh overlay click can resume editing.

Input regressions run with `bash tests/test_linux_gpu_text_input.sh` and
`bash tests/test_linux_wayland_text_input.sh` after a whole-solution Release build.
The latter uses a private nested KWin/Wayland session and synthetic public text;
see `tests/NativeWaylandTextInputProbe/README.md` for dependencies and lifecycle cases.

## Build

Requirements: .NET 10 SDK, a C compiler, `pkg-config`, and development files for X11, Xext, Xrender and OpenGL.

```bash
./tests/test_linux_gpu_renderer.sh
./tests/test_linux_package.sh
./tests/test_linux_launcher.sh
./build-linux-package.sh
```

The default artifact is `dist/GameHelper2-linux/`. It is self-contained for Windows x64 and includes the native Linux helper.

The Linux package intentionally contains only passive bundled plugins:

- Atlas2
- HealthBars
- PlayerBuffBar
- PreloadAlert
- Radar
- LootValue
- NinjaPricer

AutoHotKeyTrigger and PickupHelper remain excluded because they synthesize input. The Windows updater/launcher is also excluded; the shell launcher starts `GameHelper.exe` directly. LootValue is passive and consumes the shared `PriceProviderRegistry`; bundled NinjaPricer owns its bounded public price/league HTTP GETs, cache, and refresh lifecycle.

## Start

Start PoE2 normally through Steam, then run:

```bash
GAMEHELPER2_EXE=/absolute/path/to/GameHelper.exe ./run-gamehelper2-linux.sh
```

The launcher defaults the helper process to:

```text
GAMEHELPER2_OVERLAY_BACKEND=native-gpu
PROTON_USE_WINED3D=1
```

These variables affect only GameHelper2, not the already-running game. `GAMEHELPER2_OVERLAY_BACKEND=windows` selects upstream CTO's Windows presenter for diagnostics; it is not the supported Linux renderer.

## Diagnostics

- Managed lifecycle: `GameHelper2.renderer.log` beside `GameHelper.exe`
- Native GL vendor/renderer/version: `/tmp/gamehelper2-gpu-renderer.log`
- Native input transitions: `/tmp/gamehelper2-gpu-input.log`

If the native helper cannot start, GameHelper2 closes instead of leaving an opaque Wine overlay over the game. The helper also exits when its heartbeat becomes stale.

## Current limitations

The native protocol transfers the ImGui font atlas and untextured primitives. Arbitrary plugin textures are not transferred yet; texture-heavy passive plugins can therefore show black fallback primitives even though core settings and text overlays render correctly. Treat those plugins as packaged compatibility candidates until each receives an in-game visual test.

## Provenance

- GameHelper2 base: `Gordin/GameHelper2`, fork base `0e3ea8cb57343f941160fab02e22c26c9bd26432`
- ClickableTransparentOverlay 11.1.0 source: commit `297f3d4f07868f1dd221ed0b6276f77bef5e9bc5`, licensed under Apache-2.0
- Native GPU bridge adapted from the previously validated PoE1 Linux renderer in `YunaAUbot/ExileApi-Compiled-LinuxFork`, reference commit `9e66a786e1e7987bdca0f8b4d922e03d3bdb3ac9`

No public GameHelper2 derivative release should be made until its repository-wide licensing/permission status is clarified.

### Installing trusted plugins from Git

Start using `run-gamehelper2-linux.sh`, open Settings → Plugins → **Install from Git
repository**, enter a credential-free HTTPS repository URL, and accept that both
building and loading the source execute unrestricted code with your user permissions.
This is not a sandbox. Trust also covers future versions when startup updates are on.
Each source has a persistent **Check for updates at startup** checkbox, on by default.
The UI shows the installed and latest built Git commits and installation/update errors.

Prerequisites on Linux: Python 3, Git, and the native **.NET 10 SDK**, plus network
access to the repository and its NuGet feeds. Put `dotnet` on PATH or launch with
`DOTNET=/absolute/path/to/dotnet ./run-gamehelper2-linux.sh`. The Windows SDK or Git
inside Proton is not used. Direct Windows launches do not provide this installer;
use the Linux launcher for this feature. Private repositories requiring credentials
are currently unsupported; never paste a token into the URL.

New plugins are built and validated in staging, then atomically published into the
active `Plugins/<Name>/` directory only if it is absent. Select **Reload all plugins**
to discover and load them; no restart is required. Previously pending first installs
are also published by the worker when no installation or recovery history exists,
even with automatic updates disabled.

The launcher activates staged updates before starting GameHelper, then runs the
native worker beside Proton. Downloads and compilation never run on the render
thread or delay startup. Updates downloaded during a session activate at the next
launcher start, even if “Reload all plugins” has queued the old assembly for unloading. Reload never
applies staged updates or replaces loaded DLLs.
Startup activation has a 20-second deadline and refuses to launch if recovery cannot
complete. Each Git/build subprocess has a 180-second timeout; worker shutdown kills
its current subprocess group. Sources are limited to 32, and each checked tree to
30,000 files / 1 GiB; these are operational limits, not isolation from trusted code.

Supported repositories contain exactly one non-test `.csproj` (at root or nested).
Ambiguous projects, symlinks, unsafe names, and existing unmanaged plugin directories
are rejected. The public CampaignHelper and NinjaPricer project layouts use a host project reference; in the temporary checkout
this is replaced with managed host and third-party references from the installed
`GameHelper.deps.json`. Framework runtime packs and native DLLs are excluded;
the SDK supplies framework reference assemblies. NuGet runtime dependencies are
copied into the staged output, with host-provided DLLs removed. Their host validation
and copy-to-host targets are removed, and normal output assets are staged. The build
must contain exactly one sealed class directly deriving from `PCore<TSettings>`;
metadata is checked without executing the assembly. This cannot guarantee runtime
compatibility or successful plugin initialization; verify in the real GUI.

`https://github.com/YunaAUbot/campaignhelper2` is a supported public source URL.
Adding `https://github.com/YunaAUbot/GameHelper2-NinjaPricer` to a package containing
bundled NinjaPricer reports a conflict and never adopts or modifies that directory.
Private sources need a separate future credential workflow; this installer does not
store or request credentials.

State lives beside the executable in `PluginSources/`: `sources.json` holds accepted
URLs and update preferences, `pending/<source-id>/` holds complete staged outputs,
and `previous-<source-id>/` retains the previous activated directory for manual
recovery while the application is closed. `backup-<source-id>/` is an interrupted
activation journal and is rolled back on the next launch. Activation or recovery
errors (including failure to remove the previous copy or rename the backup) refuse
launch. Startup reconciles the installed version from the active ownership marker
before allowing discovery, including after an interrupted rollback. Existing `config/` and
`configs/` contents survive updates, as do root files named `settings`, `config`,
`preferences`, or `options` with a `.json`, `.ini`, `.cfg`, `.toml`, `.yaml`, `.yml`,
or `.xml` extension (case-insensitive). Existing configuration overrides shipped
defaults. Other files follow the new build; obsolete code and assets are not carried
forward. Plugins should store other user state under `config/` or `configs/`.
Download, compiler, validation and activation
failures retain the existing plugin or its recovery backup; activation failures block
launch until recovery succeeds. Runtime load/initialization failures require
manual recovery from the retained previous directory; automatic runtime rollback
is not implemented. Build/Git output is deliberately discarded to avoid recording
secrets printed by external tools; the UI reports a generic failed-step error.

Local verification: `DOTNET=/path/to/dotnet python3 tests/test_git_plugin_pipeline.py`
uses a generated local Git repository, the actual SDK compiler, metadata validation,
version updates, offline and build failures, rollback, config retention, ownership
conflicts (including a directory appearing at publication), ambiguous projects,
timeout, and worker shutdown. Injected finalization and registry-write failures check
startup refusal, rollback/version reconciliation, and configuration preservation on
subsequent starts. A real launcher/worker subprocess check verifies that failed
finalization starts neither Proton nor the background worker. Its reload probe compiles the production reload and
discovery methods with lifecycle stubs and real collectible assembly loads; it keeps
old contexts alive to verify that reload cannot activate an update. It does not test
the real render loop, plugin initialization, or Proton. It does not execute
third-party repository code.

`DOTNET=/path/to/dotnet python3 tests/test_git_plugin_blockers.py -v` additionally
requires `dist/GameHelper2-linux` and an inspected local public CampaignHelper checkout selected with
`GH2_CAMPAIGN_SOURCE=/absolute/path/to/checkout` (that case skips if unset). It builds that checkout against a copy of
the self-contained host, checks framework exclusion and staged seed data, invokes a
NuGet-dependent fixture, and verifies root configuration retention and obsolete asset
removal. Set `GH2_BLOCKER_ARTIFACTS` to retain CampaignHelper output and reference proof.

Daily upstream sync fails before pushing if a merge changes protected Git installer,
UI integration, launcher, renderer or regression-test paths, including clean deletions.
Those shared-file changes require review and local integration; unrelated upstream
changes and the upstream-owned LootValue snapshot keep their existing sync behavior.
