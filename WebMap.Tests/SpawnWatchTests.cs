using System.Collections.Generic;
using Xunit;
using B = Heightmap.Biome;
using W = WebMap.SpawnWatch;

namespace WebMap.Tests
{
    // A creature as the server learns of it, in plain values: whether the game's own rules
    // account for it, and how a burst becomes one entry.
    public class SpawnWatchTests : WithDir
    {
        static W.Seen At(B here, B allowed) => new W.Seen { here = (int)here, allowed = (int)allowed };
        static W.Entry Fuling(long t, float x) => new W.Entry { t = t, last = t, prefab = "Goblin", name = "Fuling", session = 1427461386L, x = x, z = 0f };

        [Fact]
        public void ACreatureItsSpawnTablesAllowThereIsExplained() =>
            Assert.Equal("biome", W.Why(At(B.Meadows | B.BlackForest, B.BlackForest)));

        [Fact]
        public void ACreatureOutOfItsBiomeWithNoRaidIsNot() =>
            Assert.Null(W.Why(At(B.Meadows, B.Plains)));

        [Fact]
        public void ARaidsOwnCreatureNearItIsExplainedAndNoOtherKind()
        {
            var s = At(B.Meadows, B.Plains);
            s.raid = "army_goblin"; s.raidRange = 96f; s.raidM = 150f; s.raidLists = true;
            Assert.Equal("raid", W.Why(s));
            s.raidLists = false;
            Assert.Null(W.Why(s));
            s.raidLists = true; s.raidM = 900f;
            Assert.Null(W.Why(s));
        }

        [Fact]
        public void TamedYoungAndSummonedAreExplained()
        {
            var s = At(B.Meadows, B.Mountain);
            s.tamed = true;
            Assert.Equal("tamed", W.Why(s));
            s.tamed = false;
            foreach (string kind in new[] { "young", "hatched", "summoned" }) { s.made = kind; Assert.Equal(kind, W.Why(s)); }
        }

        [Fact]
        public void ACreatureInALocationWithItsSpawnersOrInADungeonIsExplained()
        {
            var village = new List<W.Source> { new W.Source { name = "Ruin3", x = 100f, z = 100f, reach = 30f } };
            Assert.Equal("Ruin3", W.Reaches(125f, 110f, village));
            Assert.Null(W.Reaches(200f, 100f, village));
            var s = At(B.Meadows, B.Swamp);
            s.source = W.Reaches(125f, 110f, village);
            Assert.Equal("Ruin3", W.Why(s));
            var queen = At(B.Mistlands, 0);
            queen.inside = true;
            Assert.Equal("dungeon", W.Why(queen));
        }

        [Fact]
        public void ABurstIsOneEntryAndTwoArrivalsApartAreTwo()
        {
            var list = new List<W.Entry>();
            W.Add(list, Fuling(1000, 0f)); W.Add(list, Fuling(1002, 3f)); W.Add(list, Fuling(1004, -2f));
            Assert.Equal(3, Assert.Single(list).count);
            W.Add(list, Fuling(1060, 0f));                 // a minute on
            W.Add(list, Fuling(1061, 400f));               // far off, at once
            Assert.Equal(new[] { 3, 1, 1 }, list.ConvertAll(e => e.count));
        }

        [Fact]
        public void TheListAndWhoseGameSurviveARestart()
        {
            W.ResetForTests(Dir);
            W.Session(1427461386L, "Withers");
            var e = Fuling(1000, 5f); e.by = "Withers"; e.count = 3; e.biome = "Meadows";
            W.AddForTests(e);
            W.Flush();
            W.ResetForTests(Dir);
            var s = J.Parse(W.Json()).GetProperty("spawns")[0];
            Assert.Equal(("1427461386", "Withers", 3, "Meadows"), (s.Str("session"), s.Str("by"), s.Int("count"), s.Str("biome")));
            Assert.Contains("Withers", System.IO.File.ReadAllText(System.IO.Path.Combine(Dir, "sessions.tsv")));
        }
    }
}
