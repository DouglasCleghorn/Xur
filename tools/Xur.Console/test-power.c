// Exercise the patched kmscon OSC callback with the real libtsm parser and
// mock displays. terminal-power.inc is extracted from the patched upstream
// terminal.c by the native integration check, rather than copying its logic.
#include <assert.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <unistd.h>
#include <errno.h>
#include <libtsm.h>
#include "shl/dlist.h"

enum display_dpms {DPMS_ON,DPMS_OFF,DPMS_UNKNOWN};
struct display;
struct display_ops {int (*swap)(struct display *disp);};
struct display {enum display_dpms state;int calls;bool fail,swapping;const struct display_ops *ops;void *video;};
struct screen {struct shl_dlist list;struct display *disp;int redraws;bool swapping,pending;unsigned int xur_stalled;};
struct kmscon_terminal {struct shl_dlist screens;bool xur_sleeping,xur_fault;void *session;};
static int warnings;
#define log_warning(...) ((void)++warnings)
#define log_info(...) ((void)0)
#define display_name(disp) "test"
static void redraw_screen(struct screen *scr){++scr->redraws;}
static void do_redraw_screen(struct screen *scr){++scr->redraws;}
static bool display_is_swapping(struct display *disp){return disp->swapping;}
static bool display_is_online(struct display *disp){(void)disp;return true;}
static bool video_is_awake(void *video){(void)video;return true;}
static int failed_swap(struct display *disp){(void)disp;return -EIO;}
#include "display-swap.inc"
#include "display-pageflip.inc"
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
int main(int argc,char **argv){
    assert(argc==2);
    const char *fault=argv[1];unlink(fault);
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
    assert(a.redraws==2&&b.redraws==2);
    second.state=DPMS_UNKNOWN;first.fail=true;
    feed(vte,"\033]xurDpmsOff\007");
    assert(term.xur_sleeping&&warnings==1&&second.state==DPMS_UNKNOWN&&second.calls==3);
    assert(a.redraws==3&&b.redraws==3);
    first.state=DPMS_OFF;
    feed(vte,"\033]xurDpmsOn\007");assert(first.state==DPMS_OFF);
    first.fail=false;
    feed(vte,"\033]xurDpmsOn\007");assert(first.state==DPMS_ON);
    setenv("XUR_CONSOLE_NO_DPMS","1",1);
    feed(vte,"\033]xurDpmsOff\007");
    assert(term.xur_sleeping&&first.state==DPMS_ON&&second.state==DPMS_UNKNOWN);
    unsetenv("XUR_CONSOLE_NO_DPMS");

    const struct display_ops ops={.swap=failed_swap};first.ops=&ops;
    assert(display_swap(&first)==-EIO); // A failed flip must never report success.
    a.swapping=true;first.swapping=false;
    feed(vte,"\033]xurDpmsOn\007");assert(!a.swapping);
    first.swapping=true;
    feed(vte,"\033]xurDpmsOn\007");assert(a.swapping);
    setenv("XUR_CONSOLE_FAULT",fault,1);
    for(int i=0;i<9;i++)feed(vte,"\033]xurDisplayCheck\007");
    assert(access(fault,F_OK)!=0);
    feed(vte,"\033]xurDisplayCheck\007");assert(access(fault,F_OK)==0);
    // A healthy second connector cannot hide the stalled first connector.
    display_pageflip(NULL,NULL,&b);feed(vte,"\033]xurDisplayCheck\007");
    assert(access(fault,F_OK)==0);
    display_pageflip(NULL,NULL,&a);feed(vte,"\033]xurDisplayCheck\007");
    assert(access(fault,F_OK)!=0&&a.xur_stalled==0);
    // Rejected frames can retry; successful callbacks prevent false alarms.
    a.pending=true;int redraws=a.redraws;
    for(int i=0;i<20;i++){
        feed(vte,"\033]xurDisplayCheck\007");display_pageflip(NULL,NULL,&a);
        assert(access(fault,F_OK)!=0);
    }
    assert(a.redraws>redraws);
    feed(vte,"\033]xurDpmsOff\007");a.swapping=true;
    for(int i=0;i<12;i++)feed(vte,"\033]xurDisplayCheck\007");
    assert(access(fault,F_OK)!=0&&a.xur_stalled==0);
    tsm_vte_unref(vte);tsm_screen_unref(console);
    return 0;
}
