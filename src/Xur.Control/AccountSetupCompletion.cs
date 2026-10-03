namespace Xur.Control;

public static class AccountSetupCompletion
{
    // The account and permanent session already exist. Bootstrap authentication
    // is disabled by account state, even if deleting the old token fails.
    public static void Run(string directory,Action refresh,Action<string,Exception> warning)
    {
        try { File.Delete(Path.Combine(directory,"bootstrap-token")); }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException)
        { warning("Account created; bootstrap token cleanup failed",e); }
        try { refresh(); }
        catch(Exception e) { warning("Account created; console refresh failed",e); }
    }
}
