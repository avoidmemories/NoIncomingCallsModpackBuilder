using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CodeWalker.GameFiles;

namespace NoIncomingCallsModpackBuilder.Services;

/// <summary>
/// Reads current, untouched script resources from the selected GTA V installation.
/// It never writes into the game folder or its mods folder.
/// </summary>
public sealed class GameScriptService
{
    public const string SourceArchiveRelativePath = @"update\update2.rpf";
    public const string ScriptArchiveRelativePath = @"x64\levels\gta5\script\script_rel.rpf";

    public static readonly string[] AmbientCallerScripts =
    {
        "ambient_mrsphilips.ysc",
        "ambient_solomon.ysc",
        "ambient_sonar.ysc",
        "ambient_tonya.ysc",
        "ambient_tonyacall.ysc",
        "ambient_tonyacall2.ysc",
        "ambient_tonyacall5.ysc",
        "bootycallhandler.ysc"
    };

    public const string DialogueHandlerScript = "dialogue_handler.ysc";

    public static readonly string[] RequiredScripts =
        AmbientCallerScripts.Append(DialogueHandlerScript).ToArray();

    public GameScanResult ReadAndValidate(string gameFolder, Action<string>? progress = null)
    {
        var root = Path.GetFullPath(gameFolder.Trim());
        ValidateGameFolder(root);

        var edition = DetectEdition(root);
        var executable = Path.Combine(root, edition == GameEdition.Enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe");
        var archivePath = Path.Combine(root, SourceArchiveRelativePath);
        if (!File.Exists(archivePath))
            throw new InvalidOperationException($"Could not find {SourceArchiveRelativePath} in the selected game folder.");

        progress?.Invoke("Loading encryption keys from the selected game...");
        GTA5Keys.LoadFromPath(root, edition == GameEdition.Enhanced, null);

        progress?.Invoke("Reading update2.rpf...");
        var errors = new List<string>();
        var updateRpf = new RpfFile(archivePath, SourceArchiveRelativePath);
        updateRpf.ScanStructure(null, error => errors.Add(error));
        if (errors.Count > 0)
            throw new InvalidOperationException("Could not completely read update2.rpf: " + errors[0]);

        var scriptRpf = FindScriptArchive(updateRpf)
            ?? throw new InvalidOperationException(
                $"Could not find {ScriptArchiveRelativePath} inside update2.rpf.");

        var scripts = new Dictionary<string, GameScriptFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in RequiredScripts)
        {
            progress?.Invoke($"Extracting {name}...");
            var matching = scriptRpf.AllEntries
                .OfType<RpfFileEntry>()
                .Where(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matching.Length != 1)
                throw new InvalidOperationException(
                    $"Expected exactly one {name} in script_rel.rpf, but found {matching.Length}.");

            var bytes = scriptRpf.ExtractFile(matching[0]);
            if (bytes is null || bytes.Length < 128)
                throw new InvalidOperationException($"Could not extract a valid {name} from script_rel.rpf.");

            scripts.Add(name, new GameScriptFile(name, bytes, Sha256(bytes)));
        }

        progress?.Invoke("Checking the current script layout...");
        var notes = ScriptPatcher.Validate(scripts, edition);
        var version = ReadVersion(executable);

        return new GameScanResult
        {
            GameFolder = root,
            Edition = edition,
            GameVersion = version,
            ArchivePath = archivePath,
            ScriptArchivePath = ScriptArchiveRelativePath,
            Scripts = scripts,
            ValidationNotes = notes.AsReadOnly()
        };
    }

    public IReadOnlyList<PatchedScript> CreatePatches(
        GameScanResult scan,
        bool includeOptionalScripts,
        bool includePrimaryScript)
        => ScriptPatcher.CreatePatches(scan, includeOptionalScripts, includePrimaryScript);

    private static RpfFile? FindScriptArchive(RpfFile root)
    {
        var expected = ScriptArchiveRelativePath.Replace('/', '\\');
        return EnumerateArchives(root).FirstOrDefault(rpf =>
            rpf.Path.Replace('/', '\\').EndsWith(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<RpfFile> EnumerateArchives(RpfFile archive)
    {
        yield return archive;
        if (archive.Children is null) yield break;
        foreach (var child in archive.Children)
            foreach (var descendant in EnumerateArchives(child))
                yield return descendant;
    }

    private static GameEdition DetectEdition(string folder)
    {
        var enhanced = File.Exists(Path.Combine(folder, "GTA5_Enhanced.exe"));
        var legacy = File.Exists(Path.Combine(folder, "GTA5.exe"));
        if (enhanced && legacy)
            throw new InvalidOperationException(
                "Both GTA5.exe and GTA5_Enhanced.exe were found. Select one game installation, not a shared folder.");
        if (enhanced) return GameEdition.Enhanced;
        if (legacy) return GameEdition.Legacy;
        throw new InvalidOperationException(
            "This folder does not contain GTA5.exe or GTA5_Enhanced.exe. Select the GTA V installation folder.");
    }

    private static void ValidateGameFolder(string folder)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("The selected GTA V folder does not exist.");
        if (string.Equals(new DirectoryInfo(folder).Name, "mods", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Select the main GTA V folder, not its mods folder. The builder must read untouched original files.");
    }

    private static string ReadVersion(string executable)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(executable).ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? "Unknown build" : version;
        }
        catch
        {
            return "Unknown build";
        }
    }

    internal static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
