namespace CupheadOnline.Sync
{
    /// <summary>
    /// Cuphead's loadout enums are large hashed IDs, not small sequential values.
    /// Network packets intentionally keep byte fields, so every loadout value must
    /// pass through this stable wire-code table instead of raw casts.
    /// </summary>
    public static class LoadoutCodec
    {
        public const byte NoneCode = 255;

        public static byte EncodeWeapon(Weapon weapon)
        {
            switch (weapon)
            {
                case Weapon.level_weapon_peashot: return 1;
                case Weapon.level_weapon_spreadshot: return 2;
                case Weapon.level_weapon_arc: return 3;
                case Weapon.level_weapon_homing: return 4;
                case Weapon.level_weapon_exploder: return 5;
                case Weapon.level_weapon_boomerang: return 6;
                case Weapon.level_weapon_charge: return 7;
                case Weapon.level_weapon_bouncer: return 8;
                case Weapon.level_weapon_wide_shot: return 9;
                case Weapon.level_weapon_accuracy: return 10;
                case Weapon.level_weapon_firecracker: return 11;
                case Weapon.level_weapon_upshot: return 12;
                case Weapon.level_weapon_firecrackerB: return 13;
                case Weapon.level_weapon_pushback: return 14;
                case Weapon.level_weapon_crackshot: return 15;
                case Weapon.level_weapon_splitter: return 16;
                case Weapon.None: return NoneCode;
                default: return NoneCode;
            }
        }

        public static Weapon DecodeWeapon(byte code, bool primarySlot)
        {
            switch (code)
            {
                case 1: return Weapon.level_weapon_peashot;
                case 2: return Weapon.level_weapon_spreadshot;
                case 3: return Weapon.level_weapon_arc;
                case 4: return Weapon.level_weapon_homing;
                case 5: return Weapon.level_weapon_exploder;
                case 6: return Weapon.level_weapon_boomerang;
                case 7: return Weapon.level_weapon_charge;
                case 8: return Weapon.level_weapon_bouncer;
                case 9: return Weapon.level_weapon_wide_shot;
                case 10: return Weapon.level_weapon_accuracy;
                case 11: return Weapon.level_weapon_firecracker;
                case 12: return Weapon.level_weapon_upshot;
                case 13: return Weapon.level_weapon_firecrackerB;
                case 14: return Weapon.level_weapon_pushback;
                case 15: return Weapon.level_weapon_crackshot;
                case 16: return Weapon.level_weapon_splitter;
                case NoneCode: return primarySlot ? Weapon.level_weapon_peashot : Weapon.None;
                default: return primarySlot ? Weapon.level_weapon_peashot : Weapon.None;
            }
        }

        public static Weapon NormalizeWeapon(Weapon weapon, bool primarySlot)
        {
            byte encoded = EncodeWeapon(weapon);
            if (encoded != NoneCode)
                return weapon;

            return primarySlot ? Weapon.level_weapon_peashot : Weapon.None;
        }

        public static byte EncodeSuper(Super super)
        {
            switch (super)
            {
                case Super.level_super_beam: return 1;
                case Super.level_super_ghost: return 2;
                case Super.level_super_invincible: return 3;
                case Super.level_super_chalice_iii: return 4;
                case Super.level_super_chalice_vert_beam: return 5;
                case Super.level_super_chalice_shield: return 6;
                case Super.level_super_chalice_bounce: return 7;
                case Super.plane_super_bomb: return 8;
                case Super.plane_super_chalice_bomb: return 9;
                case Super.None: return NoneCode;
                default: return NoneCode;
            }
        }

        public static Super DecodeSuper(byte code)
        {
            switch (code)
            {
                case 1: return Super.level_super_beam;
                case 2: return Super.level_super_ghost;
                case 3: return Super.level_super_invincible;
                case 4: return Super.level_super_chalice_iii;
                case 5: return Super.level_super_chalice_vert_beam;
                case 6: return Super.level_super_chalice_shield;
                case 7: return Super.level_super_chalice_bounce;
                case 8: return Super.plane_super_bomb;
                case 9: return Super.plane_super_chalice_bomb;
                default: return Super.None;
            }
        }

        public static byte EncodeCharm(Charm charm)
        {
            switch (charm)
            {
                case Charm.charm_health_up_1: return 1;
                case Charm.charm_health_up_2: return 2;
                case Charm.charm_super_builder: return 3;
                case Charm.charm_smoke_dash: return 4;
                case Charm.charm_parry_plus: return 5;
                case Charm.charm_pit_saver: return 6;
                case Charm.charm_parry_attack: return 7;
                case Charm.charm_chalice: return 8;
                case Charm.charm_directional_dash: return 9;
                case Charm.charm_healer: return 10;
                case Charm.charm_EX: return 11;
                case Charm.charm_curse: return 12;
                case Charm.charm_float: return 13;
                case Charm.None: return NoneCode;
                default: return NoneCode;
            }
        }

        public static Charm DecodeCharm(byte code)
        {
            switch (code)
            {
                case 1: return Charm.charm_health_up_1;
                case 2: return Charm.charm_health_up_2;
                case 3: return Charm.charm_super_builder;
                case 4: return Charm.charm_smoke_dash;
                case 5: return Charm.charm_parry_plus;
                case 6: return Charm.charm_pit_saver;
                case 7: return Charm.charm_parry_attack;
                case 8: return Charm.charm_chalice;
                case 9: return Charm.charm_directional_dash;
                case 10: return Charm.charm_healer;
                case 11: return Charm.charm_EX;
                case 12: return Charm.charm_curse;
                case 13: return Charm.charm_float;
                default: return Charm.None;
            }
        }
    }
}
