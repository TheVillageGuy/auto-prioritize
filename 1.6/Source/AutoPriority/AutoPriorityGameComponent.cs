using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimWorld;
using Verse;

namespace AutoPriority
{
    public sealed class WorkTypeSettings : IExposable
    {
        public const int MaximumConfiguredWorkers = 999;

        public string WorkTypeDefName;
        public bool Enabled;
        public int WorkerCount = 1;
        public List<int> RankGroupCounts = new List<int>();
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
            RankGroupCounts = new List<int> { 1, 1 };
            RankPriorities = new List<int> { 1, 2 };
            EnsureRankPriorities();
            CircumstanceRules = CircumstanceCatalog.CreateRules(WorkTypeDefName);
        }

        public void EnsureValid()
        {
            if (RankGroupCounts == null)
            {
                RankGroupCounts = new List<int>();
            }

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
            while (RankGroupCounts.Count < 2)
            {
                RankGroupCounts.Add(1);
            }

            while (RankPriorities.Count < 2)
            {
                RankPriorities.Add(RankPriorities.Count == 0 ? 1 : 2);
            }

            for (int index = 0; index < 2; index++)
            {
                RankGroupCounts[index] = Math.Max(0, Math.Min(MaximumConfiguredWorkers, RankGroupCounts[index]));
                RankPriorities[index] = Math.Max(0, Math.Min(PriorityCompatibility.MaximumPriority, RankPriorities[index]));
            }

            WorkerCount = RankGroupCounts[0] + RankGroupCounts[1];
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref WorkTypeDefName, "workTypeDefName");
            Scribe_Values.Look(ref Enabled, "enabled", false);
            Scribe_Values.Look(ref WorkerCount, "workerCount", 1);
            Scribe_Collections.Look(ref RankGroupCounts, "rankGroupCounts", LookMode.Value);
            Scribe_Collections.Look(ref RankPriorities, "rankPriorities", LookMode.Value);
            Scribe_Collections.Look(ref CircumstanceRules, "circumstanceRules", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                EnsureValid();
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
            Priority = Math.Max(0, Math.Min(PriorityCompatibility.MaximumPriority, Priority));
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
        private sealed class PendingPawnAssignment
        {
            public Pawn Pawn;
            public readonly Dictionary<WorkTypeDef, int> Priorities = new Dictionary<WorkTypeDef, int>();

            public void Reset(Pawn pawn)
            {
                Pawn = pawn;
                Priorities.Clear();
            }
        }

        private sealed class ResolvedWorkProfile
        {
            public readonly WorkTypeSettings Settings;
            public readonly WorkTypeDef WorkType;
            public readonly CircumstanceRule[] EnabledRules;

            public ResolvedWorkProfile(
                WorkTypeSettings settings,
                WorkTypeDef workType,
                CircumstanceRule[] enabledRules)
            {
                Settings = settings;
                WorkType = workType;
                EnabledRules = enabledRules;
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
        public const double AssignmentBudgetMilliseconds = 1.0;

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
        private readonly WorkScoreCache scoreCache = new WorkScoreCache();
        private readonly List<Pawn> allColonists = new List<Pawn>();
        private readonly List<Pawn> candidates = new List<Pawn>();
        private readonly List<Pawn> capableColonists = new List<Pawn>();
        private readonly HashSet<Pawn> selected = new HashSet<Pawn>();
        private readonly HashSet<Pawn> assignedColonists = new HashSet<Pawn>();
        private readonly Dictionary<WorkTypeDef, HashSet<Pawn>> primaryAssignments =
            new Dictionary<WorkTypeDef, HashSet<Pawn>>();
        private readonly Dictionary<Pawn, PendingPawnAssignment> pendingAssignmentsByPawn =
            new Dictionary<Pawn, PendingPawnAssignment>();
        private readonly List<PendingPawnAssignment> pendingAssignments = new List<PendingPawnAssignment>();
        private readonly List<PendingPawnAssignment> pendingAssignmentPool = new List<PendingPawnAssignment>();
        private readonly Stopwatch assignmentStopwatch = new Stopwatch();

        private int nextRecalculationTick;
        private int nextPendingAssignmentIndex;
        private int pendingAssignmentTotal;
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
            if (!AutomationEnabled)
            {
                ClearPendingAssignments();
                return;
            }

            if (nextPendingAssignmentIndex < pendingAssignments.Count)
            {
                ApplyPendingAssignmentsForTick();
                return;
            }

            if (Find.TickManager.TicksGame < nextRecalculationTick)
            {
                return;
            }

            RecalculateAllMaps();
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
            ClearPendingAssignments();
            EnsureProfiles();
            runtimeCacheDirty = true;
            RebuildRuntimeCaches();
            if (applyImmediately && AutomationEnabled)
            {
                RecalculateAllMaps();
            }
        }

        public int PendingAssignmentCount
        {
            get { return Math.Max(0, pendingAssignments.Count - nextPendingAssignmentIndex); }
        }

        public int PendingAssignmentTotal
        {
            get { return pendingAssignmentTotal; }
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
                ClearPendingAssignments();
                nextRecalculationTick = Find.TickManager.TicksGame + RecalculationInterval;
                return;
            }

            EnsureManualPrioritiesEnabled();
            BeginAssignmentPlan();
            try
            {
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
            finally
            {
                FinishAssignmentPlan();
            }
        }

        private void RecalculateMap(Map map)
        {
            List<Pawn> spawned = map.mapPawns.FreeColonistsSpawned;
            allColonists.Clear();
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

            scoreCache.BeginPass();
            assignedColonists.Clear();
            foreach (HashSet<Pawn> workers in primaryAssignments.Values)
            {
                workers.Clear();
            }

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

                List<ScoredPawn> ranking;
                if (!mapRankings.TryGetValue(workType, out ranking))
                {
                    ranking = new List<ScoredPawn>(allColonists.Count);
                    mapRankings.Add(workType, ranking);
                }

                WorkScoring.Rank(workType, candidates, scoreCache, ranking);

                int extraWorkers = 0;
                int priorityBoost = 0;
                CircumstanceRule[] enabledRules = resolved.EnabledRules;
                for (int ruleIndex = 0; ruleIndex < enabledRules.Length; ruleIndex++)
                {
                    CircumstanceRule rule = enabledRules[ruleIndex];
                    if (snapshot.IsActive(rule.Type))
                    {
                        extraWorkers = Math.Max(extraWorkers, rule.ExtraWorkers);
                        priorityBoost = Math.Max(priorityBoost, rule.PriorityBoost);
                    }
                }

                selected.Clear();
                int rank = 0;
                for (int groupIndex = 0; groupIndex < 2 && rank < ranking.Count; groupIndex++)
                {
                    int groupCount = profile.RankGroupCounts[groupIndex];
                    if (groupIndex == 1)
                    {
                        // Circumstance-added workers follow rank group 2's priority.
                        groupCount += extraWorkers;
                    }

                    int configuredPriority = profile.RankPriorities[groupIndex];
                    int priority = configuredPriority == 0
                        ? 0
                        : Math.Max(1, configuredPriority - priorityBoost);
                    int groupEnd = Math.Min(ranking.Count, rank + groupCount);
                    while (rank < groupEnd)
                    {
                        Pawn pawn = ranking[rank].Pawn;
                        selected.Add(pawn);
                        assignedColonists.Add(pawn);
                        QueuePriority(pawn, workType, priority);
                        rank++;
                    }
                }

                for (int pawnIndex = 0; pawnIndex < capableColonists.Count; pawnIndex++)
                {
                    Pawn pawn = capableColonists[pawnIndex];
                    if (!selected.Contains(pawn))
                    {
                        QueuePriority(pawn, workType, 0);
                    }
                }

                HashSet<Pawn> primaryWorkers;
                if (!primaryAssignments.TryGetValue(workType, out primaryWorkers))
                {
                    primaryWorkers = new HashSet<Pawn>();
                    primaryAssignments.Add(workType, primaryWorkers);
                }

                primaryWorkers.UnionWith(selected);
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
                    QueuePriority(pawn, fallback.WorkType, priority);
                }
            }
        }

        private void BeginAssignmentPlan()
        {
            ClearPendingAssignments();
            pendingAssignmentsByPawn.Clear();
        }

        private void QueuePriority(Pawn pawn, WorkTypeDef workType, int priority)
        {
            PendingPawnAssignment assignment;
            if (pendingAssignmentsByPawn.TryGetValue(pawn, out assignment))
            {
                if (PriorityCompatibility.GetPriority(pawn, workType) == priority)
                {
                    assignment.Priorities.Remove(workType);
                }
                else
                {
                    assignment.Priorities[workType] = priority;
                }

                return;
            }

            if (PriorityCompatibility.GetPriority(pawn, workType) == priority)
            {
                return;
            }

            int poolIndex = pendingAssignmentPool.Count - 1;
            if (poolIndex >= 0)
            {
                assignment = pendingAssignmentPool[poolIndex];
                pendingAssignmentPool.RemoveAt(poolIndex);
                assignment.Reset(pawn);
            }
            else
            {
                assignment = new PendingPawnAssignment();
                assignment.Reset(pawn);
            }

            assignment.Priorities.Add(workType, priority);
            pendingAssignmentsByPawn.Add(pawn, assignment);
            pendingAssignments.Add(assignment);
        }

        private void FinishAssignmentPlan()
        {
            int writeIndex = 0;
            for (int readIndex = 0; readIndex < pendingAssignments.Count; readIndex++)
            {
                PendingPawnAssignment assignment = pendingAssignments[readIndex];
                if (assignment.Priorities.Count == 0)
                {
                    assignment.Reset(null);
                    pendingAssignmentPool.Add(assignment);
                    continue;
                }

                pendingAssignments[writeIndex++] = assignment;
            }

            if (writeIndex < pendingAssignments.Count)
            {
                pendingAssignments.RemoveRange(writeIndex, pendingAssignments.Count - writeIndex);
            }

            pendingAssignmentsByPawn.Clear();
            nextPendingAssignmentIndex = 0;
            pendingAssignmentTotal = pendingAssignments.Count;
            if (pendingAssignments.Count == 0)
            {
                nextRecalculationTick = Find.TickManager.TicksGame + RecalculationInterval;
            }
        }

        private void ApplyPendingAssignmentsForTick()
        {
            assignmentStopwatch.Reset();
            assignmentStopwatch.Start();
            int processedThisTick = 0;
            while (nextPendingAssignmentIndex < pendingAssignments.Count &&
                   (processedThisTick == 0 || assignmentStopwatch.Elapsed.TotalMilliseconds < AssignmentBudgetMilliseconds))
            {
                PendingPawnAssignment assignment = pendingAssignments[nextPendingAssignmentIndex++];
                processedThisTick++;
                Pawn pawn = assignment.Pawn;
                if (pawn != null && !pawn.Dead && pawn.workSettings != null && pawn.workSettings.Initialized)
                {
                    PriorityCompatibility.BeginBatch();
                    try
                    {
                        foreach (KeyValuePair<WorkTypeDef, int> priority in assignment.Priorities)
                        {
                            PriorityCompatibility.SetPriorityIfChanged(pawn, priority.Key, priority.Value);
                        }
                    }
                    finally
                    {
                        PriorityCompatibility.EndBatch();
                    }
                }
            }

            assignmentStopwatch.Stop();
            if (nextPendingAssignmentIndex >= pendingAssignments.Count)
            {
                nextRecalculationTick = Find.TickManager.TicksGame + RecalculationInterval;
                ClearPendingAssignments();
            }
        }

        private void ClearPendingAssignments()
        {
            for (int index = 0; index < pendingAssignments.Count; index++)
            {
                PendingPawnAssignment assignment = pendingAssignments[index];
                assignment.Reset(null);
                pendingAssignmentPool.Add(assignment);
            }

            pendingAssignments.Clear();
            pendingAssignmentsByPawn.Clear();
            nextPendingAssignmentIndex = 0;
            pendingAssignmentTotal = 0;
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

                int enabledRuleCount = 0;
                for (int ruleIndex = 0; ruleIndex < profile.CircumstanceRules.Count; ruleIndex++)
                {
                    if (profile.CircumstanceRules[ruleIndex].Enabled)
                    {
                        enabledRuleCount++;
                    }
                }

                var enabledRules = new CircumstanceRule[enabledRuleCount];
                int enabledRuleIndex = 0;
                for (int ruleIndex = 0; ruleIndex < profile.CircumstanceRules.Count; ruleIndex++)
                {
                    CircumstanceRule rule = profile.CircumstanceRules[ruleIndex];
                    if (!rule.Enabled)
                    {
                        continue;
                    }

                    enabledRules[enabledRuleIndex++] = rule;
                    requiredCircumstances.Add(rule.Type);
                }

                enabledProfiles.Add(new ResolvedWorkProfile(profile, workType, enabledRules));
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
                    WorkerCount = Math.Max(0, pair.Value),
                    RankGroupCounts = priority == 1
                        ? new List<int> { Math.Max(0, pair.Value), 0 }
                        : new List<int> { 0, Math.Max(0, pair.Value) }
                };
                profile.EnsureRankPriorities();
                int migratedPriority = Math.Max(0, Math.Min(PriorityCompatibility.MaximumPriority, priority));
                profile.RankPriorities[0] = migratedPriority;
                profile.RankPriorities[1] = migratedPriority;

                Profiles.Add(profile);
            }
        }
    }
}
