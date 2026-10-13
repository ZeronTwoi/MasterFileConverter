// Packs a published folder into the archive people download.
//   dotnet run build/Pack.cs -- <folder> <archive.zip | archive.tar.gz>
// A .tar.gz is written with Unix permissions set explicitly (programs and scripts executable), so a
// Linux package comes out right even when it is built on Windows, whose file system has no such flag.

using System.Formats.Tar;
using System.IO.Compression;

if (args.Length != 2 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: dotnet run build/Pack.cs -- <folder> <archive.zip | archive.tar.gz>");
    return 2;
}

var folder = Path.GetFullPath(args[0]);
var archive = Path.GetFullPath(args[1]);
File.Delete(archive);
// everything sits in one top-level folder named after the archive, so unpacking never litters
var top = Path.GetFileName(archive).Replace(".tar.gz", "").Replace(".zip", "");

if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
{
    using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        zip.CreateEntryFromFile(file, top + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/'), CompressionLevel.SmallestSize);
}
else
{
    const UnixFileMode plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    const UnixFileMode executable = plain | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    using var stream = File.Create(archive);
    using var gzip = new GZipStream(stream, CompressionLevel.SmallestSize);
    using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
        // programs have no extension on Linux; scripts end in .sh
        bool runs = Path.GetExtension(file) is "" or ".sh";
        var entry = new PaxTarEntry(TarEntryType.RegularFile, top + "/" + relative)
        {
            Mode = runs ? executable : plain,
            ModificationTime = File.GetLastWriteTimeUtc(file)
        };
        using var content = File.OpenRead(file);
        entry.DataStream = content;
        tar.WriteEntry(entry);
    }
}

Console.WriteLine($"{Path.GetFileName(archive)}  {new FileInfo(archive).Length / 1048576.0:0.0} MB");
return 0;
