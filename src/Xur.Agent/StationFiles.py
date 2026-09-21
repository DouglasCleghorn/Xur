"""Descriptor-relative file operations; workstation calls run as the owning user.
Archive contents use 64 KiB buffers. Links and nested mounts are never followed.
"""
import ctypes,heapq,json,os,shutil,stat,sys,time,zipfile

def emit(value):
    sys.stdout.buffer.write((json.dumps(value)+'\n').encode());sys.stdout.buffer.flush()

started=False
FLAGS=os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK|os.O_CLOEXEC

def parts(path):
    if len(path)>4096 or path.startswith('/') or '\0' in path:raise ValueError('Invalid path.')
    result=path.split('/') if path else []
    if any(p in ('','.','..') for p in result):raise ValueError('Invalid path.')
    return result

class Tree:
    def __init__(self,root):
        self.fd=os.open('/',FLAGS|os.O_DIRECTORY)
        try:
            for p in parts(root.lstrip('/')):
                child=os.open(p,FLAGS|os.O_DIRECTORY,dir_fd=self.fd);os.close(self.fd);self.fd=child
            self.dev=os.fstat(self.fd).st_dev
            self.mounts=set()
            with open('/proc/self/mountinfo') as f:
                for line in f:
                    target=line.split()[4]
                    for a,b in [('\\040',' '),('\\011','\t'),('\\012','\n'),('\\134','\\')]:target=target.replace(a,b)
                    if target!=root and target.startswith(root.rstrip('/')+'/'):self.mounts.add(target)
        except BaseException:os.close(self.fd);raise
    def child(self,parent,name,directory=False):
        fd=os.open(name,FLAGS|(os.O_DIRECTORY if directory else 0),dir_fd=parent)
        st=os.fstat(fd)
        if st.st_dev!=self.dev or os.readlink('/proc/self/fd/'+str(fd)) in self.mounts:
            os.close(fd);raise ValueError('Nested mounts are not followed.')
        return fd
    def open(self,path,directory=False):
        fd=os.dup(self.fd)
        try:
            pp=parts(path)
            for i,p in enumerate(pp):
                child=self.child(fd,p,directory or i<len(pp)-1);os.close(fd);fd=child
            return fd
        except BaseException:os.close(fd);raise
    def parent(self,path):
        pp=parts(path)
        if not pp:raise ValueError('The root folder cannot be changed.')
        return self.open('/'.join(pp[:-1]),True),pp[-1]

def kind(st):
    return 'folder' if stat.S_ISDIR(st.st_mode) else 'file' if stat.S_ISREG(st.st_mode) else 'link' if stat.S_ISLNK(st.st_mode) else 'special'

def measure(tree,fd,path):
    deadline=time.monotonic()+12;visited=0;sizes={};seen=set();cache_bytes=0
    def walk(current,relative,depth):
        nonlocal visited,cache_bytes
        info=os.fstat(current);total=info.st_blocks*512;partial=False
        with os.scandir(current) as entries:
            for e in entries:
                if visited>=250000 or time.monotonic()>deadline or depth>=128:partial=True;break
                visited+=1
                try:
                    st=e.stat(follow_symlinks=False);identity=(st.st_dev,st.st_ino)
                    if identity in seen:continue
                    seen.add(identity)
                    if stat.S_ISDIR(st.st_mode):
                        child=tree.child(current,e.name,True)
                        try:amount,incomplete=walk(child,relative+'/'+e.name if relative else e.name,depth+1)
                        finally:os.close(child)
                        total+=amount;partial|=incomplete
                    elif st.st_dev==tree.dev:total+=st.st_blocks*512
                except (OSError,ValueError):partial=True
        cost=len(json.dumps(relative))+100
        if depth<=1 and (depth==0 or (len(sizes)<2000 and cache_bytes+cost<512*1024)):
            sizes[relative]={'bytes':total,'partial':partial};cache_bytes+=cost
        return total,partial
    walk(fd,path,0)
    return dict(ok=True,sizes=sizes)

class Sink:
    # Deliberately unseekable: ZIP data descriptors are written directly to stdout.
    def write(self,data):return sys.stdout.buffer.write(data)
    def flush(self):sys.stdout.buffer.flush()

def archive(tree,fd,name,compression):
    with zipfile.ZipFile(Sink(),'w',compression=compression,allowZip64=True) as z:
        def walk(current,prefix,depth=0):
            if depth>128:raise ValueError('Folder nesting is too deep.')
            z.writestr(prefix+'/',b'')
            with os.scandir(current) as entries:
                for e in entries:
                    st=e.stat(follow_symlinks=False)
                    if not (stat.S_ISDIR(st.st_mode) or stat.S_ISREG(st.st_mode)):continue
                    child=tree.child(current,e.name)
                    try:
                        now=os.fstat(child)
                        if (st.st_dev,st.st_ino)!=(now.st_dev,now.st_ino):raise ValueError('File changed.')
                        target=prefix+'/'+e.name
                        if stat.S_ISDIR(now.st_mode):walk(child,target,depth+1)
                        elif stat.S_ISREG(now.st_mode):
                            with z.open(target,'w',force_zip64=True) as out,os.fdopen(os.dup(child),'rb') as source:shutil.copyfileobj(source,out,65536)
                    finally:os.close(child)
        walk(fd,name)

def remove(tree,parent,name,depth=0):
    if depth>128:raise ValueError('Folder nesting is too deep.')
    st=os.stat(name,dir_fd=parent,follow_symlinks=False)
    if stat.S_ISDIR(st.st_mode):
        fd=tree.child(parent,name,True)
        try:
            now=os.fstat(fd)
            if (st.st_dev,st.st_ino)!=(now.st_dev,now.st_ino):raise ValueError('Folder changed.')
            with os.scandir(fd) as entries:
                for e in entries:remove(tree,fd,e.name,depth+1)
            now=os.stat(name,dir_fd=parent,follow_symlinks=False)
            if (st.st_dev,st.st_ino)!=(now.st_dev,now.st_ino):raise ValueError('Folder changed.')
            os.rmdir(name,dir_fd=parent)
        finally:os.close(fd)
    else:os.unlink(name,dir_fd=parent)

def main():
    global started
    mode,home,path,query=sys.argv[1:]
    if mode not in ('list','size','download','stored','compressed','delete','move') or len(query)>4096:raise ValueError('Invalid operation.')
    parts(path);tree=Tree(home)
    try:
        if mode in ('delete','move'):
            parent,name=tree.parent(path)
            try:
                # Reject a mount itself, including bind mounts on the same device.
                st=os.stat(name,dir_fd=parent,follow_symlinks=False)
                if stat.S_ISDIR(st.st_mode):os.close(tree.child(parent,name,True))
                if mode=='delete':
                    if query!='confirm':raise ValueError('Delete confirmation is required.')
                    remove(tree,parent,name)
                else:
                    dest,target=tree.parent(query)
                    try:
                        # Linux renameat2 NOREPLACE prevents accidental overwrites, including races.
                        libc=ctypes.CDLL(None,use_errno=True)
                        if libc.renameat2(parent,os.fsencode(name),dest,os.fsencode(target),1):
                            error=ctypes.get_errno();raise OSError(error,os.strerror(error))
                    finally:os.close(dest)
                emit(dict(ok=True));return
            finally:os.close(parent)
        fd=tree.open(path,mode in ('list','size'))
        try:
            info=os.fstat(fd)
            if mode=='size':emit(measure(tree,fd,path));return
            if mode in ('download','stored','compressed'):
                if not path or not (stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode)):raise ValueError('Select a regular file or folder.')
                name=parts(path)[-1]
                if stat.S_ISDIR(info.st_mode):
                    emit(dict(ok=True,name=name+'.zip',size=None,contentType='application/zip'));started=True
                    archive(tree,fd,name,zipfile.ZIP_STORED if mode=='stored' else zipfile.ZIP_DEFLATED)
                elif mode in ('stored','compressed'):
                    with os.fdopen(os.dup(fd),'rb') as source,zipfile.ZipFile(source) as original:
                        # Validate archive before sending a successful response. Nothing is extracted to disk.
                        if any(e.flag_bits&1 for e in original.infolist()):raise ValueError('Encrypted ZIP files can only be downloaded as originals.')
                        emit(dict(ok=True,name=os.path.splitext(name)[0]+('-uncompressed.zip' if mode=='stored' else '-compressed.zip'),size=None,contentType='application/zip'));started=True
                        with zipfile.ZipFile(Sink(),'w',compression=zipfile.ZIP_STORED if mode=='stored' else zipfile.ZIP_DEFLATED,allowZip64=True) as output:
                            for entry in original.infolist():
                                with original.open(entry) as src,output.open(entry.filename,'w',force_zip64=True) as dest:shutil.copyfileobj(src,dest,65536)
                else:
                    emit(dict(ok=True,name=name,size=info.st_size,contentType='application/octet-stream'));started=True
                    left=info.st_size
                    while left:
                        data=os.read(fd,min(left,65536))
                        if not data:raise OSError('File changed during download.')
                        sys.stdout.buffer.write(data);left-=len(data)
                return
            def entries():
                with os.scandir(fd) as scan:
                    for e in scan:
                        if query.casefold() not in e.name.casefold():continue
                        try:st=e.stat(follow_symlinks=False)
                        except OSError:continue
                        k=kind(st)
                        absolute=os.readlink('/proc/self/fd/'+str(fd)).rstrip('/')+'/'+e.name
                        if st.st_dev!=tree.dev or absolute in tree.mounts:k='mount'
                        yield dict(name=e.name,kind=k,bytes=st.st_size if k=='file' else None,modified=st.st_mtime)
            found=heapq.nsmallest(501,entries(),key=lambda e:(e['kind']!='folder',e['name'].casefold(),e['name']))
            emit(dict(ok=True,path=path,entries=found[:500],truncated=len(found)>500))
        finally:os.close(fd)
    finally:os.close(tree.fd)

if __name__=='__main__':
    try:main()
    except (OSError,ValueError,zipfile.BadZipFile,NotImplementedError,RuntimeError) as e:
        if not started:emit(dict(ok=False,error=str(e) or 'Item unavailable. Links and nested mounts are not followed.'))
        sys.exit(1)
