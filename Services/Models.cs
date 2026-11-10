using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace NoIncomingCallsModpackBuilder.Services;

public enum GameEdition
{
    Legacy,
    Enhanced
}

public sealed record GameScriptFile(string Name, byte[] OriginalBytes, string OriginalSha256);

public sealed class GameScanResult
{
    public required string GameFolder { get; init; }
    public required GameEdition Edition { get; init; }
    public required string GameVersion { get; init; }
    public required string ArchivePath { get; init; }
    public required string ScriptArchivePath { get; init; }
    public required IReadOnlyDictionary<string, GameScriptFile> Scripts { get; init; }
    public required ReadOnlyCollection<string> ValidationNotes { get; init; }
}

public sealed record PatchedScript(string Name, byte[] Bytes, string OriginalSha256, string PatchedSha256);

public sealed class BuildResult
{
    public required string OutputPath { get; init; }
    public required int ScriptCount { get; init; }
    public required GameEdition Edition { get; init; }
}
