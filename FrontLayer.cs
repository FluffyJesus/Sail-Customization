using UnityEngine;

namespace SailEmblems
{
    /// <summary>
    /// Front side only (Кэп, 1.1). The game's canvas shader draws both faces of the cloth with one texture, so the
    /// cloth wears the plain canvas and the painted one goes over it as the renderer's second material on a one-sided
    /// shader: Unity draws the mesh again for it, front faces only, at the same depth, moving with the simulated cloth.
    /// Which face is the front is the sail model's.
    ///  - whole sail: the game's Standard shader with our painted texture;
    ///  - torn sail (Sail Damage 1.0.3+): Sail Damage wears the plain canvas with its holes and hands over the painted one
    ///    with the same holes (FrontTexture), drawn blended on Legacy Transparent/Diffuse so the holes stay open (the
    ///    game has no one-sided cutout shader with a normal map).
    /// The layer is off while the sail is furled (it would show on the bunched-up cloth).
    /// </summary>
    internal sealed class FrontLayer
    {
        private static Shader standard;
        private static bool looked;

        /// <summary>The game's one-sided Standard shader; looked up once, warned about once if missing.</summary>
        internal static Shader Standard
        {
            get
            {
                if (looked) return standard;
                looked = true;
                standard = Shader.Find("Standard");
                if (standard == null) Plugin.Log.LogWarning("[Emblems] no Standard shader in this build: Front side only paints both sides");
                return standard;
            }
        }

        private Material whole, torn;
        private bool splitAsked; // Sail Damage was asked to keep a painted copy of its torn canvas

        /// <summary>After a painting laid out for the front side: the whole-sail layer shows the painted canvas.</summary>
        internal void Dress(PaintedCanvas pc)
        {
            if (whole == null) whole = new Material(Standard) { name = pc.Mat.name + " (front)" };
            whole.mainTexture = pc.Painted;
            whole.mainTextureScale = pc.Mat.mainTextureScale;
            whole.mainTextureOffset = pc.Mat.mainTextureOffset;
            whole.color = Color.white;
            if (whole.HasProperty("_Glossiness")) whole.SetFloat("_Glossiness", 0.1f); // canvas, not paint on metal
            if (whole.HasProperty("_Metallic")) whole.SetFloat("_Metallic", 0f);
            if (pc.Bump != null)
            {
                whole.SetTexture("_BumpMap", pc.Bump);
                whole.EnableKeyword("_NORMALMAP");
            }
            // after the canvas, so the paint lands on top where both are at the same depth
            whole.renderQueue = Mathf.Max(2000, pc.Mat.renderQueue) + 1;
        }

        /// <summary>Every frame for a painted sail: the right layer on, or none.</summary>
        internal void Sync(ClothSlot slot, Sail sail, EmblemState st, PaintedCanvas pc)
        {
            Material want = null;
            bool tornSail = SailDamageLink.Draws(sail);
            if (st.FrontOnly && !st.IsEmpty && !slot.Furled)
            {
                if (tornSail)
                    want = Torn(sail, pc);
                else if (pc.Front && pc.Mat != null && slot.OnCloth == pc.Mat)
                    want = whole;
            }
            else if (splitAsked && tornSail && (!st.FrontOnly || st.IsEmpty))
                AskSplit(sail, false);
            slot.SetLayer(want, whole, torn);
        }

        private Material Torn(Sail sail, PaintedCanvas pc)
        {
            if (!SailDamageLink.SetFrontOnly(sail, true)) return null; // its plain canvas not there yet: next frame
            splitAsked = true;
            Texture front = SailDamageLink.FrontTexture(sail, out Vector2 scale, out Vector2 offset);
            if (front == null) return null;
            if (torn == null)
            {
                Shader s = Shader.Find("Legacy Shaders/Transparent/Diffuse");
                if (s == null) return null;
                torn = new Material(s) { name = (pc.Mat != null ? pc.Mat.name : "SailEmblem") + " (front, torn)", color = Color.white };
            }
            if (torn.mainTexture != front) torn.mainTexture = front;
            torn.mainTextureScale = scale;
            torn.mainTextureOffset = offset;
            return torn;
        }

        private void AskSplit(Sail sail, bool on)
        {
            SailDamageLink.SetFrontOnly(sail, on);
            splitAsked = on;
        }

        /// <summary>Paint taken off: no layer, and Sail Damage needs no painted copy any more.</summary>
        internal void Remove(ClothSlot slot, Sail sail)
        {
            if (splitAsked) AskSplit(sail, false);
            slot?.SetLayer(null, whole, torn);
        }

        internal void Destroy()
        {
            if (whole != null) Object.Destroy(whole);
            if (torn != null) Object.Destroy(torn);
            whole = torn = null;
        }
    }
}
