/* Offline packaging probe: render local HTML through the real WebKit helpers.
 * Resolve the stable C API dynamically so this probe needs no SDK on the host.
 */
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>

static void (*quit_loop)(void);
static int loaded;
static void changed(void *view, int event, void *data)
{
    (void)view; (void)data;
    if (event == 3) { loaded = 1; quit_loop(); } /* WEBKIT_LOAD_FINISHED */
}
static int expired(void *data) { (void)data; quit_loop(); return 0; }
static void *symbol(void *library, const char *name)
{
    void *result = dlsym(library, name);
    if (!result) { fprintf(stderr, "Missing WebKit API: %s\n", name); exit(1); }
    return result;
}
#define API(type, name) type = symbol(library, name)
int main(void)
{
    void *library = dlopen("libwebkit2gtk-4.1.so.0", RTLD_NOW | RTLD_GLOBAL);
    if (!library) { fprintf(stderr, "%s\n", dlerror()); return 1; }
    API(int (*init)(int *, char ***), "gtk_init_check");
    API(void *(*window_new)(int), "gtk_window_new");
    API(void *(*view_new)(void), "webkit_web_view_new");
    API(void (*add)(void *, void *), "gtk_container_add");
    API(void (*show)(void *), "gtk_widget_show_all");
    API(void (*html)(void *, const char *, const char *), "webkit_web_view_load_html");
    API(unsigned long (*connect)(void *, const char *, void (*)(void), void *, void *, int), "g_signal_connect_data");
    API(unsigned int (*timeout)(unsigned int, int (*)(void *), void *), "g_timeout_add");
    API(void (*main_loop)(void), "gtk_main");
    quit_loop = symbol(library, "gtk_main_quit");
    if (!init(NULL, NULL)) return 1;
    void *window = window_new(0), *view = view_new();
    connect(view, "load-changed", (void (*)(void))changed, NULL, NULL, 0);
    add(window, view);
    show(window);
    html(view, "<html><body>Orion offline packaging verification</body></html>", NULL);
    timeout(15000, expired, NULL);
    main_loop();
    puts(loaded ? "WebKit helper rendering verified" : "WebKit rendering timed out");
    return loaded ? 0 : 1;
}
