# GameHelper2 Linux GPU build

This fork runs the Windows x64 GameHelper2 process inside the existing Path of Exile 2 Steam/Proton prefix and renders its ImGui draw lists through a native Linux GPU compositor.

## Architecture

- `run-gamehelper2-linux.sh` identifies a running PoE2 process by executable name **and** Steam app ID `2694490`.
- Only GameHelper2 is launched in a dedicated process group. The group is terminated when PoE2 exits.
- The managed renderer is based on ClickableTransparentOverlay 11.1.0 and keeps its public API.
- Under Linux, compact ImGui vertices, indices, clipping rectangles and the font atlas are sent over a loopback-only connection to `gamehelper2-gpu-overlay`.
- The native helper creates an ARGB X11/GLX window above the `Path of Exile 2` XWayland window and renders with OpenGL. It does **not** copy a complete monitor-sized pixel surface back through the CPU.
- X Shape regions keep ordinary transparent/text-only areas click-through while interactive ImGui windows receive mouse events. Keyboard input is grabbed only while ImGui requests it.

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

It excludes AutoHotKeyTrigger and PickupHelper because they synthesize input, LootValue because it contacts a third-party HTTP service, and the Windows updater/launcher. The shell launcher starts `GameHelper.exe` directly.

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
