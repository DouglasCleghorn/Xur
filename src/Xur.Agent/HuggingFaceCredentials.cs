using System.Net.Http.Headers;
using System.Text.RegularExpressions;
namespace Xur.Agent;

// Never export the token or place it in a recipe, command argument or log.
public sealed class HuggingFaceCredentials(string state)
{
    public string TokenPath => Path.Combine(state,"secrets","huggingface-token");
    public bool Configured => File.Exists(TokenPath);
    public string? Read() { try { return File.ReadAllText(TokenPath); } catch(FileNotFoundException) { return null; } catch(DirectoryNotFoundException) { return null; } }
    public void Save(string? token)
    {
        if(string.IsNullOrWhiteSpace(token)) { File.Delete(TokenPath);return; }
        token=token.Trim();
        if(!Regex.IsMatch(token,@"^hf_[A-Za-z0-9]{8,252}$"))throw new InvalidOperationException("Enter a valid Hugging Face access token beginning with hf_.");
        var folder=Path.GetDirectoryName(TokenPath)!;Directory.CreateDirectory(folder,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var temp=TokenPath+"."+Guid.NewGuid().ToString("N");
        try { using(var stream=new FileStream(temp,new FileStreamOptions {Mode=FileMode.CreateNew,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
            using(var writer=new StreamWriter(stream))writer.Write(token);
            File.Move(temp,TokenPath,true);
        } finally { File.Delete(temp); }
    }
    public HttpRequestMessage Request(string url)
    {
        var request=new HttpRequestMessage(HttpMethod.Get,url);
        if(request.RequestUri is {Scheme:"https",Host:"huggingface.co",IsDefaultPort:true,UserInfo:""} && Read() is {Length:>0} token)
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        return request;
    }
    public string[] ContainerArguments()=>Configured
        ? ["--volume",TokenPath+":/run/secrets/huggingface-token:ro,z","--env","HF_TOKEN_PATH=/run/secrets/huggingface-token"] : [];
}
public record HuggingFaceTokenRequest(string? Token);
