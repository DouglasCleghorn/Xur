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
