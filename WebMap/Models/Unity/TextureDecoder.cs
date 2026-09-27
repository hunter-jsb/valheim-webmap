// Ported from f00d4tehg0dz/valheim-webmap (MIT).
using System;

namespace WebMap.Models.Unity
{
    // Block-compressed texture decoding to RGBA32: DXT1 (BC1), DXT5 (BC3), BC7, plus the
    // uncompressed 8-bit formats. Rows come out bottom-up as Unity stores them; the
    // caller flips. BC7 decoding is a port of bcdec (see LICENSE, third-party notices).
    internal static class TextureDecoder
    {
        // Unity TextureFormat values
        public const int Alpha8 = 1, RGB24 = 3, RGBA32 = 4, ARGB32 = 5, DXT1 = 10, DXT5 = 12, BC7 = 25;

        public static bool Supported(int fmt) => fmt == DXT1 || fmt == DXT5 || fmt == BC7 || fmt == RGBA32 || fmt == ARGB32 || fmt == RGB24 || fmt == Alpha8;

        public static byte[] Decode(byte[] d, int w, int h, int fmt)
        {
            var o = new byte[w * h * 4];
            switch (fmt)
            {
                case DXT1: Bc1(d, w, h, o); break;
                case DXT5: Bc3(d, w, h, o); break;
                case BC7: Bc7Decode(d, w, h, o); break;
                case RGBA32: Buffer.BlockCopy(d, 0, o, 0, Math.Min(d.Length, o.Length)); break;
                case ARGB32: for (int i = 0; i + 3 < d.Length && i + 3 < o.Length; i += 4) { o[i] = d[i + 1]; o[i + 1] = d[i + 2]; o[i + 2] = d[i + 3]; o[i + 3] = d[i]; } break;
                case RGB24: for (int i = 0, j = 0; i + 2 < d.Length && j + 3 < o.Length; i += 3, j += 4) { o[j] = d[i]; o[j + 1] = d[i + 1]; o[j + 2] = d[i + 2]; o[j + 3] = 255; } break;
                case Alpha8: for (int i = 0, j = 0; i < d.Length && j + 3 < o.Length; i++, j += 4) { o[j] = o[j + 1] = o[j + 2] = 255; o[j + 3] = d[i]; } break;
                default: return null;
            }
            return o;
        }

        private static void Rgb565(int v, out int r, out int g, out int b)
        {
            r = ((v >> 11) & 31) * 255 / 31; g = ((v >> 5) & 63) * 255 / 63; b = (v & 31) * 255 / 31;
        }

        private static void ColorBlock(byte[] d, int p, byte[] o, int w, int h, int bx, int by, byte[] alpha, bool force4)
        {
            int c0 = d[p] | (d[p + 1] << 8), c1 = d[p + 2] | (d[p + 3] << 8);
            uint idx = (uint)(d[p + 4] | (d[p + 5] << 8) | (d[p + 6] << 16) | (d[p + 7] << 24));
            Rgb565(c0, out int r0, out int g0, out int b0); Rgb565(c1, out int r1, out int g1, out int b1);
            int[] pr = new int[4], pg = new int[4], pb = new int[4], pa = { 255, 255, 255, 255 };
            pr[0] = r0; pg[0] = g0; pb[0] = b0; pr[1] = r1; pg[1] = g1; pb[1] = b1;
            if (c0 > c1 || force4)
            {
                pr[2] = (2 * r0 + r1) / 3; pg[2] = (2 * g0 + g1) / 3; pb[2] = (2 * b0 + b1) / 3;
                pr[3] = (r0 + 2 * r1) / 3; pg[3] = (g0 + 2 * g1) / 3; pb[3] = (b0 + 2 * b1) / 3;
            }
            else
            {
                pr[2] = (r0 + r1) / 2; pg[2] = (g0 + g1) / 2; pb[2] = (b0 + b1) / 2;
                pr[3] = pg[3] = pb[3] = 0; pa[3] = 0;
            }
            for (int i = 0; i < 16; i++)
            {
                int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                if (x >= w || y >= h) continue;
                int k = (int)((idx >> (2 * i)) & 3);
                int q = (y * w + x) * 4;
                o[q] = (byte)pr[k]; o[q + 1] = (byte)pg[k]; o[q + 2] = (byte)pb[k]; o[q + 3] = alpha != null ? alpha[i] : (byte)pa[k];
            }
        }

        private static void Bc1(byte[] d, int w, int h, byte[] o)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4, p = 0;
            for (int by = 0; by < bh; by++) for (int bx = 0; bx < bw; bx++) { if (p + 8 > d.Length) return; ColorBlock(d, p, o, w, h, bx, by, null, false); p += 8; }
        }

        private static void Bc3(byte[] d, int w, int h, byte[] o)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4, p = 0;
            var alpha = new byte[16]; var pal = new int[8];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    if (p + 16 > d.Length) return;
                    int a0 = d[p], a1 = d[p + 1];
                    ulong bits = 0; for (int i = 0; i < 6; i++) bits |= (ulong)d[p + 2 + i] << (8 * i);
                    pal[0] = a0; pal[1] = a1;
                    if (a0 > a1) for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1) / 7;
                    else { for (int i = 1; i < 5; i++) pal[i + 1] = ((5 - i) * a0 + i * a1) / 5; pal[6] = 0; pal[7] = 255; }
                    for (int i = 0; i < 16; i++) alpha[i] = (byte)pal[(int)((bits >> (3 * i)) & 7)];
                    ColorBlock(d, p + 8, o, w, h, bx, by, alpha, true);
                    p += 16;
                }
        }

        // ---------------------------------------------------------------- BC7
        private static readonly byte[] P2 = { 128,0,1,1,0,0,1,1,0,0,1,1,0,0,1,129,128,0,0,1,0,0,0,1,0,0,0,1,0,0,0,129,128,1,1,1,0,1,1,1,0,1,1,1,0,1,1,129,128,0,0,1,0,0,1,1,0,0,1,1,0,1,1,129,128,0,0,0,0,0,0,1,0,0,0,1,0,0,1,129,128,0,1,1,0,1,1,1,0,1,1,1,1,1,1,129,128,0,0,1,0,0,1,1,0,1,1,1,1,1,1,129,128,0,0,0,0,0,0,1,0,0,1,1,0,1,1,129,128,0,0,0,0,0,0,0,0,0,0,1,0,0,1,129,128,0,1,1,0,1,1,1,1,1,1,1,1,1,1,129,128,0,0,0,0,0,0,1,0,1,1,1,1,1,1,129,128,0,0,0,0,0,0,0,0,0,0,1,0,1,1,129,128,0,0,1,0,1,1,1,1,1,1,1,1,1,1,129,128,0,0,0,0,0,0,0,1,1,1,1,1,1,1,129,128,0,0,0,1,1,1,1,1,1,1,1,1,1,1,129,128,0,0,0,0,0,0,0,0,0,0,0,1,1,1,129,128,0,0,0,1,0,0,0,1,1,1,0,1,1,1,129,128,1,129,1,0,0,0,1,0,0,0,0,0,0,0,0,128,0,0,0,0,0,0,0,129,0,0,0,1,1,1,0,128,1,129,1,0,0,1,1,0,0,0,1,0,0,0,0,128,0,129,1,0,0,0,1,0,0,0,0,0,0,0,0,128,0,0,0,1,0,0,0,129,1,0,0,1,1,1,0,128,0,0,0,0,0,0,0,129,0,0,0,1,1,0,0,128,1,1,1,0,0,1,1,0,0,1,1,0,0,0,129,128,0,129,1,0,0,0,1,0,0,0,1,0,0,0,0,128,0,0,0,1,0,0,0,129,0,0,0,1,1,0,0,128,1,129,0,0,1,1,0,0,1,1,0,0,1,1,0,128,0,129,1,0,1,1,0,0,1,1,0,1,1,0,0,128,0,0,1,0,1,1,1,129,1,1,0,1,0,0,0,128,0,0,0,1,1,1,1,129,1,1,1,0,0,0,0,128,1,129,1,0,0,0,1,1,0,0,0,1,1,1,0,128,0,129,1,1,0,0,1,1,0,0,1,1,1,0,0,128,1,0,1,0,1,0,1,0,1,0,1,0,1,0,129,128,0,0,0,1,1,1,1,0,0,0,0,1,1,1,129,128,1,0,1,1,0,129,0,0,1,0,1,1,0,1,0,128,0,1,1,0,0,1,1,129,1,0,0,1,1,0,0,128,0,129,1,1,1,0,0,0,0,1,1,1,1,0,0,128,1,0,1,0,1,0,1,129,0,1,0,1,0,1,0,128,1,1,0,1,0,0,1,0,1,1,0,1,0,0,129,128,1,0,1,1,0,1,0,1,0,1,0,0,1,0,129,128,1,129,1,0,0,1,1,1,1,0,0,1,1,1,0,128,0,0,1,0,0,1,1,129,1,0,0,1,0,0,0,128,0,129,1,0,0,1,0,0,1,0,0,1,1,0,0,128,0,129,1,1,0,1,1,1,1,0,1,1,1,0,0,128,1,129,0,1,0,0,1,1,0,0,1,0,1,1,0,128,0,1,1,1,1,0,0,1,1,0,0,0,0,1,129,128,1,1,0,0,1,1,0,1,0,0,1,1,0,0,129,128,0,0,0,0,1,129,0,0,1,1,0,0,0,0,0,128,1,0,0,1,1,129,0,0,1,0,0,0,0,0,0,128,0,129,0,0,1,1,1,0,0,1,0,0,0,0,0,128,0,0,0,0,0,129,0,0,1,1,1,0,0,1,0,128,0,0,0,0,1,0,0,129,1,1,0,0,1,0,0,128,1,1,0,1,1,0,0,1,0,0,1,0,0,1,129,128,0,1,1,0,1,1,0,1,1,0,0,1,0,0,129,128,1,129,0,0,0,1,1,1,0,0,1,1,1,0,0,128,0,129,1,1,0,0,1,1,1,0,0,0,1,1,0,128,1,1,0,1,1,0,0,1,1,0,0,1,0,0,129,128,1,1,0,0,0,1,1,0,0,1,1,1,0,0,129,128,1,1,1,1,1,1,0,1,0,0,0,0,0,0,129,128,0,0,1,1,0,0,0,1,1,1,0,0,1,1,129,128,0,0,0,1,1,1,1,0,0,1,1,0,0,1,129,128,0,129,1,0,0,1,1,1,1,1,1,0,0,0,0,128,0,129,0,0,0,1,0,1,1,1,0,1,1,1,0,128,1,0,0,0,1,0,0,0,1,1,1,0,1,1,129 };
        private static readonly byte[] P3 = { 128,0,1,129,0,0,1,1,0,2,2,1,2,2,2,130,128,0,0,129,0,0,1,1,130,2,1,1,2,2,2,1,128,0,0,0,2,0,0,1,130,2,1,1,2,2,1,129,128,2,2,130,0,0,2,2,0,0,1,1,0,1,1,129,128,0,0,0,0,0,0,0,129,1,2,2,1,1,2,130,128,0,1,129,0,0,1,1,0,0,2,2,0,0,2,130,128,0,2,130,0,0,2,2,1,1,1,1,1,1,1,129,128,0,1,1,0,0,1,1,130,2,1,1,2,2,1,129,128,0,0,0,0,0,0,0,129,1,1,1,2,2,2,130,128,0,0,0,1,1,1,1,129,1,1,1,2,2,2,130,128,0,0,0,1,1,129,1,2,2,2,2,2,2,2,130,128,0,1,2,0,0,129,2,0,0,1,2,0,0,1,130,128,1,1,2,0,1,129,2,0,1,1,2,0,1,1,130,128,1,2,2,0,129,2,2,0,1,2,2,0,1,2,130,128,0,1,129,0,1,1,2,1,1,2,2,1,2,2,130,128,0,1,129,2,0,0,1,130,2,0,0,2,2,2,0,128,0,0,129,0,0,1,1,0,1,1,2,1,1,2,130,128,1,1,129,0,0,1,1,130,0,0,1,2,2,0,0,128,0,0,0,1,1,2,2,129,1,2,2,1,1,2,130,128,0,2,130,0,0,2,2,0,0,2,2,1,1,1,129,128,1,1,129,0,1,1,1,0,2,2,2,0,2,2,130,128,0,0,129,0,0,0,1,130,2,2,1,2,2,2,1,128,0,0,0,0,0,129,1,0,1,2,2,0,1,2,130,128,0,0,0,1,1,0,0,130,2,129,0,2,2,1,0,128,1,2,130,0,129,2,2,0,0,1,1,0,0,0,0,128,0,1,2,0,0,1,2,129,1,2,2,2,2,2,130,128,1,1,0,1,2,130,1,129,2,2,1,0,1,1,0,128,0,0,0,0,1,129,0,1,2,130,1,1,2,2,1,128,0,2,2,1,1,0,2,129,1,0,2,0,0,2,130,128,1,1,0,0,129,1,0,2,0,0,2,2,2,2,130,128,0,1,1,0,1,2,2,0,1,130,2,0,0,1,129,128,0,0,0,2,0,0,0,130,2,1,1,2,2,2,129,128,0,0,0,0,0,0,2,129,1,2,2,1,2,2,130,128,2,2,130,0,0,2,2,0,0,1,2,0,0,1,129,128,0,1,129,0,0,1,2,0,0,2,2,0,2,2,130,128,1,2,0,0,129,2,0,0,1,130,0,0,1,2,0,128,0,0,0,1,1,129,1,2,2,130,2,0,0,0,0,128,1,2,0,1,2,0,1,130,0,129,2,0,1,2,0,128,1,2,0,2,0,1,2,129,130,0,1,0,1,2,0,128,0,1,1,2,2,0,0,1,1,130,2,0,0,1,129,128,0,1,1,1,1,130,2,2,2,0,0,0,0,1,129,128,1,0,129,0,1,0,1,2,2,2,2,2,2,2,130,128,0,0,0,0,0,0,0,130,1,2,1,2,1,2,129,128,0,2,2,1,129,2,2,0,0,2,2,1,1,2,130,128,0,2,130,0,0,1,1,0,0,2,2,0,0,1,129,128,2,2,0,1,2,130,1,0,2,2,0,1,2,2,129,128,1,0,1,2,2,130,2,2,2,2,2,0,1,0,129,128,0,0,0,2,1,2,1,130,1,2,1,2,1,2,129,128,1,0,129,0,1,0,1,0,1,0,1,2,2,2,130,128,2,2,130,0,1,1,1,0,2,2,2,0,1,1,129,128,0,0,2,1,129,1,2,0,0,0,2,1,1,1,130,128,0,0,0,2,129,1,2,2,1,1,2,2,1,1,130,128,2,2,2,0,129,1,1,0,1,1,1,0,2,2,130,128,0,0,2,1,1,1,2,129,1,1,2,0,0,0,130,128,1,1,0,0,129,1,0,0,1,1,0,2,2,2,130,128,0,0,0,0,0,0,0,2,1,129,2,2,1,1,130,128,1,1,0,0,129,1,0,2,2,2,2,2,2,2,130,128,0,2,2,0,0,1,1,0,0,129,1,0,0,2,130,128,0,2,2,1,1,2,2,129,1,2,2,0,0,2,130,128,0,0,0,0,0,0,0,0,0,0,0,2,129,1,130,128,0,0,130,0,0,0,1,0,0,0,2,0,0,0,129,128,2,2,2,1,2,2,2,0,2,2,2,129,2,2,130,128,1,0,129,2,2,2,2,2,2,2,2,2,2,2,130,128,1,1,129,2,0,1,1,130,2,0,1,2,2,2,0 };
        private static readonly int[] W2 = { 0, 21, 43, 64 };
        private static readonly int[] W3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        private static readonly int[] W4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };
        private static readonly int[] BitsRgb = { 4, 6, 5, 7, 5, 7, 7, 5 };
        private static readonly int[] BitsA = { 0, 0, 0, 0, 6, 8, 7, 5 };
        private const int HasP = 0xCB;   // 0b11001011: modes with P-bits

        private struct Bits
        {
            public ulong Lo, Hi;
            public int Read(int n)
            {
                ulong mask = (1UL << n) - 1;
                int v = (int)(Lo & mask);
                Lo >>= n;
                Lo |= (Hi & mask) << (64 - n);
                Hi >>= n;
                return v;
            }
        }

        private static int Interp(int a, int b, int[] wt, int i) => (a * (64 - wt[i]) + b * wt[i] + 32) >> 6;

        private static void Bc7Decode(byte[] d, int w, int h, byte[] o)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4, p = 0;
            var ep = new int[6, 4]; var idx = new int[16];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++, p += 16)
                {
                    if (p + 16 > d.Length) return;
                    var bs = new Bits();
                    for (int i = 0; i < 8; i++) { bs.Lo |= (ulong)d[p + i] << (8 * i); bs.Hi |= (ulong)d[p + 8 + i] << (8 * i); }
                    int mode = 0;
                    while (mode < 8 && bs.Read(1) == 0) mode++;
                    if (mode >= 8)
                    {
                        for (int i = 0; i < 16; i++) { int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2); if (x < w && y < h) { int q = (y * w + x) * 4; o[q] = o[q + 1] = o[q + 2] = o[q + 3] = 0; } }
                        continue;
                    }
                    int partition = 0, nparts = 1, rotation = 0, isb = 0;
                    if (mode == 0 || mode == 1 || mode == 2 || mode == 3 || mode == 7)
                    {
                        nparts = (mode == 0 || mode == 2) ? 3 : 2;
                        partition = bs.Read(mode == 0 ? 4 : 6);
                    }
                    int nend = nparts * 2;
                    if (mode == 4 || mode == 5) { rotation = bs.Read(2); if (mode == 4) isb = bs.Read(1); }
                    for (int c = 0; c < 3; c++) for (int j = 0; j < nend; j++) ep[j, c] = bs.Read(BitsRgb[mode]);
                    if (BitsA[mode] > 0) for (int j = 0; j < nend; j++) ep[j, 3] = bs.Read(BitsA[mode]); else for (int j = 0; j < nend; j++) ep[j, 3] = 0;
                    if (mode == 0 || mode == 1 || mode == 3 || mode == 6 || mode == 7)
                    {
                        for (int i = 0; i < nend; i++) for (int j = 0; j < 4; j++) ep[i, j] <<= 1;
                        if (mode == 1)
                        {
                            int i0 = bs.Read(1), j0 = bs.Read(1);
                            for (int k = 0; k < 3; k++) { ep[0, k] |= i0; ep[1, k] |= i0; ep[2, k] |= j0; ep[3, k] |= j0; }
                        }
                        else if ((HasP & (1 << mode)) != 0)
                        {
                            for (int i = 0; i < nend; i++) { int pb = bs.Read(1); for (int k = 0; k < 4; k++) ep[i, k] |= pb; }
                        }
                    }
                    for (int i = 0; i < nend; i++)
                    {
                        int j = BitsRgb[mode] + ((HasP >> mode) & 1);
                        for (int k = 0; k < 3; k++) { ep[i, k] = (ep[i, k] << (8 - j)) & 0xff; ep[i, k] |= ep[i, k] >> j; }
                        j = BitsA[mode] + ((HasP >> mode) & 1);
                        if (j > 0) { ep[i, 3] = (ep[i, 3] << (8 - j)) & 0xff; ep[i, 3] |= ep[i, 3] >> j; }
                    }
                    if (BitsA[mode] == 0) for (int j = 0; j < nend; j++) ep[j, 3] = 255;
                    int ib = (mode == 0 || mode == 1) ? 3 : (mode == 6 ? 4 : 2);
                    int ib2 = mode == 4 ? 3 : (mode == 5 ? 2 : 0);
                    int[] wt = ib == 2 ? W2 : (ib == 3 ? W3 : W4);
                    int[] wt2 = ib2 == 2 ? W2 : W3;
                    byte[] table = nparts == 2 ? P2 : P3;
                    for (int i = 0; i < 16; i++)
                    {
                        int ps = nparts == 1 ? (i == 0 ? 128 : 0) : table[partition * 16 + i];
                        idx[i] = bs.Read((ps & 0x80) != 0 ? ib - 1 : ib);
                    }
                    for (int i = 0; i < 16; i++)
                    {
                        int ps = (nparts == 1 ? (i == 0 ? 128 : 0) : table[partition * 16 + i]) & 3;
                        int e0 = ps * 2, e1 = ps * 2 + 1, index = idx[i];
                        int r, g, b, a;
                        if (ib2 == 0)
                        {
                            r = Interp(ep[e0, 0], ep[e1, 0], wt, index); g = Interp(ep[e0, 1], ep[e1, 1], wt, index);
                            b = Interp(ep[e0, 2], ep[e1, 2], wt, index); a = Interp(ep[e0, 3], ep[e1, 3], wt, index);
                        }
                        else
                        {
                            int index2 = bs.Read(i == 0 ? ib2 - 1 : ib2);
                            if (isb == 0)
                            {
                                r = Interp(ep[e0, 0], ep[e1, 0], wt, index); g = Interp(ep[e0, 1], ep[e1, 1], wt, index);
                                b = Interp(ep[e0, 2], ep[e1, 2], wt, index); a = Interp(ep[e0, 3], ep[e1, 3], wt2, index2);
                            }
                            else
                            {
                                r = Interp(ep[e0, 0], ep[e1, 0], wt2, index2); g = Interp(ep[e0, 1], ep[e1, 1], wt2, index2);
                                b = Interp(ep[e0, 2], ep[e1, 2], wt2, index2); a = Interp(ep[e0, 3], ep[e1, 3], wt, index);
                            }
                        }
                        int t;
                        if (rotation == 1) { t = a; a = r; r = t; }
                        else if (rotation == 2) { t = a; a = g; g = t; }
                        else if (rotation == 3) { t = a; a = b; b = t; }
                        int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                        if (x < w && y < h) { int q = (y * w + x) * 4; o[q] = (byte)r; o[q + 1] = (byte)g; o[q + 2] = (byte)b; o[q + 3] = (byte)a; }
                    }
                }
        }
    }
}
