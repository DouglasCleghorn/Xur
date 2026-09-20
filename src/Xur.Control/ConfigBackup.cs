using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;
public static class ConfigBackup
{
    public static byte[] Create(Profile[] profiles,JsonElement host,StationDefinition[]? workstations=null)=>JsonSerializer.SerializeToUtf8Bytes(new{
        schema=1,kind="xur-configuration",createdAt=DateTimeOffset.UtcNow,application=ApplicationIdentity.Id,profiles,host,workstations,
        includes=new[]{"Saved profiles and workload definitions","GPU/USB assignments and workstation user references","Timezone and NTP preferences","GPU power limits","Selected model/container recipes","Update preferences"},
        excludes=new[]{"Account passwords, API keys and Hugging Face token","TLS keys and Tailscale identity","Sunshine pairing keys","Models, images, volumes and user files","Container build contexts","Running workload state and operation journals"},
        note="Configuration export for recovery and reference. Workload definitions may contain private environment variables or URLs. Store privately. Data and credentials require separate backups."
    },new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true});
}
