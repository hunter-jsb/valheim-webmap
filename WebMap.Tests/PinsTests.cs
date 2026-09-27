using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Mod = WebMap.WebMap;

namespace WebMap.Tests
{
    // Pins written from the site: POST /pins after the token check, as MapDataServer.WritePin.
    public class PinsTests : WithDir
    {
        const string Game = "Steam_76561198055335685,1789275213-7472,dot,Grant,10.00,20.00,camp";
        readonly MapDataServer server = new MapDataServer(true);
        string File_ => Path.Combine(Dir, "pins.csv");

        public PinsTests()
        {
            Mod.mapDataServer = server;      // where the save and the fog read from
            Mod.worldDataPath = Dir;
            server.pins.Add(Game);
        }
        public override void Dispose() { Mod.mapDataServer = null; Mod.worldDataPath = null; base.Dispose(); }

        string Write(string body, out string id, string who = "Withers") => server.WritePin(body, who, out id);
        string[] Line(string id) => server.pins.Single(l => l.Split(',')[1] == id).Split(',');

        [Fact]
        public void ASitePinIsPlacedByTheWebAndSaved()
        {
            Assert.Null(Write("{\"op\":\"add\",\"x\":100,\"z\":-50,\"type\":\"fire\",\"text\":\"Camp, north\"}", out string id));
            Assert.Equal(new[] { "web", id, "fire", "Withers", "100.00", "-50.00", "Camp", " north" }, Line(id));
            Assert.Equal(server.pins, File.ReadAllLines(File_));
        }

        [Fact]
        public void SitePinIdsNeverCollideWithEachOtherOrTheGamesOwn()
        {
            var ids = new List<string>();
            for (int i = 0; i < 50; i++) { Assert.Null(Write("{\"op\":\"add\",\"x\":1,\"z\":1}", out string id)); ids.Add(id); }
            Assert.Equal(51, server.pins.Select(l => l.Split(',')[1]).Distinct().Count());
            Assert.All(ids, id => Assert.StartsWith("w", id));
        }

        [Fact]
        public void AnEditChangesOnlyWhatItNames()
        {
            Write("{\"op\":\"add\",\"x\":100,\"z\":-50,\"type\":\"house\",\"text\":\"Home, sweet\"}", out string id);
            Assert.Null(Write("{\"op\":\"edit\",\"id\":\"" + id + "\",\"type\":\"cave\"}", out _, "Grant"));
            Assert.Equal(new[] { "web", id, "cave", "Withers", "100.00", "-50.00", "Home", " sweet" }, Line(id));
            Assert.Null(Write("{\"op\":\"edit\",\"id\":\"" + id + "\",\"x\":7,\"z\":8}", out _, "Grant"));
            Assert.Equal(new[] { "7.00", "8.00", "Home", " sweet" }, Line(id).Skip(4));
        }

        [Fact]
        public void ADeleteTakesUpThatPinAlone()
        {
            Write("{\"op\":\"add\",\"x\":100,\"z\":-50}", out string id);
            Assert.Null(Write("{\"op\":\"delete\",\"id\":\"" + id + "\"}", out _));
            Assert.Equal(new[] { Game }, server.pins);
            Assert.Equal(new[] { Game }, File.ReadAllLines(File_));
        }

        [Theory]
        [InlineData("{\"op\":\"move\",\"id\":\"1789275213-7472\"}")]
        [InlineData("{\"op\":\"add\",\"x\":1,\"z\":1,\"type\":\"castle\"}")]
        [InlineData("{\"op\":\"add\",\"x\":1,\"z\":1,\"text\":\"0123456789012345678901234567890123456789012345678901234567890\"}")]
        [InlineData("{\"op\":\"add\",\"x\":12001,\"z\":1}")]
        [InlineData("{\"op\":\"add\",\"x\":\"1\",\"z\":1}")]
        [InlineData("{\"op\":\"add\",\"z\":1}")]
        [InlineData("{\"op\":\"edit\",\"text\":\"no id\"}")]
        [InlineData("{\"op\":\"delete\",\"id\":\"w123\"}")]
        [InlineData("{\"op\":\"edit\",\"id\":\"1789275213-7472\",\"x\":1e9,\"z\":0}")]
        public void ARefusedWriteLeavesThePinsAsTheyWere(string body)
        {
            Assert.NotNull(Write(body, out _));
            Assert.Equal(new[] { Game }, server.pins);
            Assert.False(File.Exists(File_));
        }

        [Fact]
        public void APinOnGroundNobodyHasWalkedIsRefused()
        {
            int size = WebMapConfig.TEXTURE_SIZE;
            server.fogRgba = new byte[size * size * 4];              // all dark
            Assert.NotNull(Write("{\"op\":\"add\",\"x\":100,\"z\":-50}", out _));
            int px = size / 2 + 8, py = size / 2 - 4;                  // (100, -50) at 12 m a pixel
            server.fogRgba[(py * size + px) * 4] = 255;
            Assert.Null(Write("{\"op\":\"add\",\"x\":100,\"z\":-50}", out _));
        }

        [Fact]
        public void AnOwnerWithCommasCannotShiftThePinsFields()
        {
            Assert.Null(Write("{\"op\":\"add\",\"x\":1,\"z\":2,\"text\":\"t\"}", out string id, "Evil,Guy,Inc"));
            Assert.Equal(new[] { "web", id, "dot", "EvilGuyInc", "1.00", "2.00", "t" }, Line(id));
        }
    }
}
