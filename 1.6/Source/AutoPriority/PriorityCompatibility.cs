using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

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

        static PriorityCompatibility()
        {
            Type extensions = FindType("WorkTab.Pawn_Extensions");
            if (extensions == null)
            {
                return;
            }

            try
            {
                MethodInfo setter = extensions.GetMethod(
                    "SetPriority",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Pawn), typeof(WorkTypeDef), typeof(int), typeof(List<int>) },
                    null);
                MethodInfo getter = extensions.GetMethod(
                    "GetPriority",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Pawn), typeof(WorkTypeDef), typeof(int) },
                    null);

                Type settings = FindType("WorkTab.Settings");
                PropertyInfo maximumPriority = settings == null
                    ? null
                    : settings.GetProperty("MaxPriority", BindingFlags.Public | BindingFlags.Static);

                if (setter != null && getter != null)
                {
                    WorkTabSetter = (ScheduledPrioritySetter)Delegate.CreateDelegate(typeof(ScheduledPrioritySetter), setter);
                    WorkTabGetter = (ScheduledPriorityGetter)Delegate.CreateDelegate(typeof(ScheduledPriorityGetter), getter);
                }

                if (maximumPriority != null && maximumPriority.GetGetMethod() != null)
                {
                    WorkTabMaximumPriority = (MaximumPriorityGetter)Delegate.CreateDelegate(
                        typeof(MaximumPriorityGetter), maximumPriority.GetGetMethod());
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
                if (WorkTabMaximumPriority == null)
                {
                    return 4;
                }

                return Math.Max(4, Math.Min(99, WorkTabMaximumPriority()));
            }
        }

        public static void SetPriorityIfChanged(Pawn pawn, WorkTypeDef workType, int priority)
        {
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

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int index = 0; index < assemblies.Length; index++)
            {
                Type type = assemblies[index].GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }
    }
}
