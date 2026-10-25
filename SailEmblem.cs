using System;
using HarmonyLib;
using UnityEngine;

namespace SailEmblems
{
    /// <summary>
    /// Lives on a Sail. Gives the sail cloth its own material whose main texture is the sail's canvas with the emblem
    /// painted in, baked once per change. The texture covers only the cloth's part of the UV square (as Sail Damage's
    /// torn canvas does), with the material tiling and the sail colour baked in, so the paint keeps its own colour on a
    /// dyed sail.
    ///
    /// What the game does to the cloth material (game-code notes, ReefEffectAnimUniversal and Sail.ChangeSailColor):
    ///  - furling saves renderer.material, i.e. a copy of ours, into its private unfurledMaterial and shows a
    ///    placeholder (Refs.emptyMaterial); unfurling puts that copy back. 0.1 took the copy for a new canvas and
    ///    destroyed its texture: the sail came back white. Copies of ours are now recognised by their texture.
    ///  - recolouring sets renderer.material.color, again on a copy: ChangeSailColorPatch bakes the new colour.
    /// With Sail Damage on a damaged sail, its torn material is made from ours; SailDamageLink hands it each new canvas.
    /// </summary>
    internal sealed class SailEmblem : MonoBehaviour
    {
        private static readonly AccessTools.FieldRef<ReefEffectAnimUniversal, Material> UnfurledMaterial =
            AccessTools.FieldRefAccess<ReefEffectAnimUniversal, Material>("unfurledMaterial");
        private static readonly AccessTools.FieldRef<ReefEffectAnimUniversal, bool> IsFurled =
            AccessTools.FieldRefAccess<ReefEffectAnimUniversal, bool>("isFurled");

        /// <summary>What is painted (at the shipyard: what is ordered).</summary>
        internal EmblemState State;
        /// <summary>What the sail had when the shipyard order was opened: repainting an installed sail is paid for.</summary>
        internal EmblemState Committed;

        private Sail sail;
        private SkinnedMeshRenderer rend;
        private ReefEffectAnimUniversal reef;
        private Material source;    // the game's canvas, worn again when the paint is taken off
        private Color sailColor = Color.white;
        private Material mat;       // ours
        private Texture2D painted;  // canvas + emblem
        private Texture2D clean;    // the same canvas without it, for the studding sails (only with Studding Sails)
        private Texture2D bump;
        private Texture bumpFrom;
        private Vector4 bumpKey;
        private Rect uvRect;
        private ClothGeometry geom;
        private bool framedSet;     // measured on the set sail (furled, the bones squeeze the cloth up)
        private Vector3 frameScale;
        private bool dirty = true;
        private bool reported, noLayout;

        internal Texture PaintedTexture => painted;
        internal Texture CleanTexture => clean;

        internal static SailEmblem Of(Sail s, bool create)
        {
            if (s == null) return null;
            var e = s.GetComponent<SailEmblem>();
            if (e == null && create) e = s.gameObject.AddComponent<SailEmblem>();
            return e;
        }

        internal static EmblemState StateOf(Sail s)
        {
            var e = Of(s, false);
            return e != null ? e.State : default(EmblemState);
        }

        /// <summary>Paint the sail (an empty state takes the paint off). <paramref name="now"/>: bake at once, not at the end of the frame.</summary>
        internal static void Set(Sail s, EmblemState st, bool now = false)
        {
            if (s == null) return;
            var e = Of(s, !st.IsEmpty);
            if (e == null) return;
            if (!e.State.Equals(st)) e.reported = false;
            e.State = st;
            e.dirty = true;
            if (now) e.Refresh();
        }

        private void Awake()
        {
            sail = GetComponent<Sail>();
        }

        private void LateUpdate()
        {
            Refresh();
        }

        private void OnDestroy()
        {
            // The sail is gone (shipyard rebuild, sold): its cloth goes with it, only our textures need freeing.
            DestroyBaked();
        }

        // ------------------------------------------------------------------ the material on the cloth

        private bool Furled => reef != null && IsFurled(reef);

        private Material Worn
        {
            get => Furled ? UnfurledMaterial(reef) : rend.sharedMaterial;
            set
            {
                if (Furled) UnfurledMaterial(reef) = value;
                else rend.sharedMaterial = value;
            }
        }

        // The furl placeholder, or a copy of it (Furl saves renderer.material, an "(Instance)" copy).
        private static bool IsPlaceholder(Material m)
        {
            Material empty = Refs.emptyMaterial;
            return m != null && empty != null && (m == empty || m.name.StartsWith(empty.name, StringComparison.Ordinal));
        }

        private bool IsOurCopy(Material m) => m != null && painted != null && m != mat && m.mainTexture == painted;

        // Made from ours ("... (emblem) (Instance)"), whatever texture it has now.
        private bool IsOurs(Material m) => m != null && mat != null && m != mat && m.name.StartsWith(mat.name, StringComparison.Ordinal);

        internal void Refresh()
        {
            if (sail == null) return;
            if (rend == null)
            {
                rend = sail.cloth != null ? sail.cloth.GetComponent<SkinnedMeshRenderer>() : GetComponentInChildren<SkinnedMeshRenderer>();
                reef = sail.GetComponent<ReefEffectAnimUniversal>();
                if (rend == null) return;
            }

            Material worn = Worn;
            if (State.IsEmpty)
            {
                if (mat != null) TakeOff(worn);
                return;
            }
            if (worn == null) return;

            if (mat == null)
            {
                if (IsPlaceholder(worn)) return; // furled before the game saved its canvas: wait for the real one
                if (SailDamageLink.Draws(sail))
                {
                    // Sail Damage made its torn canvas before we painted (loading order): ours starts from the prefab's.
                    if (source == null) source = PrefabCanvas(out sailColor);
                    if (source == null) return;
                }
                else
                {
                    source = worn;
                    sailColor = ColorOf(worn);
                }
                dirty = true;
            }
            else if (worn != mat)
            {
                if (IsOurCopy(worn))
                {
                    // Furl/unfurl handed back a copy of ours: wear ours again (the copy shares our texture, keep it alive).
                    Worn = mat;
                    Destroy(worn);
                }
                else if (SailDamageLink.Draws(sail))
                {
                    // Sail Damage wears a torn copy of our canvas.
                }
                else if (IsOurs(worn))
                {
                    // A copy of ours with another texture put in: Shipyard Expansion's sail textures (SailTextureChanger:
                    // renderer.material.mainTexture = ..., on loading and leaving the shipyard too). With our tiling it
                    // showed the whole texture sheet on the sail (Кэп). That texture is the new canvas, laid as the game's.
                    Plugin.Log.LogInfo("[Emblems] " + sail.sailName + ": canvas texture changed to '" + (worn.mainTexture != null ? worn.mainTexture.name : "none") + "', painting it");
                    if (worn.mainTexture != null) source.mainTexture = worn.mainTexture;
                    Worn = mat;
                    Destroy(worn);
                    dirty = true;
                }
                else if (!IsPlaceholder(worn))
                {
                    // Something else put a new canvas on the sail (another mod's material): paint over that one.
                    Plugin.Log.LogInfo("[Emblems] " + sail.sailName + ": new canvas '" + worn.name + "' on the sail, painting it");
                    source = worn;
                    sailColor = ColorOf(worn);
                    DestroyMat();
                    dirty = true;
                }
            }

            if (!dirty && FrameStale()) dirty = true;
            if (!dirty) return;
            dirty = false;
            try
            {
                if (!Bake()) return;
            }
            catch (Exception e)
            {
                if (!reported) Plugin.Log.LogError("[Emblems] painting " + sail.sailName + " failed: " + e);
                reported = true;
                return;
            }
            if (!SailDamageLink.TakeCanvas(sail, mat, clean))
                Worn = mat;
        }

        private void TakeOff(Material worn)
        {
            if (source != null && source.HasProperty("_Color")) source.color = sailColor;
            if (SailDamageLink.Draws(sail))
                SailDamageLink.TakeCanvas(sail, source, null);
            else if (source != null && (worn == mat || IsOurCopy(worn) || worn == null))
            {
                Worn = source;
                if (IsOurCopy(worn)) Destroy(worn);
            }
            DestroyBaked();
        }

        /// <summary>Sail.ChangeSailColor just recoloured a copy of the material on the cloth: bake the new colour in.</summary>
        internal void Recoloured()
        {
            if (mat == null || rend == null) return;
            Material worn = rend.sharedMaterial;
            if (worn == null || IsPlaceholder(worn) || !worn.HasProperty("_Color")) return; // furled: the game recolours the placeholder only
            sailColor = worn.color;
            if (source != null && source.HasProperty("_Color")) source.color = sailColor;
            bool ours = IsOurCopy(worn);
            if (ours) rend.sharedMaterial = mat;
            dirty = true;
            Refresh(); // at once: Sail Damage would bake the colour into its canvas on its own otherwise
            // our copy, or the recoloured copy of Sail Damage's torn material that TakeCanvas just took off
            if (ours || worn != mat && worn != source && rend.sharedMaterial != worn) Destroy(worn);
        }

        private static Color ColorOf(Material m) => m != null && m.HasProperty("_Color") ? m.color : Color.white;

        // The clean canvas of this kind of sail, in the colour the sail was given.
        private Material PrefabCanvas(out Color color)
        {
            color = Color.white;
            if (PrefabsDirectory.instance == null || sail.prefabIndex < 0 || sail.prefabIndex >= PrefabsDirectory.instance.sails.Length) return null;
            GameObject prefab = PrefabsDirectory.instance.sails[sail.prefabIndex];
            Sail ps = prefab != null ? prefab.GetComponent<Sail>() : null;
            Renderer r = ps != null && ps.cloth != null ? ps.cloth.GetComponent<Renderer>() : null;
            if (r == null || r.sharedMaterial == null) return null;
            Color[] colors = PrefabsDirectory.instance.sailColors;
            color = colors != null && sail.activeColor >= 0 && sail.activeColor < colors.Length ? colors[sail.activeColor] : ColorOf(r.sharedMaterial);
            var m = new Material(r.sharedMaterial) { name = r.sharedMaterial.name + " (clean)" };
            if (m.HasProperty("_Color")) m.color = color;
            Plugin.Log.LogInfo("[Emblems] " + sail.sailName + ": Sail Damage got here first, painting the prefab canvas '" + m.name + "'");
            return m;
        }

        private void DestroyMat()
        {
            if (mat != null) Destroy(mat);
            mat = null;
        }

        private void DestroyBaked()
        {
            DestroyMat();
            if (painted != null) Destroy(painted);
            if (clean != null) Destroy(clean);
            if (bump != null) Destroy(bump);
            painted = clean = bump = null;
            bumpFrom = null;
        }

        // ------------------------------------------------------------------ baking

        private Vector3 ClothScale => sail.cloth != null && sail.cloth.transform.parent != null ? sail.cloth.transform.parent.localScale : Vector3.one;

        // The frame was measured on a furled sail, or the sail was scaled since (shipyard width buttons, LoadScale).
        private bool FrameStale()
        {
            if (painted == null) return false;
            if (!framedSet && sail.currentUnroll >= 0.9f) return true;
            return (ClothScale - frameScale).sqrMagnitude > 1e-6f;
        }

        private bool Bake()
        {
            EmblemDef def = EmblemLibrary.Get(State.Id);
            if (def == null)
            {
                if (!reported) Plugin.Log.LogWarning("[Emblems] unknown emblem '" + State.Id + "' on " + sail.sailName + " (custom picture removed?), kept in the save");
                reported = true;
                if (mat != null) TakeOff(Worn);
                return false;
            }

            if (!CanPaint(sail))
            {
                // painted before it was taken off the list (fin sail): stays plain, the paint stays in the save
                if (!noLayout) Plugin.Log.LogWarning("[Emblems] " + sail.sailName + " cannot be painted, left plain");
                noLayout = true;
                if (mat != null) TakeOff(Worn);
                return false;
            }
            // The cloth as drawn: BakeMesh gives a readable copy even of a mesh that is not readable itself.
            var bakedMesh = new Mesh();
            rend.BakeMesh(bakedMesh);
            Vector2[] uv = bakedMesh.uv;
            Vector3[] verts = bakedMesh.vertices;
            int[] tris = bakedMesh.triangles;
            Destroy(bakedMesh);
            if (!Bounds(uv, out uvRect))
            {
                if (!noLayout) Plugin.Log.LogWarning("[Emblems] " + sail.sailName + ": the cloth has no texture layout (all UVs on one spot), it cannot be painted");
                noLayout = true;
                return false;
            }
            Transform ship = sail.shipRigidbody != null ? sail.shipRigidbody.transform : null;
            if (ship == null)
            {
                Mast mast = GetComponentInParent<Mast>();
                ship = mast != null && mast.shipRigidbody != null ? mast.shipRigidbody.transform : transform.root;
            }
            Matrix4x4 toWorld = Matrix4x4.TRS(rend.transform.position, rend.transform.rotation, Vector3.one);
            // Everything in metres on the sail, X to the reader's right, Y up: the brig's topsail has its texture turned
            // a quarter round (0.2.0 laid its emblems on their side), the fin sail's is warped (Кэп).
            geom = ClothGeometry.Measure(uv, verts, tris, uvRect, toWorld, ship, Plugin.TexelsPerMetre, Plugin.Resolution.Value);
            framedSet = sail.currentUnroll >= 0.9f;
            frameScale = ClothScale;

            int w = geom.W, h = geom.H;
            Color32[] canvas = Canvas(source, w, h);
            float sr = sailColor.r, sg = sailColor.g, sb = sailColor.b;
            for (int i = 0; i < canvas.Length; i++)
            {
                Color32 c = canvas[i];
                canvas[i] = new Color32((byte)(c.r * sr), (byte)(c.g * sg), (byte)(c.b * sb), 255);
            }

            Color32 paint = Palette.Get(State.Color);
            float spanX = geom.SpanX, spanY = geom.SpanY, cx = geom.CenterX;
            float hM = State.Size * spanY, wM = hM * def.W / def.H;
            if (wM > 0.94f * spanX) { hM *= 0.94f * spanX / wM; wM = 0.94f * spanX; }
            float cy = geom.CenterY + State.Offset * spanY;
            cx += State.OffsetX * spanX;
            float fit = 1f;
            PatternFrame frame = null;
            float footprint; // mask pixels under one canvas texel
            if (def.Kind == EmblemKind.Emblem)
            {
                // keep it on the cloth: the topsail gaff is a narrow triangle, and its emblem came out as stray pieces (Кэп)
                fit = Fit(def, cx, cy, wM, hM, 0.02f * spanY);
                wM *= fit;
                hM *= fit;
                // mask pixels under one canvas texel: read from the mask's halvings, else a big sail's edges come out toothed
                footprint = geom.MetresPerTexel * def.W / wM;
            }
            else
            {
                frame = PatternFrame.Of(geom, State);
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
                    a = def.Sample((State.Mirror ? -mx : mx) + 0.5f, (wy - cy) / hM + 0.5f, footprint);
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
            painted = Fill(painted, outPx, w, h, "SailEmblem " + sail.sailName);
            // the plain canvas: for the studding sails, and for Sail Damage's patches (their weave showed the emblem, Кэп)
            if (StuddingLink.Present || SailDamageLink.Present)
                clean = Fill(clean, canvas, w, h, "SailEmblem clean " + sail.sailName);

            if (mat == null)
                mat = new Material(source) { name = source.name + " (emblem)" };
            mat.mainTexture = painted;
            mat.mainTextureScale = new Vector2(1f / uvRect.width, 1f / uvRect.height);
            mat.mainTextureOffset = new Vector2(-uvRect.xMin / uvRect.width, -uvRect.yMin / uvRect.height);
            if (mat.HasProperty("_Color")) mat.color = new Color(1f, 1f, 1f, sailColor.a);
            BakeBump(Mathf.Min(w, 1024), Mathf.Min(h, 1024));

            if (!reported)
                Plugin.Log.LogInfo("[Emblems] " + sail.sailName + ": " + State + ", shader " + source.shader.name +
                                   ", canvas " + (source.mainTexture != null ? source.mainTexture.name + " " + source.mainTexture.width + "x" + source.mainTexture.height : "none") +
                                   ", tiling " + source.mainTextureScale + " " + source.mainTextureOffset + ", colour " + sailColor +
                                   ", uv " + uvRect + ", " + geom.Info +
                                   (frame != null && State.AlongFoot ? ", along the foot " + Mathf.RoundToInt(Mathf.Atan2(geom.Foot.y, geom.Foot.x) * Mathf.Rad2Deg) + " deg" : "") +
                                   (fit < 0.999f ? ", emblem shrunk to " + Mathf.RoundToInt(fit * 100f) + "% to stay on the cloth" : "") +
                                   (framedSet ? "" : " (furled, measured again when set)"));
            reported = true;
            return true;
        }

        // The largest share (up to 1) of the emblem's size at which all its painted points lie on the cloth.
        private float Fit(EmblemDef def, float cx, float cy, float wM, float hM, float margin)
        {
            if (Inside(def, cx, cy, wM, hM, margin)) return 1f;
            float lo = 0.1f, hi = 1f;
            if (!Inside(def, cx, cy, wM * lo, hM * lo, margin)) return lo;
            for (int k = 0; k < 10; k++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Inside(def, cx, cy, wM * mid, hM * mid, margin)) lo = mid;
                else hi = mid;
            }
            return lo;
        }

        private bool Inside(EmblemDef def, float cx, float cy, float wM, float hM, float margin)
        {
            foreach (Vector2 p in def.Solid)
            {
                float x = cx + (State.Mirror ? 0.5f - p.x : p.x - 0.5f) * wM;
                if (geom.EdgeDistance(x, cy + (p.y - 0.5f) * hM) < margin) return false;
            }
            return true;
        }

        private static bool Bounds(Vector2[] uv, out Rect r)
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

        private static readonly System.Collections.Generic.Dictionary<Mesh, bool> paintable = new System.Collections.Generic.Dictionary<Mesh, bool>();

        /// <summary>
        /// Whether the sail's cloth has a texture layout to paint on. The junk's square sail has all its UVs on one spot
        /// (the game shows it as one colour); there is nothing to lay a picture on without giving it a new mesh.
        /// </summary>
        internal static bool CanPaint(Sail s)
        {
            SkinnedMeshRenderer r = s != null && s.cloth != null ? s.cloth.GetComponent<SkinnedMeshRenderer>() : null;
            if (r == null || r.sharedMesh == null) return false;
            // The fin sail's layout is warped: parts of the cloth get a fraction of the texture and came out blurred,
            // and a texture big enough for them froze the game (Кэп, 1.0.0). Not offered.
            if (s.sailName != null && s.sailName.IndexOf("fin", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (paintable.TryGetValue(r.sharedMesh, out bool ok)) return ok;
            var m = new Mesh();
            r.BakeMesh(m);
            ok = Bounds(m.uv, out _);
            Destroy(m);
            paintable[r.sharedMesh] = ok;
            return ok;
        }

        private static Texture2D Fill(Texture2D tex, Color32[] px, int w, int h, string name)
        {
            if (tex == null || tex.width != w || tex.height != h)
            {
                if (tex != null) Destroy(tex);
                tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            }
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        // The game's canvas (its tiling included) over the cloth's UV rectangle, through the GPU.
        private Color32[] Canvas(Material src, int w, int h)
        {
            Texture main = src.mainTexture;
            if (main == null)
            {
                var white = new Color32[w * h];
                for (int i = 0; i < white.Length; i++) white[i] = new Color32(255, 255, 255, 255);
                return white;
            }
            Color32[] px = Blit(main, src.mainTextureScale, src.mainTextureOffset, w, h, false);
            // Junk-family sails use a soft particle blob as their canvas and look plain in the game (Sail Damage notes):
            // keep the colour at the cloth's middle.
            if (main.name.StartsWith("ParticleCloud", StringComparison.Ordinal))
            {
                int x = Mathf.Clamp(Mathf.FloorToInt(geom.CenterS * w), 0, w - 1), y = Mathf.Clamp(Mathf.FloorToInt(geom.CenterT * h), 0, h - 1);
                Color32 one = px[y * w + x];
                for (int i = 0; i < px.Length; i++) px[i] = one;
            }
            return px;
        }

        private Color32[] Blit(Texture t, Vector2 sc, Vector2 of, int w, int h, bool linear)
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
            Destroy(tex);
            return px;
        }

        // The normal map shares the main texture tiling in the standard shaders: lay it over the same rectangle,
        // or the weave would come out stretched. Done once per canvas, not per repaint.
        private void BakeBump(int bw, int bh)
        {
            if (!source.HasProperty("_BumpMap")) return;
            Texture b = source.GetTexture("_BumpMap");
            if (b == null) return;
            Vector2 sc = source.mainTextureScale, of = source.mainTextureOffset;
            var key = new Vector4(sc.x * uvRect.width, sc.y * uvRect.height, of.x + sc.x * uvRect.xMin, of.y + sc.y * uvRect.yMin);
            if (bump == null || bumpFrom != b || key != bumpKey || bump.width != bw || bump.height != bh)
            {
                if (bump != null) Destroy(bump);
                bump = new Texture2D(bw, bh, TextureFormat.RGBA32, true, true) { name = "SailEmblem bump " + sail.sailName, wrapMode = TextureWrapMode.Clamp };
                bump.SetPixels32(Blit(b, sc, of, bw, bh, true));
                bump.Apply(true);
                bumpFrom = b;
                bumpKey = key;
            }
            mat.SetTexture("_BumpMap", bump);
        }
    }
}
