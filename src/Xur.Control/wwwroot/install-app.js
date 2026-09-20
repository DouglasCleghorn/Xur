(()=>{
 if('serviceWorker' in navigator&&window.isSecureContext)navigator.serviceWorker.register('/sw.js').catch(()=>{});
 if(matchMedia('(display-mode: standalone)').matches||navigator.standalone)return;
 const mobile=matchMedia('(max-width: 800px) and (pointer: coarse)').matches||/Android|iPhone|iPad|iPod/.test(navigator.userAgent);if(!mobile)return;
 try{if(Date.now()-Number(localStorage.getItem('xur.install.dismissed')||0)<30*86400000)return;}catch{}
 let deferred;
 const banner=document.createElement('aside');banner.className='install-suggestion';banner.setAttribute('aria-label','Add Xur to your home screen');
 const text=document.createElement('span');text.textContent='Keep Xur handy on your home screen.';
 const action=document.createElement('button');action.type='button';action.className='secondary';action.textContent='How to add';
 const dismiss=document.createElement('button');dismiss.type='button';dismiss.className='quiet';dismiss.textContent='×';dismiss.setAttribute('aria-label','Dismiss home screen suggestion');
 banner.append(text,action,dismiss);document.body.append(banner);
 window.addEventListener('beforeinstallprompt',e=>{e.preventDefault();deferred=e;action.textContent='Install Xur';});
 window.addEventListener('appinstalled',()=>banner.remove());
 action.onclick=async()=>{if(deferred){await deferred.prompt();const result=await deferred.userChoice;if(result.outcome==='accepted')banner.remove();deferred=null;action.textContent='How to add';}else{text.textContent=/iPhone|iPad|iPod/.test(navigator.userAgent)?'Tap Share → Add to Home Screen → Add.':'Open your browser menu → Add to Home screen or Install app. Use the Tailscale HTTPS address if installation is unavailable.';action.hidden=true;}};
 dismiss.onclick=()=>{banner.remove();try{localStorage.setItem('xur.install.dismissed',String(Date.now()));}catch{}};
})();
