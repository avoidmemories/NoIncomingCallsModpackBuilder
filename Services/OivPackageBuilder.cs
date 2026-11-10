using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

namespace NoIncomingCallsModpackBuilder.Services;

public sealed class OivPackageBuilder
{
    private const string ModpackIconResource = "NoIncomingCallsModpackBuilder.ModpackIcon.png";

    public BuildResult Build(GameScanResult scan, IReadOnlyList<PatchedScript> scripts, string outputPath)
    {
        if (scripts.Count == 0 || scripts.Any(script => !scan.Scripts.ContainsKey(script.Name)))
            throw new InvalidOperationException("The complete validated script set is required to build the OIV.");

        return BuildPackage(scan, scripts, outputPath, isOriginal: false);
    }

    /// <summary>Builds an OIV that restores the selected original scripts extracted
    /// from the selected game installation.</summary>
    public BuildResult BuildOriginal(GameScanResult scan, IEnumerable<string> scriptNames, string outputPath)
    {
        var requestedNames = scriptNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requestedNames.Count == 0 || requestedNames.Any(name => !scan.Scripts.ContainsKey(name)))
            throw new InvalidOperationException("Select at least one validated script.");

        var scripts = scan.Scripts.Values.Where(script => requestedNames.Contains(script.Name))
            .OrderBy(script => script.Name, StringComparer.OrdinalIgnoreCase)
            .Select(script => new PatchedScript(script.Name, script.OriginalBytes.ToArray(),
                script.OriginalSha256, script.OriginalSha256))
            .ToArray();
        return BuildPackage(scan, scripts, outputPath, isOriginal: true);
    }

    private static BuildResult BuildPackage(GameScanResult scan, IReadOnlyList<PatchedScript> scripts,
        string outputPath, bool isOriginal)
    {

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        if (File.Exists(fullOutputPath)) File.Delete(fullOutputPath);

        using var zip = ZipFile.Open(fullOutputPath, ZipArchiveMode.Create);
        AddText(zip, "assembly.xml", CreateAssembly(scan, scripts, isOriginal));
        AddBytes(zip, "icon.png", LoadModpackIcon());
        AddText(zip, "README.txt", CreateReadme(scan, scripts, isOriginal));
        foreach (var script in scripts.OrderBy(script => script.Name, StringComparer.OrdinalIgnoreCase))
            AddBytes(zip, $"content\\scripts\\{script.Name}", script.Bytes);

        return new BuildResult
        {
            OutputPath = fullOutputPath,
            ScriptCount = scripts.Count,
            Edition = scan.Edition
        };
    }

    private static string CreateAssembly(GameScanResult scan, IReadOnlyList<PatchedScript> scripts, bool isOriginal)
    {
        var edition = scan.Edition.ToString();
        var packageName = isOriginal ? "Original Phone Calls" : $"No Incoming Calls - {edition}";
        var tag = isOriginal ? "Original scripts" : "Generated from local game files";
        var description = $"\n<description><![CDATA[GTA V {edition} — build {scan.GameVersion}]]></description>";
        var addLines = string.Join("\r\n", scripts
            .OrderBy(script => script.Name, StringComparer.OrdinalIgnoreCase)
            .Select(script => $"<add source=\"scripts\\{script.Name}\">\\{script.Name}</add>"));
        var id = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<package version="2.0" id="{id}" target="Five">
<metadata>
<name>{packageName}</name>
<version><major>1</major><minor>0</minor><tag>{tag}</tag></version>
<gameversion>{edition}</gameversion>
<author><displayName>No Incoming Calls Modpack Builder</displayName><actionLink>https://github.com/avoidmemories/NoIncomingCallsModpackBuilder</actionLink></author>
{description}
</metadata>
<colors><headerBackground useBlackTextColor="False">$FF202020</headerBackground><iconBackground>$FF202020</iconBackground></colors>
<content>
<archive path="update\update2.rpf" createIfNotExist="True" type="RPF7">
<archive path="\x64\levels\gta5\script\script_rel.rpf" createIfNotExist="True" type="RPF7">
{addLines}
</archive>
</archive>
</content>
</package>
""";
    }

    private static string CreateReadme(GameScanResult scan, IReadOnlyList<PatchedScript> scripts, bool isOriginal)
    {
        var hashes = string.Join(Environment.NewLine, scripts
            .OrderBy(script => script.Name, StringComparer.OrdinalIgnoreCase)
            .Select(script => $"{script.Name}: {script.OriginalSha256[..12]} -> {script.PatchedSha256[..12]}"));
        return $"""
{(isOriginal ? "ORIGINAL PHONE CALL SCRIPTS" : "DISABLE PHONE CALLS MODPACK BUILDER")}

This package was generated from the current original files of the selected GTA V {scan.Edition} installation.
Game version: {scan.GameVersion}
Source archive: {GameScriptService.SourceArchiveRelativePath}\{GameScriptService.ScriptArchiveRelativePath}

{(isOriginal
    ? $"This OIV restores the {scripts.Count} original script resource{(scripts.Count == 1 ? string.Empty : "s")} extracted from the selected game installation."
    : $"The builder makes no changes to the game installation. This OIV replaces {scripts.Count} validated script resource{(scripts.Count == 1 ? string.Empty : "s")} inside the mods copy of update2.rpf.\n" +
      "dialogue_handler rejects the incoming-call branch before the phone interface and ring audio start. Optional ambient-call scripts end before their state machines run. Manual phone opening and outgoing calls are not edited.")}

Scripts and SHA-256 prefixes:
{hashes}
""";
    }

    private static void AddText(ZipArchive archive, string path, string text)
        => AddBytes(archive, path, new UTF8Encoding(false).GetBytes(text));

    private static byte[] LoadModpackIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ModpackIconResource)
            ?? throw new InvalidOperationException("The embedded modpack icon is missing.");
        using var data = new MemoryStream();
        stream.CopyTo(data);
        return data.ToArray();
    }

    private static void AddBytes(ZipArchive archive, string path, byte[] data)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }
}
