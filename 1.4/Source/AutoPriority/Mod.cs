using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoPriority
{
    public class AutoPriorityMod : Mod
    {
        public static Dictionary<WorkTypeDef, Func<Pawn, float>> Formulas;

        public static List<WorkTypeDef> KeysWorkingList;
        public static List<int> ValuesWorkingList;

        public static Harmony Harm;

        private string[] buffers;
        private Vector2 scrollPos;

        public AutoPriorityMod(ModContentPack content) : base(content)
        {
            Harm = new Harmony("legodude17.AutoPriority");
            var reAssign = new HarmonyMethod(GetType(), nameof(ReAssign));
            foreach (var method in new[]
                     {
                         AccessTools.Method(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.EnableAndInitialize)),
                         AccessTools.Method(typeof(Pawn), nameof(Pawn.Kill)),
                         AccessTools.Method(typeof(ResurrectionUtility), nameof(ResurrectionUtility.Resurrect)),
                         AccessTools.Method(typeof(Pawn), nameof(Pawn.SpawnSetup)),
                         AccessTools.Method(typeof(Pawn), nameof(Pawn.DeSpawn)),
                         AccessTools.PropertySetter(typeof(SkillRecord), nameof(SkillRecord.Level))
                     })
                Harm.Patch(method, postfix: reAssign);
            Harm.Patch(AccessTools.Method(typeof(SkillRecord), nameof(SkillRecord.Learn)),
                new HarmonyMethod(GetType(), nameof(SkillRecord_Learn_Prefix)),
                new HarmonyMethod(GetType(), nameof(SkillRecord_Learn_Postfix)));
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                Formulas = new Dictionary<WorkTypeDef, Func<Pawn, float>>
                {
                    { WorkTypeDefOf.Warden, pawn => pawn.GetStatValue(StatDefOf.NegotiationAbility) },
                    {
                        WorkTypeDefOf.Handling,
                        pawn => (pawn.GetStatValue(StatDefOf.TameAnimalChance) +
                                 pawn.GetStatValue(StatDefOf.TrainAnimalChance)) / 2
                    }
                };
            });
        }

        public static void SkillRecord_Learn_Prefix(int ___levelInt, out int __state)
        {
            __state = ___levelInt;
        }

        public static void SkillRecord_Learn_Postfix(int ___levelInt, int __state)
        {
            if (___levelInt != __state) ReAssign();
        }

        public override string SettingsCategory() => "AutoPriority".Translate();

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null || AutoPrioritySettings.Current == null)
            {
                Widgets.Label(inRect.TopPartPixels(40f), "AutoPriority.NeedGame".Translate());
                return;
            }

            if (buffers == null) buffers = new string[DefDatabase<WorkTypeDef>.DefCount * 2];
            var viewRect = new Rect(0, 0, inRect.width - 20f, DefDatabase<WorkTypeDef>.DefCount * 90f);
            var listing = new Listing_Standard();
            var workSettings = AutoPrioritySettings.Current.WorkSettings;
            Widgets.BeginScrollView(inRect, ref scrollPos, viewRect);
            listing.Begin(viewRect);
            listing.Label("AutoPriority.WorkSettings.Label".Translate());
            var i = 0;
            foreach (var def in DefDatabase<WorkTypeDef>.AllDefs)
            {
                var pair = workSettings.ContainsKey(def) ? workSettings[def] : new Pair<int, int>(0, 0);
                var first = pair.First;
                var second = pair.Second;
                listing.Label(def.labelShort);
                listing.TextFieldNumericLabeled("AutoPriority.NumPawns".Translate(), ref first, ref buffers[i]);
                i++;
                listing.TextFieldNumericLabeled("AutoPriority.Priority".Translate(), ref second, ref buffers[i], 0, 4);
                i++;
                if (first != pair.First || second != pair.Second)
                    workSettings.SetOrAdd(def, new Pair<int, int>(first, second));
                listing.GapLine(20f);
            }

            listing.End();
            Widgets.EndScrollView();
        }

        public void ApplySettings()
        {
            if (Current.ProgramState == ProgramState.Playing) ReAssign();
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            ApplySettings();
        }

        public static void ReAssign()
        {
            foreach (var map in Find.Maps.Where(map => map.mapPawns.FreeColonistsSpawned.Any()))
            {
                var pawns = map.mapPawns.FreeColonistsSpawned;
                foreach (var (def, number, priority) in from workSetting in AutoPrioritySettings.Current.WorkSettings
                         where workSetting.Key != null && workSetting.Value.First > 0 && workSetting.Value.Second > 0
                         select (workSetting.Key, workSetting.Value.First, workSetting.Value.Second))
                {
                    var formula = Formulas.ContainsKey(def)
                        ? Formulas[def]
                        : pawn => pawn.skills.AverageOfRelevantSkillsFor(def);
                    var chosenPawns = pawns.Where(p => !p.WorkTypeIsDisabled(def))
                        .OrderByDescending(formula).ThenByDescending(pawn =>
                            (pawn.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation) +
                             pawn.health.capacities.GetLevel(PawnCapacityDefOf.Sight)) / 2).Take(number).ToList();

                    foreach (var pawn in chosenPawns) pawn.workSettings.SetPriority(def, priority);

                    foreach (var pawn in pawns.Except(chosenPawns)) pawn.workSettings.SetPriority(def, 0);
                }
            }
        }
    }

    public class AutoPrioritySettings : GameComponent
    {
        public static AutoPrioritySettings Current;
        public Dictionary<WorkTypeDef, Pair<int, int>> WorkSettings = new Dictionary<WorkTypeDef, Pair<int, int>>();

        public AutoPrioritySettings(Game game) => Current = this;

        public override void ExposeData()
        {
            base.ExposeData();
            var first = WorkSettings.Select(kv => (kv.Key, kv.Value.First)).ToDictionary(p => p.Key, p => p.First);
            var second = WorkSettings.Select(kv => (kv.Key, kv.Value.Second))
                .ToDictionary(p => p.Key, p => p.Second);
            Scribe_Collections.Look(ref first, "numbersOfPawns", LookMode.Def, LookMode.Value,
                ref AutoPriorityMod.KeysWorkingList, ref AutoPriorityMod.ValuesWorkingList);
            Scribe_Collections.Look(ref second, "priorities", LookMode.Def, LookMode.Value,
                ref AutoPriorityMod.KeysWorkingList, ref AutoPriorityMod.ValuesWorkingList);
            if (first == null || second == null)
                WorkSettings = new Dictionary<WorkTypeDef, Pair<int, int>>();
            else
                WorkSettings = first.Zip(second, (pair, valuePair) => (pair.Key, pair.Value, valuePair.Value))
                    .ToDictionary(v => v.Key, v => new Pair<int, int>(v.Item2, v.Item3));
        }
    }
}