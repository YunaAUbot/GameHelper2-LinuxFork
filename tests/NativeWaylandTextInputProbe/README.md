# Synthetic native Wayland focus regression

Runs only in a private nested Xvfb -> KWin -> Xwayland/Wayland session. All synthetic mouse/key events target the **outer Xvfb**, so KWin mediates delivery. The native Wayland fullscreen stand-in is a solid public test surface, with fixture-only key logging. No real desktop or game input and no clipboard access.

Requires .NET 10 SDK, gcc, Xvfb/xvfb-run, xauth, dbus-run-session, kwin_wayland, Xwayland, wayland-scanner, libwayland development headers, and stable xdg-shell protocol XML.

```bash
GH_LIFECYCLE=focusloss tests/test_linux_wayland_text_input.sh
```

Cases: `f12`, `focusloss` (includes fresh-click resume), `disconnect`, `shutdown`, `close`. All start with an actual native click and verify ordinary `abc` reaches real ImGui InputText. They check ungrab plus restoration by requiring the native Wayland stand-in to receive a subsequent public `d` key. Close additionally validates WM_DELETE_WINDOW rather than permitting the window manager to kill the X client.

Set TEXT_NATIVE and TEXT_RENDERER to absolute artifact paths for deployed-versus-corrected comparisons. WAYLAND_FIXTURE_RESULTS selects the output log directory; DOTNET selects the SDK executable. By default the test compiles the current native source and uses the managed renderer from the whole-solution Release build. Build/runtime/Xauthority intermediates are temporary and private.

The deployed native baseline is RED with native Wayland focus even though InputText is active and WantTextInput is true; original X11-only Xvfb fixtures miss this distinction.
