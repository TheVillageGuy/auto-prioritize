using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoPriority
{
    /// <summary>
    /// Uses only signatures that are present in vanilla RimWorld. If Work Tab's
    /// optional scheduling API is found, cached delegates keep all 24 hours in
    /// sync without taking an assembly dependency on that mod.
    /// </summary>
    public static class PriorityCompatibility
    {
        private delegate void ScheduledPrioritySetter(Pawn pawn, WorkTypeDef workType, int priority, List<int> hours);
        private delegate int ScheduledPriorityGetter(Pawn pawn, WorkTypeDef workType, int hour);
        private delegate int MaximumPriorityGetter();

        private static readonly ScheduledPrioritySetter WorkTabSetter;
        private static readonly ScheduledPriorityGetter WorkTabGetter;
        private static readonly MaximumPriorityGetter WorkTabMaximumPriority;
        private static readonly FieldInfo WorkTabMaximumPriorityField;
        private static readonly FieldInfo VanillaPrioritiesField = typeof(Pawn_WorkSettings).GetField(
            "priorities", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo VanillaWorkGiversDirtyField = typeof(Pawn_WorkSettings).GetField(
            "workGiversDirty", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly HashSet<Pawn> BatchedChangedPawns = new HashSet<Pawn>();
        private static readonly Dictionary<Pawn, DefMap<WorkTypeDef, int>> BatchedPriorityMaps =
            new Dictionary<Pawn, DefMap<WorkTypeDef, int>>();
        private static readonly Dictionary<Pawn, HashSet<WorkTypeDef>> BatchedDisabledWorkTypes =
            new Dictionary<Pawn, HashSet<WorkTypeDef>>();
        private static readonly List<HashSet<WorkTypeDef>> DisabledSetPool = new List<HashSet<WorkTypeDef>>();
        private static bool batchActive;

        static PriorityCompatibility()
        {
            try
            {
                MethodInfo setter;
                MethodInfo getter;
                Type settings;
                if (!TryFindScheduledPriorityApi(out setter, out getter, out settings))
                {
                    return;
                }

                WorkTabSetter = (ScheduledPrioritySetter)Delegate.CreateDelegate(typeof(ScheduledPrioritySetter), setter);
                WorkTabGetter = (ScheduledPriorityGetter)Delegate.CreateDelegate(typeof(ScheduledPriorityGetter), getter);

                if (settings != null)
                {
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    PropertyInfo maximumPriority = settings.GetProperty("MaxPriority", flags);
                    MethodInfo maximumGetter = maximumPriority == null ? null : maximumPriority.GetGetMethod(true);
                    if (maximumGetter != null && maximumGetter.ReturnType == typeof(int))
                    {
                        WorkTabMaximumPriority = (MaximumPriorityGetter)Delegate.CreateDelegate(
                            typeof(MaximumPriorityGetter), maximumGetter);
                    }
                    else
                    {
                        FieldInfo maximumField = settings.GetField("maxPriority", flags) ?? settings.GetField("MaxPriority", flags);
                        if (maximumField != null && maximumField.FieldType == typeof(int))
                        {
                            WorkTabMaximumPriorityField = maximumField;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Log.Warning("[Let Me Skill For You] Work Tab was detected, but its optional scheduling bridge could not be initialized. Vanilla priority compatibility will be used. " + exception.Message);
            }
        }

        public static int MaximumPriority
        {
            get
            {
                int maximum;
                if (WorkTabMaximumPriority != null)
                {
                    maximum = WorkTabMaximumPriority();
                }
                else if (WorkTabMaximumPriorityField != null)
                {
                    maximum = (int)WorkTabMaximumPriorityField.GetValue(null);
                }
                else
                {
                    maximum = 4;
                }

                return Math.Max(4, Math.Min(99, maximum));
            }
        }

        public static void SetPriorityIfChanged(Pawn pawn, WorkTypeDef workType, int priority)
        {
            if (batchActive && WorkTabSetter == null && VanillaPrioritiesField != null &&
                VanillaWorkGiversDirtyField != null)
            {
                SetVanillaPriorityBatched(pawn, workType, priority);
                return;
            }

            int current = WorkTabGetter != null
                ? WorkTabGetter(pawn, workType, -1)
                : pawn.workSettings.GetPriority(workType);
            if (current == priority)
            {
                return;
            }

            if (WorkTabSetter != null)
            {
                // A null hours list means the whole day in Work Tab's public API.
                WorkTabSetter(pawn, workType, priority, null);
            }
            else
            {
                pawn.workSettings.SetPriority(workType, priority);
            }
        }

        public static int GetPriority(Pawn pawn, WorkTypeDef workType)
        {
            return WorkTabGetter != null
                ? WorkTabGetter(pawn, workType, -1)
                : pawn.workSettings.GetPriority(workType);
        }

        public static void BeginBatch()
        {
            batchActive = true;
            BatchedChangedPawns.Clear();
            BatchedPriorityMaps.Clear();
            foreach (HashSet<WorkTypeDef> disabled in BatchedDisabledWorkTypes.Values)
            {
                disabled.Clear();
                DisabledSetPool.Add(disabled);
            }

            BatchedDisabledWorkTypes.Clear();
        }

        public static void EndBatch()
        {
            if (!batchActive)
            {
                return;
            }

            batchActive = false;
            foreach (Pawn pawn in BatchedChangedPawns)
            {
                if (pawn == null || pawn.workSettings == null)
                {
                    continue;
                }

                VanillaWorkGiversDirtyField.SetValue(pawn.workSettings, true);
                HashSet<WorkTypeDef> disabled;
                if (pawn.jobs == null || !BatchedDisabledWorkTypes.TryGetValue(pawn, out disabled) || disabled.Count == 0)
                {
                    continue;
                }

                // Pawn_WorkSettings.SetPriority scans the job queue every time a
                // work type is set to zero. Scan it once for the whole batch.
                pawn.jobs.jobQueue.RemoveAll(pawn, job =>
                    job != null && !job.playerForced && job.workGiverDef != null &&
                    disabled.Contains(job.workGiverDef.workType));

                Job currentJob = pawn.jobs.curJob;
                if (currentJob != null && !currentJob.playerForced && currentJob.workGiverDef != null &&
                    disabled.Contains(currentJob.workGiverDef.workType))
                {
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
            }

            BatchedChangedPawns.Clear();
            BatchedPriorityMaps.Clear();
            foreach (HashSet<WorkTypeDef> disabled in BatchedDisabledWorkTypes.Values)
            {
                disabled.Clear();
                DisabledSetPool.Add(disabled);
            }

            BatchedDisabledWorkTypes.Clear();
        }

        private static void SetVanillaPriorityBatched(Pawn pawn, WorkTypeDef workType, int priority)
        {
            DefMap<WorkTypeDef, int> priorities;
            if (!BatchedPriorityMaps.TryGetValue(pawn, out priorities))
            {
                priorities = VanillaPrioritiesField.GetValue(pawn.workSettings) as DefMap<WorkTypeDef, int>;
                if (priorities != null)
                {
                    BatchedPriorityMaps.Add(pawn, priorities);
                }
            }

            if (priorities == null || priorities[workType] == priority)
            {
                return;
            }

            priorities[workType] = priority;
            BatchedChangedPawns.Add(pawn);

            HashSet<WorkTypeDef> disabled;
            if (!BatchedDisabledWorkTypes.TryGetValue(pawn, out disabled))
            {
                int last = DisabledSetPool.Count - 1;
                if (last >= 0)
                {
                    disabled = DisabledSetPool[last];
                    DisabledSetPool.RemoveAt(last);
                }
                else
                {
                    disabled = new HashSet<WorkTypeDef>();
                }

                BatchedDisabledWorkTypes.Add(pawn, disabled);
            }

            if (priority == 0)
            {
                disabled.Add(workType);
            }
            else
            {
                disabled.Remove(workType);
            }
        }

        private static bool TryFindScheduledPriorityApi(
            out MethodInfo setter,
            out MethodInfo getter,
            out Type settings)
        {
            setter = null;
            getter = null;
            settings = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            // Prefer the original API name, then fall back to matching the shared
            // method and settings signatures used by Work Tab forks.
            for (int index = 0; index < assemblies.Length; index++)
            {
                Type extensions = assemblies[index].GetType("WorkTab.Pawn_Extensions", false);
                if (extensions != null && TryGetPriorityMethods(extensions, out setter, out getter))
                {
                    settings = FindSettingsType(assemblies[index], extensions.Namespace);
                    return true;
                }
            }

            for (int assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
            {
                Type[] types = LoadableTypes(assemblies[assemblyIndex]);
                for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    Type extensions = types[typeIndex];
                    if (extensions == null || !extensions.IsAbstract || !extensions.IsSealed ||
                        !TryGetPriorityMethods(extensions, out setter, out getter))
                    {
                        continue;
                    }

                    settings = FindSettingsType(assemblies[assemblyIndex], extensions.Namespace);
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetPriorityMethods(Type extensions, out MethodInfo setter, out MethodInfo getter)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            setter = extensions.GetMethod(
                "SetPriority", flags, null,
                new[] { typeof(Pawn), typeof(WorkTypeDef), typeof(int), typeof(List<int>) }, null);
            getter = extensions.GetMethod(
                "GetPriority", flags, null,
                new[] { typeof(Pawn), typeof(WorkTypeDef), typeof(int) }, null);
            return setter != null && setter.ReturnType == typeof(void) &&
                   getter != null && getter.ReturnType == typeof(int);
        }

        private static Type FindSettingsType(Assembly assembly, string preferredNamespace)
        {
            Type[] types = LoadableTypes(assembly);
            Type fallback = null;
            for (int index = 0; index < types.Length; index++)
            {
                Type type = types[index];
                if (type == null || !HasMaximumPriorityMember(type))
                {
                    continue;
                }

                if (type.Namespace == preferredNamespace && type.Name == "Settings")
                {
                    return type;
                }

                if (type.Namespace == preferredNamespace || fallback == null)
                {
                    fallback = type;
                }
            }

            return fallback;
        }

        private static bool HasMaximumPriorityMember(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            PropertyInfo property = type.GetProperty("MaxPriority", flags);
            if (property != null && property.PropertyType == typeof(int))
            {
                return true;
            }

            FieldInfo field = type.GetField("maxPriority", flags) ?? type.GetField("MaxPriority", flags);
            return field != null && field.FieldType == typeof(int);
        }

        private static Type[] LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types;
            }
            catch
            {
                return new Type[0];
            }
        }
    }
}
