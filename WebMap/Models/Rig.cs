using System;
using System.Collections.Generic;
using UnityEngine;
using WebMap.Util;
using ItemType = ItemDrop.ItemData.ItemType;
using HairType = ItemDrop.ItemData.HelmetHairType;

namespace WebMap.Models
{
    // A live player as every game draws them: the look their ZDO syncs (a body, two colours,
    // a prefab hash per slot) resolved the way VisEquipment attaches it, into the library
    // parts the 3D view stands together. A part's name says how it hangs on the body:
    //   Player@body<i>    body model i with the skeleton, in the standing pose
    //   <item>@armor      an armour's attach_ children, skinned or on their bones, and its body paint
    //   <item>@<joint>    an item's attach child on that joint (a back joint takes attach_back)
    internal static class Rig
    {
        public const string Body = "Player@body", Armor = "@armor", Cat = "rig";

        // VisEquipment's attach points, by the transforms' own names
        public sealed class Joints { public string right, left, helmet, backShield, backMelee, backTwoHanded, backBow, backTool, backAtgeir; }

        // what the resolution needs of an item prefab
        public sealed class Item
        {
            public string name;
            public ItemType type, attachOverride;
            public HairType hideHair, hideBeard;
            public bool attach, armor;     // an attach or attach_skin child; any attach_ child or a body paint
            public Dictionary<HairType, string> hairFor = new Dictionary<HairType, string>(), beardFor = new Dictionary<HairType, string>();
        }

        public sealed class Look
        {
            public int model; public Vector3 skin = Vector3.one, hair = Vector3.one;
            public int helmet, chest, legs, shoulder, utility, trinket, right, left, rightBack, leftBack, hairItem, beard;
        }

        public static Look FromZdo(ZDO z) => new Look
        {
            model = z.GetInt(ZDOVars.s_modelIndex),
            skin = z.GetVec3(ZDOVars.s_skinColor, Vector3.one), hair = z.GetVec3(ZDOVars.s_hairColor, Vector3.one),
            helmet = z.GetInt(ZDOVars.s_helmetItem), chest = z.GetInt(ZDOVars.s_chestItem), legs = z.GetInt(ZDOVars.s_legItem),
            shoulder = z.GetInt(ZDOVars.s_shoulderItem), utility = z.GetInt(ZDOVars.s_utilityItem), trinket = z.GetInt(ZDOVars.s_trinketItem),
            right = z.GetInt(ZDOVars.s_rightItem), left = z.GetInt(ZDOVars.s_leftItem),
            rightBack = z.GetInt(ZDOVars.s_rightBackItem), leftBack = z.GetInt(ZDOVars.s_leftBackItem),
            hairItem = z.GetInt(ZDOVars.s_hairItem), beard = z.GetInt(ZDOVars.s_beardItem),
        };

        // VisEquipment.UpdateEquipmentVisuals: the parts to draw, body first. items: null for a hash the game lacks.
        public static List<string> Resolve(Look look, Joints j, int models, Func<int, Item> items)
        {
            var parts = new List<string> { Body + Mathf.Clamp(look.model, 0, Math.Max(0, models - 1)) };
            Item Get(int h) => h == 0 ? null : items(h);
            void Hang(int h, string joint) { var it = Get(h); if (it != null && it.attach && joint != null) parts.Add(it.name + "@" + joint); }
            void Wear(int h) { var it = Get(h); if (it != null && it.armor) parts.Add(it.name + Armor); }

            Hang(look.right, j.right);
            Hang(look.left, j.left);
            Wear(look.chest);
            Wear(look.legs);
            var helmet = Get(look.helmet);
            Hang(look.helmet, j.helmet);
            Wear(look.shoulder);
            Wear(look.utility);
            Wear(look.trinket);
            // a helmet hides the hair and beard, or swaps them for the cut the hair names for it
            Hang(Under(Get(look.beard), helmet?.hideBeard ?? HairType.Default, false), j.helmet);
            Hang(Under(Get(look.hairItem), helmet?.hideHair ?? HairType.Default, true), j.helmet);
            Hang(look.leftBack, Back(Get(look.leftBack), j, false));
            Hang(look.rightBack, Back(Get(look.rightBack), j, true));
            return parts;

            int Under(Item h, HairType hide, bool hair)
            {
                if (h == null || hide == HairType.Hidden) return 0;
                if (hide == HairType.Default) return h.name.GetStableHashCode();
                return (hair ? h.hairFor : h.beardFor).TryGetValue(hide, out string cut) ? cut.GetStableHashCode() : 0;
            }
        }

        // VisEquipment.AttachBackItem: where a sheathed item hangs
        private static string Back(Item it, Joints j, bool right)
        {
            if (it == null) return null;
            switch (it.attachOverride != ItemType.None ? it.attachOverride : it.type)
            {
                case ItemType.Torch: return right ? j.backMelee : j.backTool;
                case ItemType.Bow: return j.backBow;
                case ItemType.Tool: return j.backTool;
                case ItemType.Attach_Atgeir: return j.backAtgeir;
                case ItemType.OneHandedWeapon: return j.backMelee;
                case ItemType.TwoHandedWeapon: case ItemType.TwoHandedWeaponLeft: return j.backTwoHanded;
                case ItemType.Shield: return j.backShield;
                default: return null;
            }
        }

        public static bool IsBackJoint(Joints j, string joint) =>
            joint == j.backShield || joint == j.backMelee || joint == j.backTwoHanded || joint == j.backBow || joint == j.backTool || joint == j.backAtgeir;

        // /state's players[].look: the body, the colours, what each slot holds, the parts to draw
        public static void WriteJson(JsonWriter w, Look look, List<string> parts, Func<int, Item> items)
        {
            w.BeginObject().Prop("model", look.model);
            w.Key("skin").BeginArray().Value(look.skin.x, 3).Value(look.skin.y, 3).Value(look.skin.z, 3).End();
            w.Key("hair").BeginArray().Value(look.hair.x, 3).Value(look.hair.y, 3).Value(look.hair.z, 3).End();
            w.Key("slots").BeginObject();
            void Slot(string k, int h) { var it = h == 0 ? null : items(h); if (it != null) w.Prop(k, it.name); }
            Slot("helmet", look.helmet); Slot("chest", look.chest); Slot("legs", look.legs); Slot("shoulder", look.shoulder);
            Slot("utility", look.utility); Slot("trinket", look.trinket); Slot("right", look.right); Slot("left", look.left);
            Slot("rightBack", look.rightBack); Slot("leftBack", look.leftBack); Slot("hair", look.hairItem); Slot("beard", look.beard);
            w.End().Key("parts").BeginArray();
            foreach (var p in parts) w.Value(p);
            w.End().End();
        }

        // ObjectDB's item as the resolution reads it; game thread
        public static Item Of(GameObject prefab)
        {
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) return null;
            var sh = drop.m_itemData.m_shared;
            var it = new Item { name = prefab.name, type = sh.m_itemType, attachOverride = sh.m_attachOverride, hideHair = sh.m_helmetHideHair, hideBeard = sh.m_helmetHideBeard, armor = sh.m_armorMaterial != null };
            foreach (Transform c in prefab.transform)
            {
                if (c.name == "attach" || c.name == "attach_skin") it.attach = true;
                if (c.name.StartsWith("attach_", StringComparison.Ordinal)) it.armor = true;
            }
            foreach (var s in sh.m_helmetHairSettings) if (s != null && s.m_hairPrefab != null) it.hairFor[s.m_setting] = s.m_hairPrefab.name;
            foreach (var s in sh.m_helmetBeardSettings) if (s != null && s.m_hairPrefab != null) it.beardFor[s.m_setting] = s.m_hairPrefab.name;
            return it;
        }
    }
}
