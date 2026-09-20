using QRCoder;
namespace Xur.Control;
public static class ConsoleQr
{
    public static string LoginUrl(string serveUrl,bool accountConfigured,string code)
    {
        if(!Uri.TryCreate(serveUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0)return "";
        return new Uri(uri,"/login").AbsoluteUri+(accountConfigured||!System.Text.RegularExpressions.Regex.IsMatch(code,@"^[0-9A-Z]{3}-?[0-9A-Z]{3}$")?"":"#code="+Uri.EscapeDataString(code));
    }
    public static string[] Rows(string url)
    {
        using var generator=new QRCodeGenerator();using var data=generator.CreateQrCode(url,QRCodeGenerator.ECCLevel.M);
        var matrix=data.ModuleMatrix;var rows=new List<string>();
        // Light pixels are white terminal blocks; dark pixels are the black background.
        // Two square modules per character cell preserve aspect ratio and quiet zone.
        for(int y=0;y<matrix.Count;y+=2)
        {
            var row=new char[matrix.Count];
            for(int x=0;x<row.Length;x++)
            {
                bool top=!matrix[y][x],bottom=y+1>=matrix.Count||!matrix[y+1][x];
                row[x]=top?(bottom?'█':'▀'):(bottom?'▄':' ');
            }
            rows.Add(new string(row));
        }
        return rows.ToArray();
    }
}
