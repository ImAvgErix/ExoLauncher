using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class GpuCapabilityTests
{
    [Theory]
    [InlineData("AMD Radeon RX 9070 XT")]
    [InlineData("AMD Radeon RX 9070")]
    [InlineData("AMD Radeon RX 9060 XT")]
    [InlineData("Radeon(TM) RX 9070M")]
    [InlineData("AMD Radeon AI PRO R9700")]
    [InlineData("AMD Radeon Graphics (gfx1201)")]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("AMD Radeon Graphics (gfx1100)")]
    public void Rdna4Names_AreTheOnlyOnesThatCanRunFsr4(string adapter) =>
        Assert.True(GpuCapability.IsRdna4AdapterName(adapter));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070")]
    [InlineData("NVIDIA GeForce RTX 5090")]
    [InlineData("AMD Radeon RX 6800")]
    [InlineData("AMD Radeon R9 390")]
    [InlineData("AMD Radeon Pro W9100")]
    [InlineData("AMD Radeon 890M Graphics")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics")]
    [InlineData("")]
    [InlineData(null)]
    public void NonRdna4Names_DoNotClaimFsr4(string? adapter) =>
        Assert.False(GpuCapability.IsRdna4AdapterName(adapter));

    [Fact]
    public void Adapters_ReadTheDriverClassKeyWithoutThrowing()
    {
        var adapters = GpuCapability.Adapters();

        Assert.NotNull(adapters);
        Assert.All(adapters, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(adapters.Any(GpuCapability.IsRdna4AdapterName), GpuCapability.SupportsFsr4());
        Assert.Equal(
            GpuCapability.SupportsFsr4() ? null : GpuCapability.Fsr4NeedsRdna4,
            GpuCapability.Fsr4BlockReason());
    }
}
