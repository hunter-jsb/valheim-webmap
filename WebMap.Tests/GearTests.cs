using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;
using ItemType = ItemDrop.ItemData.ItemType;
using Anim = ItemDrop.ItemData.AnimationState;
using Skill = Skills.SkillType;

namespace WebMap.Tests
{
    // What the snapshot hands Stats a second at a time, and the hits the route hook hands Gear.
    public class GearTests : WithDir
    {
        const string A = "Withers";

        public GearTests() { Stats.Load(Dir); }

        static JsonElement Of(string part) => J.Parse(Stats.Json(new Dictionary<string, int>())).GetProperty("players")
            .EnumerateArray().First(p => p.Str("name") == A).GetProperty("gear").GetProperty(part);
        static string[] Set(string right, string left, string chest) => new[] { right, left, chest, null, null, null };

        [Fact]
        public void AnItemSortsByItsOwnData()
        {
            var hands = new (ItemType type, Skill skill, Anim anim, bool builds, float dmg, Gear.Hand want)[]
            {
                (ItemType.Bow, Skill.Crossbows, Anim.Crossbow, false, 300, Gear.Hand.Crossbow),
                (ItemType.TwoHandedWeapon, Skill.Axes, Anim.TwoHandedAxe, false, 110, Gear.Hand.Axe),
                (ItemType.TwoHandedWeaponLeft, Skill.BloodMagic, Anim.MagicItem, false, 0, Gear.Hand.Staff),
                (ItemType.Tool, Skill.Crafting, Anim.OneHanded, true, 0, Gear.Hand.Hammer),
                (ItemType.Tool, Skill.None, Anim.Atgeir, true, 0, Gear.Hand.Hoe),
                (ItemType.TwoHandedWeapon, Skill.None, Anim.FishingRod, false, 5, Gear.Hand.Fishing),
                (ItemType.Torch, Skill.Clubs, Anim.Torch, false, 19, Gear.Hand.Torch),
                (ItemType.OneHandedWeapon, Skill.Swords, Anim.Torch, false, 0, Gear.Hand.Tool),     // a tankard
            };
            foreach (var h in hands) Assert.Equal(h.want, Gear.Family(h.type, h.skill, h.anim, h.builds, h.dmg));
            Assert.True(Gear.TwoHanded(ItemType.TwoHandedWeapon, Gear.Hand.Axe));
            Assert.False(Gear.TwoHanded(ItemType.TwoHandedWeapon, Gear.Hand.Pickaxe));
            Assert.False(Gear.TwoHanded(ItemType.TwoHandedWeaponLeft, Gear.Hand.Staff));

            var chests = new (float move, float eitr, Gear.Armor want)[]
                { (0.03f, 0, Gear.Armor.Light), (0, 0, Gear.Armor.Light), (-0.02f, 0, Gear.Armor.Medium), (-0.05f, 0, Gear.Armor.Heavy), (-0.02f, 0.4f, Gear.Armor.Mage) };
            foreach (var c in chests) Assert.Equal(c.want, Gear.ArmorOf(c.move, c.eitr));
        }

        [Fact]
        public void ASecondWithABowAndLightArmourAddsToThoseAlone()
        {
            Stats.Wore(A, Gear.Hand.None, Gear.Hand.Bow, false, Gear.Armor.Light, Set(null, "BowFineWood", "ArmorLeatherChest"), 1f);
            Assert.Equal("{\"bow\":1}", Of("hand").GetRawText());
            Assert.Equal("{\"light\":1}", Of("armor").GetRawText());
        }

        [Fact]
        public void TheWornSetIsTheLatestSeen()
        {
            Stats.Wore(A, Gear.Hand.Sword, Gear.Hand.Shield, false, Gear.Armor.Heavy,
                       new[] { "SwordIron", "ShieldBanded", "ArmorIronChest", "ArmorIronLegs", "HelmetIron", null }, 1f);
            Stats.Wore(A, Gear.Hand.None, Gear.Hand.None, false, Gear.Armor.Light, Set(null, null, "ArmorLeatherChest"), 1f);
            Assert.Equal("{\"chest\":\"ArmorLeatherChest\"}", Of("worn").GetRawText());
            Assert.Equal(1, Of("hand").Int("none"));
        }

        [Fact]
        public void AHitCountsByKindAndABackstabOnlyOnACreatureUnaware()
        {
            Gear.Struck(A, true, Skill.Bows, 3f, false);             // an arrow
            Gear.Struck(A, true, Skill.ElementalMagic, 1f, false);   // a fireball is magic, not ranged
            Gear.Struck(A, false, Skill.Knives, 6f, true);           // a knife in an unaware back: melee and a backstab
            Gear.Struck(A, false, Skill.Swords, 3f, false);          // the weapon's bonus alone is none
            Assert.Equal("{\"melee\":2,\"ranged\":1,\"magic\":1,\"backstab\":1}", Of("hits").GetRawText());
        }
    }
}
