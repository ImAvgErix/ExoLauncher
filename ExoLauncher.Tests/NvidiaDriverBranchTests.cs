using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the card's driver branch could be read wrong (System > Graphics then offers a hotfix that NVIDIA's installer refuses,
/// as it did to a GTX 1080 Ti, or holds a newer card back from its hotfix):
/// - a Maxwell, Pascal or Volta card (left on release 580) is taken for one on NVIDIA's current branches;
/// - a Turing or newer card is taken for a left one;
/// - Windows' hardware id is read case-sensitively or with its subsystem part in the way;
/// - an unreadable or non-NVIDIA id counts as a current card.
/// </summary>
public sealed class NvidiaDriverBranchTests
{
    [Theory]
    [InlineData(@"pci\ven_10de&dev_1b06")] // GeForce GTX 1080 Ti (Pascal)
    [InlineData(@"pci\ven_10de&dev_13c2")] // GeForce GTX 970 (Maxwell)
    [InlineData(@"pci\ven_10de&dev_1d81")] // TITAN V (Volta)
    public void Cards_Game_Ready_left_get_no_hotfix(string hardwareId) =>
        Assert.False(NvidiaDriverService.CurrentBranches(hardwareId));

    [Theory]
    [InlineData(@"pci\ven_10de&dev_1e87")] // GeForce RTX 2080 (Turing)
    [InlineData(@"pci\ven_10de&dev_2484")] // GeForce RTX 3070 (Ampere)
    [InlineData(@"PCI\VEN_10DE&DEV_2484&SUBSYS_146B10DE")]
    public void Turing_and_newer_are_on_the_current_branches(string hardwareId) =>
        Assert.True(NvidiaDriverService.CurrentBranches(hardwareId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"pci\ven_1002&dev_73bf")]
    public void An_unknown_card_gets_no_hotfix(string? hardwareId) =>
        Assert.False(NvidiaDriverService.CurrentBranches(hardwareId));
}
