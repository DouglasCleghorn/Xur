namespace Xur.Agent;

// Only scalar measurements live here. Directory listings are always read afresh.
public sealed class FolderSizeCache
{
    public record Size(long? Bytes,bool Partial,DateTimeOffset CapturedAt,bool Scanning=false,string? Error=null);
    readonly object gate=new();
    readonly Dictionary<string,Size> sizes=[];
    readonly Dictionary<string,DateTimeOffset> scanned=[];
    readonly HashSet<string> pending=[];
    readonly SemaphoreSlim workers=new(2);
    long generation;
    public Size Get(string scope,string path,bool refresh,Func<Task<Dictionary<string,Size>>> scan,bool requireChildren=false)
    {
        var key=scope+"\0"+path;
        lock(gate)
        {
            sizes.TryGetValue(key,out var prior);
            if(pending.Contains(key))return (prior??new(null,false,DateTimeOffset.UtcNow)) with{Scanning=true};
            if(!refresh&&prior!=null&&DateTimeOffset.UtcNow-prior.CapturedAt<TimeSpan.FromMinutes(30)&&
                (!requireChildren||scanned.TryGetValue(key,out var capturedAt)&&capturedAt==prior.CapturedAt))return prior;
            if(pending.Count>=64)return new(null,false,DateTimeOffset.UtcNow,true);
            pending.Add(key);var version=generation;
            _=Task.Run(async()=>
            {
                await workers.WaitAsync();
                try
                {
                    lock(gate){if(version!=generation)return;}
                    Dictionary<string,Size> result;
                    try{result=await scan();}
                    catch{result=new(){[path]=new(null,true,DateTimeOffset.UtcNow,Error:"Could not measure folder. Refresh to retry.")};}
                    lock(gate)
                    {
                        if(version!=generation)return;
                        foreach(var item in result)
                        {
                            if(sizes.Count>=2048){var oldest=sizes.MinBy(x=>x.Value.CapturedAt).Key;sizes.Remove(oldest);scanned.Remove(oldest);}
                            sizes[scope+"\0"+item.Key]=item.Value;
                        }
                        // A preloaded child's total does not mean its own children were scanned.
                        if(result.TryGetValue(path,out var measured))scanned[key]=measured.CapturedAt;
                    }
                }
                finally{lock(gate)pending.Remove(key);workers.Release();}
            });
            return (prior??new(null,false,DateTimeOffset.UtcNow)) with{Scanning=true};
        }
    }
    public Dictionary<string,Size> Children(string scope,string path,DateTimeOffset capturedAt)
    {
        var prefix=scope+"\0"+(path.Length==0?"":path+"/");
        lock(gate)
        {
            var children=sizes.Where(item=>item.Key.StartsWith(prefix,StringComparison.Ordinal)&&item.Value.CapturedAt>=capturedAt)
                .Select(item=>(Name:item.Key[prefix.Length..],item.Value)).Where(item=>item.Name.Length>0&&!item.Name.Contains('/'))
                .ToDictionary(item=>item.Name,item=>item.Value,StringComparer.Ordinal);
            foreach(var key in pending.Where(key=>key.StartsWith(prefix,StringComparison.Ordinal)))
            {
                var name=key[prefix.Length..];if(name.Length==0||name.Contains('/'))continue;
                sizes.TryGetValue(key,out var prior);
                children[name]=(prior??new(null,false,DateTimeOffset.UtcNow)) with{Scanning=true};
            }
            return children;
        }
    }
    public void Invalidate(){lock(gate){generation++;sizes.Clear();scanned.Clear();}}
}
