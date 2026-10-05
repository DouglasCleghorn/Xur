"""Narrow integration changes to pinned kmscon; the renderer remains upstream."""
from pathlib import Path
root=Path(__file__).resolve().parent/'kmscon'
p=root/'src/seat.c';s=p.read_text();needle='struct kmscon_seat *seat = data;\n\n\tswitch (type) {'
assert s.count(needle)==2
s=s.replace(needle,'''struct kmscon_seat *seat = data;
    /* Xur owns one exact DRM card per child. Keyboard input remains on the
     * shared local VT; do not duplicate or steal workstation input events. */
    const char *card = getenv("XUR_CONSOLE_CARD");
    if (card && (type != UTERM_MONITOR_DRM || strcmp(card, node))) return;

\tswitch (type) {''',1);p.write_text(s)
p=root/'src/shl/module.c';s=p.read_text();s=s.replace('log_debug("loading global modules from %s", BUILD_MODULE_DIR);','''const char *module_dir = getenv("XUR_CONSOLE_MODULES");
    if (!module_dir) module_dir = BUILD_MODULE_DIR;
    log_debug("loading global modules from %s", module_dir);''');a=s.index('void kmscon_load_modules(void)');s=s[:a]+s[a:].replace('opendir(BUILD_MODULE_DIR)','opendir(module_dir)').replace(', BUILD_MODULE_DIR',', module_dir');p.write_text(s)

# The child has no keyboard devices, so its own inactivity timer cannot wake it.
# Root-private frame responses drive power transitions and renderer checks.
p=root/'src/terminal.c';s=p.read_text()
needle='\tbool swapping;'
assert s.count(needle)==1
s=s.replace(needle,needle+'\n\tunsigned int xur_stalled;')
needle='\tstruct kmscon_asciinema *asciinema;'
assert s.count(needle)==1
s=s.replace(needle,needle+'\n\tbool xur_sleeping;\n\tbool xur_fault;')
needle='\t\t\tlog_warning("cannot swap display [%s] %d", display_name(scr->disp), ret);\n\t\treturn;'
assert s.count(needle)==1
s=s.replace(needle,needle.replace('\n\t\treturn;', '\n\t\tscr->pending = true;\n\t\treturn;'))
needle='\tscr->swapping = false;\n\tif (scr->pending)'
assert s.count(needle)==1
s=s.replace(needle,'\tscr->xur_stalled = 0;\n'+needle)
needle='\tif (!scr->term->awake || !kmscon_session_get_foreground(scr->term->session))'
assert s.count(needle)==1
s=s.replace(needle,'''\t/* Keep parsing the PTY while asleep, without page-flipping an off display.
     * Unsupported DPMS retains the black frame as a fallback. */
\tif (display_get_dpms(scr->disp) == DPMS_OFF)
\t\treturn;

'''+needle)
needle='static void osc_event(struct tsm_vte *vte, const char *osc_string, size_t osc_len, void *data)'
assert s.count(needle)==1
s=s.replace(needle,'''/* A successful page flip clears each screen's stall counter. Checking every
 * second detects lost callbacks even when sysfs still says enabled/DPMS On.
 * The agent owns bounded restarts; this file contains no terminal content. */
static void xur_display_check(struct kmscon_terminal *term)
{
    struct shl_dlist *iter;
    struct screen *scr;
    const char *path = getenv("XUR_CONSOLE_FAULT");
    bool failed = false;
    FILE *file;

    if (!path)
        return;
    shl_dlist_for_each(iter, &term->screens) {
        scr = shl_dlist_entry(iter, struct screen, list);
        if (!term->xur_sleeping && (scr->swapping || scr->pending)) {
            if (scr->xur_stalled < 10)
                ++scr->xur_stalled;
            if (scr->xur_stalled >= 10)
                failed = true;
            /* Retry a rejected frame, but never reuse a pending scanout buffer. */
            if (!scr->swapping)
                redraw_screen(scr);
        } else {
            scr->xur_stalled = 0;
        }
    }
    if (failed && !term->xur_fault) {
        file = fopen(path, "w");
        if (file) {
            fputs("Display frame updates stalled.\\n", file);
            fclose(file);
            term->xur_fault = true;
            log_warning("Xur display stalled; requesting bounded console recovery");
        }
    } else if (!failed && term->xur_fault) {
        unlink(path);
        term->xur_fault = false;
    }
}

static void xur_display_power(struct kmscon_terminal *term, bool sleeping)
{
    struct shl_dlist *iter;
    struct screen *scr;
    int ret;

    term->xur_sleeping = sleeping;
    shl_dlist_for_each(iter, &term->screens) {
        scr = shl_dlist_entry(iter, struct screen, list);
        /* Paint the black fallback before requesting standby. */
        if (sleeping)
            redraw_screen(scr);
        ret = display_set_dpms(scr->disp,
                sleeping && !getenv("XUR_CONSOLE_NO_DPMS") ? DPMS_OFF : DPMS_ON);
        if (ret)
            log_warning("cannot set Xur display power for [%s]: %d",
                        display_name(scr->disp), ret);
        if (!sleeping) {
            /* A rejected page flip has no completion event to wait for. */
            scr->swapping = display_is_swapping(scr->disp);
            redraw_screen(scr);
        }
    }
}

'''+needle)
needle='\tif (strcmp(osc_string, "setBackground") == 0) {'
assert s.count(needle)==1
s=s.replace(needle,'''\tif (getenv("XUR_CONSOLE_CARD") && strcmp(osc_string, "xurDpmsOff") == 0) {
        xur_display_power(term, true);
    } else if (getenv("XUR_CONSOLE_CARD") && strcmp(osc_string, "xurDpmsOn") == 0) {
        xur_display_power(term, false);
    } else if (getenv("XUR_CONSOLE_CARD") && strcmp(osc_string, "xurDisplayCheck") == 0) {
        xur_display_check(term);
    } else if (strcmp(osc_string, "setBackground") == 0) {''')
needle='\t\tif (scr->disp == disp)\n\t\t\treturn 0;'
assert s.count(needle)==1
s=s.replace(needle,'''\t\tif (scr->disp == disp) {
            if (term->xur_sleeping)
                display_set_dpms(disp, getenv("XUR_CONSOLE_NO_DPMS") ? DPMS_ON : DPMS_OFF);
            return 0;
        }''')
needle='\tdisplay_ref(scr->disp);\n\tdo_redraw_screen(scr);'
assert s.count(needle)==1
s=s.replace(needle,'''\tdisplay_ref(scr->disp);
    /* Newly connected outputs inherit the shared sleep state. */
\tif (term->xur_sleeping)
\t\tdisplay_set_dpms(scr->disp, getenv("XUR_CONSOLE_NO_DPMS") ? DPMS_ON : DPMS_OFF);
\tdo_redraw_screen(scr);''')
p.write_text(s)
(root.parent/'terminal-power.inc').write_text(s[s.index('static void xur_display_check('):s.index('static void bell_event(')])
(root.parent/'display-pageflip.inc').write_text(s[s.index('static void display_pageflip('):s.index('static void blink_event(')])

# Upstream drops the backend error and marks failed flips as pending forever.
p=root/'src/video/video.c';s=p.read_text()
needle='\tif (disp->ops->swap)\n\t\tdisp->ops->swap(disp);'
assert s.count(needle)==1
s=s.replace(needle,'\tif (disp->ops->swap)\n\t\treturn disp->ops->swap(disp);')
p.write_text(s)
(root.parent/'display-swap.inc').write_text(s[s.index('int display_swap('):s.index('SHL_EXPORT\nbool display_is_swapping(')])
