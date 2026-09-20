using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace Xur.Control;

public sealed record ManagerAccount(string Id,string Username,string PasswordHash);

public sealed class ManagerAccountStore
{
    readonly string? path;
    public ManagerAccount? Account { get; private set; }
    readonly PasswordHasher<ManagerAccount> hasher=new(Options.Create(new PasswordHasherOptions { IterationCount=210000 }));
    public ManagerAccountStore(string? directory)
    {
        path=directory==null?null:Path.Combine(directory,"manager-account.json");
        if(path!=null && File.Exists(path))
        {
            Account=JsonSerializer.Deserialize<ManagerAccount>(File.ReadAllText(path));
            if(Account==null || string.IsNullOrWhiteSpace(Account.Id) || string.IsNullOrWhiteSpace(Account.Username) || string.IsNullOrWhiteSpace(Account.PasswordHash))
                throw new IOException("Invalid manager account file");
        }
    }
    public void Create(string username,string password)
    {
        if(Account!=null)throw new InvalidOperationException("An account is already configured.");
        username=username.Trim();
        if(!ValidUsername(username))
            throw new ArgumentException("Use a username or an email address, up to 254 characters.");
        if(password.Length is <8 or >256)throw new ArgumentException("Use a password between 8 and 256 characters.");
        var account=new ManagerAccount(Guid.NewGuid().ToString("N"),username,"");
        account=account with { PasswordHash=hasher.HashPassword(account,password) };
        if(path!=null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.SetUnixFileMode(Path.GetDirectoryName(path)!,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var stream=new FileStream(temporary,new FileStreamOptions { Mode=FileMode.CreateNew,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite }))
                { JsonSerializer.Serialize(stream,account);stream.Flush(true); }
                File.Move(temporary,path,overwrite:false);
                // Make the rename durable before granting the permanent session.
                var fd=OpenDirectory(Path.GetDirectoryName(path)!,0x10000|0x80000);
                if(fd<0)throw new IOException("Could not open account directory");
                try { if(SyncDirectory(fd)!=0)throw new IOException("Could not sync account directory"); }
                finally { CloseDirectory(fd); }
            }
            finally { File.Delete(temporary); }
        }
        Account=account;
    }
    public static bool ValidUsername(string username)
    {
        if(Regex.IsMatch(username,@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z"))return true;
        return username.Length<=254 && !username.Any(char.IsWhiteSpace) && System.Net.Mail.MailAddress.TryCreate(username,out var address)
            && address.Address==username && username.Count(c=>c=='@')==1 && !username.Any(c=>char.IsControl(c)||c is '<' or '>' or '"');
    }
    public bool Verify(string username,string password)=>Account is { } account && password.Length<=256
        && string.Equals(username.Trim(),account.Username,StringComparison.OrdinalIgnoreCase)
        && hasher.VerifyHashedPassword(account,account.PasswordHash,password)!=PasswordVerificationResult.Failed;
    [DllImport("libc",EntryPoint="open",SetLastError=true)] static extern int OpenDirectory(string path,int flags);
    [DllImport("libc",EntryPoint="fsync",SetLastError=true)] static extern int SyncDirectory(int fd);
    [DllImport("libc",EntryPoint="close")] static extern int CloseDirectory(int fd);
}
