namespace Xur.Agent;
public static class RegistryMirror
{
    public static string Configuration {get {using var stream=typeof(RegistryMirror).Assembly.GetManifestResourceStream("Xur.RegistryMirror")!;using var reader=new StreamReader(stream);return reader.ReadToEnd();}}
    public static void Ensure(string directory="/etc/containers/registries.conf.d")
    {
        Directory.CreateDirectory(directory);var path=Path.Combine(directory,"99-xur-docker-hub.conf");var text=Configuration;
        if(File.Exists(path)&&File.ReadAllText(path)==text)return;
        var temp=path+"."+Guid.NewGuid().ToString("N");
        try{File.WriteAllText(temp,text);File.Move(temp,path,true);}finally{File.Delete(temp);}
    }
}
