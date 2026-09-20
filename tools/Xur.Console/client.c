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
int main(int argc,char **argv){
    if(argc!=2)return 2;
    signal(SIGTERM,stop);signal(SIGINT,stop);
    curl_global_init(CURL_GLOBAL_DEFAULT);CURL *c=curl_easy_init();if(!c)return 1;
    curl_easy_setopt(c,CURLOPT_UNIX_SOCKET_PATH,argv[1]);curl_easy_setopt(c,CURLOPT_TIMEOUT_MS,2000L);
    curl_easy_setopt(c,CURLOPT_PROXY,"");curl_easy_setopt(c,CURLOPT_WRITEFUNCTION,receive);
    struct frame current={0},previous={0};
    fputs("\033[?1049h\033[2J\033[?25l",stdout);fflush(stdout);
    while(running){
        struct winsize ws={0};ioctl(STDOUT_FILENO,TIOCGWINSZ,&ws);
        char url[128];snprintf(url,sizeof(url),"http://localhost/local/console-frame?columns=%u&rows=%u",ws.ws_col?ws.ws_col:100,ws.ws_row?ws.ws_row:40);
        current.length=0;curl_easy_setopt(c,CURLOPT_URL,url);curl_easy_setopt(c,CURLOPT_WRITEDATA,&current);
        CURLcode result=curl_easy_perform(c);long status=0;curl_easy_getinfo(c,CURLINFO_RESPONSE_CODE,&status);
        if(result==CURLE_OK&&status==200 && (current.length!=previous.length||memcmp(current.data,previous.data,current.length))){
            if(fwrite(current.data,1,current.length,stdout)!=current.length)break;
            fflush(stdout);previous=current;
        }
        // Keep the last complete QR visible during manager restart/reconnection.
        usleep(500000);
    }
    curl_easy_cleanup(c);curl_global_cleanup();return 0;
}
