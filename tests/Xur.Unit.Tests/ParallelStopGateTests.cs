using Xur.Agent;
static class ParallelStopGateTests
{
    public static async Task Run(Action<bool,string> check)
    {
        using var exclusive=new SemaphoreSlim(1,1);var gate=new ParallelStopGate(exclusive);
        var a=await gate.Enter();var b=await gate.Enter().WaitAsync(TimeSpan.FromSeconds(2));
        var start=exclusive.WaitAsync();check(!start.IsCompleted,"Two agent stops share the mutation gate while starts wait");
        await a.DisposeAsync();check(!start.IsCompleted,"Finishing one stop cannot release the start boundary prematurely");
        await b.DisposeAsync();await start.WaitAsync(TimeSpan.FromSeconds(2));exclusive.Release();
        await b.DisposeAsync();check(exclusive.CurrentCount==1,"Stop group releases once after its final member finishes");
        await using(var card=await gate.Resources(["gpu:a","workload:a"]))
        {
            var same=gate.Resources(["workload:b","gpu:a"]);
            await using var other=await gate.Resources(["gpu:c","workload:c"]).WaitAsync(TimeSpan.FromSeconds(2));
            check(!same.IsCompleted,"Shared GPUs serialize while disjoint GPU stops proceed");
            await card.DisposeAsync();await using var acquired=await same.WaitAsync(TimeSpan.FromSeconds(2));
        }
        for(int i=0;i<20;i++)
            await Task.WhenAll(Claim(["a","b"]),Claim(["b","a"])).WaitAsync(TimeSpan.FromSeconds(2));
        check(true,"Oppositely ordered overlapping GPU keys cannot deadlock");
        async Task Claim(string[] keys){await using var lease=await gate.Resources(keys);await Task.Yield();}
    }
}
