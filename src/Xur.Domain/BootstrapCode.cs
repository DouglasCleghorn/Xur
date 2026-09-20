namespace Xur.Domain;

public static class BootstrapCode
{
    // Accept both the displayed ABC-DEF form and six characters from automation.
    public static string Parse(string value)
    {
        value=value.Trim().ToUpperInvariant().Replace('O','0').Replace('I','1').Replace('L','1');
        if(value.Length==7 && value[3]=='-')value=value.Remove(3,1);
        if(value.Length!=6 || value.Any(c=>!"0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c)))throw new FormatException("Invalid access code format");
        return value;
    }
}
