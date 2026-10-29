// Keep the upstream WebUI's requests inside Xur's authenticated, CSRF-protected route.
(()=>{
 const token=document.querySelector('meta[name="xur-audio-csrf"]').content;
 const prefix=location.pathname.endsWith('/')?location.pathname:location.pathname+'/';
 const original=window.fetch.bind(window);
 window.fetch=(input,init)=>{
  const url=new URL(input instanceof Request?input.url:input,location.href);
  const method=(init?.method||(input instanceof Request?input.method:'GET')).toUpperCase();
  if(url.origin===location.origin&&url.pathname.startsWith(prefix)&&!['GET','HEAD','OPTIONS'].includes(method)){
   const headers=new Headers(init?.headers||(input instanceof Request?input.headers:undefined));
   headers.set('RequestVerificationToken',token);init={...init,headers};
  }
  return original(input,init);
 };
})();
