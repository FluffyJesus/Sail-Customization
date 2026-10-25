using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace SailEmblems
{
    internal enum EmblemKind { Emblem, Pattern }

    /// <summary>One emblem or pattern: a coverage mask (0 = canvas, 255 = paint) plus a thumbnail.</summary>
    internal sealed class EmblemDef
    {
        public string Id;        // "e:anchor", "p:stripes_vertical", "e:custom/myflag" - what the save stores
        public string Name;      // shown in the window
        public EmblemKind Kind;
        public bool Custom;
        public int W, H;
        public byte[] Mask;      // W*H, row 0 = bottom (Unity texture order)
        public Texture2D Thumb;  // white with the mask as alpha, for the window

        /// <summary>Bilinear coverage at (x, y) in 0..1 of the mask; 0 outside.</summary>
        public float Sample(float x, float y) => Sample(x, y, 1f);

        /// <summary>
        /// Coverage at (x, y) where one canvas texel covers <paramref name="footprint"/> mask pixels: read from the
        /// mask halved that many times (trilinear), or a big sail's canvas picked single pixels out of a sharp mask and
        /// its edges came out toothed (Кэп, lateen).
        /// </summary>
        public float Sample(float x, float y, float footprint)
        {
            if (x < 0f || y < 0f || x > 1f || y > 1f) return 0f;
            if (footprint <= 1f) return Sample(0, x, y);
            if (mips == null) lock (this) if (mips == null) MakeMips(); // pool threads paint several sails at once
            float lod = Mathf.Min(Mathf.Log(footprint, 2f), mips.Length - 1);
            int l = Mathf.FloorToInt(lod);
            float a = Sample(l, x, y);
            return l + 1 < mips.Length ? Mathf.Lerp(a, Sample(l + 1, x, y), lod - l) : a;
        }

        private byte[][] mips;
        private int[] mipW, mipH;

        private float Sample(int level, float x, float y)
        {
            byte[] m = level == 0 ? Mask : mips[level];
            int w = level == 0 ? W : mipW[level], h = level == 0 ? H : mipH[level];
            float fx = x * w - 0.5f, fy = y * h - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, w - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
            float a = m[y0 * w + x0] + (m[y0 * w + x1] - m[y0 * w + x0]) * tx;
            float b = m[y1 * w + x0] + (m[y1 * w + x1] - m[y1 * w + x0]) * tx;
            return (a + (b - a) * ty) / 255f;
        }

        private void MakeMips()
        {
            int levels = 1, w = W, h = H;
            while (w > 1 || h > 1) { w = Mathf.Max(1, w / 2); h = Mathf.Max(1, h / 2); levels++; }
            var m = new byte[levels][];
            mipW = new int[levels];
            mipH = new int[levels];
            m[0] = Mask; mipW[0] = W; mipH[0] = H;
            for (int l = 1; l < levels; l++)
            {
                int sw = mipW[l - 1], sh = mipH[l - 1], dw = Mathf.Max(1, sw / 2), dh = Mathf.Max(1, sh / 2);
                byte[] src = m[l - 1], dst = new byte[dw * dh];
                for (int y = 0; y < dh; y++)
                    for (int x = 0; x < dw; x++)
                    {
                        int x0 = Mathf.Min(x * 2, sw - 1), x1 = Mathf.Min(x * 2 + 1, sw - 1);
                        int y0 = Mathf.Min(y * 2, sh - 1) * sw, y1 = Mathf.Min(y * 2 + 1, sh - 1) * sw;
                        dst[y * dw + x] = (byte)((src[y0 + x0] + src[y0 + x1] + src[y1 + x0] + src[y1 + x1] + 2) >> 2);
                    }
                m[l] = dst; mipW[l] = dw; mipH[l] = dh;
            }
            mips = m;
        }

        private Vector2[] solid;

        /// <summary>Points (0..1) where the emblem is painted, on a coarse grid: to keep it on the cloth.</summary>
        public Vector2[] Solid
        {
            get
            {
                if (solid != null) return solid;
                lock (this) // pool threads paint several sails at once
                {
                    if (solid != null) return solid;
                    var list = new List<Vector2>();
                    const int N = 40;
                    for (int j = 0; j < N; j++)
                        for (int i = 0; i < N; i++)
                        {
                            var p = new Vector2((i + 0.5f) / N, (j + 0.5f) / N);
                            if (Sample(p.x, p.y) > 0.5f) list.Add(p);
                        }
                    return solid = list.ToArray();
                }
            }
        }
    }

    /// <summary>
    /// Built-in emblems and patterns (PNG, embedded in the DLL) plus the player's own PNGs from
    /// BepInEx/config/SailEmblems/emblems and .../patterns. A picture with transparency uses its alpha;
    /// one without (black on white) uses its darkness.
    /// </summary>
    internal static class EmblemLibrary
    {
        internal static readonly List<EmblemDef> Emblems = new List<EmblemDef>();
        internal static readonly List<EmblemDef> Patterns = new List<EmblemDef>();
        private static readonly Dictionary<string, EmblemDef> byId = new Dictionary<string, EmblemDef>();

        internal static string CustomDir { get { return Path.Combine(Paths.ConfigPath, "SailEmblems"); } }

        internal static EmblemDef Get(string id)
        {
            EmblemDef d;
            return id != null && byId.TryGetValue(id, out d) ? d : null;
        }

        internal static void Load()
        {
            var asm = typeof(Plugin).Assembly;
            foreach (string res in asm.GetManifestResourceNames())
            {
                if (!res.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                EmblemKind kind;
                string prefix;
                if (res.StartsWith("SailEmblems.emblems.")) { kind = EmblemKind.Emblem; prefix = "SailEmblems.emblems."; }
                else if (res.StartsWith("SailEmblems.patterns.")) { kind = EmblemKind.Pattern; prefix = "SailEmblems.patterns."; }
                else continue;

                string name = res.Substring(prefix.Length, res.Length - prefix.Length - 4);
                using (var s = asm.GetManifestResourceStream(res))
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    Add(kind, name, ms.ToArray(), false);
                }
            }

            try
            {
                foreach (var pair in new[] { new KeyValuePair<string, EmblemKind>("emblems", EmblemKind.Emblem),
                                             new KeyValuePair<string, EmblemKind>("patterns", EmblemKind.Pattern) })
                {
                    string dir = Path.Combine(CustomDir, pair.Key);
                    Directory.CreateDirectory(dir);
                    foreach (string f in Directory.GetFiles(dir, "*.png"))
                        Add(pair.Value, Path.GetFileNameWithoutExtension(f), File.ReadAllBytes(f), true);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Emblems] custom pictures not loaded: " + e.Message);
            }

            Emblems.Sort((a, b) => a.Custom != b.Custom ? (a.Custom ? 1 : -1) : string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            Patterns.Sort((a, b) => a.Custom != b.Custom ? (a.Custom ? 1 : -1) : string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        }

        private static void Add(EmblemKind kind, string name, byte[] png, bool custom)
        {
            // these split the save record (EmblemStore)
            if (name.IndexOfAny(new[] { ';', '=', '|' }) >= 0)
            {
                Plugin.Log.LogWarning("[Emblems] picture name '" + name + "' has ; = or | in it, rename the file to use it");
                return;
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, png, false))
            {
                Plugin.Log.LogWarning("[Emblems] cannot read picture " + name);
                UnityEngine.Object.Destroy(tex);
                return;
            }

            Color32[] px = tex.GetPixels32();
            bool hasAlpha = false;
            foreach (var p in px) if (p.a < 250) { hasAlpha = true; break; }

            var mask = new byte[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                var p = px[i];
                if (hasAlpha) mask[i] = p.a;
                else mask[i] = (byte)(255 - (p.r * 30 + p.g * 59 + p.b * 11) / 100); // dark = paint
            }

            var thumbPx = new Color32[px.Length];
            for (int i = 0; i < px.Length; i++) thumbPx[i] = new Color32(255, 255, 255, mask[i]);
            var thumb = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, true);
            thumb.SetPixels32(thumbPx);
            thumb.Apply(true, true);
            thumb.name = "SailEmblemThumb_" + name;

            var def = new EmblemDef
            {
                Kind = kind,
                Custom = custom,
                Name = (custom ? "* " : "") + name.Replace('_', ' '),
                Id = (kind == EmblemKind.Emblem ? "e:" : "p:") + (custom ? "custom/" : "") + name,
                W = tex.width,
                H = tex.height,
                Mask = mask,
                Thumb = thumb,
            };
            UnityEngine.Object.Destroy(tex);

            if (byId.ContainsKey(def.Id)) return;
            byId[def.Id] = def;
            (kind == EmblemKind.Emblem ? Emblems : Patterns).Add(def);
        }
    }

    /// <summary>Paint colours: muted, period-plausible pigments. Index is what the save stores.</summary>
    internal static class Palette
    {
        internal static readonly Color32[] Colors =
        {
            new Color32(150,  34,  30, 255), // madder red
            new Color32(168,  84,  44, 255), // rust
            new Color32(186, 140,  60, 255), // ochre
            new Color32(204, 176, 108, 255), // straw
            new Color32( 96, 104,  56, 255), // olive
            new Color32( 46,  86,  58, 255), // forest green
            new Color32( 44,  96, 104, 255), // sea teal
            new Color32( 38,  58,  98, 255), // navy
            new Color32( 66,  58, 108, 255), // indigo
            new Color32(104,  46,  80, 255), // plum
            new Color32( 92,  62,  42, 255), // umber
            new Color32( 34,  32,  30, 255), // lamp black
            new Color32(232, 224, 204, 255), // bone white
        };

        internal static readonly string[] Names =
        {
            "Madder red", "Rust", "Ochre", "Straw", "Olive", "Forest green", "Sea teal",
            "Navy", "Indigo", "Plum", "Umber", "Lamp black", "Bone white",
        };

        internal static Color32 Get(int i)
        {
            return Colors[Mathf.Clamp(i, 0, Colors.Length - 1)];
        }
    }
}
