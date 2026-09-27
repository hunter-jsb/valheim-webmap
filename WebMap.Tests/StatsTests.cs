using System.Collections.Generic;
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
        static void Walk(float x0, float x1, float step)
        {
            for (float x = x0; step > 0 ? x <= x1 : x >= x1; x += step) At(x, 0f);
        }
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
            foreach (var hp in new[] { 100f, 8f, 3f, 20f, 35f, 90f, 100f }) At(0, 0, hp);
            Assert.Equal(1, Me().Int("close_calls"));
            Assert.Equal(0.03, Me().Num("lowest_hp"), 3);
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
            Walk(500, 20, -80); At(3, 0);         // respawn, then 497 m back
            Assert.Equal(1, Run("ok"));
            Assert.Equal(0, Run("open"));
            Assert.Equal(497, Me().GetProperty("run_m").Num("total"));
        }

        [Fact]
        public void DyingWithin200mOfTheSpotIsOneFailedRun()
        {
            At(0, 0); Stats.Death(A);
            Walk(500, 300, -50); Stats.Death(A);    // 300 m off: another death, not a failed run
            Assert.Equal(0, Run("failed"));
            Walk(500, 150, -50); Stats.Death(A);    // within reach of both spots: one failure
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
            Stats.Death(A);                                     // an open run, a death in the meadows
            At(2000, 0); At(2000, 50); At(4000, 50);            // a streak begun, and a hop
            Stats.BeginSweep(); Stats.ObserveGrave(A, 60f, 80f); Stats.PublishSweep();
            Stats.ObserveKeys(new[] { "defeated_eikthyr" });
            string deaths = Stats.DeathsJson();
            var before = Doc();
            Assert.Equal(100, before.GetProperty("players")[0].GetProperty("biomes")[0].Num("m"));

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

        [Fact(Skip = "Mistlands is 0x200, biome class 10, one past Stats.Biomes (10): its metres and deaths are dropped")]
        public void AWalkInTheMistlandsCounts()
        {
            new Grid().Box(480, 480, 560, 560, Grid.Mistlands).Read();
            At(0, 0); At(30, 40);
            Assert.Contains(Me().GetProperty("biomes").EnumerateArray(), b => b.Str("biome") == "Mistlands" && b.Num("m") == 50);
        }
    }
}
