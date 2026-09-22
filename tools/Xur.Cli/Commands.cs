using System.CommandLine;
namespace Xur.Cli;
public static class Commands
{
    public static Task<int> Invoke(string[] args, Func<Task> rootAction, Func<string, bool, Task> command)
    {
        var root = new RootCommand("Xur appliance control and local setup menu");
        root.SetAction(async (_, _) => await rootAction());
        void Add(Command parent, string name, string action, bool json = false, bool redacted = false)
        {
            var c = new Command(name); var option = new Option<bool>(json ? "--json" : "--redacted");
            if (json || redacted) c.Options.Add(option);
            c.SetAction(async (p, _) => await command(action, (json || redacted) && p.GetValue(option)));
            parent.Subcommands.Add(c);
        }
        Add(root,"setup","setup");
        Add(root,"status","status",json:true);
        foreach (var name in new[]{"network","login","hardware","logs","tailscale"})
        {
            var c = new Command(name); root.Subcommands.Add(c);
            Add(c,name == "tailscale" ? "status" : "show",name,json:name=="hardware",redacted:name=="logs");
            if (name == "tailscale") Add(c,"qr","qr");
        }
        var updates=new Command("updates");root.Subcommands.Add(updates);
        Add(updates,"status","updates",json:true);
        foreach(var action in new[]{"check","stage","rollback","enable","disable"})Add(updates,action,"updates/"+action);
        var application=new Command("application-updates");root.Subcommands.Add(application);
        Add(application,"status","application-updates",json:true);
        foreach(var action in new[]{"check","update","rollback"})Add(application,action,"application-updates/"+action);
        var all=new Command("update-all");root.Subcommands.Add(all);
        Add(all,"status","update-all",json:true);
        Add(all,"start","update-all/start");
        Add(root,"shutdown","poweroff"); Add(root,"reboot","reboot");
        return root.Parse(args).InvokeAsync();
    }
}
