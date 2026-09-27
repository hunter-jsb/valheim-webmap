using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Xunit;

namespace WebMap.Tests
{
    public class PortalsTests : WithDir
    {
        public PortalsTests() { Features.ResetForTests(Dir); Portals.ResetForTests(Dir); }

        // one sweep: the portals as the walk found them, then the document the site reads
        static JsonElement Sweep(params (string id, string name, string to, float x, float z, bool explored)[] ps)
        {
            Portals.Begin();
            foreach (var p in ps) Portals.ObserveForTests(p.id, p.name, p.to, p.x, p.z, p.explored);
            Portals.Finish();
            return J.Parse(Portals.GetJson());
        }
        static JsonElement One(JsonElement doc, string id) => doc.GetProperty("portals").EnumerateArray().First(p => p.Str("id") == id);
        static bool HasLast(JsonElement doc, string id) => One(doc, id).TryGetProperty("last", out _);

        [Fact]
        public void AGateStandingUnlinkedPointsWhereItsTwinLastStoodAndALinkedOneDoesNot()
        {
            var linked = Sweep(("a", "home", "b", 0, 0, true), ("b", "home", "a", 1000, 0, true));
            Assert.False(HasLast(linked, "a"));
            Assert.False(HasLast(linked, "b"));

            var retagged = Sweep(("a", "swamp", "", 0, 0, true), ("b", "home", "", 1000, 0, true));
            var last = One(retagged, "a").GetProperty("last");
            Assert.Equal((1000.0, 0.0), (last.Num("x"), last.Num("z")));

            var relinked = Sweep(("a", "swamp", "c", 0, 0, true), ("b", "home", "", 1000, 0, true), ("c", "swamp", "a", 2000, 0, true));
            Assert.False(HasLast(relinked, "a"));
            Assert.True(HasLast(relinked, "b"));
        }

        [Fact]
        public void AGatesLastTwinIsRememberedAcrossARestart()
        {
            Sweep(("a", "home", "b", 0, 0, true), ("b", "home", "a", 1000, 0, true));
            string file = Path.Combine(Dir, "portals.tsv");
            Assert.True(SpinWait.SpinUntil(() => File.Exists(file) && File.ReadAllText(file).Contains("a\tb"), 5000));

            Portals.ResetForTests(Dir);                  // the restart
            var doc = Sweep(("a", "swamp", "", 0, 0, true));
            Assert.Equal(1000.0, One(doc, "a").GetProperty("last").Num("x"));
        }

        [Fact]
        public void APortalInUnwalkedGroundIsNotReported()
        {
            var doc = Sweep(("a", "home", "b", 0, 0, true), ("b", "home", "a", 9000, 0, false));
            Assert.Equal(new[] { "a" }, doc.GetProperty("portals").EnumerateArray().Select(p => p.Str("id")));
            Assert.Equal(new[] { 0f, 0f }, Portals.Positions);
        }

        [Fact]
        public void TheDocumentClosesWithTheHubNamesAsTheyAreNow()
        {
            var before = Sweep(("a", "home", "b", 0, 0, true), ("b", "home", "a", 30, 0, true));
            Assert.Empty(before.GetProperty("named").EnumerateArray());
            Assert.Null(Features.SetName("hub@15,0", "Stargate", "Withers"));
            var named = Assert.Single(J.Parse(Portals.GetJson()).GetProperty("named").EnumerateArray());
            Assert.Equal("Stargate", named.Str("name"));
        }
    }
}
