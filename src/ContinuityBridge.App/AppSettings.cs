using System.Text.Json;
using Microsoft.Win32;

namespace ContinuityBridge.App;

internal sealed record AppSettings(string BaseUrl = "", bool AutoSync = false, bool Logon = false)
{
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContinuityBridge", "settings.json");
    internal static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(this));
        File.Move(SettingsPath + ".tmp", SettingsPath, true);
    }
    internal static void SetLogon(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enable)
        {
            string path = Environment.ProcessPath ?? throw new IOException("executable_missing");
            if (path.Contains('"', StringComparison.Ordinal)) throw new IOException("executable_invalid");
            key.SetValue("ContinuityBridge", "\"" + path + "\"");
        }
        else key.DeleteValue("ContinuityBridge", false);
    }
}
