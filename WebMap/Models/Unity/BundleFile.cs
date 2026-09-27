// Ported from f00d4tehg0dz/valheim-webmap (MIT).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WebMap.Models.Unity
{
    // A UnityFS asset bundle, read block by block from disk so a 700 MB bundle never
    // sits in memory. Blocks are LZ4 (or stored); LZMA bundles are reported, not read.
    internal sealed class BundleFile : IDisposable
    {
        public sealed class Node { public long Offset; public long Size; public string Name; }
        private struct Block { public int Uncompressed; public int Compressed; public int Flags; public long FileOffset; public long DataOffset; }

        private readonly FileStream fs;
        private readonly List<Block> blocks = new List<Block>();
        public readonly List<Node> Nodes = new List<Node>();
        public string UnityVersion = "";
        private int cacheIndex = -1; private byte[] cacheBuf;

        public BundleFile(string path)
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var r = new BigEndianReader(fs);
            string sig = r.CString();
            if (sig != "UnityFS") throw new InvalidDataException("not a UnityFS bundle");
            uint version = r.U32();
            r.CString(); UnityVersion = r.CString();
            long size = r.I64();
            int cbi = (int)r.U32(), ubi = (int)r.U32();
            uint flags = r.U32();
            if (version >= 7) r.Align(16);
            byte[] bi = new byte[cbi];
            long afterHeader = fs.Position;
            if ((flags & 0x80) != 0) { fs.Seek(fs.Length - cbi, SeekOrigin.Begin); ReadFull(bi, cbi); }
            else { ReadFull(bi, cbi); afterHeader = fs.Position; }
            byte[] info = Decompress(bi, cbi, (int)(flags & 0x3F), ubi);
            var ir = new BigEndianReader(new MemoryStream(info));
            ir.Skip(16);
            int nb = (int)ir.U32();
            long fileOff = afterHeader;
            if ((flags & 0x200) != 0) fileOff = (fileOff + 15) / 16 * 16;
            long dataOff = 0;
            for (int i = 0; i < nb; i++)
            {
                var b = new Block { Uncompressed = (int)ir.U32(), Compressed = (int)ir.U32(), Flags = ir.U16(), FileOffset = fileOff, DataOffset = dataOff };
                blocks.Add(b); fileOff += b.Compressed; dataOff += b.Uncompressed;
            }
            int nn = (int)ir.U32();
            for (int i = 0; i < nn; i++)
            {
                var n = new Node { Offset = ir.I64(), Size = ir.I64() };
                ir.U32(); n.Name = ir.CString();
                Nodes.Add(n);
            }
        }

        public Node Find(string name) { foreach (var n in Nodes) if (n.Name == name) return n; return null; }

        public byte[] ReadNode(Node n) => ReadRange(n.Offset, (int)n.Size);

        // bytes [offset, offset+size) of the decompressed data stream
        public byte[] ReadRange(long offset, int size)
        {
            byte[] outb = new byte[size];
            int done = 0;
            int bi = FindBlock(offset);
            while (done < size && bi < blocks.Count)
            {
                var b = blocks[bi];
                byte[] buf = BlockData(bi);
                int start = (int)(offset + done - b.DataOffset);
                int n = Math.Min(size - done, b.Uncompressed - start);
                Buffer.BlockCopy(buf, start, outb, done, n);
                done += n; bi++;
            }
            if (done < size) throw new InvalidDataException("bundle: range past end");
            return outb;
        }

        private int FindBlock(long dataOffset)
        {
            int lo = 0, hi = blocks.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (blocks[mid].DataOffset <= dataOffset) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        private byte[] BlockData(int i)
        {
            if (i == cacheIndex) return cacheBuf;
            var b = blocks[i];
            byte[] c = new byte[b.Compressed];
            fs.Seek(b.FileOffset, SeekOrigin.Begin);
            ReadFull(c, b.Compressed);
            cacheBuf = Decompress(c, b.Compressed, b.Flags & 0x3F, b.Uncompressed);
            cacheIndex = i;
            return cacheBuf;
        }

        private static byte[] Decompress(byte[] src, int len, int kind, int usize)
        {
            if (kind == 0) { if (len == usize) return src; var o = new byte[usize]; Buffer.BlockCopy(src, 0, o, 0, Math.Min(len, usize)); return o; }
            if (kind == 2 || kind == 3) { var o = new byte[usize]; Lz4.Decode(src, 0, len, o, 0, usize); return o; }
            throw new NotSupportedException("bundle compression " + kind + " (LZMA) not supported");
        }

        private void ReadFull(byte[] buf, int n)
        {
            int got = 0;
            while (got < n) { int r = fs.Read(buf, got, n - got); if (r <= 0) throw new EndOfStreamException(); got += r; }
        }

        public void Dispose() { fs.Dispose(); }
    }

    internal sealed class BigEndianReader
    {
        private readonly Stream s;
        public BigEndianReader(Stream s) { this.s = s; }
        private int B() { int b = s.ReadByte(); if (b < 0) throw new EndOfStreamException(); return b; }
        public ushort U16() => (ushort)((B() << 8) | B());
        public uint U32() => (uint)((B() << 24) | (B() << 16) | (B() << 8) | B());
        public long I64() => ((long)U32() << 32) | U32();
        public void Skip(int n) { s.Seek(n, SeekOrigin.Current); }
        public void Align(int a) { long p = s.Position; long q = (p + a - 1) / a * a; if (q != p) s.Seek(q, SeekOrigin.Begin); }
        public string CString()
        {
            var sb = new List<byte>();
            int b; while ((b = B()) != 0) sb.Add((byte)b);
            return Encoding.UTF8.GetString(sb.ToArray());
        }
    }
}
