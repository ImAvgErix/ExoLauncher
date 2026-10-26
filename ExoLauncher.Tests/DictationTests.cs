using System.Reflection;
using System.Security.Cryptography;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways dictation and Exo's parts could fail without anyone seeing why:
/// - words go in glued to the word before them ("helloWorld"), or with a space after an opening bracket or before a comma;
/// - the speech model is trusted when a file is missing, cut short, the wrong size, or changed in place at the same size
///   (a stamp from an earlier check must never vouch for different bytes);
/// - a quiet take stays too quiet to transcribe, or a loud one is pushed into clipping;
/// - the voice line's level leaves 0..1 or doesn't rise with the voice;
/// - a pinned part is added to Exo without being listed among its parts, so setup never fetches it and a feature waits
///   on its first use again;
/// - setup's words for a part don't say what and how much.
/// </summary>
public sealed class DictationTests
{
    [Theory]
    [InlineData(null, "Hello there.", null, "Hello there.")]
    [InlineData("", "  Hello there.  ", "", "Hello there.")]
    [InlineData("word", "Hello.", null, " Hello.")]
    [InlineData("end.", "Next one.", null, " Next one.")]
    [InlineData("Note:", "remember this.", null, " remember this.")]
    [InlineData("word ", "Hello.", null, "Hello.")]
    [InlineData("line\n", "Hello.", null, "Hello.")]
    [InlineData("(", "aside", ")", "aside")]
    [InlineData("\"", "quoted", "\"", "quoted")]
    [InlineData("@", "erix", null, "erix")]
    [InlineData("word", ", and more", null, ", and more")]
    [InlineData(null, "Hello", "world", "Hello ")]
    [InlineData("say", "hello", "there", " hello ")]
    [InlineData("say", "hello", " there", " hello")]
    [InlineData("say", "hello", ".", " hello")]
    [InlineData("word", "   ", "next", "")]
    public void Words_are_spaced_for_where_they_go(string? before, string spoken, string? after, string expected)
        => Assert.Equal(expected, Dictation.Join(before, spoken, after));

    [Fact]
    public void The_model_is_ready_only_when_every_file_matches_its_pin()
    {
        var folder = Folder();
        var a = Write(folder, "a.onnx", 4096, seed: 1);
        var b = Write(folder, "b.txt", 300, seed: 2);
        DictationModel.PinnedFile[] pins = [a, b];

        Assert.True(DictationModel.Verify(folder, pins));
        // Again, from the stamps it kept: still ready, nothing changed.
        Assert.True(DictationModel.Verify(folder, pins));

        // Missing.
        File.Delete(Path.Combine(folder, "b.txt"));
        Assert.False(DictationModel.Verify(folder, pins));
        Write(folder, "b.txt", 300, seed: 2);
        Assert.True(DictationModel.Verify(folder, pins));

        // Cut short, and too long.
        File.WriteAllBytes(Path.Combine(folder, "a.onnx"), Bytes(4000, 1));
        Assert.False(DictationModel.Verify(folder, pins));
        File.WriteAllBytes(Path.Combine(folder, "a.onnx"), Bytes(5000, 1));
        Assert.False(DictationModel.Verify(folder, pins));
    }

    [Fact]
    public void A_file_changed_in_place_at_the_same_size_is_never_trusted_on_an_old_check()
    {
        var folder = Folder();
        var pin = Write(folder, "encoder.onnx", 8192, seed: 3);
        Assert.True(DictationModel.Verify(folder, [pin]));

        var path = Path.Combine(folder, "encoder.onnx");
        var written = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        // Even with its write time put back: the change time and the hash still tell.
        File.SetLastWriteTimeUtc(path, written);

        Assert.False(DictationModel.Verify(folder, [pin]));
        Assert.False(DictationModel.Whole(path, pin));
    }

    [Fact]
    public void A_pin_that_changes_has_the_file_checked_again()
    {
        var folder = Folder();
        var pin = Write(folder, "joiner.onnx", 1024, seed: 4);
        Assert.True(DictationModel.Verify(folder, [pin]));
        // A new release's pin for the same name and size: the kept stamp was for the old pin.
        Assert.False(DictationModel.Verify(folder, [pin with { Sha256 = new string('0', 64) }]));
    }

    [Fact]
    public void An_unreadable_pin_fails_closed()
    {
        var folder = Folder();
        var pin = Write(folder, "tokens.txt", 64, seed: 5);
        Assert.False(DictationModel.Whole(Path.Combine(folder, "tokens.txt"), pin with { Sha256 = "not hex" }));
        Assert.False(DictationModel.Whole(Path.Combine(folder, "missing.txt"), pin));
    }

    [Fact]
    public void The_pinned_speech_model_is_four_files_from_one_pinned_archive()
    {
        Assert.Equal(["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"], DictationModel.Files.Select(f => f.Name));
        Assert.All(DictationModel.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
        Assert.Equal(DictationModel.Archive.ExpectedSize, DictationModel.Component.Download);
        Assert.Equal(DictationModel.Files.Sum(f => f.Size), DictationModel.Component.Unpack);
        Assert.Matches("^[0-9a-f]{64}$", DictationModel.Archive.ExpectedSha256);
    }

    [Fact]
    public void A_quiet_take_is_brought_up_and_a_loud_one_left_alone()
    {
        var quiet = Enumerable.Range(0, 1600).Select(i => 0.02f * MathF.Sin(i * 0.1f)).ToArray();
        Dictation.Normalize(quiet);
        Assert.InRange(quiet.Max(MathF.Abs), 0.19f, 0.21f); // at most ten times louder

        var soft = Enumerable.Range(0, 1600).Select(i => 0.1f * MathF.Sin(i * 0.1f)).ToArray();
        Dictation.Normalize(soft);
        Assert.InRange(soft.Max(MathF.Abs), 0.49f, 0.51f);

        var loud = Enumerable.Range(0, 1600).Select(i => 0.8f * MathF.Sin(i * 0.1f)).ToArray();
        var before = loud.ToArray();
        Dictation.Normalize(loud);
        Assert.Equal(before, loud);

        var silence = new float[1600];
        Dictation.Normalize(silence);
        Assert.All(silence, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void The_voice_lines_level_rises_with_the_voice_and_stays_in_range()
    {
        static float[] Tone(float amplitude) => Enumerable.Range(0, 960).Select(i => amplitude * MathF.Sin(i * 0.2f)).ToArray();
        Assert.Equal(0f, Dictation.Level(new float[960]));
        Assert.Equal(0f, Dictation.Level(Tone(0.0005f)));
        Assert.Equal(1f, Dictation.Level(Tone(1f)));
        float[] amplitudes = [0.002f, 0.01f, 0.03f, 0.1f, 0.3f];
        var levels = amplitudes.Select(a => Dictation.Level(Tone(a))).ToArray();
        Assert.All(levels, l => Assert.InRange(l, 0f, 1f));
        for (var i = 1; i < levels.Length; i++) Assert.True(levels[i] > levels[i - 1], $"{amplitudes[i]} should read louder than {amplitudes[i - 1]}");
    }

    [Fact]
    public void Every_pinned_part_is_one_of_Exos_components()
    {
        var pinned = typeof(ExoComponents).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(f => f.FieldType == typeof(GitHubReleaseAsset))
            .Select(f => (GitHubReleaseAsset)f.GetValue(null)!)
            .ToList();
        Assert.NotEmpty(pinned);
        var sizes = ExoComponents.All.Select(c => c.Download).ToHashSet();
        Assert.All(pinned, asset => Assert.Contains(asset.ExpectedSize, sizes));
        Assert.Equal(pinned.Count, ExoComponents.All.Count);
        Assert.Equal(ExoComponents.All.Count, ExoComponents.All.Select(c => c.Location()).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Setup_says_what_comes_and_how_much()
    {
        var model = DictationModel.Component;
        Assert.Equal("Parakeet speech model · 210 of 465 MB", ExoComponents.Words(model, 210L * 1048576, unpacking: false));
        Assert.Equal("Unpacking Parakeet speech model · 300 of 639 MB", ExoComponents.Words(model, 300L * 1048576, unpacking: true));
        Assert.Equal("1,157", ExoComponents.Megabytes(1157L * 1048576));
    }

    private static string Folder()
    {
        var folder = Path.Combine(TestDataDirectory.Root, "dictation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static DictationModel.PinnedFile Write(string folder, string name, int length, int seed)
    {
        var bytes = Bytes(length, seed);
        File.WriteAllBytes(Path.Combine(folder, name), bytes);
        return new DictationModel.PinnedFile(name, length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}
