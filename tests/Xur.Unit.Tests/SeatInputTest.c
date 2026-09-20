/* Run the real interposer against fake device syscalls; never create host input. */
#define _GNU_SOURCE
#include <assert.h>
#include <sys/stat.h>
#include <sys/sysmacros.h>
#include <linux/uinput.h>
#include <linux/uhid.h>
#include <stdarg.h>
#include <stdlib.h>
#include <string.h>
static int fake_stat(const char *path,struct stat *s){memset(s,0,sizeof(*s));s->st_mode=S_IFCHR;s->st_rdev=makedev(10,strstr(path,"uinput")?1:2);return 0;}
static int fake_fstat(int fd,struct stat *s){memset(s,0,sizeof(*s));s->st_mode=S_IFCHR;s->st_rdev=makedev(10,fd);return 0;}
static unsigned long calls[10];static int count;static char tag[128];static struct uhid_event captured;
static int backend_ioctl(int fd,unsigned long req,...){(void)fd;calls[count++]=req;if(req==UI_SET_PHYS){va_list ap;va_start(ap,req);strcpy(tag,va_arg(ap,const char*));va_end(ap);}return 0;}
static ssize_t backend_write(int fd,const void *data,size_t n){(void)fd;if(n==sizeof(captured))memcpy(&captured,data,n);return n;}
static void *fake_dlsym(void *handle,const char *name){(void)handle;return !strcmp(name,"ioctl")?(void*)backend_ioctl:(void*)backend_write;}
#define stat fake_stat
#define fstat fake_fstat
#define dlsym fake_dlsym
/* Keep struct stat's tag intact when the source invokes stat(). */
#undef stat
#define stat(path,buf) fake_stat(path,buf)
#define ioctl seat_ioctl
#define write seat_write
#include "../../tools/Xur.Input/seat-input.c"
int main(void){
 setenv("XUR_INPUT_PHYS","xur/seat-xur-0123456789ab",1);
 assert(seat_ioctl(1,UI_DEV_CREATE)==0);assert(count==2&&calls[0]==UI_SET_PHYS&&calls[1]==UI_DEV_CREATE);assert(!strcmp(tag,"xur/seat-xur-0123456789ab"));
 struct uhid_event e={.type=UHID_CREATE2};strcpy((char*)e.u.create2.phys,"old");assert(seat_write(2,&e,sizeof(e))==sizeof(e));assert(!strcmp((char*)captured.u.create2.phys,tag));assert(!strcmp((char*)e.u.create2.phys,"old"));
 setenv("XUR_INPUT_PHYS","xur/seat-xur-fedcba987654",1);assert(seat_ioctl(1,UI_DEV_CREATE)==0);assert(!strcmp(tag,"xur/seat-xur-fedcba987654"));
 unsetenv("XUR_INPUT_PHYS");assert(seat_ioctl(1,UI_DEV_CREATE)==-1&&errno==EPERM);assert(seat_write(2,&e,sizeof(e))==-1&&errno==EPERM);
 setenv("XUR_INPUT_PHYS","seat0",1);assert(seat_ioctl(1,UI_DEV_CREATE)==-1);
 /* Normal ioctl/write traffic and another device's create are unchanged. */
 assert(seat_ioctl(3,UI_DEV_CREATE)==0);assert(seat_ioctl(1,UI_DEV_DESTROY)==0);assert(seat_write(3,&e,sizeof(e))==sizeof(e));assert(!strcmp((char*)captured.u.create2.phys,"old"));
 return 0;
}
