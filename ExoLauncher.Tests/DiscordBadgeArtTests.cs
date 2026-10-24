using System.Text.Json.Nodes;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// The Server Tag badges' art read out of Discord's client, which fails silently (the picker falls back to names, or
/// draws a wrong badge): a badge matched to the wrong number; a tint slot read as the wrong tint (primary for
/// secondary) or a fixed color lost; a tint's luminances or weights taken from another scope's array of the same name;
/// a spread, a fallback (s?.[1]??"#…"), a gradient's runtime id or a weight more than the luminances breaking the read; markup or script passed through
/// as an attribute; a reshaped module answered with half a badge instead of nothing.
/// </summary>
public sealed class DiscordBadgeArtTests
{
    // A module shaped as Discord's (minified JSX calls, a tint helper, constants before each badge), its art made up.
    private const string Chunk = """
        "use strict";(this.webpackChunkdiscord_app=this.webpackChunkdiscord_app||[]).push([["806354"],{620757(A,h,v){v.d(h,{J:()=>v7});var l=v(477900);
        function i(A){let{primaryBaseColors:h}=A,s=h,a=[];return{primaryColorsTransformed:s,secondaryColorsTransformed:a}}
        let s=["#E79418","#FAE330"],a=[.5,.75],f=[{base:5,tint:1},{base:3,tint:1}];
        function e(A){let{width:h=24,height:v=24,primaryTintColor:H,...V}=A,{primaryColorsTransformed:Z}=i({primaryBaseColors:s,primaryTintColor:H,primaryTintLuminances:a,primaryLuminanceWeights:f});return(0,l.jsxs)("svg",{...(0,M.A)(V),width:h,height:v,viewBox:"0 0 16 16",fill:"none",xmlns:"http://www.w3.org/2000/svg",children:[(0,l.jsx)("path",{d:"M1 1h2v2H1z",fill:Z[1]}),(0,l.jsx)("path",{d:"M3 3h2v2H3z",fill:"#000",opacity:".5"}),(0,l.jsx)("path",{d:"M5 5h1v1H5z",fill:"<script>"})]})}
        let AX=["#111111","#222222"],AJ=[.2,.6],AG=[{base:1,tint:1},{base:2,tint:1},{base:9,tint:1}],AT=["#333333"],AW=[.4],AY=[{base:3,tint:2}];
        function AN(A){let{width:h=24,height:v=24,primaryTintColor:V,secondaryTintColor:Z,...t}=A,s=H.useMemo(()=>`badge-force-gradient-${(0,Ak.A)()}`,[]),{primaryColorsTransformed:a,secondaryColorsTransformed:f}=i({primaryBaseColors:AX,primaryTintColor:V,primaryTintLuminances:AJ,primaryLuminanceWeights:AG,secondaryBaseColors:AT,secondaryTintColor:Z,secondaryTintLuminances:AW,secondaryLuminanceWeights:AY});return(0,l.jsxs)("svg",{...(0,M.A)(t),width:h,height:v,viewBox:"0 0 16 16",fill:"none",children:[(0,l.jsx)("path",{d:"M0 0h1v1H0z",fill:f[0]}),(0,l.jsx)("path",{fillRule:"evenodd",d:"M1 0h1v1H1z",fill:f?.[0]??AX[1]}),(0,l.jsx)("path",{d:"M2 0h1v1H2z",fill:`url(#${s})`}),(0,l.jsx)("defs",{children:(0,l.jsxs)("radialGradient",{id:s,cx:"0.75",r:"1",children:[(0,l.jsx)("stop",{stopColor:a[1],offset:"30%"}),(0,l.jsx)("stop",{stopColor:AX[0],offset:"70%"})]})})]})}
        function v7(A){let{badge:h,primaryTintColor:v,secondaryTintColor:H,...M}=A;switch(h){case v6.bl.FORCE:return(0,l.jsx)(AN,{primaryTintColor:v,secondaryTintColor:H,...M});case v6.bl.BEE:return(0,l.jsx)(e,{primaryTintColor:v,...M});case v6.bl.NEW_THING:return(0,l.jsx)(e,{primaryTintColor:v,...M})}return null}}}]);
        """;

    private static JsonArray Badges() => (JsonArray)DiscordBadgeArt.Parse(Chunk)!["badges"]!;

    private static JsonObject Badge(int id) => Badges().OfType<JsonObject>().Single(b => (int)b["id"]! == id);

    [Fact]
    public void EachBadgeKeepsItsDiscordNumberAndUnknownOnesAreLeftOut()
    {
        Assert.Equal(new[] { 12, 40 }, Badges().Select(b => (int)b!["id"]!).Order());
    }

    [Fact]
    public void TintsAndFixedColorsAreReadAsTheyAre()
    {
        var bee = Badge(40);
        var paths = bee["nodes"]!.AsArray();
        Assert.Equal(1, (int)paths[0]!["attrs"]!["fill"]!["p"]!);
        Assert.Equal("#000", (string)paths[1]!["attrs"]!["fill"]!);
        Assert.Equal(".5", (string)paths[1]!["attrs"]!["opacity"]!);
        Assert.Null(bee["secondary"]);
        Assert.Equal(new[] { 0.5, 0.75 }, bee["primary"]!["lum"]!.AsArray().Select(n => (double)n!));
        Assert.Equal(3, (double)bee["primary"]!["weights"]![1]!["base"]!);
    }

    [Fact]
    public void SecondaryFallbackGradientAndConstantsSurviveTheRead()
    {
        var force = Badge(12);
        var nodes = force["nodes"]!.AsArray();
        Assert.Equal(0, (int)nodes[0]!["attrs"]!["fill"]!["s"]!);
        // f?.[0]??AX[1]: the secondary tint, its fallback read past.
        Assert.Equal(0, (int)nodes[1]!["attrs"]!["fill"]!["s"]!);
        Assert.Equal("evenodd", (string)nodes[1]!["attrs"]!["fillRule"]!);
        Assert.True((bool)nodes[2]!["attrs"]!["fill"]!["url"]!);
        var gradient = nodes[3]!["children"]![0]!;
        Assert.Equal("radialGradient", (string)gradient["tag"]!);
        Assert.True((bool)gradient["attrs"]!["id"]!["id"]!);
        Assert.Equal(1, (int)gradient["children"]![0]!["attrs"]!["stopColor"]!["p"]!);
        // AX[0] is a constant base color, not a tint.
        Assert.Equal("#111111", (string)gradient["children"]![1]!["attrs"]!["stopColor"]!);
        // Three weights for two luminances (Discord's sun and wind do this): each luminance takes the weight at its place.
        Assert.Equal(2, force["primary"]!["lum"]!.AsArray().Count);
        Assert.Equal(new[] { 0.4 }, force["secondary"]!["lum"]!.AsArray().Select(n => (double)n!));
    }

    [Fact]
    public void ATintsArraysComeFromTheBadgesOwnScope()
    {
        // The helper's own a=[] above must not stand in for the bee's a=[.5,.75].
        Assert.Equal(2, Badge(40)["primary"]!["lum"]!.AsArray().Count);
    }

    [Fact]
    public void MarkupNeverPassesAsAnAttribute()
    {
        Assert.Null(Badge(40)["nodes"]![2]!["attrs"]!["fill"]);
    }

    [Fact]
    public void AModuleThatIsNotThereIsNoArt()
    {
        Assert.Null(DiscordBadgeArt.Parse("""(this.webpackChunkdiscord_app=[]).push([["1"],{1(e){}}])"""));
        Assert.Null(DiscordBadgeArt.Parse(Chunk.Replace("primaryTintLuminances", "lums", StringComparison.Ordinal)));
    }
}
