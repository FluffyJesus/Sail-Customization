using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SailEmblems
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "fluffyjesus.sailwind.sailemblems";
        public const string NAME = "Sail Emblems";
        public const string VERSION = "1.1.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> PriceShare;
        internal static ConfigEntry<int> Resolution;
        internal const float TexelsPerMetre = 100f;
        internal static ConfigEntry<float> ButtonColumn;
        internal static ConfigEntry<float> ButtonRow;
        internal static ConfigEntry<bool> VerboseLog;

        /// <summary>Details of every painting, saving, loading and linking: only with Debug / VerboseLog on.</summary>
        internal static void Debug(string text)
        {
            if (VerboseLog != null && VerboseLog.Value) Log.LogInfo(text);
        }

        private Harmony harmony;
        private bool linked;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Offer emblems and patterns at the shipyard");
            PriceShare = Config.Bind("General", "PriceShare", 0.1f,
                "Painting price as a share of the sail price (a new painted sail costs this much more; repainting an installed sail costs this share)");
            // 0.2.1's "Resolution" (1024 for every sail) left steps along the emblems on a lateen: a new key, so the
            // new default reaches configs written before
            Resolution = Config.Bind("Look", "MaxResolution", 2048,
                new ConfigDescription("Largest side of a painted sail texture, px. A sail gets about " + TexelsPerMetre +
                                      " px per metre up to this (higher = sharper emblems on big sails, more memory)",
                    new AcceptableValueList<int>(512, 1024, 2048, 4096)));
            ButtonColumn = Config.Bind("Shipyard", "ButtonColumn", 4f, "Where the Paint button stands: columns left of the vanilla \"- Width\" button");
            ButtonRow = Config.Bind("Shipyard", "ButtonRow", 0f, "Where the Paint button stands: rows up from the Width row");
            VerboseLog = Config.Bind("Debug", "VerboseLog", false,
                "Write details of every painting (sizes, layout, shaders), saving and loading to BepInEx/LogOutput.log. Warnings and errors are always written.");

            EmblemLibrary.Load();
            harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{NAME} {VERSION} loaded: {EmblemLibrary.Emblems.Count} emblems, {EmblemLibrary.Patterns.Count} patterns");
        }

        private void Update()
        {
            // other plugins are all loaded by the first frame
            if (!linked)
            {
                linked = true;
                StuddingLink.Init(harmony);
            }
            EmblemWindow.Update();
            EmblemStore.Update();
        }

        private void OnGUI()
        {
            EmblemWindow.OnGUI();
        }
    }
}
