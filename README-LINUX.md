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
This is not a sandbox. Each manual build requires source trust acceptance.

The actual `Plugins/<Name>/` folders are the entire inventory. Removing a folder
removes its entry on the next background scan. No central registration is needed.
A folder with no own Git checkout is **Local**, including old bundled or manually
installed plugins. Old owner markers and registry URLs never supply a remote.
`sources.json`, legacy pending builds, backups and credential mappings are left
untouched but are not read as inventory or reinstall instructions.

Use a credential-free HTTPS or Git SSH URL to Add a repository. If its plugin folder
is absent, Add performs a fresh clone, build, validation and atomic installation.
Old staging and installation history do not prevent a fresh explicit Add. Existing
Local folders are never adopted implicitly. Existing Git folders offer Update;
Update all selects only Git-linked folders that currently exist (at most 32 per batch).
Startup preferences control metadata checks only; builds require a manual request.

New installations contain a pristine checkout at `.git-source/`, with its real
`.git` directory, remote, branch and commit history. Builds run from a disposable
copy whose project references and copy targets can be adapted to the installed host.
The retained source checkout is not modified by that adaptation. Existing folder-root
`.git` directories and `.git` worktree files are also inspected; parent repositories
are never inherited. The worker uses the checkout's upstream remote/branch, falling
back to origin/remote HEAD where no upstream is configured.

`.git-build-manifest.json` records the built commit and hashes of installed artifacts.
The UI separates **Installed binary**, **Source HEAD**, and **Pending build**. Fetching
or advancing source HEAD cannot change the installed binary revision. Revision status
compares the verified installed commit with the fetched upstream using real ancestry:
Up to date, Behind, Ahead, Diverged, or Unknown. Missing or changed build proof is
Unknown even if source HEAD is current. Source ancestry is tracked separately. Offline
checks show Unknown and retain only historical check timestamps. Rendering reads a
background folder snapshot; Git and network work run in the native worker.

Updates leave active DLLs untouched until the next launcher start. A pending reference
inside `.git-plugin.json` binds the staged build to that folder instance and its original
artifact hashes. Deleting/replacing the folder cancels its authority to activate: orphaned
staging or backups never resurrect it. Activation checks provenance, keeps user config,
and atomically swaps complete directories with Linux `renameat2(RENAME_EXCHANGE)`.
There is no interval with a missing active folder. The previous complete directory is
retained under `PluginSources/backups/<unique-token>/`; if backup finalization fails,
it remains under the same token in `pending/`. It is never automatically restored over
later user data. Legacy backups remain available for manual recovery.

User configuration in `config/`, `configs/`, or root `settings`, `config`, `preferences`,
`options` files with JSON/INI/CFG/TOML/YAML/YML/XML extensions wins over shipped defaults.
Other assets follow the new build. New installs can be loaded with **Reload all plugins**;
updates activate only after restart. The worker verifies the plugin assembly metadata
before publication and removes host-provided DLLs from plugin output.

Authentication uses the launching user's ordinary Git credential helpers, SSH agent and
SSH configuration. The optional existing GitHub CLI login uses `gh auth git-credential`.
No credential provisioning, permission changes, repository-specific source constants,
or private tokens in URLs/logs are involved. Controller token environment variables and
injected Git configuration are not forwarded. Git/build output is discarded.
Discovery and revision checks use a disposable Git context with only the checkout's
objects, refs, and literal remote/branch settings. Checkout-local command settings,
includes, hooks, helpers, fsmonitor and filters are not used; normal user credentials
and SSH configuration still apply. Checks leave the source checkout unchanged.

Release packaging strips `.git-source`, plugin Git state/provenance files and
`PluginSources` from staged runtime output. Other runtime checkouts are rejected,
including those without reflogs, so their working-tree source cannot ship accidentally.
The host source repository's own `.git` is outside this packaging boundary.

If an existing read-only deploy key needs routing, its owner must configure ordinary SSH
Host aliases with the existing IdentityFile/known_hosts and appropriate IdentitiesOnly
and host verification settings, then use that alias in the Git URL (or an owner-managed
Git URL rewrite). This is separate from plugin inventory. The worker does not consume
legacy deploy-key mappings, remove keys, or broaden repository access.

Known limitation: generic Git imports have been confirmed working in live use, but
our plugin repositories still fail import because their folder structures are not
supported by the current installer. This publication preserves the deployed behavior;
repository-layout compatibility remains unresolved.

Linux prerequisites: Python 3, Git and the native .NET 10 SDK. Put `dotnet` on PATH or
set `DOTNET=/absolute/path/to/dotnet`. Repositories need exactly one non-test `.csproj`;
ambiguous projects, symlinks and unsafe names are rejected. Builds resolve managed host
references from `GameHelper.deps.json`, excluding framework packs and native DLLs.
Trees are limited to 30,000 files / 1 GiB; subprocesses have a 180-second timeout.

Targeted local validation:

```bash
DOTNET=/path/to/dotnet python3 -m unittest discover -s tests -p 'test_git_plugin_*.py' -v
```

The folder, status, staging and reload fixtures use temporary local repositories and
real Git/.NET builds. They do not touch the gaming PC. The existing host migration helper
now targets host files only; legacy source lists and authentication files are preserved
byte-for-byte and never applied as plugin registration.
