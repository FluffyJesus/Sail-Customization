using System;
using System.Collections.Generic;
using UnityEngine;

namespace SailEmblems
{
    /// <summary>
    /// One painting of one sail. Painting is mostly arithmetic over a million texels or two; on the main thread it froze
    /// the game for a moment at every click in the paint window (Кэп, 1.0.0). Since 1.0.1 the main thread only reads
    /// what Unity must give it (Start: the drawn cloth, the game's canvas) and later uploads the result; Compute runs
    /// on a pool thread, as Sail Damage draws its holes.
    /// </summary>
    internal sealed class PaintJob
    {
        // in
        internal EmblemState State;
        internal EmblemDef Def;
        internal Vector2[] Uv;
        internal Vector3[] Verts;
        internal int[] Tris;
        internal Rect UvRect;
        internal Matrix4x4 ToWorld;
        internal Vector3 Forward, Starboard;
        internal int MaxRes, W, H;
        internal Color32[] Canvas;      // the game's, W x H, not tinted yet
        internal bool Plain;            // a particle blob canvas: one colour taken from the cloth's middle
        internal Color Tint;
        internal Texture SourceTexture; // the game's canvas texture it was read from
        internal bool WantClean;
        internal bool FramedSet;
        // out
        internal ClothGeometry Geom;
        internal Color32[][] Painted, Clean; // mip chains
        internal float Fit = 1f;
        internal Exception Error;
        internal volatile bool Done;

        // The drawn cloth of a set sail (renderer space), by mesh and sail scale: see Start.
        private static readonly Dictionary<KeyValuePair<Mesh, Vector3>, Vector3[]> setShapes = new Dictionary<KeyValuePair<Mesh, Vector3>, Vector3[]>();

        /// <summary>The set shape of this cloth is known: a furled sail can be painted right.</summary>
        internal static bool KnowsSetShape(SkinnedMeshRenderer rend, Vector3 scale) => setShapes.ContainsKey(new KeyValuePair<Mesh, Vector3>(rend.sharedMesh, scale));

        /// <summary>
        /// Reads the cloth and the canvas (main thread) and queues the rest. Null with <paramref name="why"/> when the
        /// cloth has no texture layout to paint on.
        /// </summary>
        internal static PaintJob Start(Sail sail, SkinnedMeshRenderer rend, Material source, Color tint, EmblemState state, EmblemDef def,
                                       bool wantClean, out string why)
        {
            why = null;
            // The cloth as drawn: BakeMesh gives a readable copy even of a mesh that is not readable itself.
            var bakedMesh = new Mesh();
            rend.BakeMesh(bakedMesh);
            var j = new PaintJob { State = state, Def = def, Uv = bakedMesh.uv, Verts = bakedMesh.vertices, Tris = bakedMesh.triangles };
            UnityEngine.Object.Destroy(bakedMesh);
            // A furled sail's bones squeeze the cloth up to the yard, folded: painted on that shape the emblem came out
            // twice and small until the sail was fully set and painted again (Кэп, after leaving the shipyard, where
            // the sails are built again furled). The set shape of this kind of sail is kept and used instead.
            j.FramedSet = sail.currentUnroll >= 0.9f;
            Vector3 scale = sail.cloth != null && sail.cloth.transform.parent != null ? sail.cloth.transform.parent.localScale : Vector3.one;
            var key = new KeyValuePair<Mesh, Vector3>(rend.sharedMesh, scale);
            if (j.FramedSet)
                setShapes[key] = j.Verts;
            else if (setShapes.TryGetValue(key, out Vector3[] set) && set.Length == j.Verts.Length)
            {
                j.Verts = set;
                j.FramedSet = true;
            }
            if (!Bounds(j.Uv, out j.UvRect))
            {
                why = "the cloth has no texture layout (all UVs on one spot)";
                return null;
            }
            Transform ship = sail.shipRigidbody != null ? sail.shipRigidbody.transform : null;
            if (ship == null)
            {
                Mast mast = sail.GetComponentInParent<Mast>();
                ship = mast != null && mast.shipRigidbody != null ? mast.shipRigidbody.transform : sail.transform.root;
            }
            j.ToWorld = Matrix4x4.TRS(rend.transform.position, rend.transform.rotation, Vector3.one);
            j.Forward = ship != null ? ship.forward : Vector3.forward;
            j.Starboard = ship != null ? ship.right : Vector3.right;
            j.MaxRes = Plugin.Resolution.Value;
            ClothGeometry.Size(j.Uv, j.Verts, j.UvRect, j.ToWorld, Plugin.TexelsPerMetre, j.MaxRes, out j.W, out j.H);
            Texture main = source.mainTexture;
            j.Canvas = main != null ? Blit(main, source.mainTextureScale, source.mainTextureOffset, j.UvRect, j.W, j.H, false) : null;
            j.Plain = main != null && main.name.StartsWith("ParticleCloud", StringComparison.Ordinal);
            j.Tint = tint;
            j.SourceTexture = main;
            j.WantClean = wantClean;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    j.Compute();
                }
                catch (Exception e)
                {
                    j.Error = e;
                }
                j.Done = true;
            });
            return j;
        }

        // Off the main thread: only the job's own copies, the emblem's mask and plain arithmetic.
        private void Compute()
        {
            EmblemState st = State;
            EmblemDef def = Def;
            // Everything in metres on the sail, X to the reader's right, Y up: the brig's topsail has its texture turned
            // a quarter round (0.2.0 laid its emblems on their side).
            ClothGeometry geom = ClothGeometry.Measure(Uv, Verts, Tris, UvRect, ToWorld, Forward, Starboard, Plugin.TexelsPerMetre, MaxRes);
            Geom = geom;
            int w = geom.W, h = geom.H;
            Color32[] canvas = Canvas ?? new Color32[w * h];
            if (Canvas == null)
                for (int i = 0; i < canvas.Length; i++) canvas[i] = new Color32(255, 255, 255, 255);
            if (Plain)
            {
                // Junk-family sails use a soft particle blob as their canvas and look plain in the game (Sail Damage
                // notes): keep the colour at the cloth's middle.
                int x = Mathf.Clamp(Mathf.FloorToInt(geom.CenterS * w), 0, w - 1), y = Mathf.Clamp(Mathf.FloorToInt(geom.CenterT * h), 0, h - 1);
                Color32 one = canvas[y * w + x];
                for (int i = 0; i < canvas.Length; i++) canvas[i] = one;
            }
            float sr = Tint.r, sg = Tint.g, sb = Tint.b;
            for (int i = 0; i < canvas.Length; i++)
            {
                Color32 c = canvas[i];
                canvas[i] = new Color32((byte)(c.r * sr), (byte)(c.g * sg), (byte)(c.b * sb), 255);
            }

            Color32 paint = Palette.Get(st.Color);
            float spanX = geom.SpanX, spanY = geom.SpanY, cx = geom.CenterX;
            float hM = st.Size * spanY, wM = hM * def.W / def.H;
            if (wM > 0.94f * spanX) { hM *= 0.94f * spanX / wM; wM = 0.94f * spanX; }
            float cy = geom.CenterY + st.Offset * spanY;
            cx += st.OffsetX * spanX;
            PatternFrame frame = null;
            float footprint; // mask pixels under one canvas texel
            if (def.Kind == EmblemKind.Emblem)
            {
                // keep it on the cloth: the topsail gaff is a narrow triangle, and its emblem came out as stray pieces (Кэп)
                Fit = FitOnCloth(geom, def, st.Mirror, cx, cy, wM, hM, 0.02f * spanY);
                wM *= Fit;
                hM *= Fit;
                // read from the mask's halvings, else a big sail's edges come out toothed
                footprint = geom.MetresPerTexel * def.W / wM;
            }
            else
            {
                frame = PatternFrame.Of(geom, st);
                footprint = geom.MetresPerTexel * def.W / (frame.SpanX * frame.ScaleX);
            }

            var outPx = new Color32[canvas.Length];
            for (int i = 0; i < canvas.Length; i++)
            {
                float wx = geom.X[i], wy = geom.Y[i];
                float a;
                if (def.Kind == EmblemKind.Emblem)
                {
                    // the mirror turns the picture about its own middle
                    float mx = (wx - cx) / wM;
                    a = def.Sample((st.Mirror ? -mx : mx) + 0.5f, (wy - cy) / hM + 0.5f, footprint);
                }
                else
                {
                    frame.Map(geom, wx, wy, out float u, out float v);
                    if (def.Custom || !Patterns.TryCoverage(def.Id, wx, wy, u, v, geom, frame, geom.MetresPerTexel, out a))
                        a = def.Sample((u - frame.MinX) / frame.SpanX, (v - frame.MinY) / frame.SpanY, footprint);
                }
                Color32 b = canvas[i];
                if (a <= 0f)
                {
                    outPx[i] = b;
                    continue;
                }
                // the paint soaks into the canvas: keep a little of the weave's light and shade
                float shade = 0.75f + 0.25f * (b.r + b.g + b.b) / 765f;
                outPx[i] = new Color32(
                    (byte)Mathf.Clamp(b.r + (paint.r * shade - b.r) * a, 0f, 255f),
                    (byte)Mathf.Clamp(b.g + (paint.g * shade - b.g) * a, 0f, 255f),
                    (byte)Mathf.Clamp(b.b + (paint.b * shade - b.b) * a, 0f, 255f),
                    255);
            }
            geom.X = geom.Y = null; // 32 MB on a big sail; only the cloth map is kept
            Canvas = null;
            Painted = Mips(outPx, w, h);
            if (WantClean) Clean = Mips(canvas, w, h);
        }

        internal string Describe(Material source)
        {
            return State + ", shader " + source.shader.name +
                   ", canvas " + (source.mainTexture != null ? source.mainTexture.name + " " + source.mainTexture.width + "x" + source.mainTexture.height : "none") +
                   ", tiling " + source.mainTextureScale + " " + source.mainTextureOffset + ", colour " + Tint + ", uv " + UvRect + ", " + Geom.Info +
                   (State.IsPattern && State.AlongFoot ? ", along the foot " + Mathf.RoundToInt(Mathf.Atan2(Geom.Foot.y, Geom.Foot.x) * Mathf.Rad2Deg) + " deg" : "") +
                   (Fit < 0.999f ? ", emblem shrunk to " + Mathf.RoundToInt(Fit * 100f) + "% to stay on the cloth" : "") +
                   (FramedSet ? "" : " (furled, measured again when set)");
        }

        // Box-filtered mip chain, as Texture2D.Apply(true) makes it, but off the main thread.
        private static Color32[][] Mips(Color32[] px, int w, int h)
        {
            int levels = 1;
            for (int s = Mathf.Max(w, h); s > 1; s >>= 1) levels++;
            var mips = new Color32[levels][];
            mips[0] = px;
            for (int l = 1; l < levels; l++)
            {
                int sw = Mathf.Max(1, w >> (l - 1)), sh = Mathf.Max(1, h >> (l - 1));
                int dw = Mathf.Max(1, w >> l), dh = Mathf.Max(1, h >> l);
                Color32[] src = mips[l - 1], dst = new Color32[dw * dh];
                for (int y = 0; y < dh; y++)
                {
                    int y0 = Mathf.Min(y * 2, sh - 1) * sw, y1 = Mathf.Min(y * 2 + 1, sh - 1) * sw;
                    for (int x = 0; x < dw; x++)
                    {
                        int x0 = Mathf.Min(x * 2, sw - 1), x1 = Mathf.Min(x * 2 + 1, sw - 1);
                        Color32 a = src[y0 + x0], b = src[y0 + x1], c = src[y1 + x0], d = src[y1 + x1];
                        dst[y * dw + x] = new Color32((byte)((a.r + b.r + c.r + d.r + 2) >> 2), (byte)((a.g + b.g + c.g + d.g + 2) >> 2),
                                                      (byte)((a.b + b.b + c.b + d.b + 2) >> 2), (byte)((a.a + b.a + c.a + d.a + 2) >> 2));
                    }
                }
                mips[l] = dst;
            }
            return mips;
        }

        // The largest share (up to 1) of the emblem's size at which all its painted points lie on the cloth.
        private static float FitOnCloth(ClothGeometry geom, EmblemDef def, bool mirror, float cx, float cy, float wM, float hM, float margin)
        {
            if (Inside(geom, def, mirror, cx, cy, wM, hM, margin)) return 1f;
            float lo = 0.1f, hi = 1f;
            if (!Inside(geom, def, mirror, cx, cy, wM * lo, hM * lo, margin)) return lo;
            for (int k = 0; k < 10; k++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Inside(geom, def, mirror, cx, cy, wM * mid, hM * mid, margin)) lo = mid;
                else hi = mid;
            }
            return lo;
        }

        private static bool Inside(ClothGeometry geom, EmblemDef def, bool mirror, float cx, float cy, float wM, float hM, float margin)
        {
            foreach (Vector2 p in def.Solid)
            {
                float x = cx + (mirror ? 0.5f - p.x : p.x - 0.5f) * wM;
                if (geom.EdgeDistance(x, cy + (p.y - 0.5f) * hM) < margin) return false;
            }
            return true;
        }

        internal static bool Bounds(Vector2[] uv, out Rect r)
        {
            r = new Rect(0f, 0f, 1f, 1f);
            if (uv == null || uv.Length == 0) return false;
            Vector2 min = uv[0], max = uv[0];
            foreach (Vector2 p in uv)
            {
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }
            if (max.x - min.x < 1e-3f || max.y - min.y < 1e-3f) return false;
            r = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            return true;
        }

        /// <summary>A texture (its tiling included) over the cloth's UV rectangle, read back through the GPU.</summary>
        internal static Color32[] Blit(Texture t, Vector2 sc, Vector2 of, Rect uvRect, int w, int h, bool linear)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.Default);
            RenderTexture before = RenderTexture.active;
            Graphics.Blit(t, rt, new Vector2(sc.x * uvRect.width, sc.y * uvRect.height),
                new Vector2(of.x + sc.x * uvRect.xMin, of.y + sc.y * uvRect.yMin));
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            RenderTexture.active = before;
            RenderTexture.ReleaseTemporary(rt);
            Color32[] px = tex.GetPixels32();
            UnityEngine.Object.Destroy(tex);
            return px;
        }
    }
}
