// Ported from f00d4tehg0dz/valheim-webmap (MIT).
using System;
using System.IO;
using System.IO.Compression;

namespace WebMap.Util
{
    // A PNG encoder that does not touch Unity.
    //
    // Texture2D.EncodeToPNG must run on the main thread and needs a Texture2D to
    // exist for every image, which is exactly what a tile renderer producing
    // thousands of images from worker threads cannot afford. This writes PNGs
    // from plain byte arrays anywhere, using the framework's DeflateStream.
    //
    // Supported layouts: RGB (3 bytes/px), RGBA (4), Gray8 (1), Gray16 (2, big-endian).
    internal static class Png
    {
        public enum Format { RGB = 3, RGBA = 4, Gray8 = 1, Gray16 = 2 }

        private static readonly uint[] crcTable = MakeCrcTable();
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static byte[] Encode(byte[] pixels, int width, int height, Format fmt, bool fast = false)
        {
            int bpp = (int)fmt;
            int stride = width * bpp;
            if (pixels.Length < stride * height) throw new ArgumentException("png: pixel buffer too small");

            // Filtered scanlines: filter byte + row. Filter 1 (Sub) or 2 (Up) helps
            // smooth data (heightmaps) compress much better than None; a cheap
            // heuristic picks per row by summing absolute residuals.
            byte[] raw = new byte[(stride + 1) * height];
            byte[] prev = new byte[stride];
            byte[] cur = new byte[stride];
            byte[] trial = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(pixels, y * stride, cur, 0, stride);
                int rowOff = y * (stride + 1);
                if (fast)
                {
                    raw[rowOff] = 0;
                    Buffer.BlockCopy(cur, 0, raw, rowOff + 1, stride);
                }
                else
                {
                    // candidate: Sub
                    long subSum = 0;
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        byte v = (byte)(cur[i] - a);
                        trial[i] = v;
                        subSum += v < 128 ? v : 256 - v;
                    }
                    // candidate: Up
                    long upSum = 0;
                    for (int i = 0; i < stride; i++)
                    {
                        byte v = (byte)(cur[i] - prev[i]);
                        upSum += v < 128 ? v : 256 - v;
                    }
                    // candidate: Paeth (best for terrain, costs the most)
                    long paethSum = 0;
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        int b = prev[i];
                        int c = i >= bpp ? prev[i - bpp] : 0;
                        byte v = (byte)(cur[i] - Paeth(a, b, c));
                        paethSum += v < 128 ? v : 256 - v;
                    }
                    if (subSum <= upSum && subSum <= paethSum)
                    {
                        raw[rowOff] = 1;
                        Buffer.BlockCopy(trial, 0, raw, rowOff + 1, stride);
                    }
                    else if (upSum <= paethSum)
                    {
                        raw[rowOff] = 2;
                        for (int i = 0; i < stride; i++) raw[rowOff + 1 + i] = (byte)(cur[i] - prev[i]);
                    }
                    else
                    {
                        raw[rowOff] = 4;
                        for (int i = 0; i < stride; i++)
                        {
                            int a = i >= bpp ? cur[i - bpp] : 0;
                            int b = prev[i];
                            int c = i >= bpp ? prev[i - bpp] : 0;
                            raw[rowOff + 1 + i] = (byte)(cur[i] - Paeth(a, b, c));
                        }
                    }
                }
                var t = prev; prev = cur; cur = t;
            }

            byte[] zdata;
            using (var ms = new MemoryStream(raw.Length / 2))
            {
                // zlib header (CM=8, CINFO=7, FLEVEL default, no dict); FCHECK makes it a multiple of 31
                ms.WriteByte(0x78); ms.WriteByte(0x9C);
                using (var ds = new DeflateStream(ms, fast ? CompressionLevel.Fastest : CompressionLevel.Optimal, true))
                    ds.Write(raw, 0, raw.Length);
                uint adler = Adler32(raw);
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                zdata = ms.ToArray();
            }

            using (var outp = new MemoryStream(zdata.Length + 64))
            {
                outp.Write(Signature, 0, 8);
                byte colorType;
                byte bitDepth = 8;
                switch (fmt)
                {
                    case Format.RGB: colorType = 2; break;
                    case Format.RGBA: colorType = 6; break;
                    case Format.Gray16: colorType = 0; bitDepth = 16; break;
                    default: colorType = 0; break;
                }
                byte[] ihdr = new byte[13];
                WriteBE(ihdr, 0, (uint)width);
                WriteBE(ihdr, 4, (uint)height);
                ihdr[8] = bitDepth; ihdr[9] = colorType; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                Chunk(outp, "IHDR", ihdr);
                Chunk(outp, "IDAT", zdata);
                Chunk(outp, "IEND", new byte[0]);
                return outp.ToArray();
            }
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            return pb <= pc ? b : c;
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            byte[] len = new byte[4]; WriteBE(len, 0, (uint)data.Length);
            s.Write(len, 0, 4);
            byte[] t = { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(t, 0, 4, 0xFFFFFFFFu);
            crc = Crc32(data, 0, data.Length, crc) ^ 0xFFFFFFFFu;
            byte[] c = new byte[4]; WriteBE(c, 0, crc);
            s.Write(c, 0, 4);
        }

        private static void WriteBE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static uint[] MakeCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        private static uint Crc32(byte[] buf, int off, int len, uint crc)
        {
            for (int i = off; i < off + len; i++) crc = crcTable[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            int i = 0, n = data.Length;
            while (n > 0)
            {
                int k = n < 5552 ? n : 5552;
                n -= k;
                while (k-- > 0) { a += data[i++]; b += a; }
                a %= 65521; b %= 65521;
            }
            return (b << 16) | a;
        }
    }
}
