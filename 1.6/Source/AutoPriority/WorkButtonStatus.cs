using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoPriority
{
    [HarmonyPatch(typeof(Def), "get_LabelCap")]
    public static class WorkButtonLabelPatch
    {
        public static void Postfix(Def __instance, ref TaggedString __result)
        {
            var mainButton = __instance as MainButtonDef;
            if (mainButton == null || mainButton.defName != "Work" ||
                Verse.Current.ProgramState != ProgramState.Playing)
            {
                return;
            }

            AutoPrioritySettings settings = AutoPrioritySettings.Current;
            if (settings == null)
            {
                return;
            }

            if (settings.IsCalculating)
            {
                __result = "AutoPriority.WorkButton.Calculating".Translate();
            }
            else if (settings.IsAdjusting)
            {
                __result = "AutoPriority.WorkButton.Adjusting".Translate();
            }
        }
    }

    [HarmonyPatch(typeof(MainButtonWorker), nameof(MainButtonWorker.DoButton))]
    public static class WorkButtonRightClickPatch
    {
        public static bool Prefix(MainButtonWorker __instance, Rect rect)
        {
            Event currentEvent = Event.current;
            if (__instance.def == null || __instance.def.defName != "Work" ||
                currentEvent == null || currentEvent.type != EventType.MouseDown ||
                currentEvent.button != 1 || !Mouse.IsOver(rect))
            {
                return true;
            }

            AutoPriorityMod mod = LoadedModManager.GetMod<AutoPriorityMod>();
            if (mod == null)
            {
                return true;
            }

            currentEvent.Use();
            Find.WindowStack.Add(new Dialog_ModSettings(mod));
            return false;
        }
    }
}
