// Never cache authenticated pages, API responses, downloads or bootstrap tokens.
self.addEventListener('install',()=>self.skipWaiting());
self.addEventListener('activate',event=>event.waitUntil(self.clients.claim()));
self.addEventListener('fetch',event=>{
 if(event.request.mode!=='navigate'||event.request.method!=='GET')return;
 event.respondWith(fetch(event.request).catch(()=>new Response('<!doctype html><html lang="en"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Xur is offline</title><body><h1>Cannot reach Xur</h1><p>Check your network or Tailscale connection, then reopen Xur.</p><a href="/">Try again</a></body></html>',{status:503,headers:{'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store'}})));
});
