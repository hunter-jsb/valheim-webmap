using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityEngine;
using Xunit;

namespace WebMap.Tests
{
    // The per-second walk: Seen is the snapshot, a second apart, as the game thread calls it.
    public class StatsTests : WithDir
    {
        const string A = "Withers";

        public StatsTests() { Features.ResetForTests(Dir); Stats.Load(Dir); Stats.BeginSweep(); }

        static JsonElement Doc() => J.Parse(Stats.Json(new Dictionary<string, int>()));
        static JsonElement Me() => Doc().GetProperty("players").EnumerateArray().First(p => p.Str("name") == A);
        static void At(float x, float z, float hp = -1f) => Stats.Seen(A, 42L, new Vector3(x, 0f, z), hp, hp < 0 ? -1f : 100f);
        static int Run(string k) => Me().GetProperty("runs").Int(k);

        [Fact]
        public void AStepIsDistanceAndAJumpOver100mIsAHop()
        {
            At(0, 0); At(30, 40); At(1000, 40); At(1000, 80);
            Assert.Equal(90, Me().Num("dist_m"));
            Assert.Equal(1, Me().Int("hops"));
        }

        [Fact]
        public void ADipUnderATenthAndBackOverThreeTenthsIsOneCloseCall()
        {
            foreach (var hp in new[] { 100f, 8f, 20f, 5f, 35f, 90f, 100f }) At(0, 0, hp);   // 20 is not yet safe
            Assert.Equal(1, Me().Int("close_calls"));
            Assert.Equal(0.05, Me().Num("lowest_hp"), 3);
        }

        [Fact]
        public void ADipThatEndsInDeathIsNoCloseCall()
        {
            At(0, 0, 100f); At(0, 0, 5f);
            Stats.Death(A);
            At(500, 0, 100f);
            Assert.Equal(0, Me().Int("close_calls"));
        }

        [Fact]
        public void ReachingTheSpotIsARecoveredRunWithTheWalkItTook()
        {
            At(0, 0); Stats.Death(A);
            for (float x = 500; x >= 20; x -= 80) At(x, 0);
            At(3, 0);                                // respawned, and 497 m back
            Assert.Equal(1, Run("ok"));
            Assert.Equal(0, Run("open"));
            Assert.Equal(497, Me().GetProperty("run_m").Num("total"));
        }

        [Fact]
        public void DyingWithin200mOfTheSpotIsOneFailedRun()
        {
            At(0, 0); Stats.Death(A);
            for (float z = 500; z >= 250; z -= 50) At(0, z);
            Stats.Death(A);                          // 250 m off: another death, not a failed run
            Assert.Equal(0, Run("failed"));
            for (float d = 500; d >= 100; d -= 50) At(d, d);
            Stats.Death(A);                          // within 200 m of both spots: one failure, not two
            Assert.Equal(1, Run("failed"));
        }

        [Fact]
        public void AGraveASweepSawAndThenDidNotIsARescue()
        {
            At(0, 0); Stats.Death(A); At(5000, 0);
            Stats.BeginSweep(); Stats.ObserveGrave(A, 4f, 3f); Stats.PublishSweep();
            Assert.Equal(1, Run("open"));
            Stats.BeginSweep(); Stats.PublishSweep();
            Assert.Equal(1, Run("rescued"));
            Assert.Equal(0, Run("open"));
        }

        [Fact]
        public void ASpotNoSweepEverSawAGraveAtIsDroppedWithoutATally()
        {
            At(0, 0); Stats.Death(A); At(5000, 0);
            Stats.BeginSweep(); Stats.PublishSweep();
            Assert.Equal(new[] { 0, 0, 0, 0 }, new[] { "ok", "failed", "rescued", "open" }.Select(Run));
        }

        [Fact]
        public void ADeathDuringAWalkWaitsForTheNextSweep()
        {
            Stats.BeginSweep();                      // the walk has passed the spot before the tombstone lands
            At(0, 0); Stats.Death(A); At(5000, 0);
            Stats.PublishSweep();
            Assert.Equal(1, Run("open"));
            Stats.BeginSweep(); Stats.ObserveGrave(A, 0f, 0f); Stats.PublishSweep();
            Stats.BeginSweep(); Stats.PublishSweep();
            Assert.Equal(1, Run("rescued"));
        }

        [Fact]
        public void ABossIsRecordedOnceAndOtherKeysNotAtAll()
        {
            Stats.ObserveKeys(new[] { "defeated_eikthyr", "KilledTroll" });
            Stats.ObserveKeys(new[] { "defeated_eikthyr", "defeated_gdking" });
            Assert.Equal(new[] { "defeated_eikthyr", "defeated_gdking" },
                         Doc().GetProperty("bosses").EnumerateArray().Select(b => b.Str("key")));
        }

        [Fact]
        public void EverythingTalliedSurvivesASaveAndALoad()
        {
            new Grid().Box(480, 480, 560, 560, Grid.Meadows).Read();
            Stats.Join(A); Stats.Chat(A);
            At(0, 0, 100f); At(30, 40, 5f); At(60, 80, 60f);    // 100 m in the meadows, a close call
            Stats.Death(A);                                     // a death in the meadows
            At(300, 80); At(220, 80); At(140, 80); At(62, 80);  // a recovered run of 238 m
            At(4000, 80); Stats.Death(A);                       // a hop, and a death left open
            At(5000, 0); At(5000, 50);                          // a streak begun
            Stats.BeginSweep(); Stats.ObserveGrave(A, 4000f, 80f); Stats.PublishSweep();
            Stats.ObserveKeys(new[] { "defeated_eikthyr" });
            Stats.Deed(A, Deeds.Kind.Kill); Stats.Deed(A, Deeds.Kind.Tree); Stats.Deed(A, Deeds.Kind.Rock); Stats.Deed(A, Deeds.Kind.Rock);
            Stats.Wore(A, Gear.Hand.Axe, Gear.Hand.None, true, Gear.Armor.Heavy, new[] { "Battleaxe", null, "ArmorIronChest", null, "HelmetIron", null }, 1f);
            Gear.Struck(A, false, Skills.SkillType.Knives, 6f, true);
            Stats.Looked(A, Look, 118);
            string deaths = Stats.DeathsJson();
            var before = Doc();
            Assert.Equal(338, before.GetProperty("players")[0].GetProperty("biomes").EnumerateArray().First(b => b.Str("biome") == "Meadows").Num("m"));
            Assert.Equal(1, before.GetProperty("players")[0].GetProperty("gear").GetProperty("hand").Int("twohanded"));
            Assert.Equal(Look, before.GetProperty("players")[0].GetProperty("look").GetRawText());

            Stats.Save();
            Stats.Load(Dir);

            var after = Doc();
            Assert.Equal(Tallies(before), Tallies(after));
            Assert.Equal(before.GetProperty("bosses").GetRawText(), after.GetProperty("bosses").GetRawText());
            Assert.Equal(deaths, Stats.DeathsJson());
        }
        // a player's tallies; being online and what stands in the world are the next sweep's to say
        static readonly string[] Live = { "online", "pieces", "portals", "ships", "graves" };
        static string Tallies(JsonElement doc) => string.Join(",", doc.GetProperty("players")[0].EnumerateObject()
            .Where(p => !Live.Contains(p.Name)).Select(p => p.Name + "=" + p.Value.GetRawText()));

        [Fact]
        public void AFileFromBeforeDeedsAndGearLoadsWithNone()
        {
            File.WriteAllText(Path.Combine(Dir, "stats.tsv"), "since\t100\np\t" + A + "\t3\t1\t2\t0\t120.5\t1700000000\t0\t1\t0\t0\t0\t0\t0\t0\t0\n");
            Stats.Load(Dir);
            Assert.Equal(3, Me().Int("joins"));
            Assert.Equal(new[] { 0, 0, 0 }, new[] { "kills", "trees", "rocks" }.Select(k => Me().Int(k)));
            Assert.Equal("{\"hand\":{},\"armor\":{},\"worn\":{},\"hits\":{\"melee\":0,\"ranged\":0,\"magic\":0,\"backstab\":0}}",
                         Me().GetProperty("gear").GetRawText());
            Assert.False(Me().TryGetProperty("look", out _));
        }

        // RigExporter.LookJson's shape; /state builds it every second for everyone online
        const string Look = "{\"model\":0,\"skin\":[0.9,0.9,0.9],\"hair\":[0.1,0.049,0.028],\"slots\":{\"helmet\":\"HelmetMage\",\"hair\":\"Hair13\"},\"parts\":[\"Player@body0\",\"HelmetMage@Helmet_attach\",\"Hair12_2@Helmet_attach\"]}";

        [Fact]
        public void TheSameLookASecondLaterRebuildsNothing()
        {
            Stats.Looked(A, Look, 90);
            string json = Stats.Json(new Dictionary<string, int>());
            Stats.Looked(A, Look, 95);
            Assert.Same(json, Stats.Json(new Dictionary<string, int>()));
            Stats.Looked(A, Look.Replace("HelmetMage", "HelmetBronze"), 95);
            Assert.Equal("HelmetBronze", Me().GetProperty("look").GetProperty("slots").Str("helmet"));
        }

        // Nobody online marks the tally stale, so a pin placed from the site went uncounted.
        [Fact]
        public void APinPlacedWhileNobodyMovesIsCounted()
        {
            At(0, 0);
            Stats.Json(new Dictionary<string, int> { [A] = 1 });
            var pins = J.Parse(Stats.Json(new Dictionary<string, int> { [A] = 2 })).GetProperty("players").EnumerateArray().First(p => p.Str("name") == A);
            Assert.Equal(2, pins.Int("pins"));
        }

        [Fact]
        public void AWalkAtSeaIsOcean()
        {
            new Grid().Read();                       // all water
            At(0, 0); At(30, 40);
            Assert.Contains(Me().GetProperty("biomes").EnumerateArray(), b => b.Str("biome") == "Ocean" && b.Num("m") == 50);
        }

        [Fact]
        public void AWalkInTheMistlandsCounts()
        {
            new Grid().Box(480, 480, 560, 560, Grid.Mistlands).Read();
            At(0, 0); At(30, 40);
            Assert.Contains(Me().GetProperty("biomes").EnumerateArray(), b => b.Str("biome") == "Mistlands" && b.Num("m") == 50);
        }
    }
}
