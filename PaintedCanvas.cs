using UnityEngine;

namespace SailEmblems
{
    /// <summary>
    /// What a painting leaves on the sail: our copy of the game's canvas material with the painted texture (or, with
    /// Front side only, the plain one: the paint then goes on a one-sided layer, FrontLayer), the plain canvas for the
    /// studding sails and Sail Damage, the normal map laid over the cloth's UV rectangle, and for Front side only a twin
    /// material showing the painted canvas, which is what Sail Damage must cut its holes into.
    /// </summary>
    internal sealed class PaintedCanvas
    {
        internal Material Mat;       // worn on the cloth
        internal Material Twin;      // Front side only: Mat with the painted texture, for Sail Damage
        internal Texture2D Painted;  // canvas + emblem
        internal Texture2D Clean;    // the same canvas without it
        internal Texture2D Bump;
        internal bool Front;         // laid out for Front side only
        private Texture bumpFrom;
        private Vector4 bumpKey;

        /// <summary>The material Sail Damage makes its torn canvas from.</summary>
        internal Material ForSailDamage => Front ? Twin : Mat;

        internal void Apply(PaintJob j, Material source, Color sailColor, bool front, string sailName)
        {
            ClothGeometry geom = j.Geom;
            Painted = Upload(Painted, j.Painted, geom.W, geom.H, "SailEmblem " + sailName);
            if (j.Clean != null) Clean = Upload(Clean, j.Clean, geom.W, geom.H, "SailEmblem clean " + sailName);
            Front = front && Clean != null;

            if (Mat == null) Mat = new Material(source) { name = source.name + " (emblem)" };
            Mat.mainTexture = Front ? Clean : Painted;
            Mat.mainTextureScale = new Vector2(1f / j.UvRect.width, 1f / j.UvRect.height);
            Mat.mainTextureOffset = new Vector2(-j.UvRect.xMin / j.UvRect.width, -j.UvRect.yMin / j.UvRect.height);
            if (Mat.HasProperty("_Color")) Mat.color = new Color(1f, 1f, 1f, sailColor.a);
            BakeBump(source, j.UvRect, Mathf.Min(geom.W, 1024), Mathf.Min(geom.H, 1024), sailName);

            if (Front)
            {
                if (Twin == null) Twin = new Material(Mat) { name = Mat.name + " (painted)" };
                else Twin.CopyPropertiesFromMaterial(Mat);
                Twin.mainTexture = Painted;
            }
        }

        private static Texture2D Upload(Texture2D tex, Color32[][] mips, int w, int h, string name)
        {
            if (tex == null || tex.width != w || tex.height != h || tex.mipmapCount != mips.Length)
            {
                if (tex != null) Object.Destroy(tex);
                tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            }
            for (int l = 0; l < mips.Length && l < tex.mipmapCount; l++)
                tex.SetPixels32(mips[l], l);
            tex.Apply(false);
            return tex;
        }

        // The normal map shares the main texture tiling in the standard shaders: lay it over the same rectangle,
        // or the weave would come out stretched. Done once per canvas, not per repaint.
        private void BakeBump(Material source, Rect uvRect, int bw, int bh, string sailName)
        {
            if (!source.HasProperty("_BumpMap")) return;
            Texture b = source.GetTexture("_BumpMap");
            if (b == null) return;
            Vector2 sc = source.mainTextureScale, of = source.mainTextureOffset;
            var key = new Vector4(sc.x * uvRect.width, sc.y * uvRect.height, of.x + sc.x * uvRect.xMin, of.y + sc.y * uvRect.yMin);
            if (Bump == null || bumpFrom != b || key != bumpKey || Bump.width != bw || Bump.height != bh)
            {
                if (Bump != null) Object.Destroy(Bump);
                Bump = new Texture2D(bw, bh, TextureFormat.RGBA32, true, true) { name = "SailEmblem bump " + sailName, wrapMode = TextureWrapMode.Clamp };
                Bump.SetPixels32(PaintJob.Blit(b, sc, of, uvRect, bw, bh, true));
                Bump.Apply(true);
                bumpFrom = b;
                bumpKey = key;
            }
            Mat.SetTexture("_BumpMap", Bump);
        }

        /// <summary>A new canvas from somewhere else: the material is made again from it, the textures stay for now.</summary>
        internal void DropMaterial()
        {
            if (Mat != null) Object.Destroy(Mat);
            if (Twin != null) Object.Destroy(Twin);
            Mat = Twin = null;
            Front = false;
        }

        internal void Destroy()
        {
            DropMaterial();
            if (Painted != null) Object.Destroy(Painted);
            if (Clean != null) Object.Destroy(Clean);
            if (Bump != null) Object.Destroy(Bump);
            Painted = Clean = Bump = null;
            bumpFrom = null;
        }
    }
}
