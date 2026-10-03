// Exercise the patched kmscon OSC callback with the real libtsm parser and
// mock displays. terminal-power.inc is extracted from the patched upstream
// terminal.c by the native integration check, rather than copying its logic.
#include <assert.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>
#include <libtsm.h>
#include "shl/dlist.h"

enum display_dpms {DPMS_ON,DPMS_OFF,DPMS_UNKNOWN};
struct display {enum display_dpms state;int calls;bool fail;};
struct screen {struct shl_dlist list;struct display *disp;int redraws;};
struct kmscon_terminal {struct shl_dlist screens;bool xur_sleeping;void *session;};
static int warnings;
#define log_warning(...) ((void)++warnings)
#define log_info(...) ((void)0)
#define display_name(disp) "test"
static void redraw_screen(struct screen *scr){++scr->redraws;}
static int display_set_dpms(struct display *disp,enum display_dpms state){
    ++disp->calls;if(disp->fail)return -1;
    if(disp->state!=DPMS_UNKNOWN)disp->state=state;
    return 0;
}
static void kmscon_session_set_background(void *session){(void)session;}
static void kmscon_session_set_foreground(void *session){(void)session;}
#include "terminal-power.inc"
static void output(struct tsm_vte *vte,const char *data,size_t len,void *user){(void)vte;(void)data;(void)len;(void)user;}
static void feed(struct tsm_vte *vte,const char *data){tsm_vte_input(vte,data,strlen(data));}
int main(void){
    struct tsm_screen *console;struct tsm_vte *vte;
    struct kmscon_terminal term={0};shl_dlist_init(&term.screens);
    struct display first={.state=DPMS_ON},second={.state=DPMS_ON};
    struct screen a={.disp=&first},b={.disp=&second};
    shl_dlist_link(&term.screens,&a.list);shl_dlist_link(&term.screens,&b.list);
    assert(!tsm_screen_new(&console,NULL,NULL));
    assert(!tsm_vte_new(&vte,console,output,NULL,NULL,NULL));
    tsm_vte_set_osc_cb(vte,osc_event,&term);
    unsetenv("XUR_CONSOLE_CARD");feed(vte,"\033]xurDpmsOff\007");
    assert(!term.xur_sleeping&&first.calls==0&&second.calls==0);
    setenv("XUR_CONSOLE_CARD","/dev/dri/card-test",1);
    feed(vte,"\033]xurDpms");feed(vte,"Off\007");
    assert(term.xur_sleeping&&first.state==DPMS_OFF&&second.state==DPMS_OFF);
    assert(a.redraws==1&&b.redraws==1);
    feed(vte,"\033[1;1Hbackground update");
    assert(term.xur_sleeping&&first.state==DPMS_OFF&&first.calls==1);
    feed(vte,"\033]xurDpmsOn\033\\");
    assert(!term.xur_sleeping&&first.state==DPMS_ON&&second.state==DPMS_ON);
    second.state=DPMS_UNKNOWN;first.fail=true;
    feed(vte,"\033]xurDpmsOff\007");
    assert(term.xur_sleeping&&warnings==1&&second.state==DPMS_UNKNOWN&&second.calls==3);
    assert(a.redraws==2&&b.redraws==2);
    tsm_vte_unref(vte);tsm_screen_unref(console);
    return 0;
}
