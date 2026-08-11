#include <X11/Xlib.h>
#include <X11/keysym.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

extern int XTestFakeKeyEvent(Display *, unsigned int, int, unsigned long);

int main(int argc, char **argv) {
    if (argc < 2 || argc > 3) return 2;
    Display *display = XOpenDisplay(NULL);
    if (!display) return 3;
    if (strcmp(argv[1], "--grab-keyboard") == 0) {
        int result = XGrabKeyboard(display, DefaultRootWindow(display), False,
                                   GrabModeAsync, GrabModeAsync, CurrentTime);
        if (result == GrabSuccess) XUngrabKeyboard(display, CurrentTime);
        XCloseDisplay(display);
        return result == GrabSuccess ? 0 : 9;
    }
    KeySym symbol = XStringToKeysym(argv[1]);
    KeyCode code = XKeysymToKeycode(display, symbol);
    if (symbol == NoSymbol || code == 0) return 6;
    if (!XTestFakeKeyEvent(display, code, True, CurrentTime)) return 7;
    XSync(display, False);
    if (argc == 3 && strcmp(argv[2], "down") == 0) { XCloseDisplay(display); return 0; }
    usleep(50000);
    if (!XTestFakeKeyEvent(display, code, False, CurrentTime)) return 8;
    XSync(display, False);
    XCloseDisplay(display);
    return 0;
}
