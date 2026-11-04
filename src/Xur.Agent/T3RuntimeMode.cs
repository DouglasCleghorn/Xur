using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public record T3RuntimeState(string Mode="Container",string? Target=null,string? Previous=null,string Stage="Ready",string? Error=null);

// Prepare while the old server is still serving. Publish intent durably before
// the handoff; an interrupted handoff resumes or rolls back after agent restart.
public sealed class T3RuntimeMode(string directory,Func<string,Task> prepare,Func<Task> apply)
{
    readonly object sync=new();
    Task? worker;
    bool editingSettings;
    string PathName=>Path.Combine(directory,"t3-runtime.json");
    static readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    public T3RuntimeState Read()
    {
        lock(sync)
        {
            var state=File.Exists(PathName)?JsonSerializer.Deserialize<T3RuntimeState>(File.ReadAllText(PathName),json)??throw new IOException("Invalid T3 runtime settings."):new();
            Validate(state.Mode);if(state.Target!=null)Validate(state.Target);if(state.Previous!=null)Validate(state.Previous);
            if(state.Stage is not ("Ready" or "Preparing" or "Switching" or "RollingBack" or "Failed")||
                state.Stage is "Preparing" or "Switching" or "RollingBack" && (state.Target==null||state.Previous==null))
                throw new IOException("Invalid T3 runtime transition state; saved settings were retained.");
            return state;
        }
    }
    public static void Validate(string mode)
    {if(mode is not ("Container" or "Native"))throw new InvalidOperationException("Choose Host or DinD.");}
    void Save(T3RuntimeState state)
    {
        lock(sync)NetworkShares.Write(PathName,JsonSerializer.Serialize(state,json));
    }
    public bool Busy=>Read().Stage is "Preparing" or "Switching" or "RollingBack";
    public T3RuntimeState Change(string mode)
    {
        Validate(mode);
        lock(sync)
        {
            var current=Read();
            if(Busy||editingSettings)throw new InvalidOperationException("Wait for the current T3 runtime switch to finish.");
            if(current.Mode==mode)return current;
            var next=new T3RuntimeState(current.Mode,mode,current.Mode,"Preparing");Save(next);Resume();return next;
        }
    }
    public async Task<T> ChangeSettings<T>(Func<Task<T>> edit)
    {
        lock(sync)
        {
            if(Busy||editingSettings)throw new InvalidOperationException("Wait for the T3 runtime or access change to finish.");
            editingSettings=true;
        }
        try{return await edit();}finally{lock(sync)editingSettings=false;}
    }
    public void Resume()
    {
        lock(sync)if(Busy && worker is not {IsCompleted:false})worker=Task.Run(Switch);
    }
    async Task Switch()
    {
        try
        {
            var current=Read();
            if(current.Stage=="Preparing")
            {
                await prepare(current.Target!);
                current=current with{Mode=current.Target!,Stage="Switching"};Save(current);
            }
            if(current.Stage=="Switching")
            {
                await apply();Save(new(current.Mode));return;
            }
            if(current.Stage=="RollingBack")await Rollback(current);
        }
        catch(Exception error)
        {
            var current=Read();var message=Redaction.Logs(error.Message);
            if(current.Stage=="Preparing"){Save(new(current.Mode,Stage:"Failed",Error:message+" The existing T3 server was kept running."));return;}
            current=current with{Mode=current.Previous!,Stage="RollingBack",Error=message};Save(current);
            await Rollback(current);
        }
    }
    async Task Rollback(T3RuntimeState current)
    {
        try{await apply();Save(new(current.Mode,Stage:"Failed",Error:current.Error+" The previous runtime was restored; reconnect using the same saved environment."));}
        catch(Exception error){Save(new(current.Mode,Stage:"Failed",Error:current.Error+" Restoring the previous runtime also failed: "+Redaction.Logs(error.Message)+". Retry T3; its home and pairings were retained."));}
    }
}
