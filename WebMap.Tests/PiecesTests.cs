using System.Linq;
using UnityEngine;
using Xunit;

namespace WebMap.Tests
{
    public class PiecesTests : WithDir
    {
        public PiecesTests() { Features.ResetForTests(Dir); Stats.Load(Dir); }

        [Fact]
        public void APieceNamesItsBuilderAndOneNeverSeenOnlineIsNobodys()
        {
            Stats.Seen("Rurik", 42L, new Vector3(0f, 0f, 0f));
            Pieces.Begin();
            Pieces.Observe(7, null, new Vector3(10f, 0f, 20f), 42L);
            Pieces.Observe(7, null, new Vector3(30f, 0f, 20f), 99L);
            Pieces.Finish();
            var doc = J.Parse(Pieces.GetJson());
            Assert.Equal(new[] { "Rurik" }, doc.GetProperty("players").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal(new[] { 0, -1 }, doc.GetProperty("pieces").EnumerateArray().Select(r => r[5].GetInt32()));
        }

        [Fact]
        public void TheSamePiecesNameABuilderSeenSinceTheLastSweep()
        {
            for (int sweep = 0; sweep < 2; sweep++)
            {
                if (sweep == 1) Stats.Seen("Astrid", 77L, new Vector3(0f, 0f, 0f));
                Pieces.Begin();
                Pieces.Observe(7, null, new Vector3(10f, 0f, 20f), 77L);
                Pieces.Finish();
            }
            Assert.Equal(new[] { "Astrid" }, J.Parse(Pieces.GetJson()).GetProperty("players").EnumerateArray().Select(p => p.GetString()));
        }
    }
}
