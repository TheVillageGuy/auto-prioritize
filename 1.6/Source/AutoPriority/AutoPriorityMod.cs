using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoPriority
{
    public sealed class AutoPriorityMod : Mod
    {
        private const float NavigationWidth = 190f;
        private Vector2 navigationScroll;
        private Vector2 detailScroll;
        private string selectedWorkTypeDefName;

        public AutoPriorityMod(ModContentPack content) : base(content)
        {
        }

        public override string SettingsCategory()
        {
            return "AutoPriority".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (Verse.Current.ProgramState != ProgramState.Playing || Verse.Current.Game == null || AutoPrioritySettings.Current == null)
            {
                Widgets.Label(inRect.TopPartPixels(50f), "AutoPriority.NeedGame".Translate());
                return;
            }

            AutoPrioritySettings settings = AutoPrioritySettings.Current;
            settings.EnsureProfiles();
            List<WorkTypeDef> workTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading
                .Where(workType => workType.visible)
                .OrderByDescending(workType => workType.naturalPriority)
                .ThenBy(workType => workType.labelShort)
                .ToList();
            if (workTypes.Count == 0)
            {
                Widgets.Label(inRect, "AutoPriority.NoWorkTypes".Translate());
                return;
            }

            if (selectedWorkTypeDefName.NullOrEmpty() || workTypes.All(workType => workType.defName != selectedWorkTypeDefName))
            {
                selectedWorkTypeDefName = workTypes[0].defName;
            }

            bool changed = DrawGlobalControls(inRect.TopPartPixels(58f), settings);
            Rect body = new Rect(inRect.x, inRect.y + 64f, inRect.width, inRect.height - 64f);
            Rect navigation = new Rect(body.x, body.y, NavigationWidth, body.height);
            Rect detail = new Rect(navigation.xMax + 10f, body.y, body.width - NavigationWidth - 10f, body.height);

            DrawNavigation(navigation, workTypes);
            WorkTypeDef selected = workTypes.First(workType => workType.defName == selectedWorkTypeDefName);
            changed |= DrawProfile(detail, settings, selected);

            if (changed)
            {
                settings.NotifySettingsChanged(false);
            }
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            if (Verse.Current.ProgramState == ProgramState.Playing && AutoPrioritySettings.Current != null)
            {
                AutoPrioritySettings.Current.NotifySettingsChanged(true);
            }
        }

        private static bool DrawGlobalControls(Rect rect, AutoPrioritySettings settings)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(8f);
            bool changed = false;
            bool enabled = settings.AutomationEnabled;
            Widgets.CheckboxLabeled(new Rect(inner.x, inner.y, 235f, 30f), "AutoPriority.AutomationEnabled".Translate(), ref enabled);
            if (enabled != settings.AutomationEnabled)
            {
                settings.AutomationEnabled = enabled;
                changed = true;
            }

            Rect intervalLabel = new Rect(inner.x + 250f, inner.y, 250f, 30f);
            Widgets.Label(intervalLabel, "AutoPriority.Interval".Translate(settings.RecalculationInterval.ToStringTicksToPeriod()));
            if (Widgets.ButtonText(new Rect(inner.xMax - 130f, inner.y, 120f, 30f), "AutoPriority.ApplyNow".Translate()))
            {
                settings.NotifySettingsChanged(true);
            }

            return changed;
        }

        private void DrawNavigation(Rect rect, List<WorkTypeDef> workTypes)
        {
            Widgets.DrawMenuSection(rect);
            Rect outRect = rect.ContractedBy(2f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 18f, workTypes.Count * 34f + 4f);
            Widgets.BeginScrollView(outRect, ref navigationScroll, viewRect);
            for (int index = 0; index < workTypes.Count; index++)
            {
                WorkTypeDef workType = workTypes[index];
                Rect row = new Rect(0f, index * 34f, viewRect.width, 32f);
                if (selectedWorkTypeDefName == workType.defName)
                {
                    Widgets.DrawHighlightSelected(row);
                }
                else if (Mouse.IsOver(row))
                {
                    Widgets.DrawHighlight(row);
                }

                Rect labelRect = row.ContractedBy(7f, 4f);
                Widgets.Label(labelRect, workType.pawnLabel.NullOrEmpty() ? workType.labelShort.CapitalizeFirst() : workType.pawnLabel);
                if (Widgets.ButtonInvisible(row))
                {
                    selectedWorkTypeDefName = workType.defName;
                    detailScroll = Vector2.zero;
                }

                TooltipHandler.TipRegion(row, workType.description);
            }

            Widgets.EndScrollView();
        }

        private bool DrawProfile(Rect rect, AutoPrioritySettings settings, WorkTypeDef workType)
        {
            Widgets.DrawMenuSection(rect);
            WorkTypeSettings profile = settings.ProfileFor(workType);
            profile.EnsureValid();
            float contentHeight = 490f + Math.Max(1, profile.WorkerCount) * 36f + profile.CircumstanceRules.Count * 104f;
            IReadOnlyList<ScoredPawn> ranking = Find.CurrentMap == null
                ? new List<ScoredPawn>()
                : settings.RankingFor(Find.CurrentMap, workType);
            contentHeight += Math.Min(10, ranking.Count) * 26f;

            Rect outRect = rect.ContractedBy(8f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 18f, contentHeight);
            Widgets.BeginScrollView(outRect, ref detailScroll, viewRect);
            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            Text.Font = GameFont.Medium;
            string heading = workType.pawnLabel.NullOrEmpty()
                ? workType.LabelCap.ToString()
                : workType.pawnLabel.CapitalizeFirst();
            listing.Label(heading);
            Text.Font = GameFont.Small;
            listing.Label(workType.description);
            listing.GapLine();

            bool changed = false;
            bool enabled = profile.Enabled;
            listing.CheckboxLabeled("AutoPriority.ManageWorkType".Translate(), ref enabled, "AutoPriority.ManageWorkType.Desc".Translate());
            if (enabled != profile.Enabled)
            {
                profile.Enabled = enabled;
                changed = true;
            }

            int workers = Mathf.RoundToInt(listing.SliderLabeled(
                "AutoPriority.NumPawns.Value".Translate(profile.WorkerCount), profile.WorkerCount, 0f, 20f, 0.65f,
                "AutoPriority.NumPawns.Desc".Translate()));
            if (workers != profile.WorkerCount)
            {
                profile.WorkerCount = workers;
                profile.EnsureRankPriorities();
                changed = true;
            }

            listing.Gap();
            listing.Label("AutoPriority.RankPriorities".Translate());
            listing.Label("AutoPriority.RankPriorities.Desc".Translate());
            for (int index = 0; index < profile.WorkerCount; index++)
            {
                int priority = Mathf.RoundToInt(listing.SliderLabeled(
                    "AutoPriority.PriorityForRank".Translate(index + 1, profile.RankPriorities[index]),
                    profile.RankPriorities[index], 1f, 4f));
                if (priority != profile.RankPriorities[index])
                {
                    profile.RankPriorities[index] = priority;
                    changed = true;
                }
            }

            listing.GapLine();
            listing.Label("AutoPriority.CalculationFactors".Translate());
            listing.Label("AutoPriority.CalculationFactors.Desc".Translate());
            foreach (ScoreFactor factor in WorkScoring.FactorsFor(workType))
            {
                string direction = factor.LowerIsBetter ? "AutoPriority.LowerIsBetter".Translate() : string.Empty;
                listing.Label("  " + factor.Label + " — " + factor.Weight.ToStringPercent() + direction);
            }

            listing.GapLine();
            listing.Label("AutoPriority.Circumstances".Translate());
            listing.Label("AutoPriority.Circumstances.Desc".Translate());
            ColonyCircumstanceSnapshot snapshot = Find.CurrentMap == null ? null : settings.SnapshotFor(Find.CurrentMap);
            foreach (CircumstanceInfo info in CircumstanceCatalog.All)
            {
                CircumstanceRule rule = profile.CircumstanceRules.First(item => item.Type == info.Type);
                bool ruleEnabled = rule.Enabled;
                string active = snapshot != null && snapshot.IsActive(info.Type)
                    ? "AutoPriority.Active".Translate(snapshot.Count(info.Type))
                    : "AutoPriority.Inactive".Translate();
                listing.CheckboxLabeled(info.LabelKey.Translate() + " — " + active, ref ruleEnabled, info.DescriptionKey.Translate());
                if (ruleEnabled != rule.Enabled)
                {
                    rule.Enabled = ruleEnabled;
                    changed = true;
                }

                if (rule.Enabled)
                {
                    int extra = Mathf.RoundToInt(listing.SliderLabeled(
                        "AutoPriority.ExtraWorkers".Translate(rule.ExtraWorkers), rule.ExtraWorkers, 0f, 10f));
                    int boost = Mathf.RoundToInt(listing.SliderLabeled(
                        "AutoPriority.PriorityBoost".Translate(rule.PriorityBoost), rule.PriorityBoost, 0f, 3f));
                    if (extra != rule.ExtraWorkers || boost != rule.PriorityBoost)
                    {
                        rule.ExtraWorkers = extra;
                        rule.PriorityBoost = boost;
                        changed = true;
                    }
                }
                else
                {
                    listing.Gap(48f);
                }

                listing.Gap(4f);
            }

            listing.GapLine();
            listing.Label("AutoPriority.CurrentRanking".Translate());
            if (ranking.Count == 0)
            {
                listing.Label("AutoPriority.NoRanking".Translate());
            }
            else
            {
                for (int index = 0; index < Math.Min(10, ranking.Count); index++)
                {
                    listing.Label((index + 1) + ". " + ranking[index].Pawn.LabelShortCap + " — " + ranking[index].Score.ToString("0.0"));
                }
            }

            listing.GapLine();
            Rect buttons = listing.GetRect(34f);
            if (Widgets.ButtonText(buttons.LeftPart(0.48f), "AutoPriority.ResetWorkType".Translate()))
            {
                profile.ResetToDefaults();
                changed = true;
            }

            if (Widgets.ButtonText(buttons.RightPart(0.48f), "AutoPriority.ApplyNow".Translate()))
            {
                settings.NotifySettingsChanged(true);
            }

            listing.End();
            Widgets.EndScrollView();
            return changed;
        }
    }
}
