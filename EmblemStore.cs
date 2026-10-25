using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace SailEmblems
{
    /// <summary>
    /// Keeping the paint. 0.1 kept it only on the Sail object, and the game rebuilds sails from records:
    ///  - leaving the shipyard (DischargeShip -> CancelOrder) or cancelling an order calls
    ///    SaveableBoatCustomization.LoadData with the records GetData took when the order was opened or paid: every
    ///    sail is destroyed and made again from its prefab (Mast.LoadSail). Each record now carries its sail's paint
    ///    (a table on the SaveSailData object, as Studding Sails keeps its own in the record), so the paint comes back,
    ///    and a cancelled repaint goes back to what was paid for;
    ///  - the save file: GameState.modData under our GUID, the sail found by content as Sail Damage does
    ///    (boat sceneIndex : mast orderIndex : prefab : how many of that prefab come before it on the mast).
    /// </summary>
    internal static class EmblemStore
    {
        private sealed class Box { internal EmblemState State; }

        private static readonly ConditionalWeakTable<SaveSailData, Box> records = new ConditionalWeakTable<SaveSailData, Box>();
        // From the save, not matched to a sail yet (that boat not found yet); written back so nothing is lost.
        private static readonly Dictionary<string, EmblemState> pending = new Dictionary<string, EmblemState>();
        private static float retry;

        internal static string SaveKey(Sail sail)
        {
            Transform parent = sail.transform.parent;
            Mast mast = parent != null ? parent.GetComponent<Mast>() : null;
            SaveableObject boat = sail.GetComponentInParent<SaveableObject>();
            if (boat == null && sail.shipRigidbody != null)
                boat = sail.shipRigidbody.GetComponentInParent<SaveableObject>();
            if (mast == null || boat == null || mast.sails == null)
                return null;
            int n = 0;
            foreach (GameObject go in mast.sails)
            {
                if (go == sail.gameObject)
                    break;
                Sail other = go != null ? go.GetComponent<Sail>() : null;
                if (other != null && other.prefabIndex == sail.prefabIndex)
                    n++;
            }
            return boat.sceneIndex + ":" + mast.orderIndex + ":" + sail.prefabIndex + ":" + n;
        }

        // ------------------------------------------------------------------ records (shipyard rebuilds)

        [HarmonyPatch(typeof(SaveableBoatCustomization), "GetData")]
        private static class GetDataPatch
        {
            private static readonly FieldInfo Refs = AccessTools.Field(typeof(SaveableBoatCustomization), "refs");

            private static void Postfix(SaveableBoatCustomization __instance, SaveBoatCustomizationData __result)
            {
                try
                {
                    var refs = Refs != null ? Refs.GetValue(__instance) as BoatRefs : null;
                    if (refs == null || refs.masts == null || __result == null || __result.sails == null) return;
                    List<SaveSailData> list = __result.sails;
                    var used = new bool[list.Count];
                    int i = 0;
                    // GetData walks the same masts and sails in the same order; check each record anyway (another mod
                    // could add or drop some), as Studding Sails does.
                    foreach (Mast mast in refs.masts)
                    {
                        if (!mast || !mast.gameObject.activeSelf) continue;
                        foreach (GameObject go in mast.sails)
                        {
                            int guess = i++;
                            Sail sail = go != null ? go.GetComponent<Sail>() : null;
                            EmblemState st = SailEmblem.StateOf(sail);
                            if (st.IsEmpty) continue;
                            int at = Find(list, used, guess, mast, sail);
                            if (at < 0)
                            {
                                Plugin.Log.LogWarning("[Emblems] no record for " + sail.sailName + ", its paint is not kept");
                                continue;
                            }
                            used[at] = true;
                            records.Remove(list[at]);
                            records.Add(list[at], new Box { State = st });
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("[Emblems] keeping the paint failed: " + e);
                }
            }

            private static bool Same(SaveSailData d, Mast mast, Sail s)
            {
                return d != null && d.mastIndex == mast.orderIndex && d.prefabIndex == s.prefabIndex &&
                       Mathf.Abs(d.installHeight - s.GetCurrentInstallHeight()) < 0.01f &&
                       Mathf.Abs(d.scaleY - s.GetScaleY()) < 0.001f && Mathf.Abs(d.scaleZ - s.GetScaleZ()) < 0.001f;
            }

            private static int Find(List<SaveSailData> list, bool[] used, int guess, Mast mast, Sail s)
            {
                if (guess < list.Count && !used[guess] && Same(list[guess], mast, s)) return guess;
                for (int k = 0; k < list.Count; k++)
                    if (!used[k] && Same(list[k], mast, s)) return k;
                return -1;
            }
        }

        [HarmonyPatch(typeof(Mast), "LoadSail")]
        private static class LoadSailPatch
        {
            private static void Prefix(SaveSailData sailData, out Box __state)
            {
                __state = null;
                if (sailData != null) records.TryGetValue(sailData, out __state);
            }

            // LoadSail adds the new sail to the mast's list and attaches it: it is the last one.
            private static void Postfix(Mast __instance, Box __state)
            {
                if (__state == null || __instance.sails == null || __instance.sails.Count == 0) return;
                GameObject go = __instance.sails[__instance.sails.Count - 1];
                Sail sail = go != null ? go.GetComponent<Sail>() : null;
                if (sail == null) return;
                SailEmblem.Set(sail, __state.State, true);
                SailEmblem e = SailEmblem.Of(sail, false);
                if (e != null) e.Committed = __state.State;
            }
        }

        // ------------------------------------------------------------------ save file

        [HarmonyPatch(typeof(SaveLoadManager), "SaveModData")]
        private static class SavePatch
        {
            private static void Postfix()
            {
                try
                {
                    var all = new Dictionary<string, EmblemState>(pending);
                    foreach (SailEmblem e in UnityEngine.Object.FindObjectsOfType<SailEmblem>())
                    {
                        Sail sail = e.GetComponent<Sail>();
                        // at the shipyard an order not yet paid for is not the boat's paint
                        EmblemState st = sail != null && sail.IsInstalled() && GameState.currentShipyard != null ? e.Committed : e.State;
                        if (sail == null || !sail.IsInstalled() || st.IsEmpty) continue;
                        string key = SaveKey(sail);
                        if (key != null) all[key] = st;
                    }
                    var sb = new StringBuilder();
                    foreach (KeyValuePair<string, EmblemState> kv in all)
                    {
                        if (sb.Length > 0) sb.Append(';');
                        sb.Append(kv.Key).Append('=').Append(kv.Value.Encode());
                    }
                    if (sb.Length == 0) GameState.modData.Remove(Plugin.GUID);
                    else GameState.modData[Plugin.GUID] = sb.ToString();
                    Plugin.Debug("[Emblems] saved " + all.Count + " painted sails");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("[Emblems] saving failed: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(SaveLoadManager), "LoadModData")]
        private static class LoadPatch
        {
            // Vanilla calls it at the very end of LoadGame, when the boats and their sails exist.
            private static void Postfix()
            {
                pending.Clear();
                GameState.modData.TryGetValue(Plugin.GUID, out string data);
                if (!string.IsNullOrEmpty(data))
                    foreach (string part in data.Split(';'))
                    {
                        int eq = part.IndexOf('=');
                        if (eq <= 0 || !EmblemState.TryDecode(part.Substring(eq + 1), out EmblemState st)) continue;
                        pending[part.Substring(0, eq)] = st;
                    }
                int n = pending.Count;
                ApplyPending(true);
                Plugin.Debug("[Emblems] loaded " + n + " painted sails" + (pending.Count > 0 ? ", " + pending.Count + " not found yet" : ""));
            }
        }

        private static void ApplyPending(bool now)
        {
            if (pending.Count == 0) return;
            foreach (Sail sail in UnityEngine.Object.FindObjectsOfType<Sail>())
            {
                if (!sail.IsInstalled()) continue;
                string key = SaveKey(sail);
                if (key == null || !pending.TryGetValue(key, out EmblemState st)) continue;
                pending.Remove(key);
                SailEmblem.Set(sail, st, now);
                SailEmblem.Of(sail, false).Committed = st;
            }
        }

        /// <summary>Every few seconds while some saved paint has not found its sail.</summary>
        internal static void Update()
        {
            if (pending.Count == 0 || !GameState.playing) return;
            retry -= Time.unscaledDeltaTime;
            if (retry > 0f) return;
            retry = 5f;
            ApplyPending(false);
        }

        // ------------------------------------------------------------------ shipyard order

        /// <summary>ResetOrder: the order starts from what the boat has now (opened, paid, cancelled).</summary>
        [HarmonyPatch(typeof(Shipyard), "ResetOrder")]
        private static class ResetOrderPatch
        {
            private static void Postfix(Shipyard __instance)
            {
                GameObject boat = __instance.GetCurrentBoat();
                if (boat == null) return;
                foreach (SailEmblem e in boat.GetComponentsInChildren<SailEmblem>(true))
                    e.Committed = e.State;
            }
        }
    }
}
