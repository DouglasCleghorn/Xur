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
# Root-private frame responses instead drive DPMS through two explicit OSCs.
p=root/'src/terminal.c';s=p.read_text()
needle='\tstruct kmscon_asciinema *asciinema;'
assert s.count(needle)==1
s=s.replace(needle,needle+'\n\tbool xur_sleeping;')
needle='\tif (!scr->term->awake || !kmscon_session_get_foreground(scr->term->session))'
assert s.count(needle)==1
s=s.replace(needle,'''\t/* Keep parsing the PTY while asleep, without page-flipping an off display.
     * Unsupported DPMS retains the black frame as a fallback. */
\tif (display_get_dpms(scr->disp) == DPMS_OFF)
\t\treturn;

'''+needle)
needle='static void osc_event(struct tsm_vte *vte, const char *osc_string, size_t osc_len, void *data)'
assert s.count(needle)==1
s=s.replace(needle,'''static void xur_display_power(struct kmscon_terminal *term, bool sleeping)
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
        ret = display_set_dpms(scr->disp, sleeping ? DPMS_OFF : DPMS_ON);
        if (ret)
            log_warning("cannot set Xur display power for [%s]: %d",
                        display_name(scr->disp), ret);
    }
}

'''+needle)
needle='\tif (strcmp(osc_string, "setBackground") == 0) {'
assert s.count(needle)==1
s=s.replace(needle,'''\tif (getenv("XUR_CONSOLE_CARD") && strcmp(osc_string, "xurDpmsOff") == 0) {
        xur_display_power(term, true);
    } else if (getenv("XUR_CONSOLE_CARD") && strcmp(osc_string, "xurDpmsOn") == 0) {
        xur_display_power(term, false);
    } else if (strcmp(osc_string, "setBackground") == 0) {''')
needle='\t\tif (scr->disp == disp)\n\t\t\treturn 0;'
assert s.count(needle)==1
s=s.replace(needle,'''\t\tif (scr->disp == disp) {
            if (term->xur_sleeping)
                display_set_dpms(disp, DPMS_OFF);
            return 0;
        }''')
needle='\tdisplay_ref(scr->disp);\n\tdo_redraw_screen(scr);'
assert s.count(needle)==1
s=s.replace(needle,'''\tdisplay_ref(scr->disp);
    /* Newly connected outputs inherit the shared sleep state. */
\tif (term->xur_sleeping)
\t\tdisplay_set_dpms(scr->disp, DPMS_OFF);
\tdo_redraw_screen(scr);''')
p.write_text(s)
(root.parent/'terminal-power.inc').write_text(s[s.index('static void xur_display_power('):s.index('static void bell_event(')])
