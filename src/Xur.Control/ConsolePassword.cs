namespace Xur.Control;
public static class ConsolePassword
{
    public static string? Read()
    {
        if(Console.IsInputRedirected)return Console.ReadLine();
        var value=new System.Text.StringBuilder();
        while(true)
        {
            var key=Console.ReadKey(intercept:true);
            if(key.Key==ConsoleKey.Enter){Console.WriteLine();return value.ToString();}
            if(key.Key==ConsoleKey.Escape){Console.WriteLine();return "/cancel";}
            if(key.Key==ConsoleKey.Backspace){if(value.Length>0){value.Length--;Console.Write("\b \b");}continue;}
            if(key.KeyChar is >= ' ' and <= '~' && value.Length<64){value.Append(key.KeyChar);Console.Write('*');}
        }
    }
}
