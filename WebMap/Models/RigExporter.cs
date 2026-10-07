using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using WebMap.Util;

namespace WebMap.Models
{
    // A rig part (Rig's names) as glTF, on the game thread. Every part carries the Player's own
    // skeleton down to the bones it needs, posed as the game stands a Viking at rest: the server
    // runs no animation, so the idle clip is played once on a clone of the Player's Visual through
    // its humanoid Animator and the bones' local transforms kept. A skinned mesh (the body, a
    // tunic, a cape) is skinned to the body's bones, as VisEquipment rebinds it; a held or worn
    // item hangs from its attach point as a child node. Frames are flipped as PrefabExporter's:
    // z negated, winding reversed.
    internal static class RigExporter
    {
        private sealed class Player
        {
            public GameObject prefab; public VisEquipment ve; public Transform visual; public Rig.Joints joints;
            public Transform[] nodes; public Dictionary<Transform, int> index = new Dictionary<Transform, int>();
            public Vector3[] lp, ls; public Quaternion[] lr; public Matrix4x4[] model;   // the pose, local and from the rig's feet
            public bool posed; public string clip;
        }
        private static Player player;
        private static readonly Dictionary<int, Rig.Item> items = new Dictionary<int, Rig.Item>();

        // Game thread. The Player prefab and its attach points; the pose on first need.
        private static Player Prefab()
        {
            if (player != null) return player;
            GameObject go = null;
            try { go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null; } catch { }
            var ve = go != null ? go.GetComponent<VisEquipment>() : null;
            var visual = go != null ? go.transform.Find("Visual") : null;
            if (ve == null || ve.m_bodyModel == null || visual == null || ve.m_models.Length == 0) return null;
            string N(Transform t) => t != null ? t.name : null;
            var p = new Player
            {
                prefab = go, ve = ve, visual = visual,
                joints = new Rig.Joints { right = N(ve.m_rightHand), left = N(ve.m_leftHand), helmet = N(ve.m_helmet), backShield = N(ve.m_backShield), backMelee = N(ve.m_backMelee),
                                          backTwoHanded = N(ve.m_backTwohandedMelee), backBow = N(ve.m_backBow), backTool = N(ve.m_backTool), backAtgeir = N(ve.m_backAtgeir) },
                nodes = visual.GetComponentsInChildren<Transform>(true),
            };
            for (int i = 0; i < p.nodes.Length; i++) p.index[p.nodes[i]] = i;
            return player = p;
        }

        // The standing pose: the Visual cloned under a switched-off holder, its scripts dropped before they
        // wake, the idle clip evaluated at its start. Without the clip the prefab's own A-pose stands.
        private static Player Posed()
        {
            var p = Prefab();
            if (p == null || p.model != null) return p;
            int n = p.nodes.Length;
            p.lp = new Vector3[n]; p.ls = new Vector3[n]; p.lr = new Quaternion[n];
            for (int i = 0; i < n; i++) { p.lp[i] = p.nodes[i].localPosition; p.lr[i] = p.nodes[i].localRotation; p.ls[i] = p.nodes[i].localScale; }
            var holder = new GameObject("webmap-pose");
            holder.SetActive(false);
            PlayableGraph graph = default;
            try
            {
                var clone = UnityEngine.Object.Instantiate(p.visual.gameObject, holder.transform, false);
                foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
                var cn = clone.GetComponentsInChildren<Transform>(true);
                var anim = clone.GetComponent<Animator>();
                var clip = anim != null ? Idle(anim.runtimeAnimatorController) : null;
                holder.SetActive(true);
                if (clip != null && cn.Length == n)
                {
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    AnimationPlayableUtilities.PlayClip(anim, clip, out graph);
                    graph.Evaluate(0f);
                    for (int i = 0; i < n; i++) { p.lp[i] = cn[i].localPosition; p.lr[i] = cn[i].localRotation; p.ls[i] = cn[i].localScale; }
                    p.posed = true; p.clip = clip.name;
                }
            }
            catch (Exception e) { ZLog.LogWarning("WebMap: the players' standing pose failed, they stand as modelled: " + e.Message); }
            finally
            {
                try { if (graph.IsValid()) graph.Destroy(); } catch { }
                UnityEngine.Object.DestroyImmediate(holder);
            }
            p.model = new Matrix4x4[n];
            for (int i = 0; i < n; i++)
            {
                var local = Matrix4x4.TRS(p.lp[i], p.lr[i], p.ls[i]);
                p.model[i] = p.nodes[i] == p.visual || !p.index.TryGetValue(p.nodes[i].parent, out int pi) ? local : p.model[pi] * local;
            }
            ZLog.Log(p.posed ? $"WebMap: players stand in the game's {p.clip} pose" : "WebMap: players stand as modelled, no idle clip found");
            return p;
        }

        // whether the standing pose is taken; Pose takes it, a frame of its own for the export
        public static bool Ready => player != null && player.model != null;
        public static void Pose() => Posed();

        // the Player's idle: IdleTweaked, else the plainest clip named for idling
        private static AnimationClip Idle(RuntimeAnimatorController ctrl)
        {
            if (ctrl == null) return null;
            AnimationClip best = null;
            foreach (var c in ctrl.animationClips)
            {
                if (c == null) continue;
                if (c.name == "IdleTweaked") return c;
                if (c.name.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0 && (best == null || c.name.Length < best.name.Length)) best = c;
            }
            return best;
        }

        public static Rig.Item ItemOf(int hash)
        {
            if (items.TryGetValue(hash, out var it)) return it;
            GameObject go = null;
            try { go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(hash) : null; } catch { }
            it = go != null ? Rig.Of(go) : null;
            if (go != null || ObjectDB.instance != null) items[hash] = it;
            return it;
        }

        // Game thread, from the player snapshot: a player's look for /state, its parts queued for the library.
        public static string LookJson(ZDO z) => LookJson(Rig.FromZdo(z));
        internal static string LookJson(Rig.Look look)
        {
            var p = Prefab();
            if (p == null || ObjectDB.instance == null) return null;
            var parts = Rig.Resolve(look, p.joints, p.ve.m_models.Length, ItemOf);
            if (WebMapConfig.EXPORT_MODELS) foreach (var part in parts) ModelStore.RequestRig(part);
            var w = new JsonWriter(512);
            Rig.WriteJson(w, look, parts, ItemOf);
            return w.ToString();
        }

        // Game thread. False when the part names nothing the game has.
        public static bool Export(string name, string modelsDir, PrefabExporter.Result res)
        {
            var p = Posed();
            int at = name.LastIndexOf('@');
            if (p == null || at <= 0) return false;
            string item = name.Substring(0, at), how = name.Substring(at + 1);
            var part = new Part(p, name, modelsDir, res);
            if (name.StartsWith(Rig.Body, StringComparison.Ordinal))
            {
                if (!int.TryParse(name.Substring(Rig.Body.Length), out int m) || m < 0 || m >= p.ve.m_models.Length) return false;
                var model = p.ve.m_models[m];
                var mats = p.ve.m_bodyModel.sharedMaterials;
                part.Skinned(model.m_mesh, s => s == 0 ? model.m_baseMaterial : s < mats.Length ? mats[s] : null, "body");
                res.overlayChest = Paint(model.m_baseMaterial, "_ChestTex", modelsDir, res);
                res.overlayLegs = Paint(model.m_baseMaterial, "_LegsTex", modelsDir, res);
                return part.Done();
            }
            GameObject go = null;
            try { go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(item) : null; } catch { }
            if (go == null) return false;
            if (how == "armor")
            {
                // VisEquipment.AttachArmor: each attach_<bone> child on that bone, attach_skin on the body's skin
                foreach (Transform c in go.transform)
                {
                    if (!c.name.StartsWith("attach_", StringComparison.Ordinal)) continue;
                    string bone = c.name.Substring(7);
                    if (bone == "skin") part.Skin(c);
                    else { var joint = Find(p, bone); if (joint != null) part.Hang(c, joint, null); }
                }
                var sh = go.GetComponent<ItemDrop>()?.m_itemData.m_shared;
                if (sh != null && sh.m_armorMaterial != null)
                {
                    if (sh.m_itemType == ItemDrop.ItemData.ItemType.Chest) res.overlayChest = Paint(sh.m_armorMaterial, "_ChestTex", modelsDir, res);
                    if (sh.m_itemType == ItemDrop.ItemData.ItemType.Legs) res.overlayLegs = Paint(sh.m_armorMaterial, "_LegsTex", modelsDir, res);
                }
                return part.Done();
            }
            // VisEquipment.AttachItem: the first attach child (attach_back on the back, attach_skin in hand), plus equipoffset
            var jointT = Find(p, how);
            if (jointT == null) return false;
            bool back = Rig.IsBackJoint(p.joints, how);
            Transform pick = null;
            foreach (Transform c in go.transform)
                if ((back && c.name == "attach_back") || c.name == "attach" || (!back && c.name == "attach_skin")) { pick = c; break; }
            if (pick == null) return false;
            if (pick.name == "attach_skin") part.Skin(pick);
            else part.Hang(pick, jointT, go.transform.Find("equipoffset"));
            return part.Done();
        }

        // a body paint (the chest or legs a garment paints over the skin) as a texture file; "none" is no paint
        private static string Paint(Material mat, string prop, string modelsDir, PrefabExporter.Result res)
        {
            Texture2D t = null;
            try { t = mat.GetTexture(prop) as Texture2D; } catch { }
            return t == null || t.name.IndexOf("none", StringComparison.OrdinalIgnoreCase) >= 0 ? null : PrefabExporter.TextureFile(t, modelsDir, res);
        }

        // Utils.FindChild over the Visual: the transform an armour piece names
        private static Transform Find(Player p, string name)
        {
            foreach (var t in p.nodes) if (t.name == name) return t;
            return null;
        }

        // One part's glTF: the skeleton as far as it is needed, its meshes, its posed bounds.
        private sealed class Part
        {
            private readonly Player p; private readonly string modelsDir; private readonly PrefabExporter.Result res;
            private readonly GlbWriter w = new GlbWriter();
            private readonly Dictionary<Transform, int> nodeOf = new Dictionary<Transform, int>();
            private readonly int root;
            private readonly float[] box = { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue };
            private int meshes, tris;
            private static readonly Color32 grey = new Color32(150, 140, 130, 255);

            public Part(Player p, string name, string modelsDir, PrefabExporter.Result res)
            {
                this.p = p; this.modelsDir = modelsDir; this.res = res;
                res.name = name;
                root = w.AddNode(new GlbWriter.Node { name = "Player" });   // the rig's feet, where the player stands
            }

            // a Visual transform's node, and every node above it, posed
            private int Node(Transform t)
            {
                if (nodeOf.TryGetValue(t, out int n)) return n;
                int i = p.index[t];
                int parent = t == p.visual ? root : Node(t.parent);
                n = w.AddNode(new GlbWriter.Node { name = t.name, parent = parent, t = T(p.lp[i]), r = R(p.lr[i]), s = S(p.ls[i]) });
                return nodeOf[t] = n;
            }

            // an attach_skin child: its skinned meshes on the body's bones, the rest where the body stands
            public void Skin(Transform attach)
            {
                int rigid = -1;
                foreach (var (r, mesh) in Renderers(attach))
                {
                    if (r is SkinnedMeshRenderer smr && smr.bones.Length > 0) { Skinned(mesh, s => At(r.sharedMaterials, s), attach.name); continue; }
                    if (rigid < 0) { rigid = ++meshes; w.AddNode(new GlbWriter.Node { name = attach.name, parent = Node(p.visual), s = S(attach.localScale), mesh = rigid }); }
                    Rigid(r, mesh, attach, p.model[p.index[p.visual]] * Matrix4x4.Scale(attach.localScale), rigid);
                }
            }

            // a child hung on a joint as SetParent leaves it: at the joint (plus the item's equipoffset), turned
            // with it, and at its own size whatever the joint's scale
            public void Hang(Transform attach, Transform joint, Transform offset)
            {
                Matrix4x4 jm = p.model[p.index[joint]];
                Vector3 js = new Vector3(jm.GetColumn(0).magnitude, jm.GetColumn(1).magnitude, jm.GetColumn(2).magnitude), own = attach.lossyScale;
                Vector3 t = offset != null ? offset.position : Vector3.zero;
                Quaternion q = offset != null ? offset.rotation : Quaternion.identity;
                Vector3 s = new Vector3(own.x / Math.Max(1e-6f, js.x), own.y / Math.Max(1e-6f, js.y), own.z / Math.Max(1e-6f, js.z));
                int mesh = ++meshes;
                w.AddNode(new GlbWriter.Node { name = attach.name, parent = Node(joint), t = T(t), r = R(q), s = S(s), mesh = mesh });
                Matrix4x4 at = jm * Matrix4x4.TRS(t, q, s);
                foreach (var (r, m) in Renderers(attach)) Rigid(r, m, attach, at, mesh);
            }

            // What an instantiated attach child shows: itself forced on, its switched-off children not,
            // the first LOD only, meshes only (no particles).
            private List<(Renderer, Mesh)> Renderers(Transform attach)
            {
                var hidden = new HashSet<Renderer>();
                foreach (var lg in attach.GetComponentsInChildren<LODGroup>(true))
                {
                    LOD[] lods;
                    try { lods = lg.GetLODs(); } catch { continue; }
                    for (int i = 1; i < lods.Length; i++) if (lods[i].renderers != null) foreach (var r in lods[i].renderers) if (r != null) hidden.Add(r);
                    if (lods.Length > 0 && lods[0].renderers != null) foreach (var r in lods[0].renderers) if (r != null) hidden.Remove(r);
                }
                var list = new List<(Renderer, Mesh)>();
                var stack = new Stack<Transform>();
                stack.Push(attach);
                while (stack.Count > 0)
                {
                    var t = stack.Pop();
                    if (t != attach && !t.gameObject.activeSelf) continue;
                    foreach (Transform c in t) stack.Push(c);
                    var r = t.GetComponent<Renderer>();
                    if (r == null || !r.enabled || hidden.Contains(r)) continue;
                    Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r is MeshRenderer ? t.GetComponent<MeshFilter>()?.sharedMesh : null;
                    if (m != null) list.Add((r, m));
                }
                return list;
            }

            private static Material At(Material[] mats, int s) => mats != null && mats.Length > 0 ? mats[Math.Min(s, mats.Length - 1)] : null;

            // a mesh baked into its attach node's frame
            private void Rigid(Renderer r, Mesh mesh, Transform attach, Matrix4x4 posed, int meshId)
            {
                res.renderers++;
                if (!PrefabExporter.Geometry(mesh, modelsDir, res, out var pos, out var nrm, out var uv, out var subIdx)) return;
                Matrix4x4 m = attach.worldToLocalMatrix * r.transform.localToWorldMatrix, nm = m.inverse.transpose;
                int vc = pos.Length / 3;
                for (int i = 0; i < vc; i++)
                {
                    Vector3 v = m.MultiplyPoint3x4(new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]));
                    Grow(posed.MultiplyPoint3x4(v));
                    pos[i * 3] = v.x; pos[i * 3 + 1] = v.y; pos[i * 3 + 2] = -v.z;
                    if (nrm != null) { Vector3 n = nm.MultiplyVector(new Vector3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2])).normalized; nrm[i * 3] = n.x; nrm[i * 3 + 1] = n.y; nrm[i * 3 + 2] = -n.z; }
                    if (uv != null) uv[i * 2 + 1] = 1f - uv[i * 2 + 1];
                }
                Prims(mesh, subIdx, pos, nrm, uv, null, null, m.determinant < 0, s => At(r.sharedMaterials, s), meshId, null);
            }

            // a mesh skinned to the body's bones, whose order every armour's skin shares
            public void Skinned(Mesh mesh, Func<int, Material> mat, string what)
            {
                res.renderers++;
                if (mesh == null || !PrefabExporter.Geometry(mesh, modelsDir, res, out var pos, out var nrm, out var uv, out var subIdx)) return;
                Matrix4x4[] bind; BoneWeight[] bw;
                try { bind = mesh.bindposes; bw = mesh.boneWeights; } catch { return; }
                var bones = p.ve.m_bodyModel.bones;
                int vc = pos.Length / 3, nb = Math.Min(bind.Length, bones.Length);
                if (nb == 0 || bw.Length != vc) return;
                var skin = new GlbWriter.Skin { joints = new int[nb], inverseBind = new float[nb * 16] };
                var joint = new Matrix4x4[nb];
                for (int b = 0; b < nb; b++)
                {
                    skin.joints[b] = bones[b] != null && p.index.ContainsKey(bones[b]) ? Node(bones[b]) : root;
                    joint[b] = (bones[b] != null && p.index.TryGetValue(bones[b], out int bi) ? p.model[bi] : Matrix4x4.identity) * bind[b];
                    for (int c = 0; c < 4; c++) for (int r = 0; r < 4; r++) skin.inverseBind[b * 16 + c * 4 + r] = bind[b][r, c] * (r == 2 ? -1 : 1) * (c == 2 ? -1 : 1);
                }
                w.Skins.Add(skin);
                var joints = new ushort[vc * 4]; var weights = new float[vc * 4];
                for (int i = 0; i < vc; i++)
                {
                    var b = bw[i];
                    int[] ji = { b.boneIndex0, b.boneIndex1, b.boneIndex2, b.boneIndex3 };
                    float[] wi = { b.weight0, b.weight1, b.weight2, b.weight3 };
                    Vector3 v = new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]), at = Vector3.zero;
                    for (int k = 0; k < 4; k++)
                    {
                        if (ji[k] < 0 || ji[k] >= nb) { ji[k] = 0; wi[k] = 0; }
                        joints[i * 4 + k] = (ushort)ji[k]; weights[i * 4 + k] = wi[k];
                        if (wi[k] > 0) at += wi[k] * joint[ji[k]].MultiplyPoint3x4(v);
                    }
                    Grow(at);
                    pos[i * 3 + 2] = -pos[i * 3 + 2];
                    if (nrm != null) nrm[i * 3 + 2] = -nrm[i * 3 + 2];
                    if (uv != null) uv[i * 2 + 1] = 1f - uv[i * 2 + 1];
                }
                int meshId = ++meshes;
                w.AddNode(new GlbWriter.Node { name = what, mesh = meshId, skin = w.Skins.Count - 1 });
                Prims(mesh, subIdx, pos, nrm, uv, joints, weights, false, mat, meshId, what == "body" ? "webmap:skin" : null);
            }

            // a primitive per sub-mesh; the skin and the hair are named for the viewer to tint
            private void Prims(Mesh mesh, List<uint[]> subIdx, float[] pos, float[] nrm, float[] uv, ushort[] joints, float[] weights, bool mirrored, Func<int, Material> mat, int meshId, string first)
            {
                int subs = subIdx != null ? subIdx.Count : mesh.subMeshCount;
                for (int s = 0; s < subs; s++)
                {
                    uint[] raw = PrefabExporter.Triangles(mesh, subIdx, s);
                    if (raw == null || raw.Length < 3) continue;
                    var idx = new uint[raw.Length - raw.Length % 3];
                    for (int i = 0; i + 2 < raw.Length; i += 3)
                    {
                        if (mirrored) { idx[i] = raw[i]; idx[i + 1] = raw[i + 1]; idx[i + 2] = raw[i + 2]; }
                        else { idx[i] = raw[i + 2]; idx[i + 1] = raw[i + 1]; idx[i + 2] = raw[i]; }
                    }
                    var prim = new GlbWriter.Primitive { positions = pos, normals = nrm, uvs = uv, indices = idx, joints = joints, weights = weights, mesh = meshId };
                    Material m = mat(s);
                    PrefabExporter.ApplyMaterial(prim, m, modelsDir, grey, res);
                    // the player shader's garments keep their colour in the chest or legs paint
                    if (prim.textureUri == null && m != null)
                        foreach (var prop in new[] { "_ChestTex", "_LegsTex" })
                        {
                            string f = Paint(m, prop, modelsDir, res);
                            if (f != null) { prim.textureUri = f; prim.r = prim.g = prim.b = 1f; break; }
                        }
                    if (s == 0 && first != null) prim.name = first;
                    else if (m != null && m.name.StartsWith("PlayerHair", StringComparison.Ordinal)) { prim.name = "webmap:hair"; prim.alphaMask = true; }
                    if (prim.textureUri != null) res.textured = true;
                    tris += idx.Length / 3;
                    w.Add(prim);
                }
            }

            // the part as it stands, in the viewer's frame (z negated)
            private void Grow(Vector3 v)
            {
                box[0] = Math.Min(box[0], v.x); box[1] = Math.Min(box[1], v.y); box[2] = Math.Min(box[2], -v.z);
                box[3] = Math.Max(box[3], v.x); box[4] = Math.Max(box[4], v.y); box[5] = Math.Max(box[5], -v.z);
            }

            public bool Done()
            {
                if (w.Count > 0)
                {
                    res.writer = w; res.triangles = tris;
                    res.posed = box[0] <= box[3] ? box : new float[6];
                }
                return true;
            }

            private static float[] T(Vector3 v) => new[] { v.x, v.y, -v.z };
            private static float[] R(Quaternion q) => new[] { -q.x, -q.y, q.z, q.w };
            private static float[] S(Vector3 v) => new[] { v.x, v.y, v.z };
        }
    }
}
