using Xur.Agent;
public static class FolderSizeCacheTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var cache=new FolderSizeCache();var calls=0;
        var release=new TaskCompletionSource<Dictionary<string,FolderSizeCache.Size>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Dictionary<string,FolderSizeCache.Size>> Scan(){Interlocked.Increment(ref calls);return release.Task;}
        check(cache.Get("home","folder",false,Scan).Scanning,"Folder sizes start in background without blocking listing");
        for(var i=0;i<10;i++)cache.Get("home","folder",true,Scan);
        await Wait(()=>calls==1);
        release.SetResult(new(){["folder"]=new(120,false,DateTimeOffset.UtcNow),["folder/child"]=new(80,false,DateTimeOffset.UtcNow)});
        await Wait(()=>!cache.Get("home","folder",false,Scan).Scanning);
        check(calls==1&&cache.Get("home","folder/child",false,Scan).Bytes==80,"Concurrent scans are deduplicated and immediate child sizes are preloaded");
        var next=new TaskCompletionSource<Dictionary<string,FolderSizeCache.Size>>(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.Get("home","folder",true,()=>next.Task);
        check(cache.Get("home","folder",false,Scan).Scanning,"Explicit size refresh starts a new scan");
        cache.Invalidate();next.SetResult(new(){["folder"]=new(999,false,DateTimeOffset.UtcNow)});
        await Task.Delay(30);
        var last=new TaskCompletionSource<Dictionary<string,FolderSizeCache.Size>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var value=cache.Get("home","folder",false,()=>last.Task);
        check(value.Bytes==null&&value.Scanning,"A mutation invalidates sizes and discards older in-flight scan results");
        last.SetResult(new(){["folder"]=new(20,true,DateTimeOffset.UtcNow)});
        await Wait(()=>!cache.Get("home","folder",false,Scan).Scanning);
        check(cache.Get("home","folder",false,Scan) is {Bytes:20,Partial:true},"Partial measurements retain their lower-bound status");
    }
    static async Task Wait(Func<bool> condition)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!condition())await Task.Delay(10,timeout.Token);}
}
