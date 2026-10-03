using System.Security.Cryptography;
using Xur.Control;

public static class AccountPersistenceTests
{
    public static void Run(Action<bool,string> check)
    {
        var directory=Path.Combine(Path.GetTempPath(),"xur-account-recovery-"+Guid.NewGuid());
        try
        {
            // Fail only the operation after the non-overwriting rename; no filesystem damage.
            bool fail=true;int syncs=0;
            var store=new ManagerAccountStore(directory,_=> { syncs++;if(fail)throw new IOException("Injected directory sync failure"); });
            var key=RandomNumberGenerator.GetBytes(32);
            var auth=new Bootstrap(store,signingKey:key);
            var code=auth.DisplayCode;var setup=auth.Login("xur",code).Session;
            bool failed=false;
            try { auth.CreateAccount(setup,"Owner","a long recovery password"); }catch(IOException){failed=true;}
            var file=Path.Combine(directory,"manager-account.json");var published=File.ReadAllText(file);
            check(failed && auth.AccountConfigured && auth.Username=="Owner" && syncs==1,
                "Post-rename sync failure reports failure while retaining the configured account");
            check(auth.DisplayCode=="" && !auth.CanSetup(setup) && !auth.Authorized(setup) && auth.Login("xur",code).Status==401,
                "A published account closes bootstrap even while durability is pending");
            check(auth.CreateAccount(setup,"other","another password").Status==409 && File.ReadAllText(file)==published,
                "Setup retries cannot replace the published account after a durability failure");
            check(!Directory.EnumerateFiles(directory,"*.tmp").Any() && !published.Contains("a long recovery password")
                && File.GetUnixFileMode(file)==(UnixFileMode.UserRead|UnixFileMode.UserWrite)
                && File.GetUnixFileMode(directory)==(UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute),
                "Failed account sync preserves hashing, owner-only permissions, and temporary-file cleanup");
            check(auth.PasswordLogin("owner","wrong password").Status==401 && syncs==1,
                "Wrong credentials cannot trigger account durability recovery");
            failed=false;try { auth.PasswordLogin("owner","a long recovery password"); }catch(IOException){failed=true;}
            check(failed && syncs==2 && auth.AccountConfigured,
                "Persistent sync failure prevents password login from issuing a manager session");
            failed=false;try { _=new ManagerAccountStore(directory,_=>throw new IOException("Injected restart sync failure")); }catch(IOException){failed=true;}
            check(failed,"Restart fails closed when a loaded account cannot be made durable");
            fail=false;
            var login=auth.PasswordLogin("owner","a long recovery password");
            check(login.Status==200 && auth.Authorized(login.Session) && syncs==3 && File.ReadAllText(file)==published,
                "Correct password login retries sync and grants a manager session only after successful durability");
            check(auth.PasswordLogin("Owner","a long recovery password").Status==200 && syncs==3,
                "Successful durability recovery is bounded to one successful retry");
            auth.Initialize(code);check(auth.DisplayCode=="" && !auth.CanSetup(setup),
                "Initialization cannot reopen bootstrap following account recovery");
            var restart=new Bootstrap(signingKey:key,directory:directory);
            check(restart.AccountConfigured && restart.PasswordLogin("owner","a long recovery password").Status==200
                && restart.Authorized(login.Session),"Recovered account credentials and manager JWT survive restart");
            bool overwriteBlocked=false;try { new ManagerAccountStore(directory).Create("other","another password"); }catch(InvalidOperationException){overwriteBlocked=true;}
            check(overwriteBlocked && File.ReadAllText(file)==published,"Loaded account cannot be overwritten by another create");
            File.WriteAllText(file,"{}");int invalidSyncs=0;
            failed=false;try { _=new ManagerAccountStore(directory,_=>invalidSyncs++); }catch(IOException){failed=true;}
            check(failed && invalidSyncs==0,"Malformed account is rejected before durability recovery and cannot reopen bootstrap");
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
        try
        {
            var store=new ManagerAccountStore(directory);
            var auth=new Bootstrap(store);var setup=auth.Login("xur",auth.DisplayCode).Session;
            var file=Path.Combine(directory,"manager-account.json");Directory.CreateDirectory(file);
            bool failed=false;try { auth.CreateAccount(setup,"Owner","a long recovery password"); }catch(IOException){failed=true;}
            check(failed && !auth.AccountConfigured && auth.CanSetup(setup) && !Directory.EnumerateFiles(directory,"*.tmp").Any(),
                "Failure before rename leaves bootstrap available and removes the temporary account");
            Directory.Delete(file);
            var result=auth.CreateAccount(setup,"Owner","a long recovery password");
            check(result.Status==200 && auth.Authorized(result.Session),
                "Setup can retry safely after a pre-rename failure is resolved");
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }
}
