import heapq,json,os,stat,sys

# Invoked with isolated Python as the workstation's Unix user, never root.
# Descriptor-relative, no-follow opens keep renamed paths and symlinks from
# escaping a home. Special files (including FIFOs) are never read.
def emit(value):
    sys.stdout.buffer.write((json.dumps(value)+"\n").encode());sys.stdout.buffer.flush()

download_started=False

def main():
    global download_started
    mode,home,path,query=sys.argv[1:]
    if mode not in ("list","download") or len(path)>4096 or len(query)>200:
        raise ValueError()
    parts=path.split("/") if path else []
    if any(p in ("",".","..") or "\x00" in p for p in parts):raise ValueError()
    fd=os.open(home,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
    try:
        for i,part in enumerate(parts):
            flags=os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK
            if i<len(parts)-1 or mode=="list":flags|=os.O_DIRECTORY
            nextfd=os.open(part,flags,dir_fd=fd);os.close(fd);fd=nextfd
        info=os.fstat(fd)
        if mode=="download":
            if not parts or not stat.S_ISREG(info.st_mode):raise ValueError()
            emit(dict(ok=True,name=parts[-1],size=info.st_size))
            download_started=True
            left=info.st_size
            while left:
                data=os.read(fd,min(left,65536))
                if not data:raise IOError("File changed")
                sys.stdout.buffer.write(data);left-=len(data)
            return
        if not stat.S_ISDIR(info.st_mode):raise ValueError()
        def entries():
            with os.scandir(fd) as scan:
                for entry in scan:
                    if query.casefold() not in entry.name.casefold():continue
                    try:s=entry.stat(follow_symlinks=False)
                    except OSError:continue
                    kind="folder" if stat.S_ISDIR(s.st_mode) else "file" if stat.S_ISREG(s.st_mode) else "link" if stat.S_ISLNK(s.st_mode) else "special"
                    yield dict(name=entry.name,kind=kind,bytes=s.st_size if kind=="file" else None,modified=s.st_mtime)
        found=heapq.nsmallest(501,entries(),key=lambda e:(e["kind"]!="folder",e["name"].casefold(),e["name"]))
        emit(dict(ok=True,path=path,entries=found[:500],truncated=len(found)>500))
    finally:os.close(fd)

try:main()
except (OSError,ValueError):
    if download_started:sys.exit(1)
    if sys.argv[1]=="list":emit(dict(ok=False,error="Folder unavailable. Links are not followed; open the original folder."))
    else:emit(dict(ok=False,error="File unavailable. Only regular files in this home can be downloaded."))
    sys.exit(1)
