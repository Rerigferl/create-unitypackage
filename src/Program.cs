using System.Buffers;
using System.Buffers.Text;
using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

unsafe
{
    ConsoleApp.Run(args, &Commands.Root);
}

static class Commands
{
    /// <summary>
    /// Create UnityPackage.
    /// </summary>
    /// <param name="input">Input directory path.</param>
    /// <param name="output">Output file path.</param>
    /// <param name="rootDirectory">-r, Root directory</param>
    /// <param name="excludeBaseDirectory">-e, Exclude base directory</param>
    /// <param name="generateGuid">-g, Generate GUID automatically</param>
    public static int Root([Argument] string input, [Argument] string output, string? rootDirectory = null, bool excludeBaseDirectory = false, bool generateGuid = false, string[]? ignores = null)
    {
        input = Path.GetFullPath(input);
        if (!Directory.Exists(input))
        {
            Console.Error.WriteLine("Invalid input");
            return 1;
        }

        var assetEntries = AssetEntry.GetEntries(input, ignores);

        using var tar = new TarWriter(new GZipStream(File.Create(output), CompressionLevel.SmallestSize), TarEntryFormat.Ustar);

        var pathNameBuffer = GC.AllocateUninitializedArray<byte>(8192);

        foreach (ref var asset in assetEntries.AsSpan())
        {
            if (string.IsNullOrEmpty(asset.MetaPath) && !generateGuid)
                continue; 

            var filePath = Path.Join(rootDirectory, asset.FilePath.AsSpan()[((excludeBaseDirectory ? input.AsSpan().TrimEnd(@"\/").Length : Path.GetDirectoryName(input.AsSpan()).Length) + 1)..]).Replace("\\", "/");

            tar.WriteEntry(new UstarTarEntry(TarEntryType.Directory, $"{asset.Guid:N}/"));

            var entry = new UstarTarEntry(TarEntryType.RegularFile, $"{asset.Guid:N}/asset.meta");
            {
                using var stream = asset.GetMetaFileStream();
                entry.DataStream = stream;
                tar.WriteEntry(entry);
            }

            entry = new(TarEntryType.RegularFile, $"{asset.Guid:N}/pathname");
            {
                try
                {
                    if (Utf8.FromUtf16(filePath, pathNameBuffer, out _, out int bytesWritten) == OperationStatus.Done)
                    {
                        entry.DataStream = new MemoryStream(pathNameBuffer, 0, bytesWritten);
                    }
                    else
                    {
                        entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(filePath));
                    }
                    tar.WriteEntry(entry);
                }
                finally
                {
                    entry.DataStream?.Dispose();
                }
            }

            if (File.Exists(asset.FilePath))
            {
                entry = new(TarEntryType.RegularFile, $"{asset.Guid:N}/asset");
                using var stream = File.OpenRead(asset.FilePath);
                entry.DataStream = stream;
                tar.WriteEntry(entry);
            }
        }

        return 0;
    }
}

internal struct AssetEntry
{
    public string FilePath;
    public string? MetaPath;
    public Guid Guid;

    public static AssetEntry[] GetEntries(string path, string[]? ignores = null)
    {
        var entries = Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions() { AttributesToSkip = FileAttributes.Hidden, RecurseSubdirectories = true }).ToArray();
        var ignoreItems = ignores is null ? new HashSet<string>() : ignores.Select(Path.GetFullPath).ToHashSet();
        List<AssetEntry> list = [];
        var guidBuffer = (stackalloc byte[128]);
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];

            if (entry.Contains(".git") || entry.EndsWith(".meta"))
                continue;

            string next = (i + 1 >= entries.Length) ? string.Empty : entries[i + 1];
            var result = new AssetEntry()
            {
                FilePath = entry,
                MetaPath = next,
            };
            if (!next.EndsWith(".meta"))
            {
                XxHash128.Hash(MemoryMarshal.AsBytes(entry.AsSpan()[(path.Length + (Path.EndsInDirectorySeparator(path) ? 0 : 1))..]), guidBuffer);
                result.Guid = MemoryMarshal.Read<Guid>(guidBuffer);
                result.MetaPath = null;
            }
            else
            {
                using (var fs = File.OpenRead(next))
                    fs.ReadExactly(guidBuffer);
                var line2 = guidBuffer[(guidBuffer.IndexOf("\n"u8) + 1)..];
                var guidSection = line2[(line2.IndexOf(":"u8) + 1)..line2.IndexOf("\n"u8)].Trim(" "u8);
                if (!Utf8Parser.TryParse(guidSection, out Guid guid, out _, 'N'))
                    continue;
                result.Guid = guid;
            }

            list.Add(result);
        }
        return [.. list];
    }

    public readonly Stream GetMetaFileStream()
    {
        if (File.Exists(MetaPath))
            return File.OpenRead(MetaPath);

        var stream = new MemoryStream(256);
        stream.SetLength(stream.Capacity);
        stream.TryGetBuffer(out var buffer);

        Utf8.TryWrite(buffer, null, $$"""
            fileFormatVersion: 2
            guid: {{Guid:N}}
            """, out var bytesWritten);

        stream.SetLength(bytesWritten);
        stream.Position = 0;

        return stream;
    }
}