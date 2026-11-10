using System;
using System.IO;
using System.Text.Json;

namespace NoIncomingCallsModpackBuilder.Services;

/// <summary>Stores the last GTA V and export folders selected by the user.</summary>
public static class UserSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoIncomingCallsModpackBuilder", "settings.json");

    public static string? LoadLastGameFolder()
        => Load()?.LastGameFolder;

    public static string? LoadLastExportFolder()
        => Load()?.LastExportFolder;

    public static void SaveLastGameFolder(string folder)
        => Save(Load() with { LastGameFolder = folder });

    public static void ClearLastGameFolder()
        => Save(Load() with { LastGameFolder = null });

    public static void SaveLastExportFolder(string folder)
        => Save(Load() with { LastExportFolder = folder });

    private static Settings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new Settings(null, null);
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings(null, null);
        }
        catch
        {
            return new Settings(null, null);
        }
    }

    private static void Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // A settings write must never prevent building a package.
        }
    }

    private sealed record Settings(string? LastGameFolder, string? LastExportFolder);
}
