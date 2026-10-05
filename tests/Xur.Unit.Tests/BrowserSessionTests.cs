using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using Xur.Control;

public static class BrowserSessionTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now=new(2026,10,4,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    public static void Run(Action<bool,string> check)
    {
        var directory=Path.Combine(".build","tests","browser-sessions",Guid.NewGuid().ToString("N"));
        try
        {
            var clock=new Clock();var key=Bootstrap.LoadSigningKey(directory);
            var auth=new Bootstrap(clock,key,directory);
            var setup=auth.Login("xur",auth.DisplayCode).Session;
            check(auth.RememberBrowserSession(setup)==null,"Setup sessions cannot become persistent manager sessions");
            var created=auth.CreateAccount(setup,"owner","a long test password",persistent:true).Session!;
            var api=auth.PasswordLogin("owner","a long test password").Session!;
            var browser=auth.PasswordLogin("owner","a long test password",persistent:true).Session!;
            var upgraded=auth.RememberBrowserSession(api)!;
            var handler=new JwtSecurityTokenHandler();
            check(!handler.ReadJwtToken(created).Payload.ContainsKey("exp") && !handler.ReadJwtToken(browser).Payload.ContainsKey("exp"),
                "Browser signup and password login issue signed sessions without a time limit");
            check(handler.ReadJwtToken(api).Payload.ContainsKey("exp") && upgraded!=api && auth.Authorized(upgraded),
                "Existing valid browser cookies upgrade while API tokens keep their expiry");
            clock.Now=clock.Now.AddHours(9);
            check(auth.Authorized(created) && auth.Authorized(browser) && auth.Authorized(upgraded) && !auth.Authorized(api),
                "Browser sessions remain valid beyond eight hours while API sessions expire");
            check(auth.RememberBrowserSession(api)==null && auth.RememberBrowserSession(null)==null && auth.RememberBrowserSession("invalid")==null,
                "Expired, missing and invalid sessions cannot become persistent");
            clock.Now=clock.Now.AddYears(20);
            var restart=new Bootstrap(clock,Bootstrap.LoadSigningKey(directory),directory);
            check(restart.Authorized(created) && restart.Authorized(browser) && restart.RememberBrowserSession(browser)==browser,
                "Persisted account and signing key preserve browser sessions after restart and decades of inactivity");
            check(!new Bootstrap(clock,directory:directory).Authorized(browser),"Persistent sessions still require the machine's signing key");
            var subject=handler.ReadJwtToken(api).Subject;
            string WithoutExpiry(string purpose)=>handler.WriteToken(new JwtSecurityToken("xur","xur-control",
                [new Claim("sub",subject),new Claim("purpose",purpose)],null,null,
                new SigningCredentials(new SymmetricSecurityKey(key),SecurityAlgorithms.HmacSha256)));
            check(!restart.Authorized(WithoutExpiry("manager")) && !restart.CanSetup(WithoutExpiry("bootstrap")),
                "Missing expiry is accepted only for explicitly persistent browser sessions");
            var context=new DefaultHttpContext();BrowserSecurity.SetSession(context,browser,persistent:true);
            var cookie=context.Response.Headers.SetCookie.ToString();
            check(cookie.Contains("max-age=34560000") && cookie.Contains("httponly") && cookie.Contains("secure") && cookie.Contains("samesite=strict") && cookie.Contains("path=/"),
                "Persistent browser cookies survive browser restarts and retain cookie protections");
            context=new DefaultHttpContext();BrowserSecurity.SetSession(context,setup!,persistent:false);
            check(context.Response.Headers.SetCookie.ToString().Contains("max-age=28800"),"Setup browser cookies retain their eight-hour limit");
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }
}
