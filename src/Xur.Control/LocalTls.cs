using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace Xur.Control;
public static class LocalTls
{
    public static X509Certificate2 Load(string directory)
    {
        var path=Path.Combine(directory,"manager-tls.pfx");
        if(File.Exists(path))return X509CertificateLoader.LoadPkcs12FromFile(path,null);
        using var rsa=RSA.Create(3072);var request=new CertificateRequest("CN=Xur",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var names=new SubjectAlternativeNameBuilder();names.AddDnsName("localhost");names.AddDnsName(Environment.MachineName);
        foreach(var address in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(a=>a.Address).Distinct())
            if(!address.IsIPv6LinkLocal)names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(5));
        Directory.CreateDirectory(directory);
        using(var file=new FileStream(path,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
        {file.Write(cert.Export(X509ContentType.Pfx));file.Flush(true);}
        return X509CertificateLoader.LoadPkcs12FromFile(path,null);
    }
}
