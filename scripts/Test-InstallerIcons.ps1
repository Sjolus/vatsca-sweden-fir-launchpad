#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $ExecutablePaths,
    [Parameter(Mandatory)] [string] $FullPackagePath,
    [string] $SourceIcon = (Join-Path $PSScriptRoot '..\Assets\app.ico')
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Shell icon extraction checks require Windows.' }
if (-not ('LaunchpadPackaging.IconVerification' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace LaunchpadPackaging {
    public static class IconVerification {
        sealed class Frame {
            public int Width, Height;
            public byte[] Bytes;
            public Frame(int width, int height, byte[] bytes) { Width = width; Height = height; Bytes = bytes; }
        }
        sealed class Section { public uint Rva, VirtualSize, Raw, RawSize; }
        sealed class Entry { public uint Id, Target; }
        sealed class Resource { public uint Type, Id, Language; public byte[] Bytes; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern uint ExtractIconExW(string path, int index, out IntPtr large, out IntPtr small, uint count);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool DestroyIcon(IntPtr icon);

        static void Range(byte[] bytes, long offset, long size) {
            if (offset < 0 || size < 0 || offset > bytes.LongLength - size)
                throw new InvalidDataException("Icon resource exceeds its file bounds.");
        }
        static ushort U16(byte[] bytes, long offset) { Range(bytes, offset, 2); return BitConverter.ToUInt16(bytes, (int)offset); }
        static uint U32(byte[] bytes, long offset) { Range(bytes, offset, 4); return BitConverter.ToUInt32(bytes, (int)offset); }
        static byte[] Slice(byte[] bytes, long offset, long size) {
            Range(bytes, offset, size);
            if (size > 16 * 1024 * 1024) throw new InvalidDataException("Icon resource is unexpectedly large.");
            var result = new byte[(int)size];
            Buffer.BlockCopy(bytes, (int)offset, result, 0, result.Length);
            return result;
        }
        static bool Equal(byte[] a, byte[] b) {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        static List<Frame> ReadIco(byte[] bytes) {
            if (U16(bytes, 0) != 0 || U16(bytes, 2) != 1) throw new InvalidDataException("Expected an ICO source.");
            int count = U16(bytes, 4);
            if (count == 0 || count > 64) throw new InvalidDataException("Unexpected source icon frame count.");
            Range(bytes, 6, count * 16);
            var result = new List<Frame>();
            for (int i = 0; i < count; i++) {
                int offset = 6 + i * 16;
                int width = bytes[offset] == 0 ? 256 : bytes[offset];
                int height = bytes[offset + 1] == 0 ? 256 : bytes[offset + 1];
                result.Add(new Frame(width, height, Slice(bytes, U32(bytes, offset + 12), U32(bytes, offset + 8))));
            }
            foreach (int required in new[] { 16, 32, 48, 256 })
                if (!result.Exists(f => f.Width == required && f.Height == required))
                    throw new InvalidDataException("Source icon lacks a required shell size: " + required);
            return result;
        }
        static long Raw(uint rva, uint size, List<Section> sections, byte[] bytes) {
            foreach (var section in sections) {
                long relative = (long)rva - section.Rva;
                if (relative >= 0 && relative <= section.RawSize && size <= section.RawSize - relative) {
                    long offset = section.Raw + relative;
                    Range(bytes, offset, size);
                    return offset;
                }
            }
            throw new InvalidDataException("Icon resource RVA is outside file-backed sections.");
        }
        static List<Entry> Entries(byte[] bytes, long start, uint resourceSize, uint relative) {
            if (relative > resourceSize || resourceSize - relative < 16) throw new InvalidDataException("Invalid resource directory.");
            long offset = start + relative;
            int count = U16(bytes, offset + 12) + U16(bytes, offset + 14);
            if (count > 4096 || resourceSize - relative - 16 < count * 8L) throw new InvalidDataException("Invalid resource entry count.");
            var result = new List<Entry>();
            for (int i = 0; i < count; i++)
                result.Add(new Entry { Id = U32(bytes, offset + 16 + 8 * i), Target = U32(bytes, offset + 20 + 8 * i) });
            return result;
        }
        static List<Resource> ReadResources(byte[] bytes) {
            if (U16(bytes, 0) != 0x5a4d) throw new InvalidDataException("Expected a Windows executable.");
            uint pe = U32(bytes, 60);
            if (U32(bytes, pe) != 0x4550) throw new InvalidDataException("Invalid PE signature.");
            int count = U16(bytes, pe + 6), optionalSize = U16(bytes, pe + 20);
            if (count == 0 || count > 96) throw new InvalidDataException("Invalid PE section count.");
            long optional = pe + 24L;
            int magic = U16(bytes, optional);
            int dataDirectory = magic == 0x20b ? 112 : magic == 0x10b ? 96 : 0;
            if (dataDirectory == 0 || optionalSize < dataDirectory + 24) throw new InvalidDataException("Invalid PE optional header.");
            uint resourceRva = U32(bytes, optional + dataDirectory + 16), resourceSize = U32(bytes, optional + dataDirectory + 20);
            if (resourceRva == 0 || resourceSize < 16) throw new InvalidDataException("Executable has no resources.");
            var sections = new List<Section>();
            for (int i = 0; i < count; i++) {
                long offset = optional + optionalSize + i * 40L;
                Range(bytes, offset, 40);
                sections.Add(new Section { Rva = U32(bytes, offset + 12), VirtualSize = U32(bytes, offset + 8), Raw = U32(bytes, offset + 20), RawSize = U32(bytes, offset + 16) });
            }
            long start = Raw(resourceRva, resourceSize, sections, bytes);
            var result = new List<Resource>();
            foreach (var type in Entries(bytes, start, resourceSize, 0)) {
                if (type.Id != 3 && type.Id != 14) continue;
                if ((type.Target & 0x80000000) == 0) throw new InvalidDataException("Icon type is not a resource directory.");
                foreach (var id in Entries(bytes, start, resourceSize, type.Target & 0x7fffffff)) {
                    if ((id.Target & 0x80000000) == 0) throw new InvalidDataException("Icon ID is not a resource directory.");
                    foreach (var language in Entries(bytes, start, resourceSize, id.Target & 0x7fffffff)) {
                        if ((language.Target & 0x80000000) != 0 || language.Target > resourceSize || resourceSize - language.Target < 16)
                            throw new InvalidDataException("Invalid icon resource data entry.");
                        long entry = start + language.Target;
                        uint size = U32(bytes, entry + 4);
                        result.Add(new Resource { Type = type.Id, Id = id.Id, Language = language.Id, Bytes = Slice(bytes, Raw(U32(bytes, entry), size, sections, bytes), size) });
                    }
                }
            }
            return result;
        }
        public static int VerifyBytes(byte[] executable, byte[] sourceIcon) {
            var expected = ReadIco(sourceIcon);
            var resources = ReadResources(executable);
            var groups = resources.FindAll(r => r.Type == 14);
            if (groups.Count == 0) throw new InvalidDataException("Executable has no icon group.");
            // Check every group, including the first group used by shortcut icon index 0.
            foreach (var group in groups) {
                var bytes = group.Bytes;
                if (U16(bytes, 0) != 0 || U16(bytes, 2) != 1 || U16(bytes, 4) != expected.Count)
                    throw new InvalidDataException("Executable icon group differs from the source icon.");
                Range(bytes, 6, expected.Count * 14);
                for (int i = 0; i < expected.Count; i++) {
                    int offset = 6 + i * 14;
                    var frame = expected[i];
                    int width = bytes[offset] == 0 ? 256 : bytes[offset];
                    int height = bytes[offset + 1] == 0 ? 256 : bytes[offset + 1];
                    uint id = U16(bytes, offset + 12);
                    var image = resources.Find(r => r.Type == 3 && r.Id == id && r.Language == group.Language);
                    if (image == null || width != frame.Width || height != frame.Height || U32(bytes, offset + 8) != frame.Bytes.Length || !Equal(image.Bytes, frame.Bytes))
                        throw new InvalidDataException("Executable icon frame differs from the source icon: " + frame.Width + "x" + frame.Height);
                }
            }
            return expected.Count;
        }
        public static void VerifyShellExtraction(string path) {
            IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
            try {
                uint count = ExtractIconExW(path, 0, out large, out small, 1);
                if (count == 0 || large == IntPtr.Zero || small == IntPtr.Zero)
                    throw new InvalidDataException("Windows could not extract both large and small shell icons.");
            } finally {
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero && small != large) DestroyIcon(small);
            }
        }
    }
}
'@
}

$sourceBytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($SourceIcon))
if ($ExecutablePaths.Count -ne 7 -or @($ExecutablePaths | Sort-Object -Unique).Count -ne 7) {
    throw 'Expected Setup and six distinct executables extracted from the portable and full packages.'
}
$checks = [Collections.Generic.List[object]]::new()
foreach ($path in $ExecutablePaths) {
    $fullPath = [IO.Path]::GetFullPath($path)
    $frames = [LaunchpadPackaging.IconVerification]::VerifyBytes([IO.File]::ReadAllBytes($fullPath), $sourceBytes)
    [LaunchpadPackaging.IconVerification]::VerifyShellExtraction($fullPath)
    $checks.Add([pscustomobject]@{ File = [IO.Path]::GetFileName($fullPath); Frames = $frames; Identity = 'Matches source ICO'; ShellSizes = 'Large and small' })
}

$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($FullPackagePath))
try {
    $icons = @($archive.Entries | Where-Object { $_.FullName -eq 'setup.ico' })
    if ($icons.Count -ne 1) { throw 'The full update package must contain one setup.ico.' }
    $stream = $icons[0].Open()
    $memory = [IO.MemoryStream]::new()
    try { $stream.CopyTo($memory); $packagedIcon = $memory.ToArray() }
    finally { $memory.Dispose(); $stream.Dispose() }
    if ($packagedIcon.Length -ne $sourceBytes.Length -or
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($packagedIcon)) -ne
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceBytes))) {
        throw 'The packaged setup.ico differs from Assets/app.ico.'
    }
}
finally { $archive.Dispose() }

[pscustomobject]@{
    ExecutableCount = $checks.Count
    FramesPerExecutable = @($checks.Frames | Sort-Object -Unique)
    SetupIconMatch = $true
    Executables = $checks.ToArray()
}
Write-Host "Verified source icon frames and Windows shell extraction in $($checks.Count) executables, plus packaged setup.ico. No executable was launched."
