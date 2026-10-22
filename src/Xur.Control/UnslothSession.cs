using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xur.Domain;

namespace Xur.Control;

public record UnslothLoginState(bool Ready,bool NeedsPassword,string Message);

// One shared Studio owner account. Its password and tokens never enter the
// browser: the proxy authenticates browsers independently using Xur sessions.
public sealed class UnslothSession(string path,Func<Task<string>> bootstrapPassword,HttpClient client,Func<DateTimeOffset>? clock=null)
{
    readonly SemaphoreSlim gate=new(1,1);
    string? token,refreshToken;
    DateTimeOffset Now=>clock?.Invoke()??DateTimeOffset.UtcNow;
    DateTimeOffset expires,retryAfter;
    UnslothLoginState state=new(false,false,"Preparing Studio sign-in.");
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public async Task<UnslothLoginState> Prepare(string? suppliedPassword=null)
    {
        await gate.WaitAsync();
        try
        {
            if(suppliedPassword==null && token!=null && Now<expires)return state;
            if(suppliedPassword==null && Now<retryAfter)return state;
            token=null;
            try
            {
                if(suppliedPassword==null && refreshToken!=null)
                {
                    using var refreshed=await client.PostAsJsonAsync("api/auth/refresh",new{refresh_token=refreshToken});
                    if(refreshed.IsSuccessStatusCode)
                    {
                        var renewed=await ReadLogin(refreshed);
                        if(!renewed.MustChangePassword)return Accept(renewed);
                    }
                    else if(refreshed.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                        throw new InvalidOperationException("Studio session renewal is unavailable. Retry shortly.");
                    refreshToken=null;
                }
                var saved=suppliedPassword==null?Read():null;
                Login? login=null;
                if(saved?.PendingPassword is {} pending)
                {
                    // A crash can occur after Studio committed the new password
                    // but before Xur committed its local credential record.
                    login=await SignIn(pending);
                    if(login!=null){saved=new(pending,null);Save(saved);}
                }
                var password=suppliedPassword??saved?.Password??await bootstrapPassword();
                login??=await SignIn(password);
                if(login==null)
                {
                    retryAfter=Now.AddMinutes(5);
                    return state=new(false,true,"Enter the current Unsloth password once to connect this existing Studio account to Xur.");
                }
                if(saved==null || saved.PendingPassword!=null || login.MustChangePassword)
                {
                    var replacement=saved?.PendingPassword??Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    // Persist both sides before changing Studio, so either side
                    // of an interrupted password rotation can be recovered.
                    Save(new(password,replacement));
                    using var request=new HttpRequestMessage(HttpMethod.Post,"api/auth/change-password")
                    {Content=JsonContent.Create(new{current_password=password,new_password=replacement})};
                    request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",login.AccessToken);
                    using var response=await client.SendAsync(request);
                    if(!response.IsSuccessStatusCode)throw new InvalidOperationException("Studio could not complete managed password setup. Retry from this page.");
                    login=await ReadLogin(response);
                    if(login.MustChangePassword)throw new InvalidOperationException("Studio still requires a password change. Check the supported Studio version.");
                    Save(new(replacement,null));
                }
                return Accept(login);
            }
            catch(Exception e) when(e is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidOperationException)
            {
                retryAfter=Now.AddSeconds(30);
                return state=new(false,false,e is InvalidOperationException?e.Message:"Studio sign-in is unavailable. Check the workload logs and retry.");
            }
        }
        finally{gate.Release();}
    }
    UnslothLoginState Accept(Login login)
    {
        token=login.AccessToken;refreshToken=login.RefreshToken;
        expires=Now.AddMinutes(2);retryAfter=default;
        return state=new(true,false,"Ready · Sign in through Xur");
    }
    public async Task<string> AccessToken()
    {
        var result=await Prepare();
        if(!result.Ready || token==null)throw new InvalidOperationException(result.Message);
        return token;
    }
    public async Task Invalidate(){await gate.WaitAsync();try{token=null;refreshToken=null;expires=default;retryAfter=default;}finally{gate.Release();}}
    public static HttpClient CreateClient()=>new(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false})
        {BaseAddress=new Uri(UnslothWorkload.Backend+"/"),Timeout=TimeSpan.FromSeconds(10)};
    async Task<Login?> SignIn(string password)
    {
        if(string.IsNullOrEmpty(password) || password.Length>4096)return null;
        using var response=await client.PostAsJsonAsync("api/auth/login",new{username="unsloth",password});
        if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)return null;
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException("Studio login is unavailable or rate limited. Retry shortly.");
        return await ReadLogin(response);
    }
    static async Task<Login> ReadLogin(HttpResponseMessage response)
    {
        await response.Content.LoadIntoBufferAsync(65536);
        var result=await response.Content.ReadFromJsonAsync<Login>();
        if(result==null || string.IsNullOrWhiteSpace(result.AccessToken) || result.AccessToken.Length>16384 || result.AccessToken.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('.' or '_' or '-')) || string.IsNullOrEmpty(result.RefreshToken) || result.RefreshToken.Length>16384)
            throw new InvalidOperationException("Studio returned an unsupported login response.");
        return result;
    }
    Stored? Read()
    {
        if(!File.Exists(path))return null;
        var saved=JsonSerializer.Deserialize<Stored>(File.ReadAllText(path),Json);
        if(saved==null || string.IsNullOrEmpty(saved.Password))throw new InvalidOperationException("Restore Xur's saved Studio credentials or reconnect the existing account.");
        return saved;
    }
    void Save(Stored saved)
    {
        var directory=Path.GetDirectoryName(path)!;Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        using(var file=new FileStream(path+".tmp",new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
        {File.SetUnixFileMode(path+".tmp",UnixFileMode.UserRead|UnixFileMode.UserWrite);JsonSerializer.Serialize(file,saved,Json);file.Flush(true);}
        File.Move(path+".tmp",path,true);
    }
    public sealed record Stored(string Password,string? PendingPassword);
    sealed record Login([property:JsonPropertyName("access_token")]string AccessToken,[property:JsonPropertyName("refresh_token")]string RefreshToken,[property:JsonPropertyName("must_change_password")]bool MustChangePassword);
}
