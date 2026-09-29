using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace WebMap.Tests
{
    public class FeaturesTests : WithDir
    {
        public FeaturesTests() { Features.ResetForTests(Dir); Portals.Positions = new float[0]; }

        static List<JsonElement> OfKind(List<JsonElement> fs, string kind) => fs.Where(f => f.Str("kind") == kind).ToList();
        static bool Land(Grid g, JsonElement f) => g.Cls[Grid.C((float)f.Num("z")) * Grid.N + Grid.C((float)f.Num("x"))] != 0;

        // ---------- geography ----------

        [Fact]
        public void TwoIslandsAChannelApartAreTwoPlacesWithTwoNames()
        {
            var fs = OfKind(new Grid().Box(500, 500, 520, 520).Box(523, 500, 543, 520).Read(), "island");
            Assert.Equal(2, fs.Count);
            Assert.NotEqual(fs[0].Str("name"), fs[1].Str("name"));
        }

        [Fact]
        public void ALandOfTwoSquareKilometresIsAContinentAndASmallerOneAnIsland()
        {
            // cells are 24 m: 60 x 60 is 2.07 km², 50 x 50 is 1.44
            var fs = new Grid().Box(100, 100, 160, 160).Box(300, 300, 350, 350).Read();
            Assert.Single(OfKind(fs, "continent"));
            Assert.Single(OfKind(fs, "island"));
            Assert.EndsWith("land", OfKind(fs, "continent")[0].Str("name").ToLowerInvariant());
        }

        [Fact]
        public void ANameGivenWhileALandWasAnIslandStillHoldsAsAContinent()
        {
            var big = Assert.Single(OfKind(new Grid().Box(100, 100, 160, 160).Read(), "continent"));
            string id = big.Str("id");
            // the names file as an older build wrote it, keyed by the kind the land was then
            System.IO.File.WriteAllText(System.IO.Path.Combine(Dir, "names.tsv"), "island" + id.Substring(id.IndexOf('@')) + "\tMidgard\tWithers\t1790000000\n");
            Features.ResetForTests(Dir);
            var now = Assert.Single(OfKind(new Grid().Box(100, 100, 160, 160).Read(), "continent"));
            Assert.Equal("Midgard", now.Str("name"));
            Assert.Equal("Withers", now.Str("by"));
        }

        [Fact]
        public void ACShapedIslandIsNamedOnItsLandNotInItsBight()
        {
            // the C's middle, where a centroid would put the name, is sea
            var g = new Grid().Box(400, 400, 460, 460).Box(410, 410, 460, 450, 0);
            var island = Assert.Single(OfKind(g.Read(), "island"));
            Assert.True(Land(g, island));
        }

        [Fact]
        public void ALakeWithARiverToTheSeaIsStillALake()
        {
            // a lake in an island, and a channel from it to the open sea
            var g = new Grid().Box(400, 400, 460, 460).Box(420, 420, 440, 440, 0).Box(440, 428, 460, 431, 0);
            Assert.Empty(OfKind(g.Read(), "lake"));          // the channel alone joins it to the sea
            Assert.Single(OfKind(g.River((440, 429), (470, 429)).Read(), "lake"));
        }

        [Fact]
        public void ANarrowInletIsABayAndAWideOneIsOpenSea()
        {
            var g = new Grid().Box(300, 300, 420, 420)
                .Box(330, 300, 340, 330, 0)       // 240 m across
                .Box(370, 300, 410, 330, 0);      // 960 m across
            var bay = Assert.Single(OfKind(g.Read(), "bay"));
            Assert.InRange(Grid.C((float)bay.Num("x")), 330, 339);
        }

        // ---------- names ----------

        [Fact]
        public void NamesAreUniqueAndNoTwoVowelsMeetBeforeAVowelEnding()
        {
            var g = new Grid();
            for (int i = 0; i < 8; i++) for (int j = 0; j < 8; j++) g.Box(200 + 30 * i, 200 + 30 * j, 216 + 30 * i, 216 + 30 * j);
            for (int j = 0; j < 60; j++) g.River((600, 480 + 8 * j), (620, 480 + 8 * j));
            var fs = g.Read();
            var names = fs.Select(f => f.Str("name")).ToList();
            Assert.Equal(64, OfKind(fs, "island").Count);
            Assert.Equal(60, OfKind(fs, "river").Count);
            Assert.Equal(names.Count, names.Distinct().Count());
            foreach (var (kind, end) in new[] { ("island", "ey"), ("river", "á") })
                foreach (var f in OfKind(fs, kind))
                {
                    string n = f.Str("name").ToLowerInvariant();
                    Assert.EndsWith(end, n);
                    Assert.True("aeiouyá".IndexOf(n[n.Length - end.Length - 1]) < 0, n);
                }
        }

        [Fact]
        public void NamesAreTheSeedsSoARestartChangesNothing()
        {
            var g = new Grid().Box(500, 500, 520, 520).Box(540, 500, 560, 520).Box(580, 500, 600, 520).River((600, 600), (640, 600));
            var a = g.Read("Mothership").Select(f => f.Str("name")).ToList();
            Assert.Equal(a, g.Read("Mothership").Select(f => f.Str("name")));
            Assert.NotEqual(a, g.Read("Somewhere else").Select(f => f.Str("name")));
        }

        [Fact]
        public void AGivenNameWinsAndAnEmptyOneGivesTheWorldsOwnBack()
        {
            var own = Assert.Single(OfKind(new Grid().Box(500, 500, 520, 520).Read(), "island"));
            string id = own.Str("id");
            JsonElement Now() => J.Parse(Features.Json()).GetProperty("features").EnumerateArray().First(f => f.Str("id") == id);

            Assert.Null(Features.SetName(id, "Midgard", "Withers"));
            Assert.Equal("Midgard", Now().Str("name"));
            Assert.Equal("Withers", Now().Str("by"));
            Assert.Null(Features.SetName(id, "", "Withers"));
            Assert.Equal(own.Str("name"), Now().Str("name"));
        }

        [Fact]
        public void ANameForNoSuchPlaceOrTooLongIsRefusedAndChangesNothing()
        {
            var own = Assert.Single(OfKind(new Grid().Box(500, 500, 520, 520).Read(), "island"));
            string before = Features.Json();
            Assert.NotNull(Features.SetName("island@0,0", "Midgard", "Withers"));
            Assert.NotNull(Features.SetName(own.Str("id"), new string('a', 41), "Withers"));
            Assert.Equal(before, Features.Json());
        }

        [Fact]
        public void AWalkedSpotNamesThePlacesItLiesInMostParticularFirstAndUnwalkedGroundNothing()
        {
            new Grid().Box(400, 400, 500, 500).Box(440, 440, 460, 460, Grid.Mountain).Read();
            var fog = new MapDataServer();
            int size = WebMapConfig.TEXTURE_SIZE;
            fog.fogRgba = new byte[size * size * 4];
            float x = Grid.W(450), z = Grid.W(450);
            try
            {
                global::WebMap.WebMap.mapDataServer = fog;
                Assert.Null(Features.At(x, z));
                int px = (int)(x / WebMapConfig.PIXEL_SIZE) + size / 2, py = (int)(z / WebMapConfig.PIXEL_SIZE) + size / 2;
                fog.fogRgba[(py * size + px) * 4] = 255;
                var here = J.Parse(Features.At(x, z)).GetProperty("here").EnumerateArray().Select(f => f.Str("kind"));
                Assert.Equal(new[] { "range", "continent" }, here);
            }
            finally { global::WebMap.WebMap.mapDataServer = null; }
        }

        [Fact]
        public void ClassAtIsMinusOneBeforeTheWorldIsReadAndOffTheGrid()
        {
            Assert.Equal(-1, Features.ClassAt(0, 0));
            new Grid().Box(500, 500, 520, 520).Read();
            Assert.Equal(Grid.Meadows, Features.ClassAt(Grid.W(510), Grid.W(510)));
            Assert.Equal(0, Features.ClassAt(Grid.W(100), Grid.W(100)));
            Assert.Equal(-1, Features.ClassAt(Grid.W(Grid.N - 1) + Grid.Cell, 0));
            Assert.Equal(-1, Features.ClassAt(0, Grid.W(0) - Grid.Cell));
        }

        // ---------- hubs: a named spot, matched by nearness ----------

        static List<JsonElement> Hubs() => J.Parse(Features.HubNamesJson()).EnumerateArray().ToList();

        [Fact]
        public void NamingAHubNeedsAPortalStandingThere()
        {
            Portals.Positions = new[] { 1000f, 1000f };
            Assert.NotNull(Features.SetName("hub@3000,3000", "Stargate", "Withers"));
            Assert.Empty(Hubs());
        }

        [Fact]
        public void RenamingAHubThatGrewReplacesItsNameRatherThanAddingOne()
        {
            Portals.Positions = new[] { 1000f, 1000f, 1100f, 1000f };
            Assert.Null(Features.SetName("hub@1000,1000", "Stargate", "Withers"));
            Assert.Null(Features.SetName("hub@1060,1000", "Crossroads", "Grant"));
            var hub = Assert.Single(Hubs());
            Assert.Equal("Crossroads", hub.Str("name"));
        }

        [Fact]
        public void HubsApartKeepTheirOwnNames()
        {
            Portals.Positions = new[] { 1000f, 1000f, 1400f, 1000f };
            Assert.Null(Features.SetName("hub@1000,1000", "Stargate", "Withers"));
            Assert.Null(Features.SetName("hub@1400,1000", "Shops", "Grant"));
            Assert.Equal(new[] { "Shops", "Stargate" }, Hubs().Select(h => h.Str("name")).OrderBy(n => n));
        }

        [Fact]
        public void AnEmptyNameTakesAHubsNameAway()
        {
            Portals.Positions = new[] { 1000f, 1000f };
            Assert.Null(Features.SetName("hub@1000,1000", "Stargate", "Withers"));
            Assert.Null(Features.SetName("hub@1010,990", "", "Withers"));
            Assert.Empty(Hubs());
        }

        [Theory]
        [InlineData("hub@")]
        [InlineData("hub@1000")]
        [InlineData("hub@1000,1000,5")]
        [InlineData("hub@east,north")]
        [InlineData("hub@1000;1000")]
        public void AMalformedHubIdIsRefused(string id)
        {
            Portals.Positions = new[] { 1000f, 1000f };
            Assert.NotNull(Features.SetName(id, "Stargate", "Withers"));
            Assert.Empty(Hubs());
        }

        // ---------- the site's write body ----------

        [Fact]
        public void TheBodysStringsComeBackWithTheirEscapesHonoured()
        {
            Assert.True(Features.ParseBody("{ \"name\" : \"Ask\\\"s \\\\ fjord\\n\\u00e9\", \"id\":\"lake@48,-96\" }", out string id, out string name));
            Assert.Equal("lake@48,-96", id);
            Assert.Equal("Ask\"s \\ fjord\né", name);
        }

        [Theory]
        [InlineData("{\"id\":\"lake@48,-96\"}")]
        [InlineData("{\"id\":\"lake@48,-96\",\"name\":5}")]
        [InlineData("")]
        public void ABodyWithoutBothStringsIsRefused(string body) => Assert.False(Features.ParseBody(body, out _, out _));
    }
}
