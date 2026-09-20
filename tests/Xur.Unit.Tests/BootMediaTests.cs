using System.Text.Json;
using Xur.Agent;

static class BootMediaTests
{
    public static void Run(Action<bool, string> check)
    {
        using var doc = JsonDocument.Parse("""
        {"path":"/dev/sdc","size":137438953472,"children":[
          {"path":"/dev/sdc1","label":"XUR USB","uuid":"ABCD-1234","partuuid":"partition-1","mountpoints":[null]}]}
        """);
        var disk = doc.RootElement;
        check(Storage.IsBootMedia(disk, @"quiet inst.stage2=hd:LABEL=XUR\x20USB rd.live.ram=1"),
            "Unmounted large USB parent remains protected after a live RAM copy, including escaped labels");
        check(Storage.IsBootMedia(disk, "inst.stage2=hd:UUID=ABCD-1234:/installer") &&
              Storage.IsBootMedia(disk, "inst.stage2=hd:PARTUUID=partition-1") &&
              Storage.IsBootMedia(disk, "inst.stage2=hd:/dev/sdc1"),
            "UUID, partition UUID and explicit child boot sources protect their whole disk");
        check(!Storage.IsBootMedia(disk, "inst.stage2=hd:LABEL=XUR_USB") &&
              !Storage.IsBootMedia(disk, "other=hd:UUID=ABCD-1234") &&
              !Storage.IsBootMedia(disk, "inst.stage2=hd:LABEL="),
            "Unrelated disks and unrelated or empty boot arguments do not match");
        using var duplicate = JsonDocument.Parse(doc.RootElement.GetRawText().Replace("sdc", "sdd"));
        check(Storage.IsBootMedia(disk, "inst.stage2=hd:UUID=ABCD-1234") &&
              Storage.IsBootMedia(duplicate.RootElement, "inst.stage2=hd:UUID=ABCD-1234"),
            "Duplicate boot source identities protect every matching parent");
    }
}
