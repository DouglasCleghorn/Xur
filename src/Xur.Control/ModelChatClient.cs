using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;
// Keep raw streaming timings and server token counts; chunks are not tokens.
public sealed class ModelChatClient(HttpClient client)
{
    public async Task<LabResponse> Send(LabTarget target,LabMessage[] messages,int maxTokens,double temperature,int number,bool warmup,Func<string,string,Task>? delta,CancellationToken cancel)
    {
        var started=DateTimeOffset.UtcNow;var clock=Stopwatch.StartNew();double? first=null;int? input=null,output=null;string? finish=null;
        var text=new StringBuilder();var reasoning=new StringBuilder();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel);timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"/"+Uri.EscapeDataString(target.Workload.Route)+"/v1/chat/completions");
            request.Headers.Add("X-Xur-Expected-Workload",target.Workload.Id);
            request.Headers.Add("X-Xur-Expected-Endpoint",target.Instance.Endpoint);
            request.Content=JsonContent.Create(new {model="model",messages=messages.Select(m=>new{role=m.Role,content=m.Content}),max_tokens=maxTokens,temperature,stream=true,stream_options=new {include_usage=true}});
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"Model endpoint returned HTTP {(int)response.StatusCode}. Open the workload logs for details.");
            if(response.Content.Headers.ContentType?.MediaType!="text/event-stream")throw new InvalidOperationException("This endpoint did not return a chat event stream. Select a text-chat model.");
            using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var data=new StringBuilder();var received=0;bool done=false;
            async Task Event()
            {
                var value=data.ToString().Trim();data.Clear();if(value.Length==0)return;
                if(value=="[DONE]"){done=true;return;}
                using var doc=JsonDocument.Parse(value);var root=doc.RootElement;
                if(root.TryGetProperty("error",out _))throw new InvalidOperationException("The model reported an inference error. Open the workload logs for details.");
                if(root.TryGetProperty("usage",out var usage)&&usage.ValueKind==JsonValueKind.Object)
                {
                    if(usage.TryGetProperty("prompt_tokens",out var p)&&p.TryGetInt32(out var n)&&n>=0)input=n;
                    if(usage.TryGetProperty("completion_tokens",out p)&&p.TryGetInt32(out n)&&n>=0)output=n;
                }
                if(root.TryGetProperty("choices",out var choices)&&choices.ValueKind==JsonValueKind.Array)
                foreach(var choice in choices.EnumerateArray())
                {
                    if(choice.TryGetProperty("finish_reason",out var f)&&f.ValueKind==JsonValueKind.String)finish=f.GetString();
                    if(!choice.TryGetProperty("delta",out var d))continue;
                    string Read(string key)=>d.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
                    var content=Read("content");var thought=Read("reasoning_content");if(thought.Length==0)thought=Read("reasoning");
                    if(content.Length+thought.Length==0)continue;
                    first??=clock.Elapsed.TotalMilliseconds;text.Append(content);reasoning.Append(thought);
                    if(text.Length+reasoning.Length>131072)throw new InvalidOperationException("Model output exceeded the test limit.");
                    if(delta!=null)await delta(content,thought);
                }
            }
            while(!done&&await reader.ReadLineAsync(timeout.Token) is {} line)
            {
                received+=line.Length;if(received>4*1024*1024)throw new InvalidOperationException("Model stream exceeded the test limit.");
                if(line.Length==0){await Event();continue;}
                if(line.StartsWith("data:",StringComparison.Ordinal))data.AppendLine(line[5..].TrimStart());
            }
            if(data.Length>0)await Event();
            if(!done && finish==null)throw new InvalidOperationException("Model stream ended before completion.");
            return Result(null);
        }
        catch(OperationCanceledException) when(!cancel.IsCancellationRequested){return Result("The model request exceeded three minutes.");}
        catch(OperationCanceledException){throw;}
        catch(Exception e) when(e is HttpRequestException or IOException or JsonException or InvalidOperationException){return Result(e is InvalidOperationException?e.Message:"The model stream failed or was malformed.");}
        LabResponse Result(string? error)=>new(number,warmup,started,clock.Elapsed.TotalMilliseconds,first,input,output,text.ToString(),reasoning.ToString(),finish,error);
    }
}
