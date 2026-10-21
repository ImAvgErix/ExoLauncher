using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace ExoLauncher.Helpers;

/// <summary>Local rolling diagnostics — no network, no analytics.</summary>
public static partial class AppLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;
    // PublicationOnly: a failed first lookup is retried instead of cached.
    private static readonly Lazy<string> LogPath = new(
        () => Path.Combine(PathHelper.LogsDir, "app.log"), LazyThreadSafetyMode.PublicationOnly);
    // Guarded by Gate. One append handle for the run instead of an open per line.
    private static FileStream? s_stream;
    private static string? s_streamPath;
    private static readonly Channel<(string Path, string Line)> Pending = Channel.CreateBounded<(string, string)>(
        new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

    static AppLog()
    {
        _ = Task.Run(async () =>
        {
            await foreach (var entry in Pending.Reader.ReadAllAsync()) WriteLine(entry.Path, entry.Line);
        });
    }
    // Source-generated so the first log call does not compile regexes at runtime.
    [GeneratedRegex(@"(?i)\b((?:authorization:\s*)?bearer\s+)\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();
    [GeneratedRegex(
        @"(?i)\b(token|access[_-]?token|refresh[_-]?token|refresh|password|passwd|secret|client_secret|authorization|api[_-]?key|session)\s*[:=]\s*\S+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();
    // The generator does not support NonBacktracking. The quoted-string branch
    // has disjoint alternatives and no nested quantifiers, so it stays linear.
    [GeneratedRegex(
        "(?i)[\"'](token|access[_-]?token|refresh[_-]?token|password|passwd|secret|client_secret|authorization|api[_-]?key|session)[\"']\\s*:\\s*(?:\"(?:\\\\.|[^\"\\\\])*\"|'[^']*'|[^,\\s}]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex QuotedSecretAssignment();
    [GeneratedRegex(
        @"(?i)\b(user[_-]?id|account[_-]?id|steam[_-]?id|friend[_-]?ids?|claimed[_-]?id|external[_-]?id|store[_-]?id|store[_-]?native[_-]?id)\s*[:=]\s*\S+",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdentityAssignment();
    [GeneratedRegex(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    // Secrets logged with no label: a three-part token (JWT, Discord) and a long opaque
    // run of mixed-case letters and digits (OAuth access tokens). Lowercase hex hashes,
    // which download checks log on purpose, are one case and stay readable.
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{16,}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex DottedToken();
    [GeneratedRegex(@"(?<![A-Za-z0-9_+=-])[A-Za-z0-9_+-]{40,}={0,2}(?![A-Za-z0-9_+=-])", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueRun();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    /// <summary>Crash path is synchronous so process termination cannot discard the final diagnostic.</summary>
    public static void Crash(string message, Exception? exception)
    {
        try
        {
            var detail = $"{message}: {exception}";
            if (detail.Length > 32768) detail = detail[..32768] + " [truncated]";
            detail = Redact(detail);
            WriteLine(LogPath.Value, $"[{DateTime.UtcNow:O}] CRASH {detail}{Environment.NewLine}");
        }
        catch { /* Last-chance diagnostics must not throw. */ }
    }

    public static void Debug(string message)
    {
#if DEBUG
        Write("DEBUG", message);
#endif
    }

    private static void Write(string level, string message)
    {
        try
        {
            var path = LogPath.Value;
            var redacted = Redact(message);
            if (redacted.Length > 32768) redacted = redacted[..32768] + " [truncated]";
            var line = $"[{DateTime.UtcNow:O}] {level} {redacted}{Environment.NewLine}";
            Pending.Writer.TryWrite((path, line));
        }
        catch { /* best-effort */ }
    }

    private static void WriteLine(string path, string line)
    {
        try
        {
            lock (Gate)
            {
                try
                {
                    var stream = OpenLocked(path);
                    // Seeking to the end keeps appends correct if another
                    // process wrote meanwhile, and gives the length without a stat.
                    if (stream.Seek(0, SeekOrigin.End) > MaxBytes)
                    {
                        CloseLocked();
                        var bak = path + ".1";
                        try { File.Delete(bak); } catch { /* */ }
                        try { File.Move(path, bak); } catch { /* */ }
                        stream = OpenLocked(path);
                        // A locked backup must not turn failed rotation into unlimited appends.
                        if (stream.Seek(0, SeekOrigin.End) > MaxBytes) return;
                    }

                    stream.Write(Encoding.UTF8.GetBytes(line));
                    stream.Flush();
                }
                catch
                {
                    // Reopen on the next line rather than keep a broken handle.
                    CloseLocked();
                }
            }
        }
        catch
        {
            /* best-effort */
        }
    }

    private static FileStream OpenLocked(string path)
    {
        if (s_stream is not null && string.Equals(s_streamPath, path, StringComparison.OrdinalIgnoreCase))
            return s_stream;
        CloseLocked();
        s_stream = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        s_streamPath = path;
        return s_stream;
    }

    private static void CloseLocked()
    {
        try { s_stream?.Dispose(); } catch { /* */ }
        s_stream = null;
        s_streamPath = null;
    }

    internal static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        var text = QuotedSecretAssignment().Replace(message, "$1=[redacted]");
        text = Bearer().Replace(text, "$1[redacted]");
        text = SecretAssignment().Replace(text, "$1=[redacted]");
        text = IdentityAssignment().Replace(text, "$1=[redacted]");
        text = DottedToken().Replace(text, "[redacted]");
        text = OpaqueRun().Replace(text, static match => LooksLikeToken(match.ValueSpan) ? "[redacted]" : match.Value);
        return Email().Replace(text, "[redacted-email]");
    }

    private static bool LooksLikeToken(ReadOnlySpan<char> run)
    {
        bool upper = false, lower = false, digit = false;
        foreach (var c in run)
        {
            upper |= char.IsAsciiLetterUpper(c);
            lower |= char.IsAsciiLetterLower(c);
            digit |= char.IsAsciiDigit(c);
        }
        return upper && lower && digit;
    }
}
