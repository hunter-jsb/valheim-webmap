using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using BepInEx;
using Xunit;
using Mod = WebMap.WebMap;

namespace WebMap.Tests
{
    // BepInEx logs the plugin's version at load: it must be the one the package is published as.
    public class VersionTests
    {
        [Fact]
        public void ThePluginReportsTheManifestsVersion()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(d.FullName, "manifest.json"))) d = d.Parent;
            string v = JsonDocument.Parse(File.ReadAllText(Path.Combine(d.FullName, "manifest.json"))).RootElement.GetProperty("version_number").GetString();
            Assert.Equal(v, typeof(Mod).GetCustomAttribute<BepInPlugin>(false).Version.ToString());
        }
    }
}
