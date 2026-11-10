using System;
using System.Collections.Generic;
using System.Linq;

namespace NoIncomingCallsModpackBuilder.Services;

/// <summary>
/// Applies the small, verified changes used by the working phone-call package.
/// No YSC offset is hard-coded: code pages are parsed from each extracted file,
/// and dialogue_handler is located by its incoming-call bytecode signature.
/// </summary>
internal static class ScriptPatcher
{
    // Code locations move between builds, but these three game globals form the
    // stable incoming-call dispatcher contract for each current game edition:
    // incoming request flag, request state, and dispatcher reset state.
    private static readonly DialoguePatchProfile LegacyDialogue = new(23033, 24040, 23023);
    private static readonly DialoguePatchProfile EnhancedDialogue = new(23050, 24057, 23040);

    public static List<string> Validate(IReadOnlyDictionary<string, GameScriptFile> scripts, GameEdition edition)
    {
        var notes = new List<string>();
        foreach (var name in GameScriptService.AmbientCallerScripts.Where(scripts.ContainsKey))
        {
            var image = YscImage.Read(scripts[name].OriginalBytes, name);
            if (image.CodeLength < 8 || image.ReadCodeByte(0) != 0x2D)
                throw new InvalidOperationException($"{name} has an unsupported script entry layout.");
            if (image.ReadCodeByte(5) == 0x2E)
                throw new InvalidOperationException($"{name} already appears to be modified; choose an unmodified GTA V installation.");
        }

        var dialogue = YscImage.Read(scripts[GameScriptService.DialogueHandlerScript].OriginalBytes,
            GameScriptService.DialogueHandlerScript);
        var candidate = FindIncomingRequest(dialogue, edition);
        if (candidate is null)
            throw new InvalidOperationException(
                "The current dialogue_handler.ysc does not contain the expected incoming-call branch. " +
                "This GTA V build is not supported yet, so no package will be created.");

        notes.Add($"{edition}: 8 dedicated ambient-call scripts are available as optional scripts.");
        notes.Add("dialogue_handler incoming-call branch was found by signature.");
        notes.Add("dialogue_handler.ysc is available as the primary script category.");
        return notes;
    }

    public static IReadOnlyList<PatchedScript> CreatePatches(
        GameScanResult scan,
        bool includeOptionalScripts,
        bool includePrimaryScript)
    {
        var patches = new List<PatchedScript>();
        if (includeOptionalScripts)
        {
            foreach (var name in GameScriptService.AmbientCallerScripts)
            {
                var source = scan.Scripts[name];
                var bytes = source.OriginalBytes.ToArray();
                var image = YscImage.Read(bytes, name);
                if (image.ReadCodeByte(0) != 0x2D || image.ReadCodeByte(5) == 0x2E)
                    throw new InvalidOperationException($"{name} changed after verification. Check the selected game folder again.");

                // LEAVE 0,0 replaces the first instruction after ENTER. The script
                // remains a valid full YSC resource, but its ambient state machine
                // never starts.
                image.WriteCode(5, new byte[] { 0x2E, 0x00, 0x00 });
                patches.Add(new PatchedScript(name, bytes, source.OriginalSha256, GameScriptService.Sha256(bytes)));
            }
        }

        if (includePrimaryScript)
        {
            var dialogueSource = scan.Scripts[GameScriptService.DialogueHandlerScript];
            var dialogueBytes = dialogueSource.OriginalBytes.ToArray();
            var dialogue = YscImage.Read(dialogueBytes, dialogueSource.Name);
            var candidate = FindIncomingRequest(dialogue, scan.Edition);
            if (candidate is null)
                throw new InvalidOperationException("dialogue_handler.ysc changed after verification. No package was created.");

            // Keep Global_23033's false branch (manual outgoing phone calls) intact.
            // The true incoming branch becomes: state=6; return.
            dialogue.WriteCode(candidate.CodeOffset + 6, candidate.Replacement);
            patches.Add(new PatchedScript(dialogueSource.Name, dialogueBytes, dialogueSource.OriginalSha256,
                GameScriptService.Sha256(dialogueBytes)));
        }

        return patches;
    }

    private static IncomingRequestCandidate? FindIncomingRequest(YscImage image, GameEdition edition)
    {
        var profile = edition == GameEdition.Enhanced ? EnhancedDialogue : LegacyDialogue;
        var code = image.GetCode();
        var matches = new List<int>();
        for (var i = 0; i <= code.Length - 13; i++)
        {
            // GLOBAL_U16_LOAD Global_23033; JZ <outgoing path>;
            // GLOBAL_U16_LOAD Global_24040; PUSH_CONST_0; IEQ_JZ <...>
            // The two jump destinations may move between game builds, so they
            // are intentionally not part of the search signature.
            if (code[i] == 0x53 && ReadU16(code, i + 1) == profile.IncomingFlagGlobal
                && code[i + 3] == 0x56
                && code[i + 6] == 0x53 && ReadU16(code, i + 7) == profile.RequestStateGlobal
                && code[i + 9] == 0x71 && code[i + 10] == 0x57)
                matches.Add(i);
        }

        if (matches.Count != 1) return null;
        var reset = profile.DispatcherResetGlobal;
        return new IncomingRequestCandidate(matches[0], new byte[]
        {
            0x77, 0x54, (byte)reset, (byte)(reset >> 8), 0x2E, 0x00, 0x00
        });
    }

    private static int ReadU16(byte[] data, int offset) => data[offset] | data[offset + 1] << 8;

    private sealed record DialoguePatchProfile(int IncomingFlagGlobal, int RequestStateGlobal, int DispatcherResetGlobal);
    private sealed record IncomingRequestCandidate(int CodeOffset, byte[] Replacement);

    private sealed class YscImage
    {
        private readonly byte[] _data;
        private readonly int[] _codePages;
        public int CodeLength { get; }

        private YscImage(byte[] data, int[] codePages, int codeLength)
        {
            _data = data;
            _codePages = codePages;
            CodeLength = codeLength;
        }

        public static YscImage Read(byte[] data, string name)
        {
            if (data.Length < 0x80) throw new InvalidOperationException($"{name} is too small to be a YSC resource.");
            var magic = BitConverter.ToUInt32(data, 0);
            var rscOffset = magic is 0x37435352 or 0x38435352 ? 0x10 : 0;
            var codeBlocksOffset = (int)(ReadUInt32(data, rscOffset + 0x10) & 0x00FFFFFF) + rscOffset;
            var codeLength = ReadInt32(data, rscOffset + 0x1C);
            if (codeLength is <= 0 or > 64 * 1024 * 1024)
                throw new InvalidOperationException($"{name} has an invalid YSC code length.");
            var pageCount = (codeLength + 0x3FFF) >> 14;
            if (codeBlocksOffset < 0 || codeBlocksOffset + pageCount * 8 > data.Length)
                throw new InvalidOperationException($"{name} has an invalid YSC code-page table.");

            var pages = new int[pageCount];
            for (var i = 0; i < pageCount; i++)
            {
                pages[i] = (int)(ReadUInt32(data, codeBlocksOffset + i * 8) & 0x00FFFFFF) + rscOffset;
                var used = Math.Min(0x4000, codeLength - i * 0x4000);
                if (pages[i] < 0 || pages[i] + used > data.Length)
                    throw new InvalidOperationException($"{name} contains an invalid YSC code page.");
            }
            return new YscImage(data, pages, codeLength);
        }

        public byte ReadCodeByte(int offset)
        {
            EnsureCodeRange(offset, 1);
            return _data[_codePages[offset / 0x4000] + offset % 0x4000];
        }

        public byte[] GetCode()
        {
            var code = new byte[CodeLength];
            for (var page = 0; page < _codePages.Length; page++)
            {
                var start = page * 0x4000;
                var count = Math.Min(0x4000, CodeLength - start);
                Buffer.BlockCopy(_data, _codePages[page], code, start, count);
            }
            return code;
        }

        public void WriteCode(int offset, ReadOnlySpan<byte> value)
        {
            EnsureCodeRange(offset, value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var codeOffset = offset + i;
                _data[_codePages[codeOffset / 0x4000] + codeOffset % 0x4000] = value[i];
            }
        }

        private void EnsureCodeRange(int offset, int length)
        {
            if (offset < 0 || length < 0 || offset > CodeLength - length)
                throw new ArgumentOutOfRangeException(nameof(offset));
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            if (offset < 0 || offset > data.Length - 4) throw new InvalidOperationException("The YSC header is truncated.");
            return BitConverter.ToUInt32(data, offset);
        }

        private static int ReadInt32(byte[] data, int offset) => unchecked((int)ReadUInt32(data, offset));
    }
}
