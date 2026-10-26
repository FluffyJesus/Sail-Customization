using System;
using System.Collections.Generic;
using UnityEngine;

namespace SailEmblems
{
    /// <summary>
    /// Lives on a painted Sail and keeps its paint on: each change is painted in the background (PaintJob) into a copy
    /// of the sail's canvas (PaintedCanvas) whose texture covers only the cloth's part of the UV square, with the
    /// material tiling and the sail colour baked in, so the paint keeps its own colour on a dyed sail.
    ///
    /// Every frame it looks at what the cloth wears (ClothSlot) and answers it:
    ///  - a copy of ours (furling saves renderer.material; recolouring sets renderer.material.color): ours goes back on.
    ///    0.1 took the copy for a new canvas and destroyed its texture, and the sail came back white;
    ///  - ours with another texture (Shipyard Expansion's sail textures): that texture is the new canvas;
    ///  - another material: a new canvas from another mod, painted over;
    ///  - Sail Damage's torn canvas: it was made from ours, nothing to do (the link: SailDamageLink).
    /// Front side only adds a one-sided layer over a plain canvas (FrontLayer).
    /// </summary>
    internal sealed class SailEmblem : MonoBehaviour
    {
        /// <summary>What is painted (at the shipyard: what is ordered).</summary>
        internal EmblemState State;
        /// <summary>What the sail had when the shipyard order was opened: repainting an installed sail is paid for.</summary>
        internal EmblemState Committed;
        /// <summary>Where the cloth's front faces look, from the last painting (Front side only paints that side).</summary>
        internal string FrontSide;

        private Sail sail;
        private ClothSlot slot;
        private Material source;           // the game's canvas, worn again when the paint is taken off
        private Color sailColor = Color.white;
        private readonly PaintedCanvas pc = new PaintedCanvas();
        private readonly FrontLayer front = new FrontLayer();
        private PaintJob job;
        private bool repaint = true;
        private bool stalled;              // nothing to paint with (unknown picture, unpaintable cloth) until the state changes
        private bool reported;             // this painting described in the log (or its failure)
        private bool framedSet;            // measured on the set sail (furled, the bones squeeze the cloth up)
        private Vector3 frameScale;

        internal Texture PaintedTexture => pc.Painted;
        internal Texture CleanTexture => pc.Clean;

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

        /// <summary>Paint the sail (an empty state takes the paint off). <paramref name="now"/>: start at once, not at the end of the frame.</summary>
        internal static void Set(Sail s, EmblemState st, bool now = false)
        {
            if (s == null) return;
            var e = Of(s, !st.IsEmpty);
            if (e == null) return;
            if (!e.State.Equals(st)) e.reported = false;
            e.State = st;
            e.repaint = true;
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
            // The sail is gone (shipyard rebuild, sold): its cloth goes with it, only what we made needs freeing.
            pc.Destroy();
            front.Destroy();
        }

        // ------------------------------------------------------------------ every frame

        internal void Refresh()
        {
            if (sail == null) return;
            if (slot == null && (slot = ClothSlot.Of(sail)) == null) return;

            Material worn = slot.Worn;
            if (State.IsEmpty)
            {
                if (pc.Mat != null) TakeOff(worn);
                return;
            }
            if (worn == null || stalled) return;
            bool torn = SailDamageLink.Draws(sail);

            if (pc.Mat == null)
            {
                // the first painting (while it is worked out, the canvas it started from stays)
                if (job == null)
                {
                    if (ClothSlot.IsPlaceholder(worn)) return; // furled before the game saved its canvas: wait for the real one
                    if (torn)
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
                    repaint = true;
                }
            }
            else
            {
                switch (ClothSlot.Classify(worn, pc.Mat, pc.Twin, torn))
                {
                    case Wearing.PaintedTwin:
                        // Sail Damage mended the sail as new and put back what it was given: ours goes on again
                        slot.Worn = pc.Mat;
                        break;
                    case Wearing.OurCopy:
                        // furl/unfurl handed back a copy of ours: wear ours again (the copy shares our texture)
                        slot.Worn = pc.Mat;
                        Destroy(worn);
                        break;
                    case Wearing.OursRetextured:
                        // Shipyard Expansion (SailTextureChanger: renderer.material.mainTexture = ..., on loading and
                        // leaving the shipyard too). With our tiling it showed the whole texture sheet on the sail (Кэп).
                        // That texture is the new canvas, laid as the game's.
                        Plugin.Debug("[Emblems] " + sail.sailName + ": canvas texture changed to '" + (worn.mainTexture != null ? worn.mainTexture.name : "none") + "', painting it");
                        if (worn.mainTexture != null) source.mainTexture = worn.mainTexture;
                        slot.Worn = pc.Mat;
                        Destroy(worn);
                        repaint = true;
                        break;
                    case Wearing.Foreign:
                        // another mod put a new canvas on the sail: paint over that one
                        Plugin.Debug("[Emblems] " + sail.sailName + ": new canvas '" + worn.name + "' on the sail, painting it");
                        source = worn;
                        sailColor = ColorOf(worn);
                        front.Remove(slot, sail);
                        pc.DropMaterial();
                        repaint = true;
                        break;
                    // Ours, SailDamage (its torn copy of ours), Placeholder (furled): nothing to do
                }
                if (pc.Mat != null)
                {
                    // painted on both faces while torn, now mended as new: the front side can be painted alone again
                    if (State.FrontOnly && !pc.Front && job == null && !torn && slot.OnCloth == pc.Mat && FrontLayer.Standard != null)
                        repaint = true;
                    front.Sync(slot, sail, State, pc);
                }
            }

            if (job != null)
            {
                if (!job.Done) return;
                PaintJob done = job;
                job = null;
                // shown even if something else was asked for meanwhile (another click): that one is started next
                Finish(done);
            }
            if (!repaint && FrameStale()) repaint = true;
            if (!repaint) return;
            // Furled, and this kind of sail never seen set yet (a save loaded with the sails furled): its folded shape
            // would give two small emblems on the first unfurl. Wait until it is set (PaintJob keeps set shapes).
            if (sail.currentUnroll < 0.9f && !PaintJob.KnowsSetShape(slot.Renderer, ClothScale)) return;
            repaint = false;
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
            front.Remove(slot, sail);
            if (source != null && source.HasProperty("_Color")) source.color = sailColor;
            if (SailDamageLink.Draws(sail))
                SailDamageLink.TakeCanvas(sail, source, null);
            else if (source != null)
            {
                Wearing w = ClothSlot.Classify(worn, pc.Mat, pc.Twin, false);
                if (w == Wearing.Ours || w == Wearing.OurCopy || w == Wearing.PaintedTwin || w == Wearing.Nothing)
                {
                    slot.Worn = source;
                    if (w == Wearing.OurCopy) Destroy(worn);
                }
            }
            pc.Destroy();
            front.Destroy();
        }

        /// <summary>Sail.ChangeSailColor just recoloured a copy of the material on the cloth: paint the new colour in.</summary>
        internal void Recoloured()
        {
            if (pc.Mat == null || slot == null) return;
            Material worn = slot.OnCloth;
            if (worn == null || ClothSlot.IsPlaceholder(worn) || !worn.HasProperty("_Color")) return; // furled: the game recolours the placeholder only
            sailColor = worn.color;
            if (source != null && source.HasProperty("_Color")) source.color = sailColor;
            bool ours = ClothSlot.Classify(worn, pc.Mat, pc.Twin, false) == Wearing.OurCopy;
            if (ours) slot.Renderer.sharedMaterial = pc.Mat;
            repaint = true;
            Refresh();
            // our copy, or the recoloured copy of Sail Damage's torn material that TakeCanvas took off
            if (ours || worn != pc.Mat && worn != source && slot.OnCloth != worn) Destroy(worn);
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

        // ------------------------------------------------------------------ painting

        private Vector3 ClothScale => sail.cloth != null && sail.cloth.transform.parent != null ? sail.cloth.transform.parent.localScale : Vector3.one;

        // Measured on a furled sail, or the sail was scaled since (shipyard width buttons, LoadScale): paint again.
        private bool FrameStale()
        {
            if (pc.Painted == null) return false;
            if (!framedSet && sail.currentUnroll >= 0.9f) return true;
            return (ClothScale - frameScale).sqrMagnitude > 1e-6f;
        }

        private void Begin()
        {
            EmblemDef def = EmblemLibrary.Get(State.Id);
            if (def == null)
            {
                if (!reported) Plugin.Log.LogWarning("[Emblems] unknown emblem '" + State.Id + "' on " + sail.sailName + " (custom picture removed?), kept in the save");
                reported = true;
                Stall();
                return;
            }
            if (!CanPaint(sail))
            {
                // painted before it was taken off the list (fin sail): stays plain, the paint stays in the save
                Plugin.Log.LogWarning("[Emblems] " + sail.sailName + " cannot be painted, left plain");
                Stall();
                return;
            }
            // the plain canvas: for the studding sails, Sail Damage's patches (their weave showed the emblem, Кэп), the back
            bool wantClean = StuddingLink.Present || SailDamageLink.Present || State.FrontOnly;
            job = PaintJob.Start(sail, slot.Renderer, source, sailColor, State, def, wantClean, out string why);
            if (job == null)
            {
                Plugin.Log.LogWarning("[Emblems] " + sail.sailName + ": " + why + ", it cannot be painted");
                Stall();
                return;
            }
            framedSet = job.FramedSet;
            frameScale = ClothScale;
        }

        // nothing to paint with until the state changes; what was painted comes off
        private void Stall()
        {
            stalled = true;
            if (pc.Mat != null) TakeOff(slot.Worn);
        }

        // Back on the main thread: upload, dress the material, hand it to Sail Damage or wear it.
        private void Finish(PaintJob j)
        {
            if (j.Error != null)
            {
                if (!reported) Plugin.Log.LogError("[Emblems] painting " + sail.sailName + " failed: " + j.Error);
                reported = true;
                return;
            }
            if (State.IsEmpty || source == null) return;
            if (source.mainTexture != j.SourceTexture)
            {
                // The canvas texture changed while this was worked out: Shipyard Expansion sets its chosen sail texture
                // (or none) right after leaving the shipyard rebuilds the sails, on the game's material we started from.
                // 1.1.0 wore the old one, and a texture turned off came back (Кэп). Paint the new one instead.
                repaint = true;
                return;
            }
            bool frontOnly = j.State.FrontOnly && FrontLayer.Standard != null && !SailDamageLink.Draws(sail);
            pc.Apply(j, source, sailColor, frontOnly, sail.sailName);
            FrontSide = j.Geom.FrontSide;
            if (pc.Front) front.Dress(pc);
            if (!reported)
                Plugin.Debug("[Emblems] " + sail.sailName + ": " + j.Describe(source) + (pc.Front ? ", front side only: " + FrontSide : ""));
            reported = true;
            if (!SailDamageLink.TakeCanvas(sail, pc.ForSailDamage, pc.Clean))
                slot.Worn = pc.Mat;
            front.Sync(slot, sail, State, pc);
        }

        private static readonly Dictionary<Mesh, bool> paintable = new Dictionary<Mesh, bool>();

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
            ok = PaintJob.Bounds(m.uv, out _);
            Destroy(m);
            paintable[r.sharedMesh] = ok;
            return ok;
        }
    }
}
