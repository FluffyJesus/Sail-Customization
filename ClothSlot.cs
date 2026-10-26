using System;
using HarmonyLib;
using UnityEngine;

namespace SailEmblems
{
    /// <summary>What a sail's cloth wears right now, as seen from Sail Emblems.</summary>
    internal enum Wearing
    {
        Nothing,        // no material at all (rare, at creation)
        Placeholder,    // the game's furl placeholder (Refs.emptyMaterial or a copy)
        Ours,           // our material
        OurCopy,        // a copy of ours, same texture: furling (renderer.material) or recolouring made it
        OursRetextured, // a copy of ours with another texture put in: Shipyard Expansion's sail textures
        PaintedTwin,    // what we handed Sail Damage, put back by it after a repair as new
        SailDamage,     // Sail Damage's torn canvas (made from ours)
        Foreign,        // anything else: the game's canvas, or another mod's material
    }

    /// <summary>
    /// The cloth's material slot. While the sail is furled the game keeps the real canvas in ReefEffectAnimUniversal's
    /// private unfurledMaterial and shows a placeholder (game-code notes); Worn reads and writes whichever holds it.
    /// Also the renderer's second material, the one-sided Front side only layer.
    /// </summary>
    internal sealed class ClothSlot
    {
        private static readonly AccessTools.FieldRef<ReefEffectAnimUniversal, Material> UnfurledMaterial =
            AccessTools.FieldRefAccess<ReefEffectAnimUniversal, Material>("unfurledMaterial");
        private static readonly AccessTools.FieldRef<ReefEffectAnimUniversal, bool> IsFurled =
            AccessTools.FieldRefAccess<ReefEffectAnimUniversal, bool>("isFurled");

        internal readonly SkinnedMeshRenderer Renderer;
        private readonly ReefEffectAnimUniversal reef;

        private ClothSlot(SkinnedMeshRenderer r, ReefEffectAnimUniversal reef)
        {
            Renderer = r;
            this.reef = reef;
        }

        internal static ClothSlot Of(Sail sail)
        {
            SkinnedMeshRenderer r = sail.cloth != null ? sail.cloth.GetComponent<SkinnedMeshRenderer>() : sail.GetComponentInChildren<SkinnedMeshRenderer>();
            return r != null ? new ClothSlot(r, sail.GetComponent<ReefEffectAnimUniversal>()) : null;
        }

        internal bool Furled => reef != null && IsFurled(reef);

        /// <summary>The sail's canvas: on the cloth, or saved away while furled.</summary>
        internal Material Worn
        {
            get => Furled ? UnfurledMaterial(reef) : Renderer.sharedMaterial;
            set
            {
                if (Furled) UnfurledMaterial(reef) = value;
                else Renderer.sharedMaterial = value;
            }
        }

        /// <summary>On the cloth itself (the placeholder while furled).</summary>
        internal Material OnCloth => Renderer.sharedMaterial;

        // The furl placeholder, or a copy of it (Furl saves renderer.material, an "(Instance)" copy).
        internal static bool IsPlaceholder(Material m)
        {
            Material empty = Refs.emptyMaterial;
            return m != null && empty != null && (m == empty || m.name.StartsWith(empty.name, StringComparison.Ordinal));
        }

        internal static Wearing Classify(Material m, Material ours, Material twin, bool sailDamageDraws)
        {
            if (m == null) return Wearing.Nothing;
            if (ours != null && m == ours) return Wearing.Ours;
            if (twin != null && m == twin) return Wearing.PaintedTwin;
            if (ours != null && m.mainTexture != null && m.mainTexture == ours.mainTexture) return Wearing.OurCopy;
            if (sailDamageDraws) return Wearing.SailDamage;
            // made from ours ("... (emblem) (Instance)"), whatever texture it has now
            if (ours != null && m.name.StartsWith(ours.name, StringComparison.Ordinal)) return Wearing.OursRetextured;
            if (IsPlaceholder(m)) return Wearing.Placeholder;
            return Wearing.Foreign;
        }

        /// <summary>
        /// The renderer's second material: Unity draws a mesh again with each extra material, so a one-sided layer there
        /// moves with the simulated cloth. Null takes our layers (<paramref name="a"/>, <paramref name="b"/>) off.
        /// </summary>
        internal void SetLayer(Material layer, Material a, Material b)
        {
            if (layer == null && a == null && b == null) return;
            Material[] mats = Renderer.sharedMaterials;
            if (layer != null)
            {
                if (mats.Length != 2 || mats[1] != layer)
                    Renderer.sharedMaterials = new[] { mats.Length > 0 ? mats[0] : null, layer };
            }
            else if (mats.Length > 1 && (Array.IndexOf(mats, a) >= 0 || Array.IndexOf(mats, b) >= 0))
                Renderer.sharedMaterials = Array.FindAll(mats, m => m != a && m != b);
        }
    }
}
