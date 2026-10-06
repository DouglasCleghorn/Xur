using Microsoft.Win32.SafeHandles;
using Xur.IO;

namespace Xur.Util.Tests;

static class SteamTests
{
    public static async Task Run()
    {
        using var fixture=new Fixture();var uid=Linux.EffectiveUser();
        var relative="Example Game/payload.bin";
        fixture.Write("alice/.local/share/Steam/steamapps/common/"+relative,"");
        fixture.Write("bob/.local/share/Steam/steamapps/common/"+relative,"");
        var a=fixture.PathOf("alice/.local/share/Steam/steamapps/common");var b=fixture.PathOf("bob/.local/share/Steam/steamapps/common");
        var payload=Enumerable.Repeat((byte)'A',1024*1024).Concat(Enumerable.Repeat((byte)'B',1024*1024)).Concat("end"u8.ToArray()).ToArray();
        File.WriteAllBytes(Path.Combine(a,relative),payload);File.WriteAllBytes(Path.Combine(b,relative),payload);
        var kernel=new ComparingExtents();var database=fixture.PathOf("pairs.sqlite");
        using var db=new NativeSqlite(database);db.Execute("CREATE TABLE pairs (key TEXT PRIMARY KEY,metadata TEXT,seen INTEGER,cursor INTEGER)");
        using var treeA=new DirectoryTree(a);using var treeB=new DirectoryTree(b);
        var libraryA=new SteamSharing.Library(treeA,uid,a);var libraryB=new SteamSharing.Library(treeB,uid,b);
        (SteamSharing.Candidate A,SteamSharing.Candidate B) Pair()
        {
            using var fileA=treeA.Open(relative);using var fileB=treeB.Open(relative);
            return(new(libraryA,relative,DirectoryTree.Inspect(fileA)),new(libraryB,relative,DirectoryTree.Inspect(fileB)));
        }
        var worker=new SteamSharing(database,kernel,minimumAge:0);var pair=Pair();worker.Pair(db,pair.A,pair.B,default);
        Verify.That(kernel.Calls.Select(c=>(c.Offset,c.Length)).SequenceEqual(new[]{(0UL,1048576UL),(1048576UL,1048576UL),(2097152UL,3UL)}),"Extent sharing uses bounded chunks and handles the final partial chunk");
        pair=Pair();worker.Pair(db,pair.A,pair.B,default);Verify.That(kernel.Calls.Count==3,"Unchanged completed pairs use the persistent cache");
        using(var changed=new FileStream(Path.Combine(b,relative),FileMode.Open,FileAccess.Write))changed.Write(Enumerable.Repeat((byte)'C',1024*1024).ToArray());
        pair=Pair();worker.Pair(db,pair.A,pair.B,default);Verify.That(kernel.Calls.Count==6&&kernel.Different==1,"Changed files are compared again without sharing unequal ranges");
        pair=Pair();File.Delete(Path.Combine(b,relative));File.WriteAllBytes(Path.Combine(b,relative),payload);
        worker.Pair(db,pair.A,pair.B,default);Verify.That(kernel.Calls.Count==6,"Replaced destination inodes fail revalidation before the ioctl");
        var resumed=new SteamSharing(database,kernel,minimumAge:0);var budgetCalls=0;resumed.Continue=()=>budgetCalls++==0;
        pair=Pair();resumed.Pair(db,pair.A,pair.B,default);
        Verify.That(db.Query("SELECT cursor FROM pairs").Single()[0]=="1048576","A limited pass checkpoints the next offset");
        kernel.Calls.Clear();pair=Pair();new SteamSharing(database,kernel,minimumAge:0).Pair(db,pair.A,pair.B,default);
        Verify.That(kernel.Calls.Select(c=>c.Offset).SequenceEqual(new[]{1048576UL,2097152UL}),"The next pass resumes after the cached checkpoint");
        fixture.Write("outside",new string('x',4096));File.CreateSymbolicLink(Path.Combine(a,"linked"),fixture.PathOf("outside"));
        await new Runtime().Run(["ln",fixture.PathOf("outside"),Path.Combine(a,"hardlink")]);
        await new Runtime().Run(["mkfifo",Path.Combine(a,"fifo")]);
        Directory.CreateDirectory(fixture.PathOf("alice/.local/share/Steam/steamapps/compatdata"));fixture.Write("alice/.local/share/Steam/steamapps/compatdata/private",new string('x',4096));
        var accounts=new[]{new SteamAccount(uid,fixture.PathOf("alice")),new SteamAccount(uid,fixture.PathOf("bob"))};
        var report=new SteamSharing(fixture.PathOf("run.sqlite"),kernel,minimumAge:0).Run(accounts);
        Verify.That(report["files"]!.GetValue<long>()==2,"Only private regular game files qualify; links, FIFOs and compatdata are excluded");
        Verify.That(report["pairs"]!.GetValue<long>()==0,"Libraries belonging to the same account are not deduplicated with one another");
        report=new SteamSharing(fixture.PathOf("limited.sqlite"),kernel,limit:1,minimumAge:0).Run(accounts);
        Verify.That(report["limited"]!.GetValue<bool>(),"Steam scanning respects the global entry budget");
        report=new SteamSharing(fixture.PathOf("unsupported.sqlite"),new ComparingExtents{SupportedFilesystem=false},minimumAge:0).Run(accounts);
        Verify.That(report["unsupportedLibraries"]!.GetValue<long>()==2&&report["files"]!.GetValue<long>()==0,"Unsupported filesystems are skipped before scanning or sharing");
        var external=fixture.PathOf("Attached Disk/SteamLibrary");Directory.CreateDirectory(Path.Combine(external,"steamapps/common"));
        fixture.Write("alice/.local/share/Steam/steamapps/libraryfolders.vdf","\"libraryfolders\" { \"0\" { \"path\" \""+fixture.PathOf("alice/.local/share/Steam")+"\" } \"1\" { \"path\" \""+external+"\" } }");
        report=new SteamSharing(fixture.PathOf("discovery.sqlite"),kernel).Run(accounts[..1]);
        Verify.That(report["libraries"]!.GetValue<long>()==2,"Custom Steam library discovery deduplicates the standard library path");
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Verify.Reject(()=>Task.Run(()=>new SteamSharing(fixture.PathOf("cancel.sqlite"),kernel).Run(accounts,cancel.Token)),"Steam scanning responds to cancellation");
        using(var file=treeA.Open(relative))Verify.That(new ExtentSharing().Supported(file)==new ExtentSharing().Supported(treeA.Root),"Native filesystem probing agrees for held directory and file descriptors");
        db.Execute("INSERT OR REPLACE INTO pairs VALUES (?,?,?,?)","quoted'key","metadata","1","4");
        Verify.That(db.Query("SELECT cursor FROM pairs WHERE key=?","quoted'key").Single()[0]=="4","Shared SQLite binds values instead of constructing SQL");
        File.CreateSymbolicLink(fixture.PathOf("db-alias"),database);
        await Verify.Reject(()=>Task.Run(()=>{using var alias=new NativeSqlite(fixture.PathOf("db-alias"));}),"Database aliases are rejected");
        using var readonlyDb=new NativeSqlite(database,readOnly:true);
        await Verify.Reject(()=>Task.Run(()=>readonlyDb.Execute("DELETE FROM pairs")),"Read-only compatibility queries cannot mutate existing profiles");
    }
    sealed class ComparingExtents:ExtentSharing
    {
        public bool SupportedFilesystem=true;
        public List<(ulong Offset,ulong Length)> Calls=[];
        public int Different;
        public override bool Supported(SafeFileHandle fd)=>SupportedFilesystem;
        public override ExtentResult Share(SafeFileHandle source,SafeFileHandle destination,ulong offset,ulong length)
        {
            Calls.Add((offset,length));var a=new byte[(int)length];var b=new byte[(int)length];
            var readA=RandomAccess.Read(source,a,(long)offset);var readB=RandomAccess.Read(destination,b,(long)offset);
            if(readA!=readB||!a.AsSpan(0,readA).SequenceEqual(b.AsSpan(0,readB))){Different++;return new(0,1);}
            return new((ulong)readA,0);
        }
    }
}
