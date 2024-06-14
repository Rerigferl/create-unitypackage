using System.Buffers;
using System.Buffers.Text;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Unicode;

ConsoleApp.Run(args, Commands.Root);

static class Commands
{
    /// <summary>
    /// Create UnityPackage.
    /// </summary>
    /// <param name="input">Input directory path.</param>
    /// <param name="output">Output file path.</param>
    /// <param name="rootDirectory">-r, Root directory</param>
    public static int Root([Argument] string input, [Argument] string output, string? rootDirectory = null)
    {
        if (!Directory.Exists(input))
        {
            Console.Error.WriteLine("Invalid input");
            return 1;
        }

        var entries = Directory.EnumerateFiles(input, ".meta*", SearchOption.AllDirectories);

        using var destination = File.Create(output);
        using var gz = new GZipStream(destination, CompressionLevel.SmallestSize);
        using var tar = new TarWriter(gz, TarEntryFormat.Pax);

        var guidBuffer = (stackalloc byte[128]);
        var pathNameBuffer = GC.AllocateUninitializedArray<byte>(8192);

        foreach (var metaPath in entries)
        {
            Guid guid;
            try
            {
                using (var fs = File.OpenRead(metaPath))
                    fs.ReadExactly(guidBuffer);
                var line2 = guidBuffer[(guidBuffer.IndexOf("\n"u8) + 1)..];
                var guidSection = line2[(line2.IndexOf(":"u8) + 1)..line2.IndexOf("\n"u8)].Trim(" "u8);
                if (!Utf8Parser.TryParse(guidSection, out guid, out _, 'N'))
                    continue;
            }
            catch
            {
                continue;
            }

            var assetPath = metaPath[..^".meta".Length];
            var filePath = Path.Join(rootDirectory, metaPath.AsSpan()[(Path.GetDirectoryName(input.AsSpan()).Length + 1)..])[..^".meta".Length].Replace("\\", "/");

            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, guid.ToString("N")));

            var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{guid:N}/asset.meta");
            {
                using var stream = File.OpenRead(metaPath);
                entry.DataStream = stream;
                tar.WriteEntry(entry);
            }

            entry = new PaxTarEntry(TarEntryType.RegularFile, $"{guid:N}/pathName");
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

            if (File.Exists(assetPath))
            {
                entry = new(TarEntryType.RegularFile, $"{guid:N}/asset");
                using var stream = File.OpenRead(assetPath);
                entry.DataStream = stream;
                tar.WriteEntry(entry);
            }
        }

        return 0;
    }
}