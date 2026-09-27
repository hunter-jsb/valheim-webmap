using System;
using System.Collections.Generic;
using UnityEngine;
using ItemType = ItemDrop.ItemData.ItemType;
using Anim = ItemDrop.ItemData.AnimationState;
using Skill = Skills.SkillType;

namespace WebMap
{
    // What a player holds and wears, read off the ZDO every client draws them
    // from, and sorted by the item's own data so a later patch's gear sorts
    // itself; and the hits on creatures the server forwards, by kind.
    //
    // Game thread only: the snapshot and RouteRPC both run there.
    internal static class Gear
    {
        // Knife through Polearm are the melee weapons, kept together
        public enum Hand { None, Bow, Crossbow, Staff, Shield, Knife, Sword, Axe, Mace, Spear, Polearm, Hammer, Hoe, Cultivator, Pickaxe, Torch, Fishing, Tool, TwoHanded }
        public enum Armor { None, Light, Medium, Heavy, Mage }
        public enum Hit { Melee, Ranged, Magic, Backstab }
        public enum Diet { None, Hearty, Quick, Eitr, Balanced }
        public static readonly string[] HandNames = Lower(typeof(Hand)), ArmorNames = Lower(typeof(Armor)), HitNames = Lower(typeof(Hit)), DietNames = Lower(typeof(Diet));
        public static readonly string[] Slots = { "right", "left", "chest", "legs", "helmet", "shoulder", "rightBack", "leftBack" };
        private static string[] Lower(Type e) => Array.ConvertAll(Enum.GetNames(e), n => n.ToLowerInvariant());

        // Chest movement penalties: leather, troll, lox, fenring, Askvin none; root 2%;
        // bronze, iron, wolf, padded, carapace, flametal 5%.
        private const float LightMove = -0.01f, MediumMove = -0.035f;

        public static Hand Family(ItemType type, Skill skill, Anim anim, bool builds, float damage)
        {
            if (type == ItemType.Shield) return Hand.Shield;
            if (type == ItemType.Torch) return Hand.Torch;
            switch (skill)
            {
                case Skill.Bows: return Hand.Bow;
                case Skill.Crossbows: return Hand.Crossbow;
                case Skill.ElementalMagic: case Skill.BloodMagic: return Hand.Staff;
                case Skill.Pickaxes: return Hand.Pickaxe;
                case Skill.Fishing: return Hand.Fishing;
                case Skill.Farming: return Hand.Cultivator;   // the scythe too
                case Skill.Crafting: return Hand.Hammer;
                case Skill.Unarmed: return Hand.None;         // fist weapons: the game's own unarmed
            }
            if (anim == Anim.FishingRod) return Hand.Fishing;  // the rod and the hoe have no skill of their own
            if (type == ItemType.Tool) return builds && anim != Anim.Feaster ? Hand.Hoe : Hand.Tool;
            if (damage <= 0f) return Hand.Tool;                // a tankard is a sword that cannot hurt
            switch (skill)
            {
                case Skill.Knives: return Hand.Knife;
                case Skill.Swords: return Hand.Sword;
                case Skill.Axes: return Hand.Axe;
                case Skill.Clubs: return Hand.Mace;
                case Skill.Spears: return Hand.Spear;
                case Skill.Polearms: return Hand.Polearm;
            }
            return Hand.Tool;
        }

        // pickaxes and the fishing rod are two-handed too, but no weapon
        public static bool TwoHanded(ItemType type, Hand family)
            => (type == ItemType.TwoHandedWeapon || type == ItemType.TwoHandedWeaponLeft) && family >= Hand.Knife && family <= Hand.Polearm;

        public static Armor ArmorOf(float movement, float eitrRegen)
            => eitrRegen > 0f ? Armor.Mage : movement >= LightMove ? Armor.Light : movement >= MediumMove ? Armor.Medium : Armor.Heavy;

        // The bonus is the weapon's (3x on most, 6x on knives), sent with every hit;
        // the game applies it only to a creature not yet alerted.
        public static void Struck(string player, bool ranged, Skill skill, float backstabBonus, bool unaware)
        {
            var kind = skill == Skill.ElementalMagic || skill == Skill.BloodMagic ? Hit.Magic : ranged ? Hit.Ranged : Hit.Melee;
            Stats.Struck(player, kind, backstabBonus > 1f && unaware);
        }

        // A hit the server forwards: on a creature only, not a tree, a rock, a piece or a player.
        public static void Struck(string player, ZDOID target, HitData hit)
        {
            ZDO t = ZDOMan.instance.GetZDO(target);
            if (t == null || DeedsGonePatch.KindOf(t.GetPrefab()) != (int)Deeds.Kind.Kill) return;
            Struck(player, hit.m_ranged, hit.m_skill, hit.m_backstabBonus, !t.GetBool(ZDOVars.s_alert));
        }

        private struct Item { public string name; public Hand hand; public bool twoHanded; public Armor armor; }
        private static readonly Dictionary<int, Item> items = new Dictionary<int, Item>();
        private static bool warned;

        // An item's prefab hash, as VisEquipment syncs it, sorted once.
        private static Item Of(int hash)
        {
            if (items.TryGetValue(hash, out var it)) return it;
            if (ZNetScene.instance == null) return new Item { hand = Hand.Tool, armor = Armor.Light };
            GameObject go = ZNetScene.instance.GetPrefab(hash);
            ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
            it = new Item { name = go != null ? go.name : null, hand = Hand.Tool, armor = Armor.Light };   // no item data: held, no penalty
            if (drop != null)
            {
                var s = drop.m_itemData.m_shared;
                it.hand = Family(s.m_itemType, s.m_skillType, s.m_animationState, s.m_buildPieces != null, s.m_damages.GetTotalDamage());
                it.twoHanded = TwoHanded(s.m_itemType, it.hand);
                it.armor = ArmorOf(s.m_movementModifier, s.m_eitrRegenModifier);
            }
            items[hash] = it;
            return it;
        }
        private static string Name(int hash) => hash != 0 ? Of(hash).name : null;

        // A player's food never reaches the server; the health, stamina and eitr it adds do.
        // max_health is synced, and stamina and eitr refill to their food-borne maxima whenever
        // the player stands still, so a slowly fading peak of each stands in for the maximum.
        private const float BaseHp = 25f, BaseStamina = 75f, PeakFadePerSecond = 0.5f;
        private struct Peak { public float st, eitr; }
        private static readonly Dictionary<string, Peak> peaks = new Dictionary<string, Peak>();

        // One eitr food is a choice; otherwise health against stamina, half again as much to lean.
        public static Diet DietOf(float hp, float st, float eitr)
        {
            if (hp + st + eitr < 30f) return Diet.None;
            if (eitr >= 40f) return Diet.Eitr;
            if (hp >= st * 1.5f) return Diet.Hearty;
            if (st >= hp * 1.5f) return Diet.Quick;
            return Diet.Balanced;
        }

        // Once per online player per snapshot; nothing while dead or in bed.
        public static void Sample(string player, ZDO z, float seconds)
        {
            try
            {
                if (z.GetBool(ZDOVars.s_dead) || z.GetBool(ZDOVars.s_inBed)) return;
                int r = z.GetInt(ZDOVars.s_rightItem), l = z.GetInt(ZDOVars.s_leftItem), c = z.GetInt(ZDOVars.s_chestItem);
                Item ri = r != 0 ? Of(r) : default, li = l != 0 ? Of(l) : default, ci = c != 0 ? Of(c) : default;
                var worn = new[]
                {
                    r != 0 ? ri.name : Name(z.GetInt(ZDOVars.s_rightBackItem)),   // a sheathed weapon is still carried
                    l != 0 ? li.name : Name(z.GetInt(ZDOVars.s_leftBackItem)),
                    ci.name, Name(z.GetInt(ZDOVars.s_legItem)), Name(z.GetInt(ZDOVars.s_helmetItem)), Name(z.GetInt(ZDOVars.s_shoulderItem)),
                    // the back slots on their own too: a class reads the weapons carried while a hammer is in hand
                    Name(z.GetInt(ZDOVars.s_rightBackItem)), Name(z.GetInt(ZDOVars.s_leftBackItem)),
                };
                Stats.Wore(player, ri.hand, li.hand, ri.twoHanded || li.twoHanded, ci.armor, worn, seconds);
                float hp = Math.Max(0f, z.GetFloat(ZDOVars.s_maxHealth, BaseHp) - BaseHp);
                peaks.TryGetValue(player, out Peak pk);
                pk.st = Math.Max(z.GetFloat(ZDOVars.s_stamina, 0f), pk.st - PeakFadePerSecond * seconds);
                pk.eitr = Math.Max(z.GetFloat(ZDOVars.s_eitr, 0f), pk.eitr - PeakFadePerSecond * seconds);
                peaks[player] = pk;
                float st = Math.Max(0f, pk.st - BaseStamina);
                Stats.Fed(player, DietOf(hp, st, pk.eitr), hp, st, pk.eitr, seconds);
            }
            catch (Exception e)
            {
                if (!warned) { warned = true; ZLog.LogWarning("WebMap: gear: reading a player's equipment failed: " + e.Message); }
            }
        }
    }
}
