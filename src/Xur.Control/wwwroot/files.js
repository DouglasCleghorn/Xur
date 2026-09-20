(() => {
 const root=document.querySelector('#file-explorer');if(!root)return;
 const user=document.querySelector('#file-user'),filter=document.querySelector('#file-filter'),list=document.querySelector('#file-list'),status=document.querySelector('#file-status'),crumbs=document.querySelector('#file-breadcrumbs');
 let folder='',request,users=[];
 const url=(action,path=folder)=>'/api/files/'+action+'?'+new URLSearchParams({user:user.value,path,q:filter.value});
 const link=(text,click)=>{const a=document.createElement('button');a.type='button';a.className='quiet file-folder';a.textContent=text;a.onclick=e=>{e.preventDefault();click();};return a;};
 const size=n=>n==null?'':n<1024?n+' B':n<1048576?(n/1024).toFixed(1)+' KiB':n<1073741824?(n/1048576).toFixed(1)+' MiB':(n/1073741824).toFixed(2)+' GiB';
 async function load(path='',push=true){
  request?.abort();const current=new AbortController();request=current;folder=path;list.replaceChildren();crumbs.replaceChildren();status.textContent='Loading files…';
  if(push){const next=new URL(location.href);next.searchParams.set('user',user.value);next.searchParams.set('path',path);next.searchParams.set('q',filter.value);history.pushState(null,'',next);}
  const account=users.find(u=>u.username===user.value);
  const segments=[{name:account?.name||'Home',open:()=>{filter.value='';load('');}}];
  let parent='';for(const segment of path.split('/').filter(Boolean)){parent+=(parent?'/':'')+segment;const target=parent;segments.push({name:segment,open:()=>{filter.value='';load(target);}});}
  window.xurFileNavigation(crumbs,segments,path?()=>{filter.value='';load(path.split('/').slice(0,-1).join('/'));}:null);
  try{
   const response=await (window.xurFetch ?? window.fetch)(url('list',path),{signal:current.signal});const data=await response.json();if(!response.ok||!data.ok)throw Error(data.error||'Could not read this folder.');
   for(const item of data.entries){
    const row=document.createElement('div');row.className='file-row';row.setAttribute('role','listitem');
    const details=document.createElement('div');details.className='file-name';const target=(path?path+'/':'')+item.name;
    const title=item.kind==='folder'?link(item.name,()=>{filter.value='';load(target);}):document.createElement('strong');title.textContent=item.name;details.append(title);
    const meta=document.createElement('span');meta.className='secondary-text';meta.textContent=(item.kind==='folder'?'Folder':item.kind==='link'?'Link — open the original location':item.kind==='special'?'Special file':size(item.bytes))+' · '+new Date(item.modified*1000).toLocaleString();details.append(meta);row.append(details);
    if(item.kind==='file'){const download=document.createElement('a');download.className='button quiet';download.textContent='Download';download.href=url('download',target);row.append(download);}
    list.append(row);
   }
   status.textContent=data.truncated?'Showing 500 matches. Narrow the filter to find more.':data.entries.length?data.entries.length+' items':'No matching files in this folder.';
  }catch(e){if(e.name!=='AbortError')status.textContent=e.message;}
 }
 function restore(){const query=new URLSearchParams(location.search);user.value=users.some(u=>u.username===query.get('user'))?query.get('user'):users[0]?.username||'';filter.value=query.get('q')||'';if(user.value)load(query.get('path')||'',false);}
 document.querySelector('#file-refresh').onclick=()=>load(folder,false);
 document.querySelector('#file-controls').onsubmit=e=>{e.preventDefault();load(folder);};user.onchange=()=>{filter.value='';load('');};window.addEventListener('popstate',restore);
 (window.xurFetch ?? window.fetch)('/api/files/roots').then(async r=>{if(!r.ok)throw Error('Workstation users are unavailable.');return r.json();}).then(data=>{users=data;user.replaceChildren(...users.map(u=>new Option(u.name+' · '+u.username,u.username)));if(!users.length){status.textContent='No workstation users yet. Add one in Profiles.';return;}restore();}).catch(e=>status.textContent=e.message);
})();
