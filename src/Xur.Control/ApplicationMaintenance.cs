namespace Xur.Control;
public sealed class ApplicationMaintenance
{
    readonly object gate=new();int active;
    public const string Marker="/var/lib/xur/app/maintenance";
    public int Active {get{lock(gate)return active;}}
    public bool Enter(){lock(gate){if(File.Exists(Marker))return false;active++;return true;}}
    public void Exit(){lock(gate)active--;}
}
