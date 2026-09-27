using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using WebMap.Models;
using Xunit;
using ItemType = ItemDrop.ItemData.ItemType;
using HairType = ItemDrop.ItemData.HelmetHairType;

namespace WebMap.Tests
{
    // Live players in 3D: what a look hangs where, and the skin the glTF carries for it.
    public class RigTests
    {
        static readonly Rig.Joints joints = new Rig.Joints { right = "RightHand_Attach", left = "LeftHand_Attach", helmet = "Helmet_attach", backMelee = "BackOneHanded_attach", backTool = "BackTool_attach" };

        // A part on the wrong joint puts the sword on the head; a helmet that leaves the hair on shows it through.
        [Fact]
        public void ALookHangsEachItemWhereTheGameDoes()
        {
            var items = new Dictionary<int, Rig.Item>();
            void Add(Rig.Item it) => items[it.name.GetStableHashCode()] = it;
            Add(new Rig.Item { name = "HelmetBronze", type = ItemType.Helmet, attach = true, hideHair = HairType.HiddenHat });
            Add(new Rig.Item { name = "ArmorBronzeChest", type = ItemType.Chest, armor = true });
            Add(new Rig.Item { name = "SwordBronze", type = ItemType.OneHandedWeapon, attach = true });
            var hair = new Rig.Item { name = "Hair5", type = ItemType.Customization, attach = true };
            hair.hairFor[HairType.HiddenHat] = "Hair5_2";
            Add(hair);
            Add(new Rig.Item { name = "Hair5_2", type = ItemType.Customization, attach = true });
            int H(string n) => n.GetStableHashCode();
            var look = new Rig.Look { model = 1, helmet = H("HelmetBronze"), chest = H("ArmorBronzeChest"), right = H("SwordBronze"), hairItem = H("Hair5"), rightBack = H("SwordBronze") };

            var parts = Rig.Resolve(look, joints, 2, h => items.TryGetValue(h, out var it) ? it : null);
            Assert.Equal(new[] { "Player@body1", "SwordBronze@RightHand_Attach", "ArmorBronzeChest@armor", "HelmetBronze@Helmet_attach", "Hair5_2@Helmet_attach", "SwordBronze@BackOneHanded_attach" }, parts);
        }

        // Without joints, weights and inverse binds the body lies in its bind pose, a heap at the feet.
        [Fact]
        public void ASkinnedMeshCarriesItsSkinAndAStaticOneNone()
        {
            var w = new GlbWriter();
            int root = w.AddNode(new GlbWriter.Node { name = "Hips" });
            int arm = w.AddNode(new GlbWriter.Node { name = "Arm", parent = root, t = new[] { 0f, 1f, 0f } });
            var ibm = new float[32];
            for (int b = 0; b < 2; b++) for (int i = 0; i < 4; i++) ibm[b * 16 + i * 5] = 1f;
            ibm[16 + 13] = -1f;
            w.Skins.Add(new GlbWriter.Skin { joints = new[] { root, arm }, inverseBind = ibm });
            w.AddNode(new GlbWriter.Node { name = "body", mesh = 1, skin = 0 });
            w.AddNode(new GlbWriter.Node { name = "sword", parent = arm, mesh = 2 });
            float[] tri = { 0, 0, 0, 1, 0, 0, 0, 2, 0 };
            w.Add(new GlbWriter.Primitive { positions = tri, indices = new uint[] { 0, 1, 2 }, mesh = 1,
                joints = new ushort[] { 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0 }, weights = new[] { 1f, 0, 0, 0, 0.5f, 0.5f, 0, 0, 1f, 0, 0, 0 } });
            w.Add(new GlbWriter.Primitive { positions = tri, indices = new uint[] { 0, 1, 2 }, mesh = 2 });
            byte[] glb = w.Write("rig", out _);

            var gl = JsonDocument.Parse(Encoding.UTF8.GetString(glb, 20, BitConverter.ToInt32(glb, 12))).RootElement;
            var skin = gl.GetProperty("skins")[0];
            Assert.Equal(new[] { root, arm }, skin.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()));
            var inv = gl.GetProperty("accessors")[skin.GetProperty("inverseBindMatrices").GetInt32()];
            Assert.Equal("MAT4", inv.Str("type"));
            Assert.Equal(2, inv.Int("count"));
            var nodes = gl.GetProperty("nodes");
            var body = nodes.EnumerateArray().First(n => n.Str("name") == "body");
            var sword = nodes.EnumerateArray().First(n => n.Str("name") == "sword");
            Assert.Equal(0, body.Int("skin"));
            Assert.False(sword.TryGetProperty("skin", out _));
            var skinned = gl.GetProperty("meshes")[body.Int("mesh")].GetProperty("primitives")[0].GetProperty("attributes");
            var rigid = gl.GetProperty("meshes")[sword.Int("mesh")].GetProperty("primitives")[0].GetProperty("attributes");
            Assert.True(skinned.TryGetProperty("JOINTS_0", out _) && skinned.TryGetProperty("WEIGHTS_0", out _));
            Assert.False(rigid.TryGetProperty("JOINTS_0", out _) || rigid.TryGetProperty("WEIGHTS_0", out _));
            Assert.Contains(arm, nodes[root].GetProperty("children").EnumerateArray().Select(c => c.GetInt32()));
        }
    }
}
