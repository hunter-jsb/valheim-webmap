using System;
using System.IO;
using System.IO.Compression;
using Xunit;

namespace WebMap.Tests
{
    public class ThreeDTests
    {
        // Spoilers: the 3D view's chunks leave the server only where somebody has walked.
        [Fact]
        public void AChunkNobodyHasWalkedSendsNothingAndAWalkedOneItsObjects()
        {
            int size = WebMapConfig.TEXTURE_SIZE, half = size / 2;
            var fog = new byte[size * size * 4];
            int p = (int)Math.Round(100f / WebMapConfig.PIXEL_SIZE + half);     // one walked pixel at (100, 100)
            fog[(p * size + p) * 4] = 255;
            MapFog.RebuildChunks(fog);
            Assert.True(MapFog.ChunkExplored(0, 0));
            Assert.False(MapFog.ChunkExplored(1, 0));
            Assert.Null(WorldObjects.ChunkBytes(1, 0, out _));
            Assert.Equal("OBJ2", System.Text.Encoding.ASCII.GetString(WorldObjects.ChunkBytes(0, 0, out _), 0, 4));
            MapFog.RebuildChunks(null);
        }

        // A rock mined out for a base must not stand there whole in 3D.
        [Fact]
        public void AMinedRocksFallenPiecesLeaveTheServerWithIt()
        {
            byte[] Health(params float[] hp)
            {
                var ms = new MemoryStream(); var w = new BinaryWriter(ms);
                w.Write(hp.Length); foreach (float h in hp) w.Write(h);   // MineRock5.SaveHealth's package
                return ms.ToArray();
            }
            Assert.Null(WorldObjects.GoneFromHealth(Health(5f, 5f, 5f)));
            byte[] gone = WorldObjects.GoneFromHealth(Health(5f, 0f, -2f, 3f));
            Assert.Equal(new byte[] { 0b0110 }, gone);

            var bytes = WorldObjects.EncodeForTests(new[] {
                new WorldObjects.Obj { prefab = 1, qw = 1, sx = 1, sy = 1, sz = 1 },
                new WorldObjects.Obj { prefab = 2, qw = 1, sx = 1, sy = 1, sz = 1, gone = gone } });
            int rec = 12 + 2 * 4, table = rec + 2 * 44;
            Assert.Equal(2, bytes[rec + 44 + 2]);                       // the second object's flag
            Assert.Equal(1, BitConverter.ToInt32(bytes, table));        // one mined rock
            Assert.Equal(1, BitConverter.ToInt32(bytes, table + 4));    // it is object 1
            Assert.Equal(8, BitConverter.ToUInt16(bytes, table + 8));   // eight areas' bits
            Assert.Equal(0b0110, bytes[table + 10]);
        }

        // A misread terraform blob puts the ground wrong under every build on it.
        [Fact]
        public void ATerraformBlobGivesLevelPlusSmoothHeldWithinEightMetres()
        {
            var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
            using (var w = new BinaryWriter(gz))
            {
                w.Write(1); w.Write(2); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(4f);   // TerrainComp.Save's header
                w.Write(TerrainPatches.N);
                for (int i = 0; i < TerrainPatches.N; i++)
                {
                    if (i == 100) { w.Write(true); w.Write(-3f); w.Write(-0.5f); }
                    else if (i == 200) { w.Write(true); w.Write(12f); w.Write(1f); }
                    else w.Write(false);
                }
                w.Write(0);                                                                 // no paint
            }
            var patch = TerrainPatches.Decode(ms.ToArray());
            Assert.Equal(-3.5f, patch.delta[100]);
            Assert.Equal(8f, patch.delta[200]);
            Assert.Equal(0f, patch.delta[101]);
        }
    }
}
