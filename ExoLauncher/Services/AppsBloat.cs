namespace ExoLauncher.Services;

/// <summary>
/// Bloatware, as System › Apps groups it: what Windows puts on every PC that nothing needs (Tips, Solitaire, Bing News,
/// Feedback Hub, personal Teams…), the apps Windows installs as ads (TikTok, Candy Crush, Disney+…), and the trials a PC
/// maker adds (McAfee, Norton, WildTangent). Each says why in the page's words; none is removed without the user's press.
/// What Exo or the games here use never counts (AppsKeep, InstalledPrograms.Cares): not Spotify, Xbox, the Store or Photos'
/// codecs, nor Roblox or Minecraft, which are games.
/// </summary>
internal static class AppsBloat
{
    private const string Windows = "Comes with Windows; nothing needs it";
    private const string Ad = "Windows installed it as an ad";
    private const string Trial = "A trial your PC came with";

    // Store package names (before the publisher hash) of Windows' own extras.
    private static readonly string[] WindowsPackages =
    [
        "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingSearch", "Microsoft.GetHelp", "Microsoft.Getstarted",
        "Microsoft.MicrosoftSolitaireCollection", "Microsoft.MicrosoftOfficeHub", "Microsoft.People", "Microsoft.PowerAutomateDesktop",
        "Microsoft.Todos", "Microsoft.WindowsFeedbackHub", "Microsoft.WindowsMaps", "Microsoft.ZuneVideo", "MicrosoftTeams", "MSTeams",
        "Microsoft.549981C3F5F10", "Microsoft.MixedReality.Portal", "Microsoft.SkypeApp", "microsoft.windowscommunicationsapps",
        "Microsoft.Windows.DevHome", "Microsoft.3DBuilder", "Microsoft.Microsoft3DViewer", "Microsoft.Print3D", "Microsoft.Wallet",
        "Microsoft.Messaging", "Microsoft.OneConnect", "MicrosoftCorporationII.MicrosoftFamily", "Microsoft.MicrosoftJournal",
    ];

    // The sponsored apps Windows installs, by the start of their package names.
    private static readonly string[] AdPackages =
    [
        "king.com.", "Disney.", "BytedancePte.Ltd.TikTok", "Facebook.Instagram", "Facebook.Facebook", "7EE7776C.LinkedInforWindows",
        "AmazonVideo.PrimeVideo", "PricelinePartnerNetwork.Booking", "AdobeSystemsIncorporated.AdobePhotoshopExpress", "5A894077.McAfeeSecurity",
        "Clipchamp.Clipchamp", "4DF9E0F8.Netflix",
    ];

    // Desktop programs a PC maker adds, by the start of their names.
    private static readonly string[] TrialNames = ["McAfee", "Norton ", "Norton360", "WildTangent", "Dropbox Promotion", "Booking.com", "ExpressVPN Trial", "Avast Free Antivirus Trial"];

    /// <summary>Why <paramref name="program"/> is bloatware; null when it isn't.</summary>
    internal static string? Why(InstalledProgram program)
    {
        if (program.Care is not null || program.Replaces is not null) return null;
        if (program.Store && program.Id.StartsWith("store:", StringComparison.Ordinal))
        {
            var package = program.Id["store:".Length..].Split('_')[0];
            if (WindowsPackages.Any(known => package.Equals(known, StringComparison.OrdinalIgnoreCase))) return Windows;
            if (AdPackages.Any(known => package.StartsWith(known, StringComparison.OrdinalIgnoreCase))) return Ad;
            return null;
        }
        return TrialNames.Any(name => program.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)) ? Trial : null;
    }
}
