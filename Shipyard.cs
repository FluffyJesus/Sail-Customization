using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailEmblems
{
    /// <summary>Shipyard button: a clone of the vanilla width button that opens the paint window.</summary>
    internal sealed class EmblemButton : ShipyardButton
    {
        public override void OnActivate()
        {
            UISoundPlayer.instance.PlayUISound(UISounds.buttonHover, 0.33f, 3f);
            ShipyardUI ui = ShipyardUI.instance;
            ui.HideNewSailButtons();
            ui.CloseSailCategoryMenu();

            if (GameState.currentShipyard != null)
            {
                Sail sail = GameState.currentShipyard.sailInstaller.GetCurrentSail();
                if (sail != null && SailEmblem.CanPaint(sail)) EmblemWindow.Show(sail);
            }
            ui.RefreshButtons();
        }
    }

    /// <summary>
    /// The button shows for the selected sail, new or installed: vanilla recolours installed sails too, and 0.1 made
    /// players buy a new sail just to paint one.
    /// </summary>
    [HarmonyPatch(typeof(ShipyardUI), "UpdateMoveButtons")]
    internal static class EmblemUiPatch
    {
        private static readonly FieldInfo WidthUp = AccessTools.Field(typeof(ShipyardUI), "widthUpButton");
        private static readonly FieldInfo WidthDown = AccessTools.Field(typeof(ShipyardUI), "widthDownButton");
        private static readonly FieldInfo ScaleUp = AccessTools.Field(typeof(ShipyardUI), "scaleUpButton");
        private static readonly FieldInfo ScaleDown = AccessTools.Field(typeof(ShipyardUI), "scaleDownButton");
        private static GameObject button;

        private static void Postfix(ShipyardUI __instance)
        {
            try
            {
                Sail sail = GameState.currentShipyard != null ? GameState.currentShipyard.sailInstaller.GetCurrentSail() : null;
                bool show = Plugin.Enabled.Value && sail != null;

                if (button == null)
                {
                    if (!show) return;
                    button = CreateButton(__instance);
                    if (button == null) return;
                }
                button.SetActive(show);
                if (!show) return;

                EmblemState st = SailEmblem.StateOf(sail);
                EmblemDef def = EmblemLibrary.Get(st.Id);
                button.GetComponent<ShipyardButton>().SetText(!SailEmblem.CanPaint(sail) ? "Can't paint this sail" : def != null ? "Paint: " + def.Name : "Paint sail");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[Emblems] shipyard button failed: " + e);
            }
        }

        private static GameObject CreateButton(ShipyardUI ui)
        {
            if (WidthUp == null || WidthDown == null) return null;
            var up = WidthUp.GetValue(ui) as GameObject;
            var down = WidthDown.GetValue(ui) as GameObject;
            if (up == null || down == null) return null;

            var go = UnityEngine.Object.Instantiate(up, up.transform.parent);
            go.name = "SailEmblemButton";
            var old = go.GetComponent<ShipyardButton>();
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            foreach (var c in go.GetComponents<Component>())
                if (c != null && c.GetType().Name == "Outline") UnityEngine.Object.DestroyImmediate(c);
            go.AddComponent<EmblemButton>();

            // same grid as the Studding Sails buttons: columns left of "- Width", rows up from the Width row
            Vector3 hStep = down.transform.localPosition - up.transform.localPosition;
            var sUp = ScaleUp != null ? ScaleUp.GetValue(ui) as GameObject : null;
            var sDown = ScaleDown != null ? ScaleDown.GetValue(ui) as GameObject : null;
            Vector3 vStep = sUp != null && sDown != null
                ? sDown.transform.localPosition - sUp.transform.localPosition
                : new Vector3(-hStep.y, hStep.x, hStep.z);
            go.transform.localPosition = down.transform.localPosition + hStep * Plugin.ButtonColumn.Value - vStep * Plugin.ButtonRow.Value;
            return go;
        }
    }

    /// <summary>
    /// A painted sail costs a share more (the order of a new sail, and the refund and Sail Damage's repair, which
    /// all go through GetSailPrice).
    /// </summary>
    [HarmonyPatch(typeof(Sail), "GetSailPrice")]
    internal static class EmblemPricePatch
    {
        [ThreadStatic] internal static bool Raw;

        private static void Postfix(Sail __instance, ref float __result)
        {
            if (!Raw && Plugin.Enabled.Value && !SailEmblem.StateOf(__instance).IsEmpty)
                __result *= 1f + Plugin.PriceShare.Value;
        }

        /// <summary>The painter's fee for this sail, before the currency rate.</summary>
        internal static float Fee(Sail sail)
        {
            Raw = true;
            try
            {
                return sail.GetSailPrice() * Plugin.PriceShare.Value;
            }
            finally
            {
                Raw = false;
            }
        }
    }

    /// <summary>
    /// Repainting an installed sail: an order line, as Sail Damage adds its "sail repair". The vanilla order is built
    /// in one go and ends with the total line; add ours and set the total again. Taking the paint off is free.
    /// </summary>
    [HarmonyPatch(typeof(Shipyard), "UpdateOrder", typeof(bool))]
    internal static class EmblemOrderPatch
    {
        private static readonly AccessTools.FieldRef<Shipyard, int> OrderTotal = AccessTools.FieldRefAccess<Shipyard, int>("currentOrderTotal");
        private static readonly AccessTools.FieldRef<Shipyard, string> OrderText = AccessTools.FieldRefAccess<Shipyard, string>("currentOrderText");

        private static void Postfix(Shipyard __instance)
        {
            try
            {
                if (!Plugin.Enabled.Value) return;
                GameObject boat = __instance.GetCurrentBoat();
                if (boat == null) return;
                float rate = __instance.GetCurrencyRate();
                string currency = PlayerGold.GetCurrencyName(__instance.region);
                ShipyardUIOrderText lines = ShipyardUI.instance.shipyardOrderText;
                int added = 0;
                string text = OrderText(__instance) ?? "";
                int at = text.LastIndexOf("\n\n TOTAL: ", StringComparison.Ordinal);
                if (at >= 0) text = text.Substring(0, at);
                foreach (SailEmblem e in boat.GetComponentsInChildren<SailEmblem>(true))
                {
                    Sail sail = e.GetComponent<Sail>();
                    if (sail == null || !sail.IsInstalled() || e.State.IsEmpty || e.State.Equals(e.Committed)) continue;
                    int cost = Mathf.RoundToInt(EmblemPricePatch.Fee(sail) * rate);
                    added += cost;
                    text += "\n" + cost + ": painting " + sail.sailName;
                    lines.AddLine(cost + ": painting " + sail.sailName);
                }
                if (added == 0) return;
                int total = OrderTotal(__instance) += added;
                OrderText(__instance) = text + "\n\n TOTAL: " + total + " " + currency;
                lines.SetTotalLine(total < 0 ? "TOTAL: " + -total + " " + currency + " refund" : "TOTAL: " + total + " " + currency);
                lines.DisplayCurrentText();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[Emblems] order failed: " + e);
            }
        }
    }

    /// <summary>Line in the selected sail description.</summary>
    [HarmonyPatch(typeof(ShipyardUI), "UpdateDescriptionText")]
    internal static class EmblemDescriptionPatch
    {
        private static readonly FieldInfo DescText = AccessTools.Field(typeof(ShipyardUI), "descText");

        private static void Postfix(ShipyardUI __instance)
        {
            try
            {
                if (DescText == null || GameState.currentShipyard == null) return;
                Sail sail = GameState.currentShipyard.sailInstaller.GetCurrentSail();
                EmblemState st = SailEmblem.StateOf(sail);
                EmblemDef def = EmblemLibrary.Get(st.Id);
                if (def == null) return;
                var tm = DescText.GetValue(__instance) as TextMesh;
                if (tm != null) tm.text += "\npainted: " + def.Name + ", " + Palette.Names[Mathf.Clamp(st.Color, 0, Palette.Names.Length - 1)].ToLowerInvariant();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[Emblems] description failed: " + e);
            }
        }
    }

    /// <summary>Sail.ChangeSailColor recolours a copy of the cloth material: the colour goes into our canvas.</summary>
    [HarmonyPatch(typeof(Sail), "ChangeSailColor")]
    internal static class ChangeSailColorPatch
    {
        private static void Postfix(Sail __instance)
        {
            try
            {
                SailEmblem e = SailEmblem.Of(__instance, false);
                if (e != null) e.Recoloured();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Emblems] recolouring failed: " + ex);
            }
        }
    }
}
