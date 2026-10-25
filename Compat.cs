using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailEmblems
{
    /// <summary>
    /// Sail Damage (Кэп's mod, optional). On a damaged sail it wears its own torn copy of the canvas it found on the
    /// cloth, i.e. of ours. From 1.0.2 it takes a new canvas through SailVisuals.TakeCanvas(Sail, Material, Texture)
    /// (painted canvas, and the same without the emblem for the studding sails) and says whether it draws the sail
    /// through SailVisuals.Draws(Sail). Not referenced, found by reflection: older versions just lack the link, and
    /// then the two mods would swap materials, so we keep off sails it draws.
    /// </summary>
    internal static class SailDamageLink
    {
        private static bool tried;
        private static Func<Sail, Material, Texture, bool> take;
        private static Func<Sail, bool> draws;
        private static Func<Sail, bool, bool> setFrontOnly;
        private delegate Texture FrontTextureFn(Sail sail, out Vector2 scale, out Vector2 offset);
        private static FrontTextureFn frontTexture;
        private static bool old;

        private static void Init()
        {
            if (tried) return;
            tried = true;
            Type t = AccessTools.TypeByName("SailDamage.SailVisuals");
            if (t == null) return;
            MethodInfo mt = AccessTools.Method(t, "TakeCanvas", new[] { typeof(Sail), typeof(Material), typeof(Texture) });
            MethodInfo md = AccessTools.Method(t, "Draws", new[] { typeof(Sail) });
            if (mt == null || md == null || !mt.IsStatic || !md.IsStatic)
            {
                old = true;
                Plugin.Log.LogWarning("[Emblems] Sail Damage is older than 1.0.2: damaged sails keep the emblem they had when they were first torn");
                return;
            }
            take = (Func<Sail, Material, Texture, bool>)Delegate.CreateDelegate(typeof(Func<Sail, Material, Texture, bool>), mt);
            draws = (Func<Sail, bool>)Delegate.CreateDelegate(typeof(Func<Sail, bool>), md);
            // 1.0.3: Front side only on torn sails
            MethodInfo mf = AccessTools.Method(t, "SetFrontOnly", new[] { typeof(Sail), typeof(bool) });
            MethodInfo mx = AccessTools.Method(t, "FrontTexture");
            if (mf != null && mx != null && mf.IsStatic && mx.IsStatic)
            {
                setFrontOnly = (Func<Sail, bool, bool>)Delegate.CreateDelegate(typeof(Func<Sail, bool, bool>), mf);
                frontTexture = (FrontTextureFn)Delegate.CreateDelegate(typeof(FrontTextureFn), mx);
            }
            Plugin.Debug("[Emblems] Sail Damage found: torn sails keep their emblems");
        }

        /// <summary>Sail Damage 1.0.2+ is there and takes our canvases.</summary>
        internal static bool Present
        {
            get
            {
                Init();
                return take != null;
            }
        }

        /// <summary>Sail Damage wears its torn canvas on this sail.</summary>
        internal static bool Draws(Sail sail)
        {
            Init();
            if (draws != null) return draws(sail);
            if (!old) return false;
            // Older Sail Damage: its torn material is named "... (torn)".
            Renderer r = sail.cloth != null ? sail.cloth.GetComponent<Renderer>() : null;
            Material m = r != null ? r.sharedMaterial : null;
            return m != null && m.name.IndexOf(" (torn)", StringComparison.Ordinal) >= 0;
        }

        /// <summary>Asks Sail Damage to draw the holes into the plain canvas too (Front side only); true if it does.</summary>
        internal static bool SetFrontOnly(Sail sail, bool on)
        {
            Init();
            return setFrontOnly != null && setFrontOnly(sail, on);
        }

        /// <summary>The painted canvas with Sail Damage's holes, for the one-sided layer; null if not split.</summary>
        internal static Texture FrontTexture(Sail sail, out Vector2 scale, out Vector2 offset)
        {
            scale = Vector2.one;
            offset = Vector2.zero;
            return frontTexture != null ? frontTexture(sail, out scale, out offset) : null;
        }

        /// <summary>Hands Sail Damage the new canvas; true if it draws this sail (and now wears its torn copy of it).</summary>
        internal static bool TakeCanvas(Sail sail, Material painted, Texture clean)
        {
            Init();
            if (take != null) return take(sail, painted, clean);
            return old && Draws(sail);
        }
    }

    /// <summary>
    /// Studding Sails (optional): StuddingRig.SyncColor copies the parent sail's material, in the parent's UV layout,
    /// onto the studding canvas, which showed a strip of the emblem. Our postfix gives that copy the clean canvas.
    /// Same hook as Sail Damage's StuddingCompat; it gives its own torn studding canvas to sails it draws.
    /// </summary>
    internal static class StuddingLink
    {
        internal static bool Present { get; private set; }
        private static FieldInfo sailField, clothMat;

        internal static void Init(Harmony harmony)
        {
            Type rig = AccessTools.TypeByName("StuddingSails.StuddingRig");
            if (rig == null) return;
            MethodInfo sync = AccessTools.Method(rig, "SyncColor");
            sailField = AccessTools.Field(rig, "sail");
            clothMat = AccessTools.Field(rig, "clothMat");
            if (sync == null || sailField == null || clothMat == null)
            {
                Plugin.Log.LogWarning("[Emblems] Studding Sails found, but not its SyncColor/sail/clothMat: studding sails may show part of the emblem");
                return;
            }
            harmony.Patch(sync, postfix: new HarmonyMethod(typeof(StuddingLink), nameof(SyncColorPostfix)));
            Present = true;
            Plugin.Debug("[Emblems] Studding Sails found: studding sails get the plain canvas");
        }

        private static void SyncColorPostfix(object __instance)
        {
            try
            {
                var m = clothMat.GetValue(__instance) as Material;
                var e = SailEmblem.Of(sailField.GetValue(__instance) as Sail, false);
                if (m == null || e == null || e.PaintedTexture == null || m.mainTexture != e.PaintedTexture) return;
                if (e.CleanTexture != null)
                    m.mainTexture = e.CleanTexture; // same layout, scale and offset as the painted one
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Emblems] studding canvas: " + ex);
            }
        }
    }
}
