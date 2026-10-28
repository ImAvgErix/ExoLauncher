using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Windows' own spell checker (spellcheck.h, Windows 8 and later) for Exo Notes' right-click: whether a word is spelled
/// right in Windows' language, what it suggests, and Add to dictionary, which goes in Windows' own word list (the same one
/// the page's underlines read). One checker, made on first use and kept; COM's apartment is the caller's.
/// </summary>
internal static class NoteSpelling
{
    private static readonly Lock Gate = new();
    private static ISpellChecker? _checker;
    private static bool _tried;

    private static ISpellChecker? Checker
    {
        get
        {
            if (_tried) return _checker;
            _tried = true;
            try
            {
                var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
                var language = new[] { System.Globalization.CultureInfo.CurrentUICulture.Name, System.Globalization.CultureInfo.CurrentCulture.Name, "en-US" }
                    .FirstOrDefault(tag => tag.Length > 0 && factory.IsSupported(tag));
                if (language is not null) _checker = factory.CreateSpellChecker(language);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException) { AppLog.Warn("Notes: Windows' spell checker unavailable: " + ex.GetType().Name); }
            return _checker;
        }
    }

    /// <summary>Null: spelled right (or Windows has no checker); else up to six of Windows' suggestions, maybe none.</summary>
    internal static IReadOnlyList<string>? Suggest(string word)
    {
        if (word.Length is 0 or > 64 || word.Any(char.IsWhiteSpace)) return null;
        using var _ = Gate.EnterScope();
        if (Checker is not { } checker) return null;
        try
        {
            var errors = checker.Check(word);
            if (errors.Next(out var error) != 0) return null;
            Marshal.ReleaseComObject(error);
            var found = new List<string>();
            var list = checker.Suggest(word);
            var one = new string[1];
            while (found.Count < 6 && list.Next(1, one, IntPtr.Zero) == 0) found.Add(one[0]);
            return found;
        }
        catch (COMException ex) { AppLog.Debug("Notes: spell check failed: 0x" + ex.HResult.ToString("X8")); return null; }
    }

    internal static bool Add(string word)
    {
        if (word.Length is 0 or > 64 || word.Any(char.IsWhiteSpace)) return false;
        using var _ = Gate.EnterScope();
        if (Checker is not { } checker) return false;
        try { checker.Add(word); return true; }
        catch (COMException ex) { AppLog.Debug("Notes: add to dictionary failed: 0x" + ex.HResult.ToString("X8")); return false; }
    }

    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    private class SpellCheckerFactoryClass;

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        IEnumString SupportedLanguages { get; }
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        string LanguageTag { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
        void Add([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig]
        int Next([MarshalAs(UnmanagedType.Interface)] out object value);
    }
}
