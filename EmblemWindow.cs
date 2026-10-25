using HarmonyLib;
using UnityEngine;

namespace SailEmblems
{
    /// <summary>
    /// The paint window (IMGUI), opened by the shipyard "Paint" button for the selected sail.
    /// Every click is applied to the sail at once, so the player sees it on the boat; the order pays for it.
    /// </summary>
    internal static class EmblemWindow
    {
        private const int WindowId = 0x5E3B;
        private const int Cols = 6;
        private const float Cell = 64f;

        internal static bool Open;
        private static Sail target;
        private static int tab; // 0 emblems, 1 patterns
        private static Vector2 scroll;
        private static Rect rect = new Rect(60f, 60f, Cols * (Cell + 6f) + 40f, 600f);
        private static Texture2D[] swatches;
        private static GUIStyle cellStyle, selStyle;

        /// <summary>The mouse is over the window: the shipyard must not take the click (or turn the camera).</summary>
        internal static bool MouseOver
        {
            get
            {
                if (!Open) return false;
                Vector3 m = Input.mousePosition;
                return rect.Contains(new Vector2(m.x, Screen.height - m.y));
            }
        }

        internal static void Show(Sail sail)
        {
            target = sail;
            Open = sail != null;
            var st = SailEmblem.StateOf(sail);
            if (!st.IsEmpty) tab = st.Id.StartsWith("p:") ? 1 : 0;
        }

        internal static void Update()
        {
            if (!Open) return;
            // the sail left the shipyard (bought, removed, shipyard closed) or another one was picked
            if (target == null || GameState.currentShipyard == null || GameState.currentShipyard.sailInstaller.GetCurrentSail() != target)
            {
                Close();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
        }

        // 0.1 locked and hid the cursor here, while the shipyard still needs it.
        private static void Close()
        {
            Open = false;
            target = null;
        }

        internal static void OnGUI()
        {
            if (!Open || target == null) return;
            if (swatches == null) MakeSwatches();
            if (cellStyle == null)
            {
                cellStyle = new GUIStyle(GUI.skin.button) { padding = new RectOffset(6, 6, 6, 6) };
                selStyle = new GUIStyle(cellStyle);
                selStyle.normal.background = selStyle.active.background;
            }
            rect = GUILayout.Window(WindowId, rect, Draw, "Paint the sail");
        }

        private static void Draw(int id)
        {
            EmblemState st = SailEmblem.StateOf(target);

            int newTab = GUILayout.Toolbar(tab, new[] { "Emblems", "Patterns" });
            if (newTab != tab) { tab = newTab; scroll = Vector2.zero; }

            var list = tab == 0 ? EmblemLibrary.Emblems : EmblemLibrary.Patterns;
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(330f));
            int i = -1;
            bool done = false;
            while (!done)
            {
                GUILayout.BeginHorizontal();
                for (int c = 0; c < Cols; c++, i++)
                {
                    if (i >= list.Count) { done = true; break; }
                    if (i < 0)
                    {
                        // "plain sail" first
                        if (GUILayout.Button("none", st.IsEmpty ? selStyle : cellStyle, GUILayout.Width(Cell), GUILayout.Height(Cell)))
                            Apply(new EmblemState());
                        continue;
                    }
                    var def = list[i];
                    bool sel = st.Id == def.Id;
                    var content = new GUIContent(def.Thumb, def.Name);
                    if (GUILayout.Button(content, sel ? selStyle : cellStyle, GUILayout.Width(Cell), GUILayout.Height(Cell)))
                    {
                        // keep the colour and placement within a kind; emblem and pattern sizes mean different things
                        var ns = st.IsEmpty || st.IsPattern != (def.Kind == EmblemKind.Pattern) ? EmblemState.Default(def.Id, target) : st;
                        if (!st.IsEmpty) { ns.Color = st.Color; ns.Mirror = st.Mirror; }
                        ns.Id = def.Id;
                        Apply(ns);
                    }
                }
                GUILayout.EndHorizontal();
                if (i >= list.Count) done = true;
            }
            GUILayout.EndScrollView();
            GUILayout.Label(string.IsNullOrEmpty(GUI.tooltip) ? " " : GUI.tooltip);

            GUI.enabled = !st.IsEmpty;
            GUILayout.Label("Colour: " + Palette.Names[Mathf.Clamp(st.Color, 0, Palette.Names.Length - 1)]);
            GUILayout.BeginHorizontal();
            for (int c = 0; c < Palette.Colors.Length; c++)
            {
                bool sel = st.Color == c;
                if (GUILayout.Button(swatches[c], sel ? selStyle : cellStyle, GUILayout.Width(30f), GUILayout.Height(30f)))
                {
                    st.Color = c;
                    Apply(st);
                }
            }
            GUILayout.EndHorizontal();

            bool pattern = st.IsPattern;
            GUILayout.BeginHorizontal();
            if (pattern)
            {
                // patterns: their own scale up and across the sail (Кэп, 0.4)
                Stepper("Height x" + st.Size.ToString("0.0"), "-", "+", ref st.Size, 0.1f, 0.3f, 3f);
                GUILayout.Space(12f);
                Stepper("Width x" + st.Stretch.ToString("0.0"), "-", "+", ref st.Stretch, 0.1f, 0.3f, 3f);
            }
            else
                Stepper("Size " + Mathf.RoundToInt(st.Size * 100f) + "%", "-", "+", ref st.Size, 0.05f, 0.15f, 0.95f);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Stepper("Up " + Signed(st.Offset), "down", "up", ref st.Offset, 0.05f, -0.5f, 0.5f);
            GUILayout.Space(12f);
            Stepper("Side " + Signed(st.OffsetX), "left", "right", ref st.OffsetX, 0.05f, -0.5f, 0.5f);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            bool mirror = GUILayout.Toggle(st.Mirror, "Mirror");
            if (mirror != st.Mirror) { st.Mirror = mirror; Apply(st); }
            if (pattern)
            {
                GUILayout.Space(12f);
                bool foot = GUILayout.Toggle(st.AlongFoot, new GUIContent("Along the foot", "Lay the pattern along the sail's lower edge (a gaff's boom) instead of level"));
                if (foot != st.AlongFoot) { st.AlongFoot = foot; Apply(st); }
            }
            if (GUILayout.Button("Reset", GUILayout.Width(60f)))
            {
                var d = EmblemState.Default(st.Id, target);
                d.Color = st.Color;
                Apply(d);
            }
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            if (pendingApply)
            {
                pendingApply = false;
                Apply(st);
            }

            GUILayout.Space(6f);
            int fee = Fee(target);
            GUILayout.Label(target.IsInstalled()
                ? (fee > 0 ? "Repainting this sail: " + fee + " (taking the paint off is free)" : "Repainting this sail costs a share of its price")
                : "Painting adds " + Mathf.RoundToInt(Plugin.PriceShare.Value * 100f) + "% to the sail price");
            GUILayout.Label("Your own pictures: " + EmblemLibrary.CustomDir);
            if (GUILayout.Button("Done")) Close();
            GUI.DragWindow();
        }

        private static string Signed(float v) => (v > 0.001f ? "+" : "") + Mathf.RoundToInt(v * 100f) + "%";

        // label, two buttons stepping the value (applied at once); the state is the window's copy, saved by Apply
        private static void Stepper(string label, string less, string more, ref float value, float step, float min, float max)
        {
            GUILayout.Label(label, GUILayout.Width(90f));
            float before = value;
            if (GUILayout.Button(less, GUILayout.Width(44f))) value = Mathf.Max(min, value - step);
            if (GUILayout.Button(more, GUILayout.Width(44f))) value = Mathf.Min(max, value + step);
            value = Mathf.Round(value * 100f) / 100f;
            if (!Mathf.Approximately(before, value)) pendingApply = true;
        }

        private static bool pendingApply;

        private static int Fee(Sail sail)
        {
            Shipyard yard = GameState.currentShipyard;
            return yard != null ? Mathf.RoundToInt(EmblemPricePatch.Fee(sail) * yard.GetCurrencyRate()) : 0;
        }

        private static void Apply(EmblemState st)
        {
            SailEmblem.Set(target, st, true);
            if (GameState.currentShipyard != null) GameState.currentShipyard.UpdateOrder();
            if (ShipyardUI.instance != null)
            {
                ShipyardUI.instance.UpdateDescriptionText();
                ShipyardUI.instance.UpdateMoveButtons(); // the button shows the emblem's name
            }
        }

        private static void MakeSwatches()
        {
            swatches = new Texture2D[Palette.Colors.Length];
            for (int c = 0; c < swatches.Length; c++)
            {
                // IMGUI draws a button's image at most at its own size: 0.1's 2x2 swatches were a dot (Кэп)
                const int size = 22;
                var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var px = new Color32[size * size];
                for (int i = 0; i < px.Length; i++) px[i] = Palette.Colors[c];
                t.SetPixels32(px);
                t.Apply();
                swatches[c] = t;
            }
        }
    }

    /// <summary>Clicks on the window went through to the shipyard buttons behind it (0.1).</summary>
    [HarmonyPatch(typeof(MouseButtonPointer), "LateUpdate")]
    internal static class PointerThroughWindowPatch
    {
        private static readonly AccessTools.FieldRef<MouseButtonPointer, GoPointerButton> Pointed =
            AccessTools.FieldRefAccess<MouseButtonPointer, GoPointerButton>("pointedAtButton");

        private static void Prefix(MouseButtonPointer __instance)
        {
            if (EmblemWindow.MouseOver) Pointed(__instance) = null;
        }
    }

    /// <summary>A press on the window (dragging it, a button) also started turning the shipyard camera (0.1).</summary>
    [HarmonyPatch(typeof(ShipyardCameraRotator), "Update")]
    internal static class CameraThroughWindowPatch
    {
        private static bool Prefix()
        {
            return !(EmblemWindow.MouseOver && Input.GetMouseButtonDown(0));
        }
    }
}
