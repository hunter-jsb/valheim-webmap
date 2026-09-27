using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace WebMap.Tests
{
    // Hits, broken areas and falls, as the two hooks hand them over; seconds as the game's clock.
    public class DeedsTests : WithDir
    {
        const string A = "Withers", B = "Hild";
        static readonly ZDOID Boar = new ZDOID(7L, 1u), Beech = new ZDOID(7L, 2u), Copper = new ZDOID(7L, 3u);
        static readonly string[] Kinds = { "kills", "trees", "rocks" };

        public DeedsTests() { Stats.Load(Dir); Deeds.ResetForTests(); }

        static List<JsonElement> Players() => J.Parse(Stats.Json(new Dictionary<string, int>())).GetProperty("players").EnumerateArray().ToList();
        static int[] Of(string name) { var p = Players().First(e => e.Str("name") == name); return Kinds.Select(k => p.Int(k)).ToArray(); }

        [Fact]
        public void AHitThenAFallWithinTheWindowIsTheHittersByKind()
        {
            Deeds.Hit(Boar, A, 0f); Deeds.Gone(Boar, Deeds.Kind.Kill, 9f);
            Deeds.Hit(Beech, A, 0f); Deeds.Gone(Beech, Deeds.Kind.Tree, 9f);
            Deeds.Hit(Copper, A, 0f); Deeds.Gone(Copper, Deeds.Kind.Rock, 9f);
            Assert.Equal(new[] { 1, 1, 1 }, Of(A));
        }

        [Fact]
        public void AFallNoRecentHitExplainsIsNobodys()
        {
            Deeds.Gone(Boar, Deeds.Kind.Kill, 0f);                            // a despawn
            Deeds.Hit(Beech, A, 0f); Deeds.Gone(Beech, Deeds.Kind.Tree, 11f);  // hit, then fell long after
            Assert.Empty(Players());
        }

        [Fact]
        public void ABrokenAreaIsARockForTheLastHitter()
        {
            Deeds.Hit(Copper, A, 0f); Deeds.Hit(Copper, B, 1f);
            Deeds.AreaBroken(Copper, 2f); Deeds.AreaBroken(Copper, 3f);
            Assert.Equal(new[] { 0, 0, 2 }, Of(B));
            Assert.DoesNotContain(Players(), p => p.Str("name") == A);
        }

        [Fact]
        public void WithNoHitSeenTheOwnerIsCreditedButASeenHitWins()
        {
            Deeds.Gone(Beech, Deeds.Kind.Tree, 0f, A);                       // A's own game felled it
            Deeds.AreaBroken(Copper, 0f, A);
            Deeds.Hit(Boar, B, 0f); Deeds.Gone(Boar, Deeds.Kind.Kill, 1f, A); // B's hit passed through the server
            Assert.Equal(new[] { 0, 1, 1 }, Of(A));
            Assert.Equal(new[] { 1, 0, 0 }, Of(B));
        }
    }
}
