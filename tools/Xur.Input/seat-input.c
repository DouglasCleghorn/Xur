/* Tag Sunshine's virtual devices before creation so udev routes their first event
 * to the correct logind seat. Loaded only into Xur's restricted Sunshine unit. */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <linux/uinput.h>
#include <linux/uhid.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>
static const char *phys(void) {
    const char *s=getenv("XUR_INPUT_PHYS");
    if (!s || strncmp(s,"xur/seat-xur-",13) || strlen(s)>100 || strspn(s,"abcdefghijklmnopqrstuvwxyz0123456789-/")!=strlen(s)) return NULL;
    return s;
}
static int device(int fd,const char *path) {struct stat a,b;return !fstat(fd,&a)&&!stat(path,&b)&&S_ISCHR(a.st_mode)&&a.st_rdev==b.st_rdev;}
int ioctl(int fd,unsigned long request,...) {
    static _Thread_local int (*next)(int,unsigned long,...);if(!next)next=dlsym(RTLD_NEXT,"ioctl");
    unsigned long arg=0;
    if(request!=UI_DEV_CREATE && request!=UI_DEV_DESTROY){va_list ap;va_start(ap,request);arg=va_arg(ap,unsigned long);va_end(ap);}
    if(request==UI_DEV_CREATE && device(fd,"/dev/uinput")) {
        const char *tag=phys();if(!tag){errno=EPERM;return -1;}
        if(next(fd,UI_SET_PHYS,tag)<0)return -1;
    }
    return next(fd,request,arg);
}
ssize_t write(int fd,const void *buffer,size_t size) {
    static _Thread_local ssize_t (*next)(int,const void*,size_t);if(!next)next=dlsym(RTLD_NEXT,"write");
    if(size==sizeof(struct uhid_event) && device(fd,"/dev/uhid")) {
        struct uhid_event event;memcpy(&event,buffer,sizeof(event));
        if(event.type==UHID_CREATE2){const char *tag=phys();if(!tag){errno=EPERM;return -1;}
            snprintf((char*)event.u.create2.phys,sizeof(event.u.create2.phys),"%s",tag);return next(fd,&event,size);}
    }
    return next(fd,buffer,size);
}
