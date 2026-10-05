using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;

// A separate, typed local transport: no manager cookies, credentials or HTTP
// endpoints. Linux identifies the caller; the agent verifies its active desk.
public sealed class ProfileSwitcherBroker(ProfileManager manager,Func<int,CancellationToken,Task<ProfileSwitcherSession?>> session,Func<bool>? enter=null,Action? exit=null,ProfileAccessSettings? access=null):IAsyncDisposable
{
    static readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    readonly ConcurrentDictionary<string,(int Uid,string Workstation,DateTimeOffset Expires)> plans=new();
    readonly ConcurrentDictionary<long,Task> clients=new();
    readonly SemaphoreSlim slots=new(8);
    readonly CancellationTokenSource stopping=new();
    Socket? listener;Task? loop;string? path;long sequence;
    public void Start(string socket,CancellationToken cancellation)
    {
        path=socket;Directory.CreateDirectory(Path.GetDirectoryName(socket)!);
        File.SetUnixFileMode(Path.GetDirectoryName(socket)!,(UnixFileMode)493);
        File.Delete(socket);listener=new(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socket));File.SetUnixFileMode(socket,(UnixFileMode)438);listener.Listen(16);
        cancellation.Register(()=>stopping.Cancel());loop=Accept();
    }
    async Task Accept()
    {
        try
        {
            while(!stopping.IsCancellationRequested)
            {
                await slots.WaitAsync(stopping.Token);
                Socket client;
                try {client=await listener!.AcceptAsync(stopping.Token);}catch{slots.Release();throw;}
                var id=Interlocked.Increment(ref sequence);var task=Serve(client);clients[id]=task;
                _=task.ContinueWith(_=>clients.TryRemove(id,out var ignored),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
        }catch(OperationCanceledException){}catch(SocketException) when(stopping.IsCancellationRequested){}
    }
    async Task Serve(Socket socket)
    {
        using var client=socket;using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var uid=PeerUid(socket);
            using var stream=new NetworkStream(socket,false);
            var header=new byte[4];await stream.ReadExactlyAsync(header,timeout.Token);
            var length=BinaryPrimitives.ReadUInt32BigEndian(header);
            if(length is 0 or >16384)throw new InvalidOperationException("Invalid switcher request size.");
            var body=new byte[length];await stream.ReadExactlyAsync(body,timeout.Token);
            SwitcherReply reply;
            try {reply=new(true,await Handle(uid,JsonSerializer.Deserialize<SwitcherRequest>(body,json)??throw new InvalidOperationException("Invalid request."),timeout.Token),null);}
            catch(Exception e) when(e is InvalidOperationException or JsonException){reply=new(false,null,Redaction.Logs(e.Message));}
            catch(Exception e) when(e is IOException or HttpRequestException){reply=new(false,null,"The local manager is unavailable. Try again.");}
            catch(OperationCanceledException){throw;}
            catch(Exception e){Console.Error.WriteLine("Profile switcher request failed: "+Redaction.Logs(e.Message));reply=new(false,null,"The local manager could not complete this request. Try again.");}
            var result=JsonSerializer.SerializeToUtf8Bytes(reply,json);
            if(result.Length>4*1024*1024)result=JsonSerializer.SerializeToUtf8Bytes(new SwitcherReply(false,null,"Too many profiles to display. Open the web manager."),json);
            BinaryPrimitives.WriteUInt32BigEndian(header,(uint)result.Length);await stream.WriteAsync(header,timeout.Token);await stream.WriteAsync(result,timeout.Token);
        }
        catch(Exception e) when(e is IOException or SocketException or OperationCanceledException or InvalidOperationException){}
        finally{slots.Release();}
    }
    internal async Task<object> Handle(int uid,SwitcherRequest request,CancellationToken cancellation=default)
    {
        if(access!=null&&!access.WorkstationsAllowed)throw new InvalidOperationException("Workstation profile controls are disabled. Change Profile access in the web manager’s Settings.");
        var actor=await session(uid,cancellation);
        if(actor==null || actor.Uid!=uid || uid<1000)throw new InvalidOperationException("This user does not have an active Xur workstation.");
        if(request.Action=="state")
        {
            var state=await manager.State();
            return new {profiles=state.Profiles.Select(Display),active=state.Active==null?null:Display(state.Active),runtime=new{instances=state.Runtime.Instances.Select(i=>new{i.Id,i.Fingerprint,i.State})},operation=state.Operation==null?null:new{state.Operation.Stage}};
        }
        if(request.Action is "preview" or "preview-unload")
        {
            foreach(var old in plans.Where(p=>p.Value.Expires<=DateTimeOffset.UtcNow))plans.TryRemove(old.Key,out _);
            if(plans.Count>=256)throw new InvalidOperationException("Too many outstanding reviews. Try again shortly.");
            var plan=request.Action=="preview-unload"?await manager.PreviewUnload():await manager.Preview(request.Id??"");
            plans[plan.Id]=(uid,actor.WorkloadId,plan.Expires);
            return new{plan.Id,plan.Digest,plan.Expires,plan.Unload,target=Display(plan.Target),plan.Steps};
        }
        if(request.Action!="apply")throw new InvalidOperationException("Unknown profile switcher action.");
        if(request.Trigger is not ("keyboard" or "controller" or "plasma-menu"))throw new InvalidOperationException("Specify how the profile switcher was opened.");
        if(request.Id==null || !plans.TryGetValue(request.Id,out var owner) || owner.Uid!=uid || owner.Workstation!=actor.WorkloadId || owner.Expires<=DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Review the profile again in this workstation.");
        if(enter!=null&&!enter())throw new InvalidOperationException("Application update in progress. Retry shortly.");
        try
        {
            if(access!=null&&!access.WorkstationsAllowed)throw new InvalidOperationException("Workstation profile controls are disabled. Change Profile access in Settings.");
            var result=await manager.Apply(new(request.Id,request.Digest??""),new(request.Trigger,actor.User,uid,actor.WorkloadId,actor.Seat));
            plans.TryRemove(request.Id,out _);return result;
        }finally{exit?.Invoke();}
    }
    public static object Display(Profile profile)=>new{profile.Id,profile.Name,profile.Revision,workloads=profile.Workloads.Select(w=>new{w.Id,w.Name,w.Fingerprint,recipe=new{w.Recipe.Name,w.Recipe.Kind}})};
    internal static int PeerUid(Socket socket)
    {
        uint length=12;
        if(GetPeer((int)socket.Handle,1,17,out var peer,ref length)!=0 || length!=12)throw new IOException("Could not identify the local caller.");
        return checked((int)peer.Uid);
    }
    [StructLayout(LayoutKind.Sequential)]struct Peer {public int Pid;public uint Uid,Gid;}
    [DllImport("libc",EntryPoint="getsockopt",SetLastError=true)]static extern int GetPeer(int fd,int level,int option,out Peer peer,ref uint length);
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();if(loop!=null)await loop;await Task.WhenAll(clients.Values);
        listener?.Dispose();if(path!=null)File.Delete(path);stopping.Dispose();slots.Dispose();
    }
}
public record SwitcherRequest(string Action,string? Id=null,string? Digest=null,string? Trigger=null);
public record SwitcherReply(bool Ok,object? Data,string? Error);
