// Read the shared menu through the root-private control socket. No frame or
// bootstrap credential is retained on disk or written to the system journal.
#include <curl/curl.h>
#include <sys/ioctl.h>
#include <unistd.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <signal.h>
static volatile sig_atomic_t running=1;
static void stop(int sig){(void)sig;running=0;}
struct frame {char data[524288];size_t length;};
static size_t receive(char *ptr,size_t size,size_t count,void *user){
    struct frame *f=user;size_t n=size*count;
    if(n>sizeof(f->data)-f->length)return 0;
    memcpy(f->data+f->length,ptr,n);f->length+=n;return n;
}
/* Xur frames contain absolute row positions. Send only changed rows so a
 * cursor movement does not repaint the entire 4K framebuffer. */
static size_t next_row(const struct frame *f,size_t offset){
    for(size_t i=offset;i+5<f->length;i++){
        if(f->data[i]!='\033'||f->data[i+1]!='[')continue;
        size_t j=i+2;
        while(j<f->length&&f->data[j]>='0'&&f->data[j]<='9')j++;
        if(j>i+2&&j+2<f->length&&f->data[j]==';'&&f->data[j+1]=='1'&&f->data[j+2]=='H')return i;
    }
    return f->length;
}
static int draw(const struct frame *current,const struct frame *previous){
    if(!previous->length)return fwrite(current->data,1,current->length,stdout)==current->length;
    size_t a=next_row(current,0),b=next_row(previous,0);
    while(a<current->length){
        size_t an=next_row(current,a+1),bn=next_row(previous,b<previous->length?b+1:b);
        size_t n=an-a;
        if(n!=bn-b||memcmp(current->data+a,previous->data+b,n))
            if(fwrite(current->data+a,1,n,stdout)!=n)return 0;
        a=an;b=bn;
    }
    return 1;
}
int main(int argc,char **argv){
    if(argc!=2)return 2;
    signal(SIGTERM,stop);signal(SIGINT,stop);
    curl_global_init(CURL_GLOBAL_DEFAULT);CURL *c=curl_easy_init();if(!c)return 1;
    curl_easy_setopt(c,CURLOPT_UNIX_SOCKET_PATH,argv[1]);curl_easy_setopt(c,CURLOPT_TIMEOUT_MS,2000L);
    curl_easy_setopt(c,CURLOPT_PROXY,"");curl_easy_setopt(c,CURLOPT_WRITEFUNCTION,receive);
    struct frame current={0},previous={0};
    struct winsize last_size={0};
    fputs("\033[?1049h\033[2J\033[?25l",stdout);fflush(stdout);
    while(running){
        struct winsize ws={0};ioctl(STDOUT_FILENO,TIOCGWINSZ,&ws);
        if(ws.ws_col!=last_size.ws_col||ws.ws_row!=last_size.ws_row)previous.length=0;
        last_size=ws;
        char url[128];snprintf(url,sizeof(url),"http://localhost/local/console-frame?columns=%u&rows=%u",ws.ws_col?ws.ws_col:100,ws.ws_row?ws.ws_row:40);
        current.length=0;curl_easy_setopt(c,CURLOPT_URL,url);curl_easy_setopt(c,CURLOPT_WRITEDATA,&current);
        CURLcode result=curl_easy_perform(c);long status=0;curl_easy_getinfo(c,CURLINFO_RESPONSE_CODE,&status);
        if(result==CURLE_OK&&status==200 && (current.length!=previous.length||memcmp(current.data,previous.data,current.length))){
            if(!draw(&current,&previous))break;
            fflush(stdout);previous=current;
        }
        // Keep the last complete QR visible during manager restart/reconnection.
        usleep(100000);
    }
    curl_easy_cleanup(c);curl_global_cleanup();return 0;
}
