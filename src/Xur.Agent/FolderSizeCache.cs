namespace Xur.Agent;

// Only scalar measurements live here. Directory listings are always read afresh.
public sealed class FolderSizeCache
{
    public record Size(long? Bytes,bool Partial,DateTimeOffset CapturedAt,bool Scanning=false,string? Error=null);
    readonly object gate=new();
    readonly Dictionary<string,Size> sizes=[];
    readonly HashSet<string> pending=[];
    readonly SemaphoreSlim workers=new(2);
    long generation;
    public Size Get(string scope,string path,bool refresh,Func<Task<Dictionary<string,Size>>> scan)
    {
        var key=scope+"\0"+path;
        lock(gate)
        {
            sizes.TryGetValue(key,out var prior);
            if(pending.Contains(key))return (prior??new(null,false,DateTimeOffset.UtcNow)) with{Scanning=true};
            if(!refresh&&prior!=null&&DateTimeOffset.UtcNow-prior.CapturedAt<TimeSpan.FromMinutes(30))return prior;
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
                            if(sizes.Count>=2048)sizes.Remove(sizes.MinBy(x=>x.Value.CapturedAt).Key);
                            sizes[scope+"\0"+item.Key]=item.Value;
                        }
                    }
                }
                finally{lock(gate)pending.Remove(key);workers.Release();}
            });
            return (prior??new(null,false,DateTimeOffset.UtcNow)) with{Scanning=true};
        }
    }
    public void Invalidate(){lock(gate){generation++;sizes.Clear();}}
}
