using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace WebMap.Tests
{
    // The stations as the poll and RouteRPC hand them over, in plain values, a poll a line.
    public class KitchenTests : WithDir
    {
        const string A = "Withers", B = "Hild";
        const int Base = 777;                        // a mead base's prefab hash, as a fermenter's content holds it
        static readonly ZDOID Fire = new ZDOID(9L, 1u), Vat = new ZDOID(9L, 2u), Furnace = new ZDOID(9L, 3u);

        public KitchenTests()
        {
            Stats.Load(Dir); Kitchen.ResetForTests();
            Kitchen.Beside = (x, z) => null;
            Kitchen.Watch(Fire, new Kitchen.Kind { type = Kitchen.Type.Cook, name = "piece_cookingstation", slots = 2, to = new Dictionary<string, string> { ["RawMeat"] = "CookedMeat" } }, 10f, 20f);
            Kitchen.Watch(Vat, new Kitchen.Kind { type = Kitchen.Type.Brew, name = "fermenter", brews = new Dictionary<int, KeyValuePair<string, int>> { [Base] = new KeyValuePair<string, int>("MeadHealthMinor", 6) } }, 0f, 0f);
            Kitchen.Watch(Furnace, new Kitchen.Kind { type = Kitchen.Type.Smelt, name = "smelter", to = new Dictionary<string, string> { ["CopperOre"] = "Copper" } }, 0f, 0f);
        }

        static JsonElement Doc() => J.Parse(Stats.Json(new Dictionary<string, int>()));
        static JsonElement Of(string name) => Doc().GetProperty("players").EnumerateArray().First(p => p.Str("name") == name).GetProperty("kitchen");
        static void Empty() { Kitchen.Observe(Fire, 0, "", 0f, 0); Kitchen.Observe(Fire, 1, "", 0f, 0); }

        [Fact]
        public void ADishIsItsPlacersEvenOnAStationAnotherOwns()
        {
            Empty();
            Kitchen.Asked(Fire, A, 100f); Kitchen.Shown(Fire, B, 0, "RawMeat", 100.2f);   // A asks B's game to cook
            Kitchen.Shown(Fire, B, 1, "RawMeat", 104f);                                   // B's own
            Kitchen.Observe(Fire, 0, "RawMeat", 3f, 0); Kitchen.Observe(Fire, 1, "RawMeat", 0f, 0);
            Kitchen.Observe(Fire, 0, "CookedMeat", 26f, 1); Kitchen.Observe(Fire, 1, "CookedMeat", 23f, 1);
            Assert.Equal(1, Of(A).Int("cooked"));
            Assert.Equal(1, Of(A).GetProperty("dishes").Int("CookedMeat"));
            Assert.Equal(1, Of(B).Int("cooked"));
        }

        [Fact]
        public void ADishLeftToBurnIsBurntNotCooked()
        {
            Empty();
            Kitchen.Shown(Fire, A, 0, "RawMeat", 0f);
            Kitchen.Observe(Fire, 0, "RawMeat", 2f, 0);
            Kitchen.Observe(Fire, 0, "CookedMeat", 26f, 1);
            Kitchen.Observe(Fire, 0, "Coal", 51f, 2);
            Kitchen.Observe(Fire, 0, "", 0f, 0);                                          // the coal taken
            Assert.Equal((0, 1), (Of(A).Int("cooked"), Of(A).Int("burnt")));
            Assert.Equal("{}", Of(A).GetProperty("dishes").GetRawText());
            Assert.Equal((0, 1), (Doc().GetProperty("kitchen").Int("cooked"), Doc().GetProperty("kitchen").Int("burnt")));
        }

        [Fact]
        public void ASlotFilledBeforeTheWatchCreditsNobodyButCountsForTheWorld()
        {
            Kitchen.Observe(Fire, 0, "RawMeat", 12f, 0);                                  // first read already cooking
            Kitchen.Observe(Fire, 0, "", 0f, 0);                                          // done and taken between two polls
            Assert.Empty(Doc().GetProperty("players").EnumerateArray());
            Assert.Equal(1, Doc().GetProperty("kitchen").Int("cooked"));
            Assert.Equal(1, Doc().GetProperty("kitchen").GetProperty("stations")[0].Int("cooked"));
        }

        [Fact]
        public void AFermenterFilledThenTappedIsTheFillersMeads()
        {
            Kitchen.Observe(Vat, 0);
            Kitchen.Beside = (x, z) => A; Kitchen.Observe(Vat, Base);
            Kitchen.Beside = (x, z) => B; Kitchen.Observe(Vat, 0);                        // B stands by as it is tapped
            Assert.Equal(6, Of(A).Int("brewed"));
            Assert.Equal(6, Of(A).GetProperty("dishes").Int("MeadHealthMinor"));
        }

        [Fact]
        public void OreLoadedThenSmeltedIsTheLoaders()
        {
            Kitchen.Observe(Furnace, new string[0]);
            Kitchen.Beside = (x, z) => A; Kitchen.Observe(Furnace, new[] { "CopperOre", "CopperOre" });
            Kitchen.Beside = (x, z) => null; Kitchen.Observe(Furnace, new[] { "CopperOre" });   // nobody by as the first melts
            Assert.Equal(1, Of(A).Int("smelted"));
            Assert.Equal(1, Of(A).GetProperty("dishes").Int("Copper"));
        }

        [Fact]
        public void AnItemMadeInThePollOreIsLoadedStillCounts()
        {
            Kitchen.Observe(Furnace, new string[0]);
            Kitchen.Beside = (x, z) => A; Kitchen.Observe(Furnace, new[] { "CopperOre" }, 3f);
            Kitchen.Observe(Furnace, new[] { "CopperOre" }, 28f);
            Kitchen.Beside = (x, z) => B; Kitchen.Observe(Furnace, new[] { "CopperOre" }, 2f);   // A's melts as B loads: the count stands still
            Kitchen.Observe(Furnace, new string[0], 0f);
            Assert.Equal((1, 1), (Of(A).Int("smelted"), Of(B).Int("smelted")));
        }
    }
}
