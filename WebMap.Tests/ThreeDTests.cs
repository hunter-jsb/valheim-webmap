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
            Assert.Equal("OBJ1", System.Text.Encoding.ASCII.GetString(WorldObjects.ChunkBytes(0, 0, out _), 0, 4));
            MapFog.RebuildChunks(null);
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
