using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo could write a DualSense Edge profile the controller rejects or misreads:
/// - a CRC over the wrong bytes (the controller keeps one per profile; its own default's is the check here);
/// - an empty slot read as a profile, or the default profile's fields read from the wrong bytes;
/// - a written profile that doesn't read back as itself;
/// - a preset's points rounded the .NET way instead of the controller's;
/// - slot 1 (the controller's default) written, a curve going backwards, or a paddle as a remap target.
/// </summary>
public sealed class EdgeProfilesTests
{
    // The user's DualSense Edge, 2026-10-26: slot 1 ("Default Profile") and an empty slot, read over USB.
    private const string Chunk0 = "70 00 01 00 00 00 44 00 65 00 66 00 61 00 75 00 6C 00 74 00 20 00 50 00 72 00 6F 00 66 00 69 00 6C 00 65 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00";
    private const string Chunk1 = "71 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00";
    private const string Chunk2 = "72 02 00 00 00 FF 00 FF 00 00 00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 00 00 C0 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 FE 6C 83 18 00 00 00 00";

    private static byte[] Bytes(string hex) => [.. hex.Split(' ').Select(b => Convert.ToByte(b, 16))];

    private static byte[] Empty(byte id)
    {
        var r = new byte[64];
        r[0] = id;
        r[1] = 0x10;
        return r;
    }

    [Fact]
    public void TheEdgesOwnDefaultProfileChecksOut()
    {
        var (stored, computed) = EdgeProfiles.Checksums(Bytes(Chunk0), Bytes(Chunk1), Bytes(Chunk2));
        Assert.Equal(0x18836CFEu, stored);
        Assert.Equal(stored, computed);
    }

    [Fact]
    public void TheDefaultProfileReadsAsTheControllerShipsIt()
    {
        var p = EdgeProfiles.Parse(1, Bytes(Chunk0), Bytes(Chunk1), Bytes(Chunk2))!;
        Assert.True(p.Assigned);
        Assert.Equal("Default Profile", p.Name);
        Assert.Equal([0, 100], p.L2);
        Assert.Equal([0, 100], p.R2);
        Assert.Equal(EdgeProfiles.Sources, p.Buttons);
        Assert.Equal(EdgeProfiles.DefaultDisabled, p.Disabled);
        Assert.Equal(("strong", "strong"), (p.Vibration, p.TriggerEffects));
    }

    [Fact]
    public void AnEmptySlotIsNotAProfile()
    {
        var p = EdgeProfiles.Parse(2, Empty(0x73), Empty(0x74), Empty(0x75))!;
        Assert.False(p.Assigned);
        Assert.Equal(EdgeProfiles.Sources, p.Buttons);
    }

    [Fact]
    public void AWrittenProfileReadsBackAsItself()
    {
        var made = EdgeProfiles.New(3, "Shooters") with
        {
            Left = new EdgeProfiles.Stick(1, EdgeProfiles.Curve(1, 0.1, 2)),
            Right = new EdgeProfiles.Stick(2, EdgeProfiles.Curve(2, 0.05, -1)),
            L2 = [10, 80],
            R2 = [0, 50],
            Vibration = "weak",
            TriggerEffects = "off",
            // The left back button sends Cross, the right one Circle; both on; the sticks swapped.
            Buttons = [.. EdgeProfiles.Sources[..14], 0x05, 0x04],
            Disabled = 1u << EdgeProfiles.SwapSticksBit,
        };
        var c = EdgeProfiles.Build(made, DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000));
        Assert.Equal([0x61, 0x61, 0x61], c.Select(x => x[0]));
        var (stored, computed) = EdgeProfiles.Checksums(c[0], c[1], c[2]);
        Assert.Equal(stored, computed);

        var back = EdgeProfiles.Parse(3, c[0], c[1], c[2])!;
        Assert.Equal("Shooters", back.Name);
        Assert.Equal(made.UniqueId, back.UniqueId);
        Assert.Equal((1, 2), (back.Left.Preset, back.Right.Preset));
        Assert.Equal(made.Left.Points, back.Left.Points);
        Assert.Equal(made.Right.Points, back.Right.Points);
        Assert.Equal([10, 80], back.L2);
        Assert.Equal([0, 50], back.R2);
        Assert.Equal(("weak", "off"), (back.Vibration, back.TriggerEffects));
        Assert.Equal(made.Buttons, back.Buttons);
        Assert.Equal(made.Disabled, back.Disabled);
        Assert.Equal(1_790_000_000_000, back.Updated);
    }

    [Fact]
    public void ANewProfileCanBeWritten()
    {
        // Every button sends itself, the back buttons too (they're off until given a target).
        var c = EdgeProfiles.Build(EdgeProfiles.New(2, "New"), DateTimeOffset.UnixEpoch);
        var back = EdgeProfiles.Parse(2, c[0], c[1], c[2])!;
        Assert.Equal(EdgeProfiles.Sources, back.Buttons);
        Assert.Equal(EdgeProfiles.DefaultDisabled, back.Disabled);
    }

    [Fact]
    public void PresetsRoundAsTheControllerDoes()
    {
        Assert.Equal([0, 0, 38, 38, 107, 167, 255, 255], EdgeProfiles.Curve(1, 0, 0));
        // Quick at 10% deadzone and +2: 25.5 rounds up to 26 (the .NET default would give 26 too, but 59.7, 121.8 and
        // the adjustment's floor are where a different rule drifts).
        var quick = EdgeProfiles.Curve(1, 0.1, 2);
        Assert.Equal([26, 0, 60, 38, 116, 178, 255, 255], quick);
        Assert.Equal(0.1, EdgeProfiles.DeadzoneOf(quick));
        Assert.Equal(2, EdgeProfiles.AdjustmentOf(1, quick));
        Assert.Equal(-3, EdgeProfiles.AdjustmentOf(4, EdgeProfiles.Curve(4, 0, -3)));
    }

    [Fact]
    public void SlotsAndReportsLineUp()
    {
        Assert.Equal([0x70, 0x73, 0x76, 0x79], new[] { 1, 2, 3, 4 }.Select(EdgeProfiles.ReadId));
        Assert.Equal(((byte)0x60, (byte)0x63), EdgeProfiles.WriteIds(2));
        Assert.Equal(((byte)0x62, (byte)0x65), EdgeProfiles.WriteIds(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => EdgeProfiles.WriteIds(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EdgeProfiles.DeleteIndex(1));
    }

    [Fact]
    public void WhatTheControllerCantHoldIsRefused()
    {
        var ok = EdgeProfiles.New(2, "Racing");
        var now = DateTimeOffset.UnixEpoch;
        Assert.Throws<ArgumentOutOfRangeException>(() => EdgeProfiles.Build(ok with { Slot = 1 }, now));
        Assert.Throws<ArgumentException>(() => EdgeProfiles.Build(ok with { Name = new string('x', 41) }, now));
        Assert.Throws<ArgumentException>(() => EdgeProfiles.Build(ok with { Left = new EdgeProfiles.Stick(0xFF, [0, 0, 200, 100, 100, 150, 255, 255]) }, now));
        Assert.Throws<ArgumentException>(() => EdgeProfiles.Build(ok with { L2 = [60, 40] }, now));
        Assert.Throws<ArgumentException>(() => EdgeProfiles.Build(ok with { Buttons = [.. EdgeProfiles.Sources[..15], 0x0E] }, now));
    }
}
