using Xur.Domain;
namespace Xur.Agent;

// No persistent enable flag or keys: a basic install and every agent start stay closed.
public sealed class InstalledDiagnosticSsh(DiagnosticSsh ssh)
{
    readonly SemaphoreSlim gate=new(1,1);
    int keyCount;string? error;
    public async Task<DiagnosticSshStatus> Read()
    {
        await gate.WaitAsync();
        try{return new(await ssh.ObserveEnabled(),keyCount,error);}
        catch(Exception e) when(e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {return new(ssh.Enabled,keyCount,"Could not verify diagnostic SSH status. Check application logs.");}
        finally{gate.Release();}
    }
    public async Task Initialize()
    {
        try{await Set(new(false));}
        catch(Exception e) when(e is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {error="Could not stop a previous diagnostic SSH session. Check application logs.";}
    }
    public async Task<DiagnosticSshStatus> Set(DiagnosticSshRequest request)
    {
        await gate.WaitAsync();
        try
        {
            if(!request.Enabled){await ssh.Stop();keyCount=0;error=null;return new();}
            var publicKeys=request.PublicKeys??"";
            if(publicKeys.Length>8192)throw new InvalidOperationException("Use at most eight Ed25519 public keys.");
            string[] keys;
            try{keys=publicKeys.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(DiagnosticsConfiguration.PublicKey).ToArray();}
            catch(FormatException e){throw new InvalidOperationException(e.Message);}
            if(keys.Length is <1 or >8 || keys.Distinct(StringComparer.Ordinal).Count()!=keys.Length)
                throw new InvalidOperationException("Paste one to eight distinct Ed25519 public keys, one per line.");
            await ssh.ConfigureKeys(keys,protectExisting:true);keyCount=keys.Length;error=null;
            return new(ssh.Enabled,keyCount);
        }
        catch(Exception e) when(e is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {error=Redaction.Logs(e.Message);throw;}
        finally{gate.Release();}
    }
}
