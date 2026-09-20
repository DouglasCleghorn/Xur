using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
namespace Xur.Domain;

public record IpConfiguration(string Method="auto",string[]? Addresses=null,string? Gateway=null,string[]? Dns=null);
public record NetworkConfiguration(string Interface="",string MacAddress="",IpConfiguration? Ipv4=null,IpConfiguration? Ipv6=null);
public record NetworkDevice(string Interface,string MacAddress,string State,string[] Addresses,string? Connection,IpConfiguration Ipv4,IpConfiguration Ipv6,bool Editable);
public record NetworkChange(string Id,string Interface,string Candidate,string? Previous,string Checkpoint,DateTimeOffset Expires,string Stage,string Message,string[] Addresses);
public record NetworkSettingsStatus(NetworkDevice[] Devices,NetworkChange? Pending);
public record NetworkChangeRequest(string Id);

public static class NetworkValidation
{
    public static NetworkConfiguration Validate(NetworkConfiguration value)
    {
        if(value==null)throw new InvalidOperationException("Network configuration is required.");
        var name=value.Interface?.Trim()??"";var mac=value.MacAddress?.Trim().ToUpperInvariant()??"";
        if(name.Length>0 && !Regex.IsMatch(name,@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,14}$"))throw new InvalidOperationException("Choose an observed network interface.");
        if(mac.Length>0 && (!Regex.IsMatch(mac,@"^(?:[0-9A-F]{2}:){5}[0-9A-F]{2}$") || mac=="00:00:00:00:00:00" || (Convert.ToByte(mac[..2],16)&1)!=0))throw new InvalidOperationException("Enter a unicast MAC address such as 02:00:00:00:00:10.");
        if(name.Length==0 && mac.Length==0)throw new InvalidOperationException("Select an interface or its MAC address.");
        var ipv4=Family(value.Ipv4??new(),false);var ipv6=Family(value.Ipv6??new(),true);
        if(ipv4.Method=="disabled" && ipv6.Method=="disabled")throw new InvalidOperationException("Keep at least one IP family enabled.");
        return new(name,mac,ipv4,ipv6);
    }
    static IpConfiguration Family(IpConfiguration value,bool ipv6)
    {
        var label=ipv6?"IPv6":"IPv4";var method=value.Method?.Trim().ToLowerInvariant();
        if(method is not ("auto" or "manual" or "disabled"))throw new InvalidOperationException($"{label}: choose automatic, static or disabled.");
        var addresses=value.Addresses??[];var dns=value.Dns??[];var gateway=value.Gateway?.Trim()??"";
        if(addresses.Length>8 || dns.Length>8)throw new InvalidOperationException($"{label}: use at most eight addresses and DNS servers.");
        if(method!="manual" && (addresses.Length>0 || gateway.Length>0))throw new InvalidOperationException($"{label}: addresses and gateway require static mode.");
        if(method=="disabled" && dns.Length>0)throw new InvalidOperationException($"{label}: disabled networking cannot have DNS servers.");
        if(method=="manual" && addresses.Length==0)throw new InvalidOperationException($"{label}: enter an address with its prefix length.");
        var normalized=addresses.Select(entry=>
        {
            var parts=(entry??"").Trim().Split('/');
            if(parts.Length!=2 || !int.TryParse(parts[1],out var prefix) || prefix<1 || prefix>(ipv6?128:32))throw new InvalidOperationException($"{label}: enter CIDR addresses, such as {(ipv6?"2001:db8::10/64":"192.0.2.10/24")}.");
            var address=Address(parts[0],ipv6,label);
            if(address.IsIPv6LinkLocal)throw new InvalidOperationException("Use a global or unique-local IPv6 address; link-local addresses are automatic.");
            if(!ipv6 && prefix<31)
            {
                var bytes=address.GetAddressBytes();var number=((uint)bytes[0]<<24)|((uint)bytes[1]<<16)|((uint)bytes[2]<<8)|bytes[3];var hosts=(1u<<(32-prefix))-1;
                if((number&hosts)==0 || (number&hosts)==hosts)throw new InvalidOperationException("Use a host IPv4 address, not the subnet or broadcast address.");
            }
            return address+"/"+prefix;
        }).Distinct().ToArray();
        if(gateway.Length>0)
        {
            var ip=Address(gateway,ipv6,label);gateway=ip.ToString();
            if(!ip.IsIPv6LinkLocal && !normalized.Any(c=>Contains(c,ip)))throw new InvalidOperationException($"{label}: the gateway must be within an address subnet.");
            if(normalized.Any(c=>c.Split('/')[0]==gateway))throw new InvalidOperationException($"{label}: the gateway cannot be this machine's own address.");
        }
        return new(method!,normalized,gateway,dns.Select(d=>
        {
            var address=Address(d?.Trim()??"",ipv6,label);
            if(address.IsIPv6LinkLocal)throw new InvalidOperationException("Use a DNS server address that does not need an interface scope.");
            return address.ToString();
        }).Distinct().ToArray());
    }
    static bool Contains(string cidr,IPAddress address)
    {
        var parts=cidr.Split('/');var network=IPAddress.Parse(parts[0]).GetAddressBytes();var bytes=address.GetAddressBytes();var prefix=int.Parse(parts[1]);
        for(var i=0;i<network.Length;i++){var bits=Math.Clamp(prefix-i*8,0,8);var mask=bits==0?0:255<<(8-bits);if((network[i]&mask)!=(bytes[i]&mask))return false;}
        return true;
    }
    static IPAddress Address(string value,bool ipv6,string label)
    {
        // IPAddress also accepts legacy shorthand IPv4 integers; require dotted decimal.
        if(value.Contains('%') || !ipv6 && !Regex.IsMatch(value,@"^(?:(?:0|[1-9][0-9]{0,2})\.){3}(?:0|[1-9][0-9]{0,2})$") || !IPAddress.TryParse(value,out var ip) || ip.AddressFamily!=(ipv6?AddressFamily.InterNetworkV6:AddressFamily.InterNetwork) || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6Multicast || !ipv6 && ip.GetAddressBytes()[0]>=224)
            throw new InvalidOperationException($"{label}: enter a valid unicast IP address without a URL, port or scope suffix.");
        return ip;
    }
}
