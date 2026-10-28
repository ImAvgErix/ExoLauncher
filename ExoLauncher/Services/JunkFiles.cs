using System.Runtime.InteropServices;

namespace ExoLauncher.Services;

/// <summary>
/// Files nothing needs, as System › Apps' Junk lists them: temporary files a day old or more (this user's and Windows'),
/// crash dumps and error reports, Explorer's thumbnail cache (rebuilt as you browse), and the Recycle Bin. A file in use
/// stays, a folder link is never followed, and shader caches are left alone: games load faster with them (Exo Shaders
/// keeps them). Windows' own folders need Exo to run as administrator; without it, what can't be deleted stays.
/// </summary>
internal static partial class JunkFiles
{
    internal sealed record Found(string Id, string Name, string Detail, long Bytes, int Files);

    private static readonly TimeSpan Fresh = TimeSpan.FromDays(1);

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Common => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private static (string Id, string Name, string Detail, Func<IEnumerable<string>> Folders, string Pattern, bool OldOnly)[] Kinds =>
    [
        ("temp", "Temporary files", "Left by installers and apps, a day old or more", () => [Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")], "*", true),
        ("crash", "Crash dumps and error reports", "Kept after an app or game crashed", () =>
        [
            Path.Combine(Local, "CrashDumps"), Path.Combine(Local, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(Local, @"Microsoft\Windows\WER\ReportQueue"),
            Path.Combine(Common, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(Common, @"Microsoft\Windows\WER\ReportQueue"),
        ], "*", false),
        ("thumbs", "Thumbnail cache", "Picture previews Explorer makes again as you browse", () => [Path.Combine(Local, @"Microsoft\Windows\Explorer")], "thumbcache_*.db", false),
    ];

    /// <summary>How much of each kind there is now, the Recycle Bin last; kinds with nothing are left out.</summary>
    internal static List<Found> Scan()
    {
        var found = new List<Found>();
        foreach (var kind in Kinds)
        {
            long bytes = 0;
            var files = 0;
            foreach (var file in FilesOf(kind.Folders(), kind.Pattern, kind.OldOnly)) { bytes += file.Length; files++; }
            if (files > 0) found.Add(new Found(kind.Id, kind.Name, kind.Detail, bytes, files));
        }
        var bin = new RecycleInfo { Size = Marshal.SizeOf<RecycleInfo>() };
        if (SHQueryRecycleBinW(null, ref bin) == 0 && bin.Items > 0)
            found.Add(new Found("bin", "Recycle Bin", "What you deleted, gone for good", bin.Bytes, (int)Math.Min(bin.Items, int.MaxValue)));
        return found;
    }

    /// <summary>One kind deleted: what went (bytes, files). Files in use or out of reach stay.</summary>
    internal static (long Bytes, int Files) Clean(string id)
    {
        if (id == "bin")
        {
            var bin = new RecycleInfo { Size = Marshal.SizeOf<RecycleInfo>() };
            SHQueryRecycleBinW(null, ref bin);
            // No confirmation, progress or sound of Windows' own: Exo asked first.
            return SHEmptyRecycleBinW(0, null, 0x1 | 0x2 | 0x4) == 0 ? (bin.Bytes, (int)Math.Min(bin.Items, int.MaxValue)) : (0, 0);
        }
        if (Kinds.FirstOrDefault(kind => kind.Id == id) is not { Id: not null } chosen) return (0, 0);
        long bytes = 0;
        var files = 0;
        var folders = chosen.Folders().ToList();
        foreach (var file in FilesOf(folders, chosen.Pattern, chosen.OldOnly).ToList())
        {
            try
            {
                var length = file.Length;
                if (file.IsReadOnly) file.IsReadOnly = false;
                file.Delete();
                bytes += length;
                files++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // Folders the files left empty go too; the kind's own folders stay.
        foreach (var folder in folders.Where(Directory.Exists)) RemoveEmpty(folder, keep: true);
        return (bytes, files);
    }

    /// <summary>
    /// The files of a kind. Temporary files (<paramref name="oldOnly"/>) go by folder: a folder anything wrote to in the
    /// last day is some tool's work in progress (a build, a setup waiting for a restart) and stays whole; loose files go by age.
    /// </summary>
    private static IEnumerable<FileInfo> FilesOf(IEnumerable<string> folders, string pattern, bool oldOnly)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        var cutoff = DateTime.UtcNow - Fresh;
        bool Old(FileInfo file) => file.LastWriteTimeUtc < cutoff && file.LastAccessTimeUtc < cutoff;
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            List<FileInfo> files;
            try
            {
                var root = new DirectoryInfo(folder);
                if (!oldOnly) files = [.. root.EnumerateFiles(pattern, options)];
                else
                {
                    files = [.. root.EnumerateFiles(pattern, new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = options.AttributesToSkip }).Where(Old)];
                    foreach (var sub in root.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = options.AttributesToSkip }))
                    {
                        var inside = sub.EnumerateFiles("*", options).ToList();
                        if (inside.All(Old)) files.AddRange(inside);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
        }
    }

    private static void RemoveEmpty(string folder, bool keep)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(folder))
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) RemoveEmpty(sub, keep: false);
            if (!keep && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct RecycleInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBinW(string? root, ref RecycleInfo info);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBinW(nint window, string? root, uint flags);
}
