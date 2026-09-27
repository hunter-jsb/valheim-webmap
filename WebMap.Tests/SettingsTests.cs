using System.IO;
using WebMap;
using Xunit;

public class SettingsTests
{
    private static string Fresh() { var d = Path.Combine(Path.GetTempPath(), "webmap-settings-" + Path.GetRandomFileName()); Directory.CreateDirectory(d); WebMap.Settings.ResetForTests(d); return d; }

    [Fact]
    public void AValueOutOfRangeIsRefusedAndNothingChanges()
    {
        Fresh();
        float was = WebMapConfig.EXPLORE_RADIUS;
        Assert.NotNull(WebMap.Settings.Set("explore_radius", "9999", "tester", out _, out _));
        Assert.Equal(was, WebMapConfig.EXPLORE_RADIUS);
    }

    [Fact]
    public void ASettingSurvivesAReloadAndAnEmptyValueReturnsTheConfigs()
    {
        var d = Fresh();
        float was = WebMapConfig.EXPLORE_RADIUS;
        Assert.Null(WebMap.Settings.Set("explore_radius", "150", "tester", out string log, out bool restart));
        Assert.False(restart);
        Assert.Contains("tester", log);
        WebMapConfig.EXPLORE_RADIUS = was;
        WebMap.Settings.Load(d);                                   // a restart: the given value lies over the config again
        Assert.Equal(150f, WebMapConfig.EXPLORE_RADIUS);
        Assert.Null(WebMap.Settings.Set("explore_radius", "", "tester", out _, out _));
        Assert.Equal(was, WebMapConfig.EXPLORE_RADIUS);
    }

    [Fact]
    public void AChangeThatNeedsARestartSaysSoAndASecretIsNeverShown()
    {
        Fresh();
        Assert.Null(WebMap.Settings.Set("texture_size", "4096", "tester", out _, out bool restart));
        Assert.True(restart);
        Assert.Null(WebMap.Settings.Set("discord_webhook", "https://discord.com/api/webhooks/1/abc", "tester", out _, out _));
        Assert.DoesNotContain("abc", WebMap.Settings.Json());
        Assert.Contains("\"set\":true", WebMap.Settings.Json());
        WebMapConfig.TEXTURE_SIZE = 2048; WebMapConfig.DISCORD_WEBHOOK = "";
    }
}
