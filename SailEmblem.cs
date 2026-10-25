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
        private Material overlay;   // Front side only: the painted canvas, one-sided, drawn over the plain double-sided one
        private bool overlayOn, frontFailed;
        // Front side only: a copy of ours showing the painted canvas, handed to Sail Damage to cut its holes into
        private Material paintedTwin;
        /// <summary>Where the cloth's front faces look, from the last painting (Front side only paints that side).</summary>
        internal string FrontSide;
        private Texture2D painted;  // canvas + emblem
        private Texture2D clean;    // the same canvas without it, for the studding sails (only with Studding Sails)
        private Texture2D bump;
        private Texture bumpFrom;
        private Vector4 bumpKey;
        private Rect uvRect;
        private bool framedSet;     // measured on the set sail (furled, the bones squeeze the cloth up)
        private Vector3 frameScale;
        private bool dirty = true;
        private bool reported, noLayout;
        private bool stalled; // nothing to paint with (unknown picture, unpaintable cloth) until the state changes

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
            e.stalled = false;
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

        private bool IsOurCopy(Material m) => m != null && mat != null && m != mat && m.mainTexture != null && m.mainTexture == mat.mainTexture;

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
            if (worn == null || stalled) return;

            if (mat == null)
            {
                // (while the first painting is being worked out, the canvas it started from stays)
                if (job == null)
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
            }
            else if (worn != mat)
            {
                if (worn == paintedTwin)
                {
                    // Sail Damage mended the sail as new and put back what it was given: ours goes on again
                    Worn = mat;
                }
                else if (IsOurCopy(worn))
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
                    Plugin.Debug("[Emblems] " + sail.sailName + ": canvas texture changed to '" + (worn.mainTexture != null ? worn.mainTexture.name : "none") + "', painting it");
                    if (worn.mainTexture != null) source.mainTexture = worn.mainTexture;
                    Worn = mat;
                    Destroy(worn);
                    dirty = true;
                }
                else if (!IsPlaceholder(worn))
                {
                    // Something else put a new canvas on the sail (another mod's material): paint over that one.
                    Plugin.Debug("[Emblems] " + sail.sailName + ": new canvas '" + worn.name + "' on the sail, painting it");
                    source = worn;
                    sailColor = ColorOf(worn);
                    DestroyMat();
                    dirty = true;
                }
            }

            if (mat != null)
            {
                // torn: Sail Damage wears its own canvas, made from the painted one (it splits it in two for the front side)
                if (overlayOn && SailDamageLink.Draws(sail))
                    overlayOn = false; // it already has the painted canvas (paintedTwin), the torn layer takes over
                // mended as new: Sail Damage gave the sail back, so the front side can be painted alone again
                else if (State.FrontOnly && !overlayOn && !frontFailed && job == null && rend.sharedMaterial == mat && !SailDamageLink.Draws(sail))
                    dirty = true;
                SyncOverlay();
            }

            if (job != null)
            {
                if (!job.Done) return;
                Job finished = job;
                job = null;
                // shown even if something else was asked for meanwhile (another click): that one is started next
                Finish(finished);
            }
            if (!dirty && FrameStale()) dirty = true;
            if (!dirty) return;
            dirty = false;
            try
            {
                Begin();
            }
            catch (Exception e)
            {
                if (!reported) Plugin.Log.LogError("[Emblems] painting " + sail.sailName + " failed: " + e);
                reported = true;
            }
        }

        private void TakeOff(Material worn)
        {
            overlayOn = false;
            RemoveOverlays();
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
            Plugin.Debug("[Emblems] " + sail.sailName + ": Sail Damage got here first, painting the prefab canvas '" + m.name + "'");
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
            if (overlay != null) Destroy(overlay);
            if (tornOverlay != null) Destroy(tornOverlay);
            if (paintedTwin != null) Destroy(paintedTwin);
            paintedTwin = null;
            painted = clean = bump = null;
            overlay = tornOverlay = null;
            overlayOn = false;
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

        // Painting a sail is mostly arithmetic over a million texels or two; on the main thread it froze the game for a
        // moment at every click in the paint window (Кэп, 1.0.0). Since 1.0.1 the main thread only reads what Unity
        // must give it (the drawn cloth, the game's canvas) and later uploads the result; the rest runs on a pool
        // thread, as Sail Damage draws its holes. The sail keeps its old look for the few frames in between.
        private sealed class Job
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
            internal Color32[] Canvas; // the game's, W x H, not tinted yet
            internal bool Plain;       // a particle blob canvas: one colour taken from the cloth's middle
            internal Color Tint;
            internal bool WantClean;
            internal bool FramedSet;
            // out
            internal ClothGeometry Geom;
            internal Color32[][] Painted, Clean; // mip chains
            internal float Fit = 1f;
            internal bool AlongFoot;
            internal Exception Error;
            internal volatile bool Done;
        }

        private Job job;

        private void Begin()
        {
            EmblemDef def = EmblemLibrary.Get(State.Id);
            if (def == null)
            {
                if (!reported) Plugin.Log.LogWarning("[Emblems] unknown emblem '" + State.Id + "' on " + sail.sailName + " (custom picture removed?), kept in the save");
                reported = true;
                stalled = true;
                if (mat != null) TakeOff(Worn);
                return;
            }
            if (!CanPaint(sail))
            {
                // painted before it was taken off the list (fin sail): stays plain, the paint stays in the save
                if (!noLayout) Plugin.Log.LogWarning("[Emblems] " + sail.sailName + " cannot be painted, left plain");
                noLayout = stalled = true;
                if (mat != null) TakeOff(Worn);
                return;
            }
            // The cloth as drawn: BakeMesh gives a readable copy even of a mesh that is not readable itself.
            var bakedMesh = new Mesh();
            rend.BakeMesh(bakedMesh);
            var j = new Job { State = State, Def = def, Uv = bakedMesh.uv, Verts = bakedMesh.vertices, Tris = bakedMesh.triangles };
            Destroy(bakedMesh);
            if (!Bounds(j.Uv, out uvRect))
            {
                if (!noLayout) Plugin.Log.LogWarning("[Emblems] " + sail.sailName + ": the cloth has no texture layout (all UVs on one spot), it cannot be painted");
                noLayout = stalled = true;
                return;
            }
            Transform ship = sail.shipRigidbody != null ? sail.shipRigidbody.transform : null;
            if (ship == null)
            {
                Mast mast = GetComponentInParent<Mast>();
                ship = mast != null && mast.shipRigidbody != null ? mast.shipRigidbody.transform : transform.root;
            }
            j.UvRect = uvRect;
            j.ToWorld = Matrix4x4.TRS(rend.transform.position, rend.transform.rotation, Vector3.one);
            j.Forward = ship != null ? ship.forward : Vector3.forward;
            j.Starboard = ship != null ? ship.right : Vector3.right;
            j.MaxRes = Plugin.Resolution.Value;
            ClothGeometry.Size(j.Uv, j.Verts, uvRect, j.ToWorld, Plugin.TexelsPerMetre, j.MaxRes, out j.W, out j.H);
            Texture main = source.mainTexture;
            j.Canvas = main != null ? Blit(main, source.mainTextureScale, source.mainTextureOffset, j.W, j.H, false) : null;
            j.Plain = main != null && main.name.StartsWith("ParticleCloud", StringComparison.Ordinal);
            j.Tint = sailColor;
            // the plain canvas: for the studding sails, and for Sail Damage's patches (their weave showed the emblem, Кэп)
            j.WantClean = StuddingLink.Present || SailDamageLink.Present || State.FrontOnly;
            j.FramedSet = sail.currentUnroll >= 0.9f;
            framedSet = j.FramedSet;
            frameScale = ClothScale;
            job = j;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Compute(j);
                }
                catch (Exception e)
                {
                    j.Error = e;
                }
                j.Done = true;
            });
        }

        // Off the main thread: only the job's own copies, the emblem's mask and plain arithmetic.
        private static void Compute(Job j)
        {
            EmblemState st = j.State;
            EmblemDef def = j.Def;
            // Everything in metres on the sail, X to the reader's right, Y up: the brig's topsail has its texture turned
            // a quarter round (0.2.0 laid its emblems on their side).
            ClothGeometry geom = ClothGeometry.Measure(j.Uv, j.Verts, j.Tris, j.UvRect, j.ToWorld, j.Forward, j.Starboard, Plugin.TexelsPerMetre, j.MaxRes);
            j.Geom = geom;
            int w = geom.W, h = geom.H;
            Color32[] canvas = j.Canvas ?? new Color32[w * h];
            if (j.Canvas == null)
                for (int i = 0; i < canvas.Length; i++) canvas[i] = new Color32(255, 255, 255, 255);
            if (j.Plain)
            {
                // Junk-family sails use a soft particle blob as their canvas and look plain in the game (Sail Damage
                // notes): keep the colour at the cloth's middle.
                int x = Mathf.Clamp(Mathf.FloorToInt(geom.CenterS * w), 0, w - 1), y = Mathf.Clamp(Mathf.FloorToInt(geom.CenterT * h), 0, h - 1);
                Color32 one = canvas[y * w + x];
                for (int i = 0; i < canvas.Length; i++) canvas[i] = one;
            }
            float sr = j.Tint.r, sg = j.Tint.g, sb = j.Tint.b;
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
                j.Fit = Fit(geom, def, st.Mirror, cx, cy, wM, hM, 0.02f * spanY);
                wM *= j.Fit;
                hM *= j.Fit;
                // read from the mask's halvings, else a big sail's edges come out toothed
                footprint = geom.MetresPerTexel * def.W / wM;
            }
            else
            {
                frame = PatternFrame.Of(geom, st);
                j.AlongFoot = st.AlongFoot;
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
            j.Canvas = null;
            j.Painted = Mips(outPx, w, h);
            if (j.WantClean) j.Clean = Mips(canvas, w, h);
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

        // Back on the main thread: upload, dress the material, hand it to Sail Damage or wear it.
        private void Finish(Job j)
        {
            if (j.Error != null)
            {
                if (!reported) Plugin.Log.LogError("[Emblems] painting " + sail.sailName + " failed: " + j.Error);
                reported = true;
                return;
            }
            if (State.IsEmpty || source == null) return;
            ClothGeometry geom = j.Geom;
            painted = Upload(painted, j.Painted, geom.W, geom.H, "SailEmblem " + sail.sailName);
            if (j.Clean != null) clean = Upload(clean, j.Clean, geom.W, geom.H, "SailEmblem clean " + sail.sailName);

            Shader standard = j.State.FrontOnly ? Shader.Find("Standard") : null;
            bool front = standard != null && clean != null && !SailDamageLink.Draws(sail);
            if (j.State.FrontOnly && standard == null && !frontFailed)
                Plugin.Log.LogWarning("[Emblems] no Standard shader in this build: Front side only paints both sides");
            frontFailed = j.State.FrontOnly && standard == null;
            if (mat == null)
                mat = new Material(source) { name = source.name + " (emblem)" };
            mat.mainTexture = front ? clean : painted;
            mat.mainTextureScale = new Vector2(1f / j.UvRect.width, 1f / j.UvRect.height);
            mat.mainTextureOffset = new Vector2(-j.UvRect.xMin / j.UvRect.width, -j.UvRect.yMin / j.UvRect.height);
            if (mat.HasProperty("_Color")) mat.color = new Color(1f, 1f, 1f, sailColor.a);
            uvRect = j.UvRect;
            BakeBump(Mathf.Min(geom.W, 1024), Mathf.Min(geom.H, 1024));
            FrontSide = geom.FrontSide;
            if (front) DressOverlay(standard);
            overlayOn = front;

            if (!reported)
                Plugin.Debug("[Emblems] " + sail.sailName + ": " + j.State + ", shader " + source.shader.name +
                                   ", canvas " + (source.mainTexture != null ? source.mainTexture.name + " " + source.mainTexture.width + "x" + source.mainTexture.height : "none") +
                                   ", tiling " + source.mainTextureScale + " " + source.mainTextureOffset + ", colour " + j.Tint +
                                   ", uv " + j.UvRect + ", " + geom.Info +
                                   (j.AlongFoot ? ", along the foot " + Mathf.RoundToInt(Mathf.Atan2(geom.Foot.y, geom.Foot.x) * Mathf.Rad2Deg) + " deg" : "") +
                                   (j.Fit < 0.999f ? ", emblem shrunk to " + Mathf.RoundToInt(j.Fit * 100f) + "% to stay on the cloth" : "") +
                                   (front ? ", front side only: " + geom.FrontSide : "") +
                                   (j.FramedSet ? "" : " (furled, measured again when set)"));
            reported = true;
            Material give = mat;
            if (front)
            {
                // what a torn canvas must be made from: painted, not the plain one the cloth wears now
                if (paintedTwin == null) paintedTwin = new Material(mat) { name = mat.name + " (painted)" };
                else paintedTwin.CopyPropertiesFromMaterial(mat);
                paintedTwin.mainTexture = painted;
                give = paintedTwin;
            }
            if (!SailDamageLink.TakeCanvas(sail, give, clean))
                Worn = mat;
            SyncOverlay();
        }

        // Front side only (Кэп, 1.1 test). The game's canvas shader draws both faces of the cloth with one texture.
        // Under it the plain canvas on both faces; over it, as a second material on the same renderer (so it moves
        // with the simulated cloth), the painted canvas on the game's one-sided Standard shader: Unity draws a second
        // material over the mesh again, its front faces only, at the same depth. Which face is the front is the model's.
        private void DressOverlay(Shader standard)
        {
            if (overlay == null) overlay = new Material(standard) { name = mat.name + " (front)" };
            overlay.mainTexture = painted;
            overlay.mainTextureScale = mat.mainTextureScale;
            overlay.mainTextureOffset = mat.mainTextureOffset;
            overlay.color = Color.white;
            if (overlay.HasProperty("_Glossiness")) overlay.SetFloat("_Glossiness", 0.1f); // canvas, not paint on metal
            if (overlay.HasProperty("_Metallic")) overlay.SetFloat("_Metallic", 0f);
            if (bump != null)
            {
                overlay.SetTexture("_BumpMap", bump);
                overlay.EnableKeyword("_NORMALMAP");
            }
            // after the canvas, so the paint lands on top where both are at the same depth
            overlay.renderQueue = Mathf.Max(2000, mat.renderQueue) + 1;
        }

        // The layer is the renderer's second material while the sail is set and wears ours; off when furled (it would
        // show on the bunched-up cloth), torn, or plain.
        private void SyncOverlay()
        {
            if (rend == null) return;
            Material want = null;
            if (State.FrontOnly && !State.IsEmpty && !Furled)
            {
                if (SailDamageLink.Draws(sail))
                    want = TornOverlay();
                else if (overlayOn && rend.sharedMaterial == mat)
                    want = overlay;
            }
            else if (splitAsked && SailDamageLink.Draws(sail) && (!State.FrontOnly || State.IsEmpty))
            {
                SailDamageLink.SetFrontOnly(sail, false);
                splitAsked = false;
            }
            if (want == null && overlay == null && tornOverlay == null) return;
            Material[] mats = rend.sharedMaterials;
            bool has = mats.Length == 2 && mats[1] == want;
            if (want != null && !has)
                rend.sharedMaterials = new[] { mats.Length > 0 ? mats[0] : mat, want };
            else if (want == null && mats.Length > 1 && (Array.IndexOf(mats, overlay) >= 0 || Array.IndexOf(mats, tornOverlay) >= 0))
                rend.sharedMaterials = Array.FindAll(mats, m => m != overlay && m != tornOverlay);
        }

        private Material tornOverlay;
        private bool splitAsked;

        private void RemoveOverlays()
        {
            if (splitAsked)
            {
                SailDamageLink.SetFrontOnly(sail, false);
                splitAsked = false;
            }
            if (rend == null || overlay == null && tornOverlay == null) return;
            Material[] mats = rend.sharedMaterials;
            if (mats.Length > 1)
                rend.sharedMaterials = Array.FindAll(mats, m => m != overlay && m != tornOverlay);
        }

        // A torn sail (Sail Damage 1.0.3+): Sail Damage draws the holes into the plain canvas it wears and into the
        // painted one, which goes on top here as a one-sided layer that lets the holes through (blended; the game has
        // no one-sided cutout shader with a normal map).
        private Material TornOverlay()
        {
            if (!SailDamageLink.SetFrontOnly(sail, true)) return null; // its plain canvas not there yet: next frame
            splitAsked = true;
            Texture front = SailDamageLink.FrontTexture(sail, out Vector2 scale, out Vector2 offset);
            if (front == null) return null;
            if (tornOverlay == null)
            {
                Shader s = Shader.Find("Legacy Shaders/Transparent/Diffuse");
                if (s == null) return null;
                tornOverlay = new Material(s) { name = (mat != null ? mat.name : "SailEmblem") + " (front, torn)", color = Color.white };
            }
            if (tornOverlay.mainTexture != front) tornOverlay.mainTexture = front;
            tornOverlay.mainTextureScale = scale;
            tornOverlay.mainTextureOffset = offset;
            return tornOverlay;
        }

        private static Texture2D Upload(Texture2D tex, Color32[][] mips, int w, int h, string name)
        {
            if (tex == null || tex.width != w || tex.height != h || tex.mipmapCount != mips.Length)
            {
                if (tex != null) Destroy(tex);
                tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            }
            for (int l = 0; l < mips.Length && l < tex.mipmapCount; l++)
                tex.SetPixels32(mips[l], l);
            tex.Apply(false);
            return tex;
        }

        // The largest share (up to 1) of the emblem's size at which all its painted points lie on the cloth.
        private static float Fit(ClothGeometry geom, EmblemDef def, bool mirror, float cx, float cy, float wM, float hM, float margin)
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
