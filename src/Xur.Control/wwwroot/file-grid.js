(()=>{
 const icons={folder:'M3 7h7l2-3h8v15H3z',file:'M6 3h8l4 4v14H6z M14 3v5h4',zip:'M6 3h12v18H6z M11 3v3h2v3h-2v3h2v4h-2v3',link:'M9 15l6-6 M8 12l-2 2a4 4 0 005 5l3-3 M16 12l2-2a4 4 0 00-5-5l-3 3',mount:'M4 5h16v14H4z M7 15h1 M11 15h6',download:'M12 3v12 M7 10l5 5 5-5 M4 16v5h16v-5',copy:'M9 8h11v13H9z M15 8V3H4v13h5',move:'M4 17v3h3L20 7l-3-3z M14 7l3 3',delete:'M3 6h18 M9 6V3h6v3 M6 6l1 15h10l1-15 M10 10v7 M14 10v7',refresh:'M20 7v5h-5 M4 17v-5h5 M5 8a7 7 0 0112-3l3 7 M4 12l3 7a7 7 0 0012-3',more:'M12 4v2 M12 11v2 M12 18v2'};
 function icon(name){const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox','0 0 24 24');svg.setAttribute('fill','none');svg.setAttribute('stroke','currentColor');svg.setAttribute('stroke-width','1.7');svg.setAttribute('stroke-linecap','round');svg.setAttribute('stroke-linejoin','round');svg.setAttribute('aria-hidden','true');svg.classList.add('file-icon');if(name==='folder')svg.classList.add('folder-icon');const p=document.createElementNS(svg.namespaceURI,'path');p.setAttribute('d',icons[name==='root'?'mount':name]||icons.file);svg.append(p);return svg;}
 function element(tag,text,cls){const e=document.createElement(tag);if(text)e.textContent=text;if(cls)e.className=cls;return e;}
 function button(text,action,cls='quiet'){const b=element('button',text,cls);b.type='button';b.onclick=action;return b;}
 const size=n=>n==null?'Not measured':n<1024?n+' B':n<1048576?(n/1024).toFixed(1)+' KiB':n<1073741824?(n/1048576).toFixed(1)+' MiB':(n/1073741824).toFixed(2)+' GiB';
 async function get(url,options={}){const r=await (window.xurFetch??fetch)(url,options);const data=await r.json();if(!r.ok||data.ok===false)throw Error(data.error||'Request failed.');return data;}
 async function copy(value){if(navigator.clipboard?.writeText)return navigator.clipboard.writeText(value);const input=element('textarea');input.value=value;input.style.position='fixed';input.style.opacity='0';document.body.append(input);input.select();const ok=document.execCommand('copy');input.remove();if(!ok)throw Error('Clipboard unavailable in this browser.');}
 window.xurFilesGrid=async function(storage){
  const prefix=storage?'usage':'file',root=document.querySelector(storage?'#usage-explorer':'#file-explorer');if(!root)return;
  const host=root.querySelector('#'+prefix+'-list'),status=root.querySelector('#'+prefix+'-status'),search=root.querySelector('#'+prefix+'-filter'),nav=root.querySelector(storage?'#usage-path':'#file-breadcrumbs'),user=root.querySelector('#file-user');
  const total=root.querySelector('#usage-total');
  let find;let roots=[],selected=null,path='',version=0,rows=[],request,sizeTimer,sizeBusy=false,menu=null,truncated=false,folderSize=null,refreshSizes=false;
  const endpoint=storage?'/api/storage/files':'/api/files';
  const url=(action,target=path,extra={},location=selected)=>endpoint+'/'+action+'?'+new URLSearchParams({[storage?'id':'user']:location?.[storage?'id':'username']||'',path:target,...extra});
  function folderUrl(location,folder){const u=new URL(window.location.href);u.searchParams.set('tab',storage?'storage':'workstation');for(const key of storage?['mount','folder']:['user','path'])u.searchParams.delete(key);if(location){u.searchParams.set(storage?'mount':'user',location[storage?'id':'username']);u.searchParams.set(storage?'folder':'path',folder);}return u.href;}
  const folderLink=(name,location,folder,cls)=>window.xurFileLink(name,folderUrl(location,folder),e=>open(location,folder,true,false,e.detail===0),cls);
  const target=row=>path?path+'/'+row.name:row.name;
  const absolute=relative=>(selected?.[storage?'path':'home']||'/home/'+selected?.username).replace(/\/$/,'')+(relative?'/'+relative:'')||'/';
  const report=()=>{if(find)find.hidden=!truncated;if(!request)status.textContent=grid.getDisplayedRowCount()+' of '+rows.length+' items'+(truncated?' · First 500 entries; larger items may be omitted. Use “Find name on server” to narrow this folder.':'');};
  const measured=d=>d.error?'Unavailable':d.bytes==null?(d.scanning?'Scanning…':'Not measured'):(d.partial?'≥ ':'')+size(d.bytes)+(d.scanning?' …':'');
  function reportTotal(){if(total)total.textContent=selected?'Allocated in this folder: '+(folderSize?measured(folderSize):'Scanning…')+(folderSize?.partial?' · Incomplete: scan limits, unreadable entries or nested mounts.':'')+(folderSize?.error?' · '+folderSize.error:''):'';}
  function updateSizes(){grid.refreshClientSideRowModel('sort');grid.refreshCells({columns:['bytes','usage'],force:true});reportTotal();}
  const up=(focus=false)=>{if(selected&&(path||storage))open(path?selected:null,path.split('/').slice(0,-1).join('/'),true,false,focus);};
  const grid=agGrid.createGrid(host,{theme:agGrid.themeQuartz.withParams({backgroundColor:'#20252d',foregroundColor:'#edf1f7',headerBackgroundColor:'#282f39',borderColor:'#373f4b',accentColor:'#99c5ff',fontFamily:'IBM Plex Sans, sans-serif',fontSize:14,colorScheme:'dark',spacing:7}),rowData:[],getRowId:p=>p.data.name,defaultColDef:{sortable:true,filter:true,resizable:true,minWidth:110},rowHeight:46,headerHeight:42,suppressCellFocus:false,animateRows:false,overlayNoRowsTemplate:'<span>No matching items</span>',columnDefs:[
   {field:'name',headerName:'Name',flex:2,minWidth:storage?140:200,sort:storage?null:'asc',suppressKeyboardEvent:p=>p.event.key==='Enter'&&!!p.event.target.closest('a'),cellRenderer:p=>{const d=p.data,wrap=element('div',null,'file-cell');wrap.append(icon(d.kind==='file'&&/\.zip$/i.test(d.name)?'zip':d.kind));const entry=d.kind==='folder'||d.kind==='root'?folderLink(d.name,d.kind==='root'?d.mount:selected,d.kind==='root'?'':target(d),'button quiet file-open'):element('span',d.name);if(d.kind==='root')entry.title=d.details;wrap.append(entry);return wrap;}},
   {field:'kind',headerName:'Type',width:130,hide:storage,getQuickFilterText:p=>p.value+(/\.zip$/i.test(p.data?.name)?' ZIP archive':''),valueFormatter:p=>({root:'Mount point',folder:'Folder',file:/\.zip$/i.test(p.data?.name)?'ZIP archive':'File',link:'Link',mount:'Nested mount',special:'Special file'})[p.value]||p.value},
   {field:'bytes',headerName:storage?'Disk usage':'Size',filter:'agNumberColumnFilter',width:storage?130:155,sort:storage?'desc':null,comparator:(a,b,nodeA,nodeB,descending)=>a==null&&b==null?0:a==null?(descending?-1:1):b==null?(descending?1:-1):a-b,getQuickFilterText:p=>String(p.value??'')+' '+size(p.value),valueFormatter:p=>p.value==null&&['link','special','mount'].includes(p.data?.kind)?'—':measured(p.data),tooltipValueGetter:p=>p.data?.error||(p.data?.partial?'Incomplete allocated size. ':'')+(p.data?.capturedAt?'Measured '+new Date(p.data.capturedAt).toLocaleString():storage||p.data?.kind==='folder'?'Allocated disk space':'File length')},
   {colId:'usage',headerName:'Relative usage',width:155,hide:!storage,sortable:false,filter:false,cellRenderer:p=>{const bar=element('div',null,'file-usage-bar'),largest=Math.max(0,...rows.map(r=>r.bytes??0));if(p.data.bytes!=null){const fill=element('span');fill.style.width=(largest>0?100*p.data.bytes/largest:0)+'%';bar.append(fill);bar.title=measured(p.data)+' · bars relative to the largest listed item';}else bar.title='Not measured';bar.setAttribute('aria-label',bar.title);return bar;}},
   {field:'modified',headerName:'Modified',filter:'agDateColumnFilter',width:190,hide:storage,valueGetter:p=>p.data.modified?new Date(p.data.modified*1000):null,valueFormatter:p=>p.value?.toLocaleString()||''},
   {field:'details',headerName:'Filesystem / capacity / free',width:310,hide:!storage,tooltipField:'details'},
   {colId:'actions',headerName:'',width:58,minWidth:58,maxWidth:58,pinned:'right',lockPinned:true,sortable:false,filter:false,resizable:false,cellRenderer:p=>{const b=button('',()=>showMenu(b,p.data));b.append(icon('more'));b.setAttribute('aria-label','Actions for '+p.data.name);b.setAttribute('aria-haspopup','menu');return b;}}
  ],onFilterChanged:()=>{report();scheduleSizes();},onBodyScrollEnd:()=>scheduleSizes(),onFirstDataRendered:()=>scheduleSizes(),onSortChanged:()=>scheduleSizes(),onCellKeyDown:p=>{const e=p.event;if(e.target.closest('input,textarea,select')||e.altKey||e.ctrlKey||e.metaKey||e.shiftKey)return;if(e.key==='ArrowLeft'){e.preventDefault();up(true);}else if((e.key==='ArrowRight'||e.key==='Enter')&&p.column.getColId()==='name'&&['root','folder'].includes(p.data.kind)&&(e.key!=='Enter'||!e.target.closest('button,a'))){e.preventDefault();open(p.data.kind==='root'?p.data.mount:selected,p.data.kind==='root'?'':target(p.data),true,false,true);}}});
  function closeMenu(){if(menu){menu.remove();menu=null;}}
  document.addEventListener('click',e=>{if(menu&&!menu.contains(e.target)&&!e.target.closest('[aria-haspopup=menu]'))closeMenu();});
  function showMenu(anchor,row){
   closeMenu();menu=element('div',null,'file-menu');menu.setAttribute('role','menu');menu.setAttribute('aria-label','Actions for '+row.name);menu.setAttribute('popover','manual');
   const add=(label,glyph,action,href)=>{const item=href?window.xurFileLink(label,href,action?()=>{closeMenu();action();}:null,''):button(label,action);if(href){if(!action)item.onclick=e=>{if(e.button===0&&!e.ctrlKey&&!e.metaKey&&!e.shiftKey&&!e.altKey)closeMenu();};}else item.onclick=()=>{closeMenu();action();};item.setAttribute('role','menuitem');item.prepend(icon(glyph));menu.append(item);return item;};
   const relative=target(row);
   if(row.kind==='folder'||row.kind==='root')add('Open folder','folder',()=>row.kind==='root'?open(row.mount,''):open(selected,relative),folderUrl(row.kind==='root'?row.mount:selected,row.kind==='root'?'':relative));
   add('Copy path','copy',()=>copy(row.kind==='root'?row.mount.path:absolute(relative)).then(()=>status.textContent='Path copied.').catch(e=>status.textContent=e.message));
   if(['folder','file'].includes(row.kind)){
    add(row.kind==='folder'?'Download ZIP':/\.zip$/i.test(row.name)?'Download original ZIP':'Download','download',null,url('download',relative));
    if(row.kind==='file'&&/\.zip$/i.test(row.name))add('Download ZIP (compressed)','zip',null,url('download',relative,{format:'compressed'}));
    if(row.kind==='folder'||/\.zip$/i.test(row.name))add('Download ZIP (uncompressed)','zip',null,url('download',relative,{format:'stored'})).title='Preserves all files in a ZIP with no compression; file contents stream directly to your download.';
   }
   if(row.kind==='folder'||row.kind==='root')add('Refresh folder size','refresh',()=>refreshSize(row));
   if(!selected?.readOnly&&['file','folder','link','special'].includes(row.kind)){
    add('Rename / move…','move',()=>change(row,'move'));
    add('Delete…','delete',()=>change(row,'delete')).classList.add('destructive');
   }
   document.body.append(menu);menu.showPopover?.();const rect=anchor.getBoundingClientRect();menu.style.left=Math.max(12,Math.min(rect.right-280,innerWidth-292))+'px';menu.style.top=Math.max(12,Math.min(rect.bottom+4,innerHeight-menu.offsetHeight-12))+'px';
   const items=[...menu.querySelectorAll('[role=menuitem]')];items[0].focus();menu.onkeydown=e=>{if(e.key==='Escape'){closeMenu();anchor.focus();}else if(e.key==='Tab'){closeMenu();anchor.focus();}else if(['ArrowDown','ArrowUp','Home','End'].includes(e.key)){e.preventDefault();const i=items.indexOf(document.activeElement);items[e.key==='Home'?0:e.key==='End'?items.length-1:(i+(e.key==='ArrowDown'?1:-1)+items.length)%items.length].focus();}};
  }
  function change(row,action){
   const relative=target(row),destinationUrl=url(action,relative),generation=version,dialog=element('dialog',null,'file-dialog'),title=element('h2',action==='delete'?'Delete '+row.name+'?':'Rename / move '+row.name);title.id='file-operation-title';dialog.setAttribute('aria-labelledby',title.id);dialog.append(title);
   const form=element('form'),error=element('p');error.setAttribute('role','alert');let input;
   if(action==='delete')form.append(element('p','Permanently delete '+absolute(relative)+(row.kind==='folder'?' and all its contents':'')+'? This cannot be undone.'));
   else{const label=element('label','Destination path');input=element('input');input.value=relative;input.required=true;input.maxLength=4096;label.append(input);form.append(label,element('p','Relative to '+absolute('')+'. Use a new name or an existing destination folder plus the name. Existing items are never overwritten.','secondary-text'));}
   const actions=element('div',null,'actions'),cancel=button('Cancel',()=>dialog.close(),'secondary'),submit=element('button',action==='delete'?'Delete':'Move');submit.type='submit';submit.prepend(icon(action==='delete'?'delete':'move'));actions.append(cancel,submit);form.append(error,actions);dialog.append(form);document.body.append(dialog);dialog.onclose=()=>dialog.remove();dialog.showModal();if(input){input.focus();input.select();}else cancel.focus();
   form.onsubmit=async e=>{e.preventDefault();submit.disabled=true;cancel.disabled=true;error.textContent='';try{await get(destinationUrl,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({confirm:action==='delete',destination:input?.value})});dialog.close();if(generation===version)await open(selected,path,false);}catch(e){error.textContent=e.message;submit.disabled=false;cancel.disabled=false;}};
  }
  function breadcrumb(){
   const segments=[];if(storage)segments.push({name:'All mount points',href:folderUrl(null,''),open:()=>open(null,'')});
   if(selected){segments.push({name:selected[storage?'path':'name'],href:folderUrl(selected,''),open:()=>open(selected,'')});let p='';for(const bit of path.split('/').filter(Boolean)){p=p?p+'/'+bit:bit;const dest=p;segments.push({name:bit,href:folderUrl(selected,dest),open:()=>open(selected,dest)});}}
   window.xurFileNavigation(nav,segments,selected&&(path||storage)?{href:folderUrl(path?selected:null,path.split('/').slice(0,-1).join('/')),open:()=>up()}:null);
   if(selected){const b=button(absolute(path),()=>copy(absolute(path)).then(()=>status.textContent='Directory path copied.').catch(e=>status.textContent=e.message),'quiet copy-path');b.prepend(icon('copy'));b.title='Copy directory path';b.setAttribute('aria-label','Copy directory path');nav.append(b);}
  }
  async function open(location,folder,push=true,refresh=false,focus=false){
   request?.abort();clearTimeout(sizeTimer);closeMenu();const current=new AbortController();request=current;const gen=++version;selected=location;path=folder;rows=[];folderSize=null;refreshSizes=refresh;reportTotal();grid.setColumnsVisible(['details'],storage&&!selected);grid.setGridOption('rowData',[]);grid.setGridOption('loading',true);breadcrumb();status.textContent='Loading files…';
   if(push){search.value='';grid.setFilterModel(null);grid.setGridOption('quickFilterText','');history.pushState(null,'',folderUrl(selected,path));window.xurFileTabs();}
   try{
    if(!selected&&storage){roots=await get('/api/storage/mounts',{signal:current.signal});if(gen!==version)return;rows=roots.map(m=>{const shared=roots.filter(r=>r.id!==m.id&&(m.uuid?r.uuid===m.uuid:r.source.split('[')[0]===m.source.split('[')[0])).map(r=>r.path);return {name:m.path,kind:'root',bytes:null,scanning:true,details:m.source+' · '+size(m.bytes)+' capacity · '+size(m.available)+' free'+(shared.length?' · Shares filesystem with '+shared.join(', '):'')+(m.readOnly?' · Read only':''),mount:m};});truncated=false;}
    else if(selected){const data=await get(url('list'),{signal:current.signal});if(gen!==version)return;rows=data.entries.map(r=>storage&&r.kind==='file'?{...r,bytes:r.allocated??null}:r);truncated=data.truncated;}
    if(gen!==version)return;request=null;grid.setGridOption('rowData',rows);report();scheduleSizes();if(focus)requestAnimationFrame(()=>{if(gen===version&&rows.length){grid.ensureIndexVisible(0);grid.setFocusedCell(0,'name');}});
   }catch(e){if(gen===version&&e.name!=='AbortError'){request=null;status.textContent=e.message;}}finally{if(gen===version)grid.setGridOption('loading',false);}
  }
  function scheduleSizes(){clearTimeout(sizeTimer);sizeTimer=setTimeout(loadSizes,200);}
  async function loadSizes(){
   if(sizeBusy||root.hidden||request)return;
   const gen=version,location=selected,folder=path,refresh=refreshSizes;refreshSizes=false;sizeBusy=true;let scanning=false;
   const measure=async(row,targetPath,mount,force=false,children=false)=>{try{const d=await get(url('size',targetPath,{...(force?{refresh:'true'}:{}),...(children?{children:'true'}:{})},mount));if(gen===version){Object.assign(row,{error:null},d);scanning||=d.scanning;}}catch(e){if(gen===version){Object.assign(row,{bytes:null,error:e.message,scanning:false,children:{}});}}};
   try{
    // One scan of the current folder primes sibling sizes and supplies its total.
    if(storage&&location){
     folderSize??={bytes:null,scanning:true};await measure(folderSize,folder,location,refresh,true);if(gen!==version)return;reportTotal();
     if(folderSize.scanning){scanning=true;return;}
     // Fetch sibling scalars together, rather than spawning a scan/request per row.
     const folders=rows.filter(r=>r.kind==='folder');
     for(const row of folders){Object.assign(row,{bytes:null,partial:true,scanning:false,error:folderSize.error||null},folderSize.children?.[row.name]);scanning||=row.scanning;}
     grid.applyTransaction({update:folders});updateSizes();return;
    }
    const candidates=rows.filter(r=>['folder','root'].includes(r.kind));
    // Storage measures every listed sibling, so sorting is not limited to the viewport.
    const visible=storage?candidates:grid.getRenderedNodes().map(n=>n.data).filter(r=>r?.kind==='folder');
    for(let i=0;i<visible.length;i+=4){await Promise.all(visible.slice(i,i+4).map(row=>measure(row,row.kind==='root'?'':folder?folder+'/'+row.name:row.name,row.mount||location,refresh&&row.kind==='root')));if(gen!==version)break;}
    if(gen===version){grid.applyTransaction({update:visible});updateSizes();}
   }
   finally{sizeBusy=false;if(gen!==version)scheduleSizes();else if(scanning)sizeTimer=setTimeout(loadSizes,1500);}
  }
  async function refreshSize(row){const gen=version;try{const data=await get(url('size',row.kind==='root'?'':target(row),{refresh:'true'},row.mount||selected));if(gen!==version)return;Object.assign(row,data);grid.applyTransaction({update:[row]});updateSizes();scheduleSizes();}catch(e){status.textContent=e.message;}}
  search.oninput=()=>{grid.setGridOption('quickFilterText',search.value);};
  root.querySelector('#'+prefix+'-clear').onclick=()=>{search.value='';grid.setFilterModel(null);grid.setGridOption('quickFilterText','');};
  root.querySelector('#'+prefix+'-refresh').onclick=()=>open(selected,path,false,storage);
  find=button('Find name on server',async()=>{if(!selected)return;const gen=version;try{const d=await get(url('list',path,{q:search.value}));if(gen!==version)return;rows=d.entries.map(r=>storage&&r.kind==='file'?{...r,bytes:r.allocated??null}:r);truncated=d.truncated;grid.setGridOption('rowData',rows);report();scheduleSizes();}catch(e){status.textContent=e.message;}},'quiet');find.hidden=true;find.title='Search file names beyond the first 500 entries';root.querySelector('.file-toolbar').append(find);
  new MutationObserver(()=>{if(!root.hidden){grid.refreshHeader();scheduleSizes();}}).observe(root,{attributes:true,attributeFilter:['hidden']});
  async function restore(){const q=new URLSearchParams(location.search);selected=roots.find(r=>r[storage?'id':'username']===q.get(storage?'mount':'user'))||(storage?null:roots[0]);if(user)user.value=selected?.username||'';await open(selected,q.get(storage?'folder':'path')||'',false);}
  window.addEventListener('popstate',restore);
  try{roots=await get(storage?'/api/storage/mounts':'/api/files/roots');if(user){user.replaceChildren(...roots.map(u=>new Option(u.name+' · '+u.username,u.username)));user.onchange=()=>open(roots.find(r=>r.username===user.value),'');if(!roots.length){status.textContent='No workstation users yet. Add one in Profiles.';return;}}await restore();}catch(e){status.textContent=e.message;}
 };
})();
