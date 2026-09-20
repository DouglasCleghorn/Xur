using System.Text.RegularExpressions;
namespace Xur.Control;
public static class StationLauncher
{
    public static string Address(string host,int port=47989)
    {
        if(host.Length is <1 or >253 || !Regex.IsMatch(host,@"^[a-zA-Z0-9.:\[\]-]+$") || host.StartsWith('-'))throw new InvalidOperationException("Invalid server address.");
        if(host.Contains(':')&&!host.StartsWith('['))host="["+host+"]";
        if(port is <1024 or >65514)throw new InvalidOperationException("Invalid streaming port.");
        return host+(port==47989?"":":"+port);
    }
    public static (string Text,string Name) Create(string host,string platform,int port=47989)
    {
        host=Address(host,port);
        return platform switch {
            "windows"=>($"@echo off\r\nstart \"\" \"%ProgramFiles%\\Moonlight Game Streaming\\Moonlight.exe\" stream \"{host}\" Desktop\r\n","Xur-workstation.cmd"),
            "macos"=>($"#!/bin/sh\nexec open -a Moonlight --args stream '{host}' Desktop\n","Xur-workstation.command"),
            "linux"=>($"[Desktop Entry]\nType=Application\nName=Xur workstation\nExec=moonlight stream {host} Desktop\nIcon=computer\nTerminal=false\n","Xur-workstation.desktop"),
            _=>throw new InvalidOperationException("Choose Windows, Linux or macOS.")
        };
    }
}
