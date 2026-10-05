using Xur.Domain;
namespace Xur.Control;

public static class ProfileSwitchAudit
{
    public static ProfileSwitchOrigin Request(HttpContext context)
    {
        var trigger=context.Request.Headers["X-Xur-Switch-Trigger"].ToString();
        if(trigger is not ("web-menu" or "web-keyboard" or "web-controller" or "web-route"))trigger=context.Request.Path.StartsWithSegments("/api")?"api":"web-form";
        var user=context.Items["apiKey"] is ApiKeyInfo key?"api-key:"+key.Id:context.User.Identity?.Name??"unknown";
        return new(trigger,user);
    }
}
