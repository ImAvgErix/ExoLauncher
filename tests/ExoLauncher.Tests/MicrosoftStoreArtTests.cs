using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class MicrosoftStoreArtTests
{
    [Fact]
    public void ProductIdFor_MapsMinecraftAndRobloxRows()
    {
        Assert.Equal("9NBLGGH2JHXJ", MicrosoftStoreArt.ProductIdFor(new GameEntry
        {
            Id = "minecraft:java",
            Title = "Minecraft",
            Store = StoreKind.Minecraft,
        }));
        Assert.Equal("9PMF91N3LZ3M", MicrosoftStoreArt.ProductIdFor(new GameEntry
        {
            Id = "roblox:player",
            Title = "Roblox",
            Store = StoreKind.Roblox,
        }));
    }

    [Fact]
    public void PortraitUrlsFromCatalog_PrefersPosterThenBoxArt()
    {
        const string json = """
            {
              "Product": {
                "LocalizedProperties": [
                  {
                    "Images": [
                      {
                        "ImagePurpose": "Logo",
                        "Uri": "//store-images.s-microsoft.com/image/logo.png",
                        "Height": 300,
                        "Width": 300
                      },
                      {
                        "ImagePurpose": "BoxArt",
                        "Uri": "//store-images.s-microsoft.com/image/box.png",
                        "Height": 1080,
                        "Width": 1080
                      },
                      {
                        "ImagePurpose": "Poster",
                        "Uri": "//store-images.s-microsoft.com/image/poster.png",
                        "Height": 1080,
                        "Width": 720
                      }
                    ]
                  }
                ]
              }
            }
            """;

        var urls = MicrosoftStoreArt.PortraitUrlsFromCatalog(json);
        Assert.Equal(2, urls.Count);
        Assert.Equal("https://store-images.s-microsoft.com/image/poster.png", urls[0]);
        Assert.Equal("https://store-images.s-microsoft.com/image/box.png", urls[1]);
    }
}
