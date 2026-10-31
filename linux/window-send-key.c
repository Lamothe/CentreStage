/* Send keys only with XSendEvent to a verified, visible window owned by PID.
 * No XTEST, focus change, or seat-global input. */
#include <X11/Xlib.h>
#include <X11/Xatom.h>
#include <X11/keysym.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int bad_window;
static int on_x_error(Display *display, XErrorEvent *error)
{
    (void)display;
    (void)error;
    bad_window = 1;
    return 0;
}

static int owned_visible_window(Display *d, Window w, unsigned long pid, XWindowAttributes *attr)
{
    Atom property = XInternAtom(d, "_NET_WM_PID", True), actual;
    int format;
    unsigned long count, remaining;
    unsigned char *data = NULL;
    int valid = property != None && XGetWindowAttributes(d, w, attr)
        && attr->map_state == IsViewable
        && XGetWindowProperty(d, w, property, 0, 1, False, XA_CARDINAL,
                              &actual, &format, &count, &remaining, &data) == Success
        && actual == XA_CARDINAL && format == 32 && count == 1
        && data && *(unsigned long *)data == pid;
    if (data) XFree(data);
    return valid;
}

int main(int argc, char **argv)
{
    if (argc != 5) {
        fprintf(stderr, "usage: %s <window-id> <pid> <F9|F10|F12|Escape> <down|up>\n", argv[0]);
        return 2;
    }
    char *end;
    errno = 0;
    unsigned long wid = strtoul(argv[1], &end, 0);
    if (errno || *end || !wid) return 2;
    unsigned long pid = strtoul(argv[2], &end, 10);
    if (errno || *end || !pid) return 2;
    KeySym sym = XStringToKeysym(argv[3]);
    if (sym != XK_F9 && sym != XK_F10 && sym != XK_F12 && sym != XK_Escape) {
        fprintf(stderr, "unsupported key\n");
        return 2;
    }
    int down = strcmp(argv[4], "down") == 0;
    if (!down && strcmp(argv[4], "up") != 0) return 2;
    Display *d = XOpenDisplay(NULL);
    if (!d) { fprintf(stderr, "cannot open X display\n"); return 2; }
    XSetErrorHandler(on_x_error);
    Window w = (Window)wid;
    XWindowAttributes attr;
    KeyCode code = XKeysymToKeycode(d, sym);
    /* Prevent destruction or replacement between validation and delivery. */
    XGrabServer(d);
    int valid = owned_visible_window(d, w, pid, &attr);
    XSync(d, False);
    if (!valid || bad_window || !code) {
        fprintf(stderr, "window is not a visible window owned by the requested PID\n");
        XUngrabServer(d);
        XCloseDisplay(d);
        return 2;
    }
    XKeyEvent key = {0};
    key.display = d;
    key.window = w;
    key.root = attr.root;
    key.time = CurrentTime;
    key.same_screen = True;
    key.keycode = code;
    key.type = down ? KeyPress : KeyRelease;
    int sent = XSendEvent(d, w, False, down ? KeyPressMask : KeyReleaseMask, (XEvent *)&key);
    XSync(d, False);
    XUngrabServer(d);
    XCloseDisplay(d);
    if (!sent || bad_window) { fprintf(stderr, "XSendEvent failed\n"); return 2; }
    return 0;
}
