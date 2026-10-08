(() => {
 const form=document.querySelector('form[action="/auth/login"]');
 if(form){
  let pending=false,ready=false;
  async function refreshTokens(){
   const response=await fetch('/auth/login-token',{credentials:'same-origin',cache:'no-store',redirect:'error',headers:{Accept:'application/json'}});
   if(!response.ok)throw Error('Could not refresh the sign-in form. Try again.');
   const tokens=await response.json(),input=form.elements.namedItem(tokens.fieldName);
   if(!tokens.requestToken||!input)throw Error('Could not refresh the sign-in form. Reload the page and try again.');
   input.value=tokens.requestToken;
   return tokens;
  }
  window.addEventListener('pageshow',async()=>{
   pending=false;ready=false;
   const button=form.querySelector('button[type="submit"]');
   if(button)button.disabled=false;
   // An older Strict cookie can be withheld on an external link's first
   // navigation. A same-origin check recovers it, including restored tabs.
   try{if((await refreshTokens()).signedIn&&!pending)location.replace('/');}catch{}
  });
  form.addEventListener('submit',async event=>{
   if(ready)return;
   event.preventDefault();
   if(pending)return;
   pending=true;
   const button=event.submitter??form.querySelector('button[type="submit"]');
   const error=document.querySelector('#login-error');
   if(button)button.disabled=true;
   if(error)error.hidden=true;
   try{
    // A restored tab or another tab signing in/out may have changed the
    // cookie or identity since this form was rendered. Refresh just the CSRF
    // token before authentication and password-manager detection.
    if((await refreshTokens()).signedIn){location.replace('/');return;}
    let credential;
    if(form.elements.namedItem('password') && typeof window.PasswordCredential==='function' && navigator.credentials?.store){
     try{credential=new PasswordCredential(form);}catch{}
    }
    if(credential){
     // Ask the browser to save only credentials the server has accepted. Keep
     // them in this form's memory; never use localStorage or URL parameters.
     const login=await fetch(form.action,{method:'POST',body:new FormData(form),credentials:'same-origin',cache:'no-store',redirect:'error',headers:{Accept:'application/json'}});
     const result=await login.json();
     if(!login.ok||!result.signedIn){
      if(error){error.textContent=result.error??'Could not sign in. Try again.';error.hidden=false;}
      return;
     }
     // Saving is optional: a declined prompt or disabled password manager must
     // never turn successful authentication into a failed sign-in.
     try{await navigator.credentials.store(credential);}catch{}
     location.assign(result.redirectTo);
     return;
    }
    ready=true;
    if(button)button.disabled=false;
    form.requestSubmit(button);
   }catch{
    ready=false;
    if(error){error.textContent='Could not connect to Xur. Your details are still here; try signing in again.';error.hidden=false;}
   }finally{
    pending=false;
    if(button)button.disabled=false;
   }
  });
 }
 const input=document.querySelector('#code');
 const fragment=new URLSearchParams(location.hash.slice(1)),code=fragment.get('code');
 if(fragment.has('code'))history.replaceState(null,'',location.pathname+location.search);
 if(!input)return;
 if(code&&/^[0-9A-Za-z]{3}-?[0-9A-Za-z]{3}$/.test(code)){const token=code.replace('-','').toUpperCase();input.value=token.slice(0,3)+'-'+token.slice(3);}

 const clean=s=>s.toUpperCase().replace(/[^0-9A-Z]/g,'').slice(0,6);
 input.addEventListener('beforeinput',event=>{
  if(event.inputType==='deleteContentBackward' && input.selectionStart===4 && input.selectionEnd===4 && input.value[3]==='-')
   input.setSelectionRange(2,4);
 });
 input.addEventListener('input',()=>{
  const caret=input.selectionStart??input.value.length;
  const count=clean(input.value.slice(0,caret)).length;
  const value=clean(input.value);
  input.value=value.slice(0,3)+(value.length>=3?'-'+value.slice(3):'');
  const position=count+(count>=3?1:0);
  input.setSelectionRange(position,position);
 });
})();
