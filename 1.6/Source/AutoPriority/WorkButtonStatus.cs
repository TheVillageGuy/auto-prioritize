using HarmonyLib;
using RimWorld;
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
}
