// Ported from f00d4tehg0dz/valheim-webmap (MIT).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WebMap.Util;

namespace WebMap.Models
{
    // A minimal binary glTF 2.0 writer: one mesh, one primitive per material,
    // positions + normals (+ uv0 and an embedded PNG texture when there is
    // one), 32-bit indices. No Unity types: it can run on any thread once the
    // geometry has been copied out of the engine.
    internal sealed class GlbWriter
    {
        public sealed class Primitive
        {
            public string name;
            public float[] positions;     // xyz, already in glTF's right-handed frame
            public float[] normals;       // xyz or null
            public float[] uvs;           // uv or null (v already flipped)
            public uint[] indices;
            public float r = 1, g = 1, b = 1, a = 1;
            public byte[] texturePng;     // optional, embedded
            public string textureUri;     // optional, relative to the .glb (preferred: shared between prefabs)
            public bool alphaMask, foliage;        // cutout (leaves, fences with holes)
            public bool doubleSided = true;
            public float metallic = 0f, roughness = 0.9f;
        }

        private readonly List<Primitive> prims = new List<Primitive>();
        public void Add(Primitive p) { if (p != null && p.indices != null && p.indices.Length >= 3) prims.Add(p); }
        public int Count => prims.Count;

        public byte[] Write(string name, out float[] bounds)
        {
            bounds = new float[] { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue };
            var bin = new MemoryStream();
            var j = new JsonWriter(4096);
            var views = new List<int[]>();       // [offset, length, target]
            var accessors = new List<string>();  // raw json
            var images = new List<string>();     // json per image
            var materials = new List<string>();
            var primitivesJson = new List<string>();

            int AddView(byte[] data, int target)
            {
                Align(bin, 4);
                int off = (int)bin.Length;
                bin.Write(data, 0, data.Length);
                views.Add(new[] { off, data.Length, target });
                return views.Count - 1;
            }
            int AddAccessor(int view, int count, string type, int componentType, float[] min = null, float[] max = null)
            {
                var a = new JsonWriter(160);
                a.BeginObject().Prop("bufferView", view).Prop("componentType", componentType).Prop("count", count).Prop("type", type);
                if (min != null) { a.Key("min").BeginArray(); foreach (var v in min) a.Value(v, 5); a.End(); }
                if (max != null) { a.Key("max").BeginArray(); foreach (var v in max) a.Value(v, 5); a.End(); }
                a.End();
                accessors.Add(a.ToString());
                return accessors.Count - 1;
            }

            for (int pi = 0; pi < prims.Count; pi++)
            {
                var p = prims[pi];
                int vcount = p.positions.Length / 3;
                float[] min = { float.MaxValue, float.MaxValue, float.MaxValue }, max = { float.MinValue, float.MinValue, float.MinValue };
                for (int i = 0; i < vcount; i++)
                    for (int k = 0; k < 3; k++)
                    {
                        float v = p.positions[i * 3 + k];
                        if (v < min[k]) min[k] = v; if (v > max[k]) max[k] = v;
                        if (v < bounds[k]) bounds[k] = v; if (v > bounds[k + 3]) bounds[k + 3] = v;
                    }
                int posAcc = AddAccessor(AddView(ToBytes(p.positions), 34962), vcount, "VEC3", 5126, min, max);
                int nrmAcc = p.normals != null && p.normals.Length == p.positions.Length ? AddAccessor(AddView(ToBytes(p.normals), 34962), vcount, "VEC3", 5126) : -1;
                int uvAcc = p.uvs != null && p.uvs.Length == vcount * 2 ? AddAccessor(AddView(ToBytes(p.uvs), 34962), vcount, "VEC2", 5126) : -1;
                int idxAcc = AddAccessor(AddView(ToBytes(p.indices), 34963), p.indices.Length, "SCALAR", 5125);

                int texIndex = -1;
                if (uvAcc >= 0 && (p.textureUri != null || p.texturePng != null))
                {
                    var im = new JsonWriter(128);
                    im.BeginObject().Prop("mimeType", "image/png");
                    if (p.textureUri != null) im.Prop("uri", p.textureUri);
                    else im.Prop("bufferView", AddView(p.texturePng, 0));
                    im.End();
                    images.Add(im.ToString());
                    texIndex = images.Count - 1;
                }
                var m = new JsonWriter(256);
                m.BeginObject().Prop("name", p.name ?? ("mat" + pi)).Prop("doubleSided", p.doubleSided);
                m.Key("pbrMetallicRoughness").BeginObject();
                m.Key("baseColorFactor").BeginArray().Value(p.r, 4).Value(p.g, 4).Value(p.b, 4).Value(p.a, 4).End();
                m.Prop("metallicFactor", p.metallic, 3).Prop("roughnessFactor", p.roughness, 3);
                if (texIndex >= 0) m.Key("baseColorTexture").BeginObject().Prop("index", texIndex).End();
                m.End();
                if (p.alphaMask) m.Prop("alphaMode", "MASK").Prop("alphaCutoff", 0.5f, 2);
                else if (p.a < 0.999f) m.Prop("alphaMode", "BLEND");
                m.End();
                materials.Add(m.ToString());

                var pr = new JsonWriter(160);
                pr.BeginObject().Key("attributes").BeginObject().Prop("POSITION", posAcc);
                if (nrmAcc >= 0) pr.Prop("NORMAL", nrmAcc);
                if (uvAcc >= 0) pr.Prop("TEXCOORD_0", uvAcc);
                pr.End().Prop("indices", idxAcc).Prop("material", materials.Count - 1).Prop("mode", 4).End();
                primitivesJson.Add(pr.ToString());
            }
            if (prims.Count == 0) bounds = new float[6];

            j.BeginObject();
            j.Key("asset").BeginObject().Prop("version", "2.0").Prop("generator", "Valheim WebMap").End();
            j.Prop("scene", 0);
            j.Key("scenes").BeginArray().BeginObject().Key("nodes").BeginArray().Value(0).End().End().End();
            j.Key("nodes").BeginArray().BeginObject().Prop("name", name).Prop("mesh", 0).End().End();
            j.Key("meshes").BeginArray().BeginObject().Prop("name", name).Key("primitives").BeginArray();
            foreach (var s in primitivesJson) j.Raw(s);
            j.End().End().End();
            j.Key("materials").BeginArray(); foreach (var s in materials) j.Raw(s); j.End();
            if (images.Count > 0)
            {
                j.Key("images").BeginArray();
                foreach (string im in images) j.Raw(im);
                j.End();
                j.Key("samplers").BeginArray().BeginObject().Prop("magFilter", 9729).Prop("minFilter", 9987).Prop("wrapS", 10497).Prop("wrapT", 10497).End().End();
                j.Key("textures").BeginArray();
                for (int i = 0; i < images.Count; i++) j.BeginObject().Prop("sampler", 0).Prop("source", i).End();
                j.End();
            }
            j.Key("accessors").BeginArray(); foreach (var s in accessors) j.Raw(s); j.End();
            j.Key("bufferViews").BeginArray();
            foreach (var v in views)
            {
                j.BeginObject().Prop("buffer", 0).Prop("byteOffset", v[0]).Prop("byteLength", v[1]);
                if (v[2] != 0) j.Prop("target", v[2]);
                j.End();
            }
            j.End();
            Align(bin, 4);
            j.Key("buffers").BeginArray().BeginObject().Prop("byteLength", (int)bin.Length).End().End();
            j.End();

            byte[] json = Encoding.UTF8.GetBytes(j.ToString());
            int jsonPad = (4 - json.Length % 4) % 4;
            byte[] binBytes = bin.ToArray();
            int total = 12 + 8 + json.Length + jsonPad + 8 + binBytes.Length;
            using (var ms = new MemoryStream(total))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(0x46546C67u); bw.Write(2u); bw.Write((uint)total);
                bw.Write((uint)(json.Length + jsonPad)); bw.Write(0x4E4F534Au); bw.Write(json);
                for (int i = 0; i < jsonPad; i++) bw.Write((byte)0x20);
                bw.Write((uint)binBytes.Length); bw.Write(0x004E4942u); bw.Write(binBytes);
                bw.Flush();
                return ms.ToArray();
            }
        }

        private static void Align(MemoryStream s, int n) { while (s.Length % n != 0) s.WriteByte(0); }

        private static byte[] ToBytes(float[] f) { var b = new byte[f.Length * 4]; Buffer.BlockCopy(f, 0, b, 0, b.Length); return b; }
        private static byte[] ToBytes(uint[] u) { var b = new byte[u.Length * 4]; Buffer.BlockCopy(u, 0, b, 0, b.Length); return b; }
    }
}
