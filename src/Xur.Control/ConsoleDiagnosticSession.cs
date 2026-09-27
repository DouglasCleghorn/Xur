using Xur.Domain;
namespace Xur.Control;

// Call while holding the same gate as physical input and asynchronous menu refresh.
public static class ConsoleDiagnosticSession
{
    public static async Task<IResult> Act(ConsoleDiagnosticAction action,Func<ConsoleDiagnosticSnapshot> snapshot,
        Action consume,Func<int,Task> select,Func<string,Task> submit)
    {
        var current=snapshot();
        if(action.Revision!=current.Revision)return Results.Conflict(new{error="Screen changed. Read the current console before sending another action."});
        if((action.Option==null)==(action.Text==null))return Results.BadRequest(new{error="Provide exactly one option or text value."});
        if(action.Text is {} text)
        {
            if(!current.AcceptsText || text.Length>(current.Secret?64:1024) || text.Any(c=>c<' ' || c>'~'))return Results.BadRequest(new{error="This screen cannot accept that text."});
            consume();await submit(text);
        }
        else
        {
            if(current.Options.SingleOrDefault(o=>o.Id==action.Option)?.Enabled!=true)return Results.BadRequest(new{error="Option absent or disabled on the current screen."});
            if(current.Screen=="setup-confirm" && action.Option=='y' && !action.ConfirmErase)
                return Results.Conflict(new{error="Erasing the reviewed disk requires confirmErase: true with the current screen revision."});
            consume();await select(action.Option!.Value);
        }
        return Results.Json(snapshot());
    }
}
