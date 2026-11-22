using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Bazaar.Client.Updater.Linux.Tests;

/// <summary>Builds small tar.gz archives for the tests, including hostile ones.</summary>
internal static class TarBuilder
{
    public sealed record Item(string Name, TarEntryType Type, string Text = "", string? Link = null, UnixFileMode Mode = (UnixFileMode)0b110_100_100);

    public static Item File(string name, string text = "x", UnixFileMode mode = (UnixFileMode)0b110_100_100) =>
        new(name, TarEntryType.RegularFile, text, null, mode);

    public static Item Dir(string name) => new(name, TarEntryType.Directory, Mode: (UnixFileMode)0b111_101_101);

    public static Item Link(string name, string target) => new(name, TarEntryType.SymbolicLink, Link: target);

    public static Item HardLink(string name, string target) => new(name, TarEntryType.HardLink, Link: target);

    public static byte[] Build(params Item[] items)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var item in items)
            {
                var entry = new PaxTarEntry(item.Type, item.Name) { Mode = item.Mode };
                if (item.Link is not null)
                {
                    entry.LinkName = item.Link;
                }

                if (item.Type == TarEntryType.RegularFile)
                {
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(item.Text));
                }

                writer.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }

    public static byte[] Release(string marker = "1") =>
        Build(Dir("."), File("Bazaar", marker, (UnixFileMode)0b111_101_101), File("lib/core.dll", "dll-" + marker), Link("lib/core-link.dll", "core.dll"));
}
