using System.Net.Http.Json;
using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;
public sealed class ConsoleComputerName(HttpClient client,bool local=false)
{
    string Prefix=>local?"/local":"";
    const string Prompt="Choose the name shown on your network and in Tailscale.\nUse letters, numbers and hyphens. Enter saves; Escape skips for now.";
    string current="xur",message=Prompt;
    bool saved;
    public bool Closed {get;private set;}
    public ConsoleScreen Screen=>new("computer-name"+(saved?"-saved":""),"Server name",message,[new('0',saved?"Back to menu":"Skip for now")],saved?null:current);
    public async Task Open()
    {
        Closed=false;saved=false;message=Prompt;
        try{current=(await client.GetFromJsonAsync<ComputerNameStatus>(Prefix+"/computer-name"))?.Name??"xur";}catch{current="xur";}
    }
    public void Select(char key){if(key=='0')Closed=true;}
    public async Task Submit(string name)
    {
        try
        {
            using var result=await client.PostAsJsonAsync(Prefix+"/computer-name",new ComputerNameRequest(name));
            if(result.IsSuccessStatusCode){var status=await result.Content.ReadFromJsonAsync<ComputerNameStatus>();message=status?.Name+"\n"+status?.Message;saved=true;return;}
            using var error=JsonDocument.Parse(await result.Content.ReadAsStringAsync());message=error.RootElement.GetProperty("error").GetString()??"Could not save the server name.";
        }
        catch{message="Could not save the server name. Check status and try again.";}
    }
}
