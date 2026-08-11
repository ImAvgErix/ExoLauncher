namespace ExoLauncher.Helpers;

/// <summary>
/// Validates a directory before a recursive delete rooted in an Exo-managed library.
/// </summary>
internal static class RecursiveDeleteGuard
{
    internal static bool TryValidateManagedChild(
        string libraryRoot,
        string candidatePath,
        out string validatedPath,
        out string error)
    {
        validatedPath = string.Empty;
        error = "Refusing to delete an unsafe path.";

        if (string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(candidatePath))
            return false;

        try
        {
            var root = Normalize(libraryRoot);
            var candidate = Normalize(candidatePath);
            var relative = Path.GetRelativePath(root, candidate);

            if (string.Equals(relative, ".", StringComparison.Ordinal)
                || Path.IsPathFullyQualified(relative)
                || IsParentTraversal(relative))
            {
                error = "Refusing to delete the library root or a folder outside it.";
                return false;
            }

            if (!Directory.Exists(root) || !Directory.Exists(candidate))
            {
                error = "Install path not found.";
                return false;
            }

            // A reparse point in the path to the deletion root can redirect a
            // lexically-contained path elsewhere. Directory.Delete does not recurse
            // through nested reparse points, so reject every component used to reach
            // the candidate while leaving ordinary in-tree junction entries safe.
            var current = root;
            if (IsReparsePoint(current))
            {
                error = "Refusing to delete through a reparse-point library path.";
                return false;
            }

            foreach (var component in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.Directory) == 0
                    || (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    error = "Refusing to delete through a reparse-point library path.";
                    return false;
                }
            }

            validatedPath = candidate;
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or UnauthorizedAccessException)
        {
            error = "Refusing to delete a path that could not be validated.";
            return false;
        }
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsParentTraversal(string relative) =>
        string.Equals(relative, "..", StringComparison.Ordinal)
        || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
