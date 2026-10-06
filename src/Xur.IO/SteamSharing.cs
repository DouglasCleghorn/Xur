using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Xur.IO;

public sealed record SteamAccount(uint Uid, string Home);

/// <summary>Bounded cross-account extent sharing for stable, private Steam game inodes.</summary>
[SupportedOSPlatform("linux")]
public sealed partial class SteamSharing(string database, ExtentSharing? extents = null, TimeSpan? duration = null, int limit = 250000, int minimumAge = 120)
{
    static readonly string[] Libraries = [".local/share/Steam", ".steam/steam", ".steam/root", ".var/app/com.valvesoftware.Steam/.local/share/Steam"];
    readonly ExtentSharing kernel = extents ?? new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Dictionary<string,long> counters = new() { ["libraries"]=0, ["unsupportedLibraries"]=0, ["files"]=0, ["pairs"]=0, ["cachedPairs"]=0, ["sharedBytes"]=0, ["differentRanges"]=0, ["errors"]=0 };
    int entries; bool limited;
    internal Func<bool>? Continue {get;set;}
    internal sealed record Library(DirectoryTree Tree, uint Uid, string Path);
    internal sealed record Candidate(Library Library, string Path, DirectoryTree.Metadata Metadata);
    bool Budget(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if ((Continue?.Invoke() ?? true) && clock.Elapsed < (duration ?? TimeSpan.FromSeconds(600))) return true;
        limited = true; return false;
    }
    bool Eligible(DirectoryTree.Metadata item, uint uid) => item.Kind == "file" && item.Uid == uid && item.Links == 1 && item.Size >= 4096 &&
        Math.Max(item.Modified, item.ChangedSeconds + item.ChangedNanoseconds / 1e9) <= (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds - minimumAge;
    static string Fingerprint(DirectoryTree.Metadata item) => FormattableString.Invariant($"{item.DeviceMajor},{item.DeviceMinor},{item.Inode},{item.Size},{item.ModifiedSeconds},{item.ModifiedNanoseconds},{item.ChangedSeconds},{item.ChangedNanoseconds}");
    static bool Stable(DirectoryTree.Metadata before, DirectoryTree.Metadata after) => before.Unchanged(after) && before.Uid == after.Uid && before.Links == after.Links;
    List<Library> Discover(IEnumerable<SteamAccount> accounts, CancellationToken token)
    {
        var roots = new List<Library>(); var seen = new HashSet<(uint,uint,ulong)>();
        try
        {
            foreach (var account in accounts)
            {
                if (!Budget(token)) break;
                if (account.Uid is <1000 or >=65534 || !Path.IsPathFullyQualified(account.Home)) continue;
                DirectoryTree home;
                try { home = new(account.Home); } catch (IOException) { continue; }
                using (home)
                {
                    if (home.Identity.Uid != account.Uid) continue;
                    var paths = Libraries.Select(p => Path.Combine(account.Home, p)).ToHashSet(StringComparer.Ordinal);
                    foreach (var location in Libraries)
                    {
                        try
                        {
                            using var fd = home.Open(location + "/steamapps/libraryfolders.vdf"); var item = DirectoryTree.Inspect(fd);
                            if (item.Uid != account.Uid || item.Kind != "file" || item.Size > 65536) continue;
                            using var stream = new FileStream(DirectoryTree.Duplicate(fd), FileAccess.Read);
                            var bytes = new byte[65537]; var count = 0; int read;
                            while (count < bytes.Length && (read = stream.Read(bytes,count,bytes.Length-count)) != 0) { token.ThrowIfCancellationRequested(); count += read; }
                            if (count > 65536) continue;
                            var content = new System.Text.UTF8Encoding(false,true).GetString(bytes,0,count);
                            foreach (Match match in LibraryPaths().Matches(content)) paths.Add(match.Groups[1].Value);
                        }
                        catch (Exception error) when (error is IOException or ArgumentException) { }
                    }
                    foreach (var path in paths.Order(StringComparer.Ordinal))
                    {
                        if (!Budget(token)) break;
                        DirectoryTree tree;
                        try { tree = new(Path.Combine(path, "steamapps/common")); } catch (Exception error) when (error is IOException or ArgumentException) { continue; }
                        var item = tree.Identity;
                        if (item.Uid != account.Uid || !seen.Add((item.DeviceMajor,item.DeviceMinor,item.Inode))) { tree.Dispose(); continue; }
                        counters["libraries"]++;
                        bool supported;
                        try { supported = kernel.Supported(tree.Root); } catch { tree.Dispose(); throw; }
                        if (!supported) { counters["unsupportedLibraries"]++; tree.Dispose(); continue; }
                        roots.Add(new(tree,account.Uid,path));
                    }
                }
            }
            return roots;
        }
        catch { foreach (var library in roots) library.Tree.Dispose(); throw; }
    }
    IEnumerable<Candidate> Scan(Library library, string path, int depth, CancellationToken token)
    {
        if (!Budget(token) || depth >= 64) { limited = true; yield break; }
        using var folder = library.Tree.Open(path,true);
        foreach (var name in DirectoryTree.Names(folder))
        {
            if (!Budget(token) || entries >= limit) { limited = true; yield break; }
            entries++; var relative = path.Length == 0 ? name : path + "/" + name;
            DirectoryTree.Metadata item;
            try { item = DirectoryTree.Inspect(folder,name); } catch (IOException) { counters["errors"]++; continue; }
            if (item.Uid != library.Uid || !library.Tree.Within(item)) continue;
            if (item.Kind == "folder")
            {
                // Materialize bounded recursion failures here so an inaccessible child doesn't stop the pass.
                List<Candidate> children;
                try { children = Scan(library,relative,depth+1,token).ToList(); } catch (IOException) { counters["errors"]++; continue; }
                foreach (var child in children) yield return child;
            }
            else if (Eligible(item,library.Uid)) yield return new(library,relative,item);
        }
    }
    internal void Pair(NativeSqlite db, Candidate source, Candidate destination, CancellationToken token)
    {
        var key = new JsonArray(JsonValue.Create(source.Library.Uid),JsonValue.Create(source.Library.Path),JsonValue.Create(destination.Library.Uid),JsonValue.Create(destination.Library.Path),JsonValue.Create(source.Path)).ToJsonString();
        var metadata = Fingerprint(source.Metadata)+";"+Fingerprint(destination.Metadata);
        var row = db.Query("SELECT metadata,cursor FROM pairs WHERE key=?",key).FirstOrDefault();
        var cursor = row is not null && row[0] == metadata ? ulong.Parse(row[1]!,CultureInfo.InvariantCulture) : 0;
        if (cursor >= source.Metadata.Size) { counters["cachedPairs"]++; return; }
        using var a = source.Library.Tree.Open(source.Path); using var b = destination.Library.Tree.Open(destination.Path,access:2);
        var beforeA = DirectoryTree.Inspect(a); var beforeB = DirectoryTree.Inspect(b);
        if (!Eligible(beforeA,source.Library.Uid) || !Eligible(beforeB,destination.Library.Uid) || Fingerprint(beforeA) != Fingerprint(source.Metadata) || Fingerprint(beforeB) != Fingerprint(destination.Metadata)) return;
        counters["pairs"]++; var checkpoint = cursor;
        bool Cache()
        {
            var afterA = DirectoryTree.Inspect(a); var afterB = DirectoryTree.Inspect(b);
            if (!Stable(beforeA,afterA) || !Stable(beforeB,afterB)) return false;
            db.Execute("INSERT OR REPLACE INTO pairs VALUES (?,?,?,?)",key,Fingerprint(afterA)+";"+Fingerprint(afterB),DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),cursor.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        while (cursor < beforeA.Size)
        {
            if (!Budget(token)) { Cache(); return; }
            var length = Math.Min(1024UL*1024,beforeA.Size-cursor); var result = kernel.Share(a,b,cursor,length);
            if (result.Status == 1) { counters["differentRanges"]++; cursor += length; }
            else if (result.Status == 0 && result.Bytes is >0 && result.Bytes <= length) { counters["sharedBytes"] += checked((long)result.Bytes); cursor += result.Bytes; }
            else throw new IOException("Unexpected deduplication result.");
            if (cursor-checkpoint >= 16*1024*1024) { if (!Cache()) return; checkpoint = cursor; }
        }
        Cache();
    }
    public JsonObject Run(IEnumerable<SteamAccount> accounts, CancellationToken token = default)
    {
        var roots = Discover(accounts,token);
        try
        {
            using var db = new NativeSqlite(database);
            db.Execute("CREATE TABLE IF NOT EXISTS pairs (key TEXT PRIMARY KEY,metadata TEXT,seen INTEGER,cursor INTEGER)");
            db.Execute("DELETE FROM pairs WHERE seen < ?",(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-30*86400).ToString(CultureInfo.InvariantCulture));
            var groups = new Dictionary<(uint,uint,string,ulong),List<Candidate>>();
            foreach (var library in roots)
                foreach (var file in Scan(library,"",0,token))
                {
                    counters["files"]++; var item = file.Metadata; var key = (item.DeviceMajor,item.DeviceMinor,file.Path,item.Size);
                    if (!groups.TryGetValue(key,out var group)) groups[key] = group = [];
                    group.Add(file);
                }
            var unsupported = new HashSet<Library>();
            foreach (var group in groups.Values.Where(g=>g.Count>1))
                for (var index=1;index<group.Count && Budget(token);index++)
                {
                    var destination=group[index]; if(unsupported.Contains(destination.Library))continue;
                    foreach(var source in group.Take(index))
                    {
                        if(!Budget(token))break;
                        if(source.Library.Uid==destination.Library.Uid||unsupported.Contains(source.Library))continue;
                        try { Pair(db,source,destination,token); }
                        catch(IOException error)
                        {
                            counters["errors"]++;
                            if(error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode:95 or 25 })
                            {unsupported.Add(destination.Library);counters["unsupportedLibraries"]++;break;}
                        }
                    }
                }
            var report=new JsonObject();foreach(var (name,value) in counters)report[name]=value;report["limited"]=limited;return report;
        }
        finally { foreach(var library in roots)library.Tree.Dispose(); }
    }
    [GeneratedRegex("\"path\"\\s*\"(/[^\"\\\\\\r\\n\\x00]*)\"",RegexOptions.CultureInvariant)] private static partial Regex LibraryPaths();
}
