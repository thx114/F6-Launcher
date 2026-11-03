using Microsoft.Win32;

namespace HoYoShadeHub.Setup.Services;

public static class RegistryHelper
{

    public static void WriteUninstallInfo(string folder, string version, long size)
    {
        string exe = Path.Combine(folder, "HoYoShadeHub.exe");
        string setupExe = Path.Combine(folder, "HoYoShadeHub.Setup.exe");
        using var subkey = Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\F6 Launcher");
        subkey.SetValue("Publisher", "thx114", RegistryValueKind.String);
        subkey.SetValue("DisplayName", "F6 Launcher", RegistryValueKind.String);
        subkey.SetValue("DisplayIcon", exe, RegistryValueKind.String);
        subkey.SetValue("DisplayVersion", version, RegistryValueKind.String);
        subkey.SetValue("InstallLocation", folder, RegistryValueKind.String);
        subkey.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
        subkey.SetValue("InstallDate", $"{DateTime.Now:yyyyMMdd}", RegistryValueKind.String);
        subkey.SetValue("UninstallString", $"\"{setupExe}\" uninstall", RegistryValueKind.String);
        subkey.SetValue("QuietUninstallString", $"\"{setupExe}\" uninstall /S", RegistryValueKind.String);
    }


    public static void DeleteUninstallInfo()
    {
        Registry.LocalMachine.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\F6 Launcher", false);
    }


    public static void WriteUrlProtocol(string folder)
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\F6Launcher", false);
        Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\F6Launcher", "", "URL:F6Launcher Protocol");
        Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\F6Launcher", "URL Protocol", "");
        Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\F6Launcher\DefaultIcon", "", "HoYoShadeHub.exe,1");
        Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\F6Launcher\Shell\Open\Command", "", $"""
            "{Path.Combine(folder, "HoYoShadeHub.exe")}" "%1"
            """);
    }


    public static void DeleteUrlProtocol()
    {
        Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\F6Launcher", false);
    }


    public static string? GetInstallLocation()
    {
        using var subkey = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\F6 Launcher");
        return subkey?.GetValue("InstallLocation") as string;
    }


    public static void DeleteRegistrySetting()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\F6 Launcher", false);
    }

}





