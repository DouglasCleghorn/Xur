(() => {
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
