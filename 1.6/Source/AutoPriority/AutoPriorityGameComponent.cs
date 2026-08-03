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

    public sealed class LeftoverWorkSetting : IExposable
    {
        public string WorkTypeDefName;
        public bool Enabled;
        public int Priority = 3;

        public LeftoverWorkSetting()
        {
        }

        public LeftoverWorkSetting(string workTypeDefName)
        {
            WorkTypeDefName = workTypeDefName;
            Priority = DefaultPriority(workTypeDefName);
        }

        public void EnsureValid()
        {
            Priority = Math.Max(1, Math.Min(4, Priority));
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref WorkTypeDefName, "workTypeDefName");
            Scribe_Values.Look(ref Enabled, "enabled", false);
            Scribe_Values.Look(ref Priority, "priority", 3);
            EnsureValid();
        }

        private static int DefaultPriority(string defName)
        {
            switch (defName)
            {
                case "Firefighter":
                case "Patient":
                case "PatientBedRest":
                    return 1;
                default:
                    return 3;
            }
        }
    }

    // The class name is kept for compatibility with saves made by the older releases.
    public sealed class AutoPrioritySettings : GameComponent
    {
        private sealed class ResolvedWorkProfile
        {
            public readonly WorkTypeSettings Settings;
            public readonly WorkTypeDef WorkType;

            public ResolvedWorkProfile(WorkTypeSettings settings, WorkTypeDef workType)
            {
                Settings = settings;
                WorkType = workType;
            }
        }

        private sealed class ResolvedLeftoverWork
        {
            public readonly LeftoverWorkSetting Settings;
            public readonly WorkTypeDef WorkType;

            public ResolvedLeftoverWork(LeftoverWorkSetting settings, WorkTypeDef workType)
            {
                Settings = settings;
                WorkType = workType;
            }
        }

        public const int MinimumRecalculationInterval = 250;
        public const int MaximumRecalculationInterval = 60000;

        private static List<WorkTypeDef> legacyNumberKeys;
        private static List<int> legacyNumberValues;
        private static List<WorkTypeDef> legacyPriorityKeys;
        private static List<int> legacyPriorityValues;

        private readonly Dictionary<int, ColonyCircumstanceSnapshot> lastSnapshots =
            new Dictionary<int, ColonyCircumstanceSnapshot>();
        private readonly Dictionary<int, Dictionary<WorkTypeDef, List<ScoredPawn>>> lastRankings =
            new Dictionary<int, Dictionary<WorkTypeDef, List<ScoredPawn>>>();
        private readonly Dictionary<string, WorkTypeSettings> profilesByDefName =
            new Dictionary<string, WorkTypeSettings>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LeftoverWorkSetting> leftoverProfilesByDefName =
            new Dictionary<string, LeftoverWorkSetting>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ResolvedWorkProfile> enabledProfiles = new List<ResolvedWorkProfile>();
        private readonly List<ResolvedLeftoverWork> enabledLeftoverWorkTypes = new List<ResolvedLeftoverWork>();
        private readonly HashSet<CircumstanceType> requiredCircumstances = new HashSet<CircumstanceType>();
        private static readonly IReadOnlyList<ScoredPawn> EmptyRanking = new List<ScoredPawn>();

        private int nextRecalculationTick;
        private int knownWorkTypeCount = -1;
        private bool runtimeCacheDirty = true;

        public static AutoPrioritySettings Current;
        public bool AutomationEnabled = true;
        public int RecalculationInterval = 2500;
        public List<WorkTypeSettings> Profiles = new List<WorkTypeSettings>();
        public bool ManageLeftoverColonists;
        public List<LeftoverWorkSetting> LeftoverWorkTypes = new List<LeftoverWorkSetting>();

        public AutoPrioritySettings(Game game)
        {
            Current = this;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Current = this;
            EnsureProfiles();
            RebuildRuntimeCaches();
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
            nextRecalculationTick = Find.TickManager.TicksGame + RecalculationInterval;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref AutomationEnabled, "automationEnabled", true);
            Scribe_Values.Look(ref RecalculationInterval, "recalculationInterval", 2500);
            Scribe_Collections.Look(ref Profiles, "workProfiles", LookMode.Deep);
            Scribe_Values.Look(ref ManageLeftoverColonists, "manageLeftoverColonists", false);
            Scribe_Collections.Look(ref LeftoverWorkTypes, "leftoverWorkTypes", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.LoadingVars && Profiles.NullOrEmpty())
            {
                ReadLegacySettings();
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                RecalculationInterval = Math.Max(MinimumRecalculationInterval, Math.Min(MaximumRecalculationInterval, RecalculationInterval));
                if (Profiles == null)
                {
                    Profiles = new List<WorkTypeSettings>();
                }

                if (LeftoverWorkTypes == null)
                {
                    LeftoverWorkTypes = new List<LeftoverWorkSetting>();
                }

                Profiles.RemoveAll(profile => profile == null || profile.WorkTypeDefName.NullOrEmpty());
                foreach (WorkTypeSettings profile in Profiles)
                {
                    profile.EnsureValid();
                }

                LeftoverWorkTypes.RemoveAll(profile => profile == null || profile.WorkTypeDefName.NullOrEmpty());
                foreach (LeftoverWorkSetting profile in LeftoverWorkTypes)
                {
                    profile.EnsureValid();
                }

                EnsureProfiles();
                runtimeCacheDirty = true;
            }
        }

        public void EnsureProfiles()
        {
            if (Profiles == null)
            {
                Profiles = new List<WorkTypeSettings>();
            }

            if (LeftoverWorkTypes == null)
            {
                LeftoverWorkTypes = new List<LeftoverWorkSetting>();
            }

            List<WorkTypeDef> workTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading;
            if (knownWorkTypeCount == workTypes.Count && profilesByDefName.Count > 0 && leftoverProfilesByDefName.Count > 0)
            {
                return;
            }

            profilesByDefName.Clear();
            leftoverProfilesByDefName.Clear();
            for (int index = 0; index < Profiles.Count; index++)
            {
                WorkTypeSettings profile = Profiles[index];
                if (profile == null || profile.WorkTypeDefName.NullOrEmpty() || profilesByDefName.ContainsKey(profile.WorkTypeDefName))
                {
                    continue;
                }

                profile.EnsureValid();
                profilesByDefName.Add(profile.WorkTypeDefName, profile);
            }

            for (int index = 0; index < LeftoverWorkTypes.Count; index++)
            {
                LeftoverWorkSetting profile = LeftoverWorkTypes[index];
                if (profile == null || profile.WorkTypeDefName.NullOrEmpty() || leftoverProfilesByDefName.ContainsKey(profile.WorkTypeDefName))
                {
                    continue;
                }

                profile.EnsureValid();
                leftoverProfilesByDefName.Add(profile.WorkTypeDefName, profile);
            }

            for (int index = 0; index < workTypes.Count; index++)
            {
                WorkTypeDef workType = workTypes[index];
                if (!profilesByDefName.ContainsKey(workType.defName))
                {
                    var profile = new WorkTypeSettings(workType.defName);
                    Profiles.Add(profile);
                    profilesByDefName.Add(workType.defName, profile);
                }

                if (!leftoverProfilesByDefName.ContainsKey(workType.defName))
                {
                    var leftoverProfile = new LeftoverWorkSetting(workType.defName);
                    LeftoverWorkTypes.Add(leftoverProfile);
                    leftoverProfilesByDefName.Add(workType.defName, leftoverProfile);
                }
            }

            knownWorkTypeCount = workTypes.Count;
            runtimeCacheDirty = true;
        }

        public WorkTypeSettings ProfileFor(WorkTypeDef workType)
        {
            EnsureProfiles();
            WorkTypeSettings profile;
            if (profilesByDefName.TryGetValue(workType.defName, out profile))
            {
                return profile;
            }

            knownWorkTypeCount = -1;
            EnsureProfiles();
            return profilesByDefName[workType.defName];
        }

        public LeftoverWorkSetting LeftoverProfileFor(WorkTypeDef workType)
        {
            EnsureProfiles();
            LeftoverWorkSetting profile;
            if (leftoverProfilesByDefName.TryGetValue(workType.defName, out profile))
            {
                return profile;
            }

            knownWorkTypeCount = -1;
            EnsureProfiles();
            return leftoverProfilesByDefName[workType.defName];
        }

        public void NotifySettingsChanged(bool applyImmediately)
        {
            EnsureProfiles();
            runtimeCacheDirty = true;
            RebuildRuntimeCaches();
            nextRecalculationTick = Find.TickManager.TicksGame;
            if (applyImmediately && AutomationEnabled)
            {
                RecalculateAllMaps();
                nextRecalculationTick = Find.TickManager.TicksGame + RecalculationInterval;
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
            Dictionary<WorkTypeDef, List<ScoredPawn>> mapRankings;
            List<ScoredPawn> ranking;
            return lastRankings.TryGetValue(map.uniqueID, out mapRankings) && mapRankings.TryGetValue(workType, out ranking)
                ? ranking
                : EmptyRanking;
        }

        public void RecalculateAllMaps()
        {
            if (!AutomationEnabled || Verse.Current.ProgramState != ProgramState.Playing)
            {
                return;
            }

            EnsureProfiles();
            RebuildRuntimeCaches();
            if (enabledProfiles.Count == 0 && (!ManageLeftoverColonists || enabledLeftoverWorkTypes.Count == 0))
            {
                return;
            }

            EnsureManualPrioritiesEnabled();
            List<Map> maps = Find.Maps;
            for (int index = 0; index < maps.Count; index++)
            {
                Map map = maps[index];
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
            List<Pawn> spawned = map.mapPawns.FreeColonistsSpawned;
            var allColonists = new List<Pawn>(spawned.Count);
            for (int index = 0; index < spawned.Count; index++)
            {
                Pawn pawn = spawned[index];
                if (pawn != null && !pawn.Dead && pawn.workSettings != null && pawn.workSettings.Initialized)
                {
                    allColonists.Add(pawn);
                }
            }

            if (allColonists.Count == 0)
            {
                return;
            }

            ColonyCircumstanceSnapshot snapshot = requiredCircumstances.Count == 0
                ? new ColonyCircumstanceSnapshot()
                : ColonyCircumstanceSnapshot.Capture(map, requiredCircumstances);
            lastSnapshots[map.uniqueID] = snapshot;
            Dictionary<WorkTypeDef, List<ScoredPawn>> mapRankings;
            if (!lastRankings.TryGetValue(map.uniqueID, out mapRankings))
            {
                mapRankings = new Dictionary<WorkTypeDef, List<ScoredPawn>>();
                lastRankings.Add(map.uniqueID, mapRankings);
            }

            var scoreCache = new WorkScoreCache();
            var candidates = new List<Pawn>(allColonists.Count);
            var capableColonists = new List<Pawn>(allColonists.Count);
            var selected = new HashSet<Pawn>();
            var assignedColonists = new HashSet<Pawn>();
            var primaryAssignments = new Dictionary<WorkTypeDef, HashSet<Pawn>>();

            for (int profileIndex = 0; profileIndex < enabledProfiles.Count; profileIndex++)
            {
                ResolvedWorkProfile resolved = enabledProfiles[profileIndex];
                WorkTypeSettings profile = resolved.Settings;
                WorkTypeDef workType = resolved.WorkType;
                candidates.Clear();
                capableColonists.Clear();
                for (int pawnIndex = 0; pawnIndex < allColonists.Count; pawnIndex++)
                {
                    Pawn pawn = allColonists[pawnIndex];
                    if (pawn.WorkTypeIsDisabled(workType))
                    {
                        continue;
                    }

                    capableColonists.Add(pawn);
                    if (!pawn.Downed && !pawn.InMentalState)
                    {
                        candidates.Add(pawn);
                    }
                }

                List<ScoredPawn> ranking = WorkScoring.Rank(workType, candidates, scoreCache);
                mapRankings[workType] = ranking;

                int extraWorkers = 0;
                int priorityBoost = 0;
                for (int ruleIndex = 0; ruleIndex < profile.CircumstanceRules.Count; ruleIndex++)
                {
                    CircumstanceRule rule = profile.CircumstanceRules[ruleIndex];
                    if (rule.Enabled && snapshot.IsActive(rule.Type))
                    {
                        extraWorkers = Math.Max(extraWorkers, rule.ExtraWorkers);
                        priorityBoost = Math.Max(priorityBoost, rule.PriorityBoost);
                    }
                }

                int selectedCount = Math.Min(ranking.Count, profile.WorkerCount + extraWorkers);
                selected.Clear();
                for (int rank = 0; rank < selectedCount; rank++)
                {
                    Pawn pawn = ranking[rank].Pawn;
                    selected.Add(pawn);
                    assignedColonists.Add(pawn);
                    int priority = Math.Max(1, profile.PriorityForRank(rank) - priorityBoost);
                    PriorityCompatibility.SetPriorityIfChanged(pawn, workType, priority);
                }

                for (int pawnIndex = 0; pawnIndex < capableColonists.Count; pawnIndex++)
                {
                    Pawn pawn = capableColonists[pawnIndex];
                    if (!selected.Contains(pawn))
                    {
                        PriorityCompatibility.SetPriorityIfChanged(pawn, workType, 0);
                    }
                }

                primaryAssignments[workType] = new HashSet<Pawn>(selected);
            }

            if (ManageLeftoverColonists)
            {
                ApplyLeftoverWork(allColonists, assignedColonists, primaryAssignments);
            }
        }

        private void ApplyLeftoverWork(
            List<Pawn> allColonists,
            HashSet<Pawn> assignedColonists,
            Dictionary<WorkTypeDef, HashSet<Pawn>> primaryAssignments)
        {
            for (int workIndex = 0; workIndex < enabledLeftoverWorkTypes.Count; workIndex++)
            {
                ResolvedLeftoverWork fallback = enabledLeftoverWorkTypes[workIndex];
                HashSet<Pawn> primaryWorkers;
                primaryAssignments.TryGetValue(fallback.WorkType, out primaryWorkers);

                for (int pawnIndex = 0; pawnIndex < allColonists.Count; pawnIndex++)
                {
                    Pawn pawn = allColonists[pawnIndex];
                    if (pawn.WorkTypeIsDisabled(fallback.WorkType))
                    {
                        continue;
                    }

                    // Keep the rank-specific priority when this pawn was selected
                    // as a primary worker for the same work type.
                    if (primaryWorkers != null && primaryWorkers.Contains(pawn))
                    {
                        continue;
                    }

                    int priority = assignedColonists.Contains(pawn) ? 0 : fallback.Settings.Priority;
                    PriorityCompatibility.SetPriorityIfChanged(pawn, fallback.WorkType, priority);
                }
            }
        }

        private void RebuildRuntimeCaches()
        {
            if (!runtimeCacheDirty)
            {
                return;
            }

            enabledProfiles.Clear();
            enabledLeftoverWorkTypes.Clear();
            requiredCircumstances.Clear();
            foreach (WorkTypeSettings profile in profilesByDefName.Values)
            {
                if (profile == null || !profile.Enabled || profile.WorkTypeDefName.NullOrEmpty())
                {
                    continue;
                }

                // Missing mod-added work types remain dormant in the save. They are
                // picked up automatically if their defining mod is loaded again.
                WorkTypeDef workType = DefDatabase<WorkTypeDef>.GetNamedSilentFail(profile.WorkTypeDefName);
                if (workType == null)
                {
                    continue;
                }

                enabledProfiles.Add(new ResolvedWorkProfile(profile, workType));
                for (int ruleIndex = 0; ruleIndex < profile.CircumstanceRules.Count; ruleIndex++)
                {
                    CircumstanceRule rule = profile.CircumstanceRules[ruleIndex];
                    if (rule.Enabled)
                    {
                        requiredCircumstances.Add(rule.Type);
                    }
                }
            }

            if (ManageLeftoverColonists)
            {
                foreach (LeftoverWorkSetting profile in leftoverProfilesByDefName.Values)
                {
                    if (profile == null || !profile.Enabled || profile.WorkTypeDefName.NullOrEmpty())
                    {
                        continue;
                    }

                    WorkTypeDef workType = DefDatabase<WorkTypeDef>.GetNamedSilentFail(profile.WorkTypeDefName);
                    if (workType != null)
                    {
                        enabledLeftoverWorkTypes.Add(new ResolvedLeftoverWork(profile, workType));
                    }
                }
            }

            runtimeCacheDirty = false;
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
