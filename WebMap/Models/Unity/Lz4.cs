// Ported from f00d4tehg0dz/valheim-webmap (MIT).
using System;

namespace WebMap.Models.Unity
{
    // LZ4 block decompression (the raw block format, no frame): what Unity asset bundles use.
    internal static class Lz4
    {
        public static int Decode(byte[] src, int srcOff, int srcLen, byte[] dst, int dstOff, int dstLen)
        {
            int si = srcOff, se = srcOff + srcLen, di = dstOff, de = dstOff + dstLen;
            while (si < se)
            {
                int token = src[si++];
                int lit = token >> 4;
                if (lit == 15)
                {
                    int b;
                    do { b = src[si++]; lit += b; } while (b == 255);
                }
                if (di + lit > de || si + lit > se) throw new InvalidOperationException("lz4: literal overrun");
                Buffer.BlockCopy(src, si, dst, di, lit);
                si += lit; di += lit;
                if (si >= se) break;
                int offset = src[si] | (src[si + 1] << 8); si += 2;
                int ml = token & 15;
                if (ml == 15)
                {
                    int b;
                    do { b = src[si++]; ml += b; } while (b == 255);
                }
                ml += 4;
                int start = di - offset;
                if (start < dstOff || di + ml > de) throw new InvalidOperationException("lz4: match overrun");
                if (offset >= ml) { Buffer.BlockCopy(dst, start, dst, di, ml); di += ml; }
                else for (int i = 0; i < ml; i++) dst[di++] = dst[start + i];
            }
            return di - dstOff;
        }
    }
}
