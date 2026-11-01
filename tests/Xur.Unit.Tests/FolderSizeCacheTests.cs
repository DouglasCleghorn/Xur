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
        var capturedAt=DateTimeOffset.UtcNow;
        release.SetResult(new(){["folder"]=new(120,false,capturedAt),["folder/child"]=new(80,false,capturedAt),["folder/child/deeper"]=new(40,false,capturedAt),["folder/old"]=new(10,false,capturedAt.AddMinutes(-1))});
        await Wait(()=>!cache.Get("home","folder",false,Scan).Scanning);
        check(calls==1&&cache.Get("home","folder/child",false,Scan).Bytes==80,"Concurrent scans are deduplicated and immediate child sizes are preloaded");
        var children=cache.Children("home","folder",capturedAt);
        check(children.Count==1&&children["child"].Bytes==80&&cache.Children("other","folder",capturedAt).Count==0,"Batched sizes include only current direct children within the selected scope");
        var childRefresh=new TaskCompletionSource<Dictionary<string,FolderSizeCache.Size>>(TaskCreationOptions.RunContinuationsAsynchronously);
        check(cache.Get("home","folder/child",false,()=>childRefresh.Task,requireChildren:true).Scanning,"Drilling into a preloaded child starts its own scan to measure grandchildren");
        check(cache.Children("home","folder",capturedAt)["child"].Scanning,"A child refresh remains visible as scanning in parent batches");
        childRefresh.SetResult(new(){["folder/child"]=new(90,false,capturedAt.AddSeconds(1))});
        await Wait(()=>!cache.Get("home","folder/child",false,Scan).Scanning);
        check(cache.Children("home","folder",capturedAt)["child"].Bytes==90,"A parent batch includes newer child measurements after refresh");
        check(!cache.Get("home","folder/child",false,Scan,requireChildren:true).Scanning,"A completed scan with no child folders is reused without rescanning");
        var next=new TaskCompletionSource<Dictionary<string,FolderSizeCache.Size>>(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.Get("home","folder",true,()=>next.Task);
        check(cache.Get("home","folder",false,Scan).Scanning,"Explicit size refresh starts a new scan");
        cache.Invalidate();next.SetResult(new(){["folder"]=new(999,false,DateTimeOffset.UtcNow)});
        check(cache.Children("home","folder",capturedAt).Count==0,"Invalidation also clears batched sibling sizes");
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
