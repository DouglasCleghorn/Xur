using System.Security.Cryptography;
using Xur.Control;
public static class AccountTests
{
    public static void Run(Action<bool,string> check)
    {
        var directory=Path.Combine(Path.GetTempPath(),"xur-account-"+Guid.NewGuid());Directory.CreateDirectory(directory);
        try
        {
            var key=RandomNumberGenerator.GetBytes(32);var auth=new Bootstrap(signingKey:key,directory:directory);var code=auth.DisplayCode;
            var setup=auth.Login("xur",code).Session;
            check(auth.CanSetup(setup) && !auth.Authorized(setup),"Token session is restricted to account creation");
            check(auth.CreateAccount(null,"owner","a long test password").Status==401,"Anonymous account creation is denied");
            check(auth.CreateAccount(setup,"owner","short").Status==400 && !auth.AccountConfigured,"Invalid password does not consume setup");
            var result=auth.CreateAccount(setup,"Owner","a long test password");
            check(result.Status==200 && auth.Authorized(result.Session),"Account creation returns a manager session");
            check(auth.DisplayCode=="" && auth.Login("xur",code).Status==401 && !auth.CanSetup(setup) && !auth.Authorized(setup),"Configured account removes code and invalidates bootstrap sessions");
            check(auth.CreateAccount(setup,"other","another password").Status==409,"Second setup cannot overwrite the account");
            var file=Path.Combine(directory,"manager-account.json");
            check(!File.ReadAllText(file).Contains("a long test password") && File.GetUnixFileMode(file)==(UnixFileMode.UserRead|UnixFileMode.UserWrite),"Account persists a password hash with owner-only permissions");
            var restart=new Bootstrap(signingKey:key,directory:directory);
            check(restart.AccountConfigured && restart.DisplayCode=="" && restart.Authorized(result.Session),"Account and manager JWT survive host restart");
            check(restart.PasswordLogin("owner","wrong password").Status==401 && restart.PasswordLogin("owner","a long test password").Status==200,"Password login validates credentials and case-insensitive username");
            for(var i=0;i<4;i++)restart.PasswordLogin("owner","wrong");
            check(restart.PasswordLogin("owner","a long test password").Status==429,"Password login is rate limited");
            restart.Initialize("ABC-DEF");check(restart.DisplayCode=="","Answer token cannot re-enable bootstrap after account setup");
            File.WriteAllText(file,"{}");bool blocked=false;try{_ = new Bootstrap(signingKey:key,directory:directory);}catch(IOException){blocked=true;}
            check(blocked,"Corrupt account state does not reopen token setup");
        }
        finally { Directory.Delete(directory,true); }
    }
}
