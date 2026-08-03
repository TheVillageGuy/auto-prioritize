using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AutoPriority
{
    public sealed class WorkTypeSettings : IExposable
    {
        public string WorkTypeDefName;
        public bool Enabled;
        public int WorkerCount = 1;
        public List<int> RankPriorities = new List<int>();
        public List<CircumstanceRule> CircumstanceRules = new List<CircumstanceRule>();

        public WorkTypeSettings()
        {
        }

        public WorkTypeSettings(string workTypeDefName)
        {
            WorkTypeDefName = workTypeDefName;
            ResetToDefaults();
        }

        public void ResetToDefaults()
        {
            Enabled = false;
            WorkerCount = DefaultWorkerCount(WorkTypeDefName);
            RankPriorities = new List<int>();
            EnsureRankPriorities();
            CircumstanceRules = CircumstanceCatalog.CreateRules(WorkTypeDefName);
        }

        public void EnsureValid()
        {
            WorkerCount = Math.Max(0, Math.Min(20, WorkerCount));
            if (RankPriorities == null)
            {
                RankPriorities = new List<int>();
            }

            if (CircumstanceRules == null)
            {
                CircumstanceRules = new List<CircumstanceRule>();
            }

            EnsureRankPriorities();
            CircumstanceCatalog.EnsureAllRules(CircumstanceRules);
        }

        public void EnsureRankPriorities()
        {
            while (RankPriorities.Count < Math.Max(1, WorkerCount))
            {
                RankPriorities.Add(RankPriorities.Count == 0 ? 1 : 2);
            }

            for (int index = 0; index < RankPriorities.Count; index++)
            {
                RankPriorities[index] = Math.Max(1, Math.Min(4, RankPriorities[index]));
            }
        }

        public int PriorityForRank(int zeroBasedRank)
        {
            EnsureRankPriorities();
            return RankPriorities[Math.Min(zeroBasedRank, RankPriorities.Count - 1)];
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref WorkTypeDefName, "workTypeDefName");
            Scribe_Values.Look(ref Enabled, "enabled", false);
            Scribe_Values.Look(ref WorkerCount, "workerCount", 1);
            Scribe_Collections.Look(ref RankPriorities, "rankPriorities", LookMode.Value);
            Scribe_Collections.Look(ref CircumstanceRules, "circumstanceRules", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                EnsureValid();
            }
        }

        private static int DefaultWorkerCount(string defName)
        {
            switch (defName)
            {
                case "Doctor":
                case "Firefighter":
                case "Hauling":
                case "Cleaning":
                    return 2;
                default:
                    return 1;
            }
        }
    }

    // The class name is kept for compatibility with saves made by the older releases.
    public sealed class AutoPrioritySettings : GameComponent
    {
        private static List<WorkTypeDef> legacyNumberKeys;
        private static List<int> legacyNumberValues;
        private static List<WorkTypeDef> legacyPriorityKeys;
        private static List<int> legacyPriorityValues;

        private readonly Dictionary<int, ColonyCircumstanceSnapshot> lastSnapshots =
            new Dictionary<int, ColonyCircumstanceSnapshot>();
        private readonly Dictionary<string, List<ScoredPawn>> lastRankings =
            new Dictionary<string, List<ScoredPawn>>();

        private int nextRecalculationTick;

        public static AutoPrioritySettings Current;
        public bool AutomationEnabled = true;
        public int RecalculationInterval = 2500;
        public List<WorkTypeSettings> Profiles = new List<WorkTypeSettings>();

        public AutoPrioritySettings(Game game)
        {
            Current = this;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Current = this;
            EnsureProfiles();
            nextRecalculationTick = Find.TickManager.TicksGame + 60;
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            if (!AutomationEnabled || Find.TickManager.TicksGame < nextRecalculationTick)
            {
                return;
            }

            RecalculateAllMaps();
            nextRecalculationTick = Find.TickManager.TicksGame + Math.Max(250, RecalculationInterval);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref AutomationEnabled, "automationEnabled", true);
            Scribe_Values.Look(ref RecalculationInterval, "recalculationInterval", 2500);
            Scribe_Collections.Look(ref Profiles, "workProfiles", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.LoadingVars && Profiles.NullOrEmpty())
            {
                ReadLegacySettings();
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                RecalculationInterval = Math.Max(250, Math.Min(15000, RecalculationInterval));
                if (Profiles == null)
                {
                    Profiles = new List<WorkTypeSettings>();
                }

                Profiles.RemoveAll(profile => profile == null || profile.WorkTypeDefName.NullOrEmpty());
                foreach (WorkTypeSettings profile in Profiles)
                {
                    profile.EnsureValid();
                }

                EnsureProfiles();
            }
        }

        public void EnsureProfiles()
        {
            if (Profiles == null)
            {
                Profiles = new List<WorkTypeSettings>();
            }

            foreach (WorkTypeDef workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                if (Profiles.All(profile => profile.WorkTypeDefName != workType.defName))
                {
                    Profiles.Add(new WorkTypeSettings(workType.defName));
                }
            }

            foreach (WorkTypeSettings profile in Profiles)
            {
                profile.EnsureValid();
            }
        }

        public WorkTypeSettings ProfileFor(WorkTypeDef workType)
        {
            EnsureProfiles();
            return Profiles.First(profile => profile.WorkTypeDefName == workType.defName);
        }

        public void NotifySettingsChanged(bool applyImmediately)
        {
            EnsureProfiles();
            nextRecalculationTick = Find.TickManager.TicksGame;
            if (applyImmediately && AutomationEnabled)
            {
                RecalculateAllMaps();
                nextRecalculationTick = Find.TickManager.TicksGame + Math.Max(250, RecalculationInterval);
            }
        }

        public ColonyCircumstanceSnapshot SnapshotFor(Map map)
        {
            ColonyCircumstanceSnapshot snapshot;
            if (!lastSnapshots.TryGetValue(map.uniqueID, out snapshot))
            {
                snapshot = ColonyCircumstanceSnapshot.Capture(map);
                lastSnapshots[map.uniqueID] = snapshot;
            }

            return snapshot;
        }

        public IReadOnlyList<ScoredPawn> RankingFor(Map map, WorkTypeDef workType)
        {
            List<ScoredPawn> ranking;
            return lastRankings.TryGetValue(RankingKey(map, workType), out ranking)
                ? ranking
                : new List<ScoredPawn>();
        }

        public void RecalculateAllMaps()
        {
            if (!AutomationEnabled || Verse.Current.ProgramState != ProgramState.Playing)
            {
                return;
            }

            EnsureProfiles();
            EnsureManualPrioritiesEnabled();
            foreach (Map map in Find.Maps)
            {
                try
                {
                    RecalculateMap(map);
                }
                catch (Exception exception)
                {
                    Log.Error("[Let Me Skill For You] Failed to calculate work priorities on " + map + ": " + exception);
                }
            }
        }

        private void RecalculateMap(Map map)
        {
            List<Pawn> allColonists = map.mapPawns.FreeColonistsSpawned
                .Where(pawn => pawn != null && !pawn.Dead && pawn.workSettings != null && pawn.workSettings.Initialized)
                .ToList();
            if (allColonists.Count == 0)
            {
                return;
            }

            ColonyCircumstanceSnapshot snapshot = ColonyCircumstanceSnapshot.Capture(map);
            lastSnapshots[map.uniqueID] = snapshot;

            foreach (WorkTypeSettings profile in Profiles.Where(item => item.Enabled))
            {
                WorkTypeDef workType = DefDatabase<WorkTypeDef>.GetNamedSilentFail(profile.WorkTypeDefName);
                if (workType == null)
                {
                    continue;
                }

                List<Pawn> candidates = allColonists
                    .Where(pawn => !pawn.Downed && !pawn.InMentalState && !pawn.WorkTypeIsDisabled(workType))
                    .ToList();
                List<ScoredPawn> ranking = WorkScoring.Rank(workType, candidates);
                lastRankings[RankingKey(map, workType)] = ranking;

                int extraWorkers = 0;
                int priorityBoost = 0;
                foreach (CircumstanceRule rule in profile.CircumstanceRules)
                {
                    if (rule.Enabled && snapshot.IsActive(rule.Type))
                    {
                        extraWorkers = Math.Max(extraWorkers, rule.ExtraWorkers);
                        priorityBoost = Math.Max(priorityBoost, rule.PriorityBoost);
                    }
                }

                int selectedCount = Math.Min(ranking.Count, profile.WorkerCount + extraWorkers);
                var selected = new HashSet<Pawn>();
                for (int rank = 0; rank < selectedCount; rank++)
                {
                    Pawn pawn = ranking[rank].Pawn;
                    selected.Add(pawn);
                    int priority = Math.Max(1, profile.PriorityForRank(rank) - priorityBoost);
                    SetPriorityIfChanged(pawn, workType, priority);
                }

                foreach (Pawn pawn in allColonists)
                {
                    if (!selected.Contains(pawn) && !pawn.WorkTypeIsDisabled(workType))
                    {
                        SetPriorityIfChanged(pawn, workType, 0);
                    }
                }
            }
        }

        private static void SetPriorityIfChanged(Pawn pawn, WorkTypeDef workType, int priority)
        {
            if (pawn.workSettings.GetPriority(workType) != priority)
            {
                pawn.workSettings.SetPriority(workType, priority);
            }
        }

        private static void EnsureManualPrioritiesEnabled()
        {
            if (Verse.Current.Game.playSettings.useWorkPriorities)
            {
                return;
            }

            Verse.Current.Game.playSettings.useWorkPriorities = true;
            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
            {
                if (pawn.Faction == Faction.OfPlayer && pawn.workSettings != null)
                {
                    pawn.workSettings.Notify_UseWorkPrioritiesChanged();
                }
            }
        }

        private static string RankingKey(Map map, WorkTypeDef workType)
        {
            return map.uniqueID + ":" + workType.defName;
        }

        private void ReadLegacySettings()
        {
            Dictionary<WorkTypeDef, int> numbers = null;
            Dictionary<WorkTypeDef, int> priorities = null;
            Scribe_Collections.Look(ref numbers, "numbersOfPawns", LookMode.Def, LookMode.Value,
                ref legacyNumberKeys, ref legacyNumberValues);
            Scribe_Collections.Look(ref priorities, "priorities", LookMode.Def, LookMode.Value,
                ref legacyPriorityKeys, ref legacyPriorityValues);

            if (numbers == null)
            {
                return;
            }

            Profiles = new List<WorkTypeSettings>();
            foreach (KeyValuePair<WorkTypeDef, int> pair in numbers)
            {
                if (pair.Key == null)
                {
                    continue;
                }

                int priority;
                if (priorities == null || !priorities.TryGetValue(pair.Key, out priority))
                {
                    priority = 0;
                }

                var profile = new WorkTypeSettings(pair.Key.defName)
                {
                    Enabled = pair.Value > 0 && priority > 0,
                    WorkerCount = Math.Max(0, pair.Value)
                };
                profile.EnsureRankPriorities();
                for (int index = 0; index < profile.RankPriorities.Count; index++)
                {
                    profile.RankPriorities[index] = Math.Max(1, Math.Min(4, priority));
                }

                Profiles.Add(profile);
            }
        }
    }
}
