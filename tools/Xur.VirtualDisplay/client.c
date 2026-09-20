// SPDX-License-Identifier: MIT
// Keep one virtual output alive inside KWin's normal DRM/input session.
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <stdlib.h>
#include <wayland-client.h>
#include <systemd/sd-daemon.h>
#include "screencast-client.h"
static struct zkde_screencast_unstable_v1 *manager;
static void global(void *data, struct wl_registry *registry, uint32_t name, const char *interface, uint32_t version) {
    (void)data;
    if (!strcmp(interface, "zkde_screencast_unstable_v1") && version >= 2)
        manager = wl_registry_bind(registry, name, &zkde_screencast_unstable_v1_interface, 2);
}
static void removed(void *data, struct wl_registry *registry, uint32_t name) { (void)data; (void)registry; (void)name; }
static void closed(void *data, struct zkde_screencast_stream_unstable_v1 *stream) {
    (void)data; (void)stream; fputs("KWin closed the virtual output\n", stderr); exit(1);
}
static void created(void *data, struct zkde_screencast_stream_unstable_v1 *stream, uint32_t node) {
    (void)data; (void)stream; (void)node;
    sd_notify(0, "READY=1\nSTATUS=Virtual monitor ready");
    puts("Xur virtual monitor ready"); fflush(stdout);
}
static void failed(void *data, struct zkde_screencast_stream_unstable_v1 *stream, const char *error) {
    (void)data; (void)stream; fprintf(stderr, "Could not create virtual monitor: %s\n", error); exit(1);
}
int main(void) {
    struct wl_display *display = wl_display_connect(NULL);
    if (!display) { fputs("Could not connect to workstation Wayland display\n", stderr); return 1; }
    struct wl_registry *registry = wl_display_get_registry(display);
    const struct wl_registry_listener registry_listener = {global, removed};
    wl_registry_add_listener(registry, &registry_listener, NULL);
    if (wl_display_roundtrip(display) < 0 || !manager) { fputs("KWin virtual monitor protocol unavailable\n", stderr); return 1; }
    struct zkde_screencast_stream_unstable_v1 *stream = zkde_screencast_unstable_v1_stream_virtual_output(manager, "Xur-Stream", 1920, 1080, wl_fixed_from_int(1), ZKDE_SCREENCAST_UNSTABLE_V1_POINTER_HIDDEN);
    const struct zkde_screencast_stream_unstable_v1_listener listener = {.closed=closed, .created=created, .failed=failed};
    zkde_screencast_stream_unstable_v1_add_listener(stream, &listener, NULL);
    while (wl_display_dispatch(display) >= 0) {}
    fputs("Workstation Wayland connection closed\n", stderr);
    return 1;
}
