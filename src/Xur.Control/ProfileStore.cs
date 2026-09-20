using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;

// One small database holds immutable revisions, the accepted state and action journal.
public sealed class ProfileStore : IDisposable
{
    readonly SqliteConnection db;
    SqliteTransaction? transaction;
    public ProfileStore(string directory)
    {
        Directory.CreateDirectory(directory);
        db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(directory,"profiles.db") }.ToString());db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS documents (kind TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(kind,id));");
        foreach(var kind in new[]{"profile","workload"})
            Execute($"CREATE TABLE IF NOT EXISTS {kind}_keys (id INTEGER PRIMARY KEY AUTOINCREMENT, runtime_key TEXT UNIQUE);");
        // Retain existing runtime keys: changing them would restart kept workloads.
        foreach(var station in List<StationDefinition>("station"))Register("workload",station.Id);
        foreach(var profile in List<Profile>("profile").Concat(List<Profile>("active"))) {
            Register(profile);
            foreach(var w in profile.Workloads.Where(w=>w.Recipe.Kind=="Workstation"))
                if(Get<StationDefinition>("station",w.Id)==null)Put("station",w.Id,new StationDefinition(w.Id,w.Name,w.User));
        }
    }
    void Execute(string sql) { using var c=db.CreateCommand();c.Transaction=transaction;c.CommandText=sql;c.ExecuteNonQuery(); }
    public T? Get<T>(string kind,string id)
    {
        using var c=db.CreateCommand();c.Transaction=transaction;c.CommandText="SELECT json FROM documents WHERE kind=$k AND id=$i";c.Parameters.AddWithValue("$k",kind);c.Parameters.AddWithValue("$i",id);
        return c.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json) : default;
    }
    public T[] List<T>(string kind)
    {
        using var c=db.CreateCommand();c.Transaction=transaction;c.CommandText="SELECT json FROM documents WHERE kind=$k ORDER BY id";c.Parameters.AddWithValue("$k",kind);
        using var r=c.ExecuteReader();var list=new List<T>();while(r.Read())list.Add(JsonSerializer.Deserialize<T>(r.GetString(0))!);return list.ToArray();
    }
    public void Put<T>(string kind,string id,T value)
    {
        using var c=db.CreateCommand();c.Transaction=transaction;c.CommandText="INSERT INTO documents VALUES($k,$i,$j) ON CONFLICT(kind,id) DO UPDATE SET json=excluded.json";
        c.Parameters.AddWithValue("$k",kind);c.Parameters.AddWithValue("$i",id);c.Parameters.AddWithValue("$j",JsonSerializer.Serialize(value));c.ExecuteNonQuery();
    }
    public void Save(Profile p,StationDefinition[]? stations=null)
    {
        using var tx=db.BeginTransaction();transaction=tx;try {Register(p);
            foreach(var station in stations??[]) {
                if(Get<StationDefinition>("station",station.Id) is {} prior && prior.User!=station.User)
                    throw new InvalidOperationException("Choose a new workstation identity to change its user.");
                Put("station",station.Id,station);
            }
            foreach(var w in p.Workloads.Where(w=>w.Recipe.Kind=="Workstation")) {
                var station=Get<StationDefinition>("station",w.Id);
                if(station!=null && station.User!=w.User)throw new InvalidOperationException("Choose a new workstation identity to change its user.");
                if(station==null)Put("station",w.Id,new StationDefinition(w.Id,w.Name,w.User));
            }
            Put("revision",p.Id+"/"+p.Revision,p);Put("profile",p.Id,p);tx.Commit();}finally{transaction=null;}
    }
    public void SaveRepairedTransition(Profile profile,Journal journal)
    {
        using var tx=db.BeginTransaction();transaction=tx;
        try
        {
            Register(profile);Put("revision",profile.Id+"/"+profile.Revision,profile);Put("profile",profile.Id,profile);
            Put("plan",journal.Plan.Id,journal.Plan);Put("journal","current",journal);tx.Commit();
        }finally{transaction=null;}
    }
    static string IdentityTable(string kind)=>kind switch {"profile"=>"profile_keys","workload"=>"workload_keys",_=>throw new ArgumentException("Unknown identity kind")};
    public string Allocate(string kind)
    {
        var table=IdentityTable(kind);
        using var command=db.CreateCommand();command.Transaction=transaction;
        command.CommandText=$"INSERT INTO {table} DEFAULT VALUES RETURNING id";
        while(true)
        {
            command.Parameters.Clear();command.CommandText=$"INSERT INTO {table} DEFAULT VALUES RETURNING id";
            var id=((long)command.ExecuteScalar()!).ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.CommandText=$"UPDATE OR IGNORE {table} SET runtime_key=$key WHERE id=$id";
            command.Parameters.AddWithValue("$key",id);command.Parameters.AddWithValue("$id",long.Parse(id));
            if(command.ExecuteNonQuery()==1)return id;
        }
    }
    void Register(Profile profile)
    {
        Register("profile",profile.Id);
        foreach(var workload in profile.Workloads)Register("workload",workload.Id);
    }
    void Register(string kind,string key)
    {
        using var command=db.CreateCommand();command.Transaction=transaction;
        command.CommandText=$"INSERT INTO {IdentityTable(kind)} (runtime_key) SELECT $key WHERE NOT EXISTS (SELECT 1 FROM {IdentityTable(kind)} WHERE runtime_key=$key)";
        command.Parameters.AddWithValue("$key",key);command.ExecuteNonQuery();
    }
    public void DeleteProfile(Profile profile)
    {
        using var tx=db.BeginTransaction();transaction=tx;
        try {Put("deleted-profile",profile.Id,profile);Remove("profile",profile.Id);Put("epoch","current",Guid.NewGuid().ToString("N"));tx.Commit();}finally{transaction=null;}
    }
    public void Remove(string kind,string id)
    {
        using var c=db.CreateCommand();c.Transaction=transaction;
        c.CommandText="DELETE FROM documents WHERE kind=$kind AND id=$id";
        c.Parameters.AddWithValue("$kind",kind);c.Parameters.AddWithValue("$id",id);c.ExecuteNonQuery();
    }
    public void Commit(Profile? p,Journal journal)
    {
        using var tx=db.BeginTransaction();transaction=tx;try {if(p==null)Remove("active","current");else Put("active","current",p);Put("epoch","current",Guid.NewGuid().ToString("N"));Put("journal","current",journal);tx.Commit();}finally{transaction=null;}
    }
    public void Cancel(Journal journal)
    {
        using var tx=db.BeginTransaction();transaction=tx;
        try {Remove("active","current");Put("journal","current",journal);Put("epoch","current",Guid.NewGuid().ToString("N"));tx.Commit();}finally{transaction=null;}
    }
    public void Dispose()=>db.Dispose();
}
public record Journal(ProfilePlan Plan,RuntimeObservation Source,int Completed,string Stage,string? Error,DateTimeOffset Updated,
    int[]? CompletedSteps=null,Dictionary<int,string>? StepErrors=null,int[]? RunningSteps=null)
{
    // Completed remains the contiguous prefix for older journals/readers. New
    // unloads additionally persist exact out-of-order completion receipts.
    public int[] Done()=>CompletedSteps??Enumerable.Range(0,Completed).ToArray();
}
