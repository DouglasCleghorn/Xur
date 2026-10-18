using System.Globalization;
using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public static class StorageEnduranceReader
{
    public static async Task<StorageEndurance> Read(string path,bool? rotational,Func<string,string[],Task<ProcessResult>>? runner=null)
    {
        // Do not send SMART commands to rotating or unidentified media just to
        // populate this column. A missing bridge/device feature is not zero wear.
        if(rotational!=false)return new(UnavailableReason:rotational==true?"Not applicable to rotating disks":"Media type unavailable");
        try
        {
            string[] args=["--json","--info","--attributes","--nocheck=standby,2",path];
            var result=await (runner!=null?runner("smartctl",args):Processes.Run("smartctl",args,15));
            // SMART exit status is a bit mask: health warnings can accompany
            // valid readings. Parse stdout even when a health bit is set.
            var reading=Parse(result.StandardOutput);
            return reading.RemainingPercent!=null?reading:reading with {UnavailableReason=(result.ExitCode&3)!=0?"SMART unavailable or device asleep":reading.UnavailableReason};
        }
        catch(System.ComponentModel.Win32Exception){return new(UnavailableReason:"SMART tool unavailable");}
        catch{return new(UnavailableReason:"SMART could not be read");}
    }

    static JsonElement Field(JsonElement element,string name)=>element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(name,out var value)?value:default;
    static decimal? Number(JsonElement element)=>element.ValueKind is JsonValueKind.Number or JsonValueKind.String && decimal.TryParse(element.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)?n:null;
    static decimal? Number(JsonElement element,string name)=>Number(Field(element,name+"_s"))??Number(Field(element,name));
    public static StorageEndurance Parse(string json)
    {
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        var nvme=Field(root,"nvme_smart_health_information_log");
        // smartctl 7.5 normalizes supported ATA/SCSI/NVMe endurance. Older
        // versions expose standard NVMe/SCSI fields. Never guess ATA attribute
        // meanings from their numeric IDs or mistake spare blocks for endurance.
        var used=Number(Field(root,"endurance_used"),"current_percent")??Number(nvme,"percentage_used")??Number(root,"scsi_percentage_used_endurance_indicator");
        if(used is null or <0 or >255)return new(UnavailableReason:"Write endurance not reported");
        var remaining=Math.Max(0,100-used.Value);
        // Standard SMART exposes a wear percentage, not remaining writable
        // bytes. Do not extrapolate from host writes, a model/TBW lookup, spare
        // capacity, or NVMe's lifetime Endurance Estimate (which is not remaining).
        return new((double)remaining);
    }
}
