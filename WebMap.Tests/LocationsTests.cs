using System.Linq;
using Xunit;

namespace WebMap.Tests
{
    public class LocationsTests
    {
        [Fact]
        public void ALocationInUnwalkedGroundIsNotReported()
        {
            Locations.ResetForTests();
            Locations.ObserveForTests("Crypt3", 100f, -200f, true);
            Locations.ObserveForTests("GoblinKing", 5000f, 5000f, false);
            Locations.Finish();
            var doc = J.Parse(Locations.GetJson());
            Assert.Equal(new[] { "Crypt3" }, doc.GetProperty("locations").EnumerateArray().Select(l => l.Str("kind")));
            Assert.Equal(1, doc.Int("count"));
        }
    }
}
