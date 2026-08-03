using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoPriority
{
    public enum CircumstanceType
    {
        MedicalEmergency,
        Fire,
        FoodShortage,
        LowMedicine,
        PrisonerLoad,
        ConstructionBacklog,
        HaulingBacklog,
        CleaningBacklog,
        PlantBacklog,
        AnimalBacklog,
        HuntingBacklog
    }

    public sealed class CircumstanceRule : IExposable
    {
        public CircumstanceType Type;
        public bool Enabled;
        public int ExtraWorkers = 1;
        public int PriorityBoost = 1;

        public CircumstanceRule()
        {
        }

        public CircumstanceRule(CircumstanceType type, bool enabled)
        {
            Type = type;
            Enabled = enabled;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref Type, "type", CircumstanceType.MedicalEmergency);
            Scribe_Values.Look(ref Enabled, "enabled", false);
            Scribe_Values.Look(ref ExtraWorkers, "extraWorkers", 1);
            Scribe_Values.Look(ref PriorityBoost, "priorityBoost", 1);
            ExtraWorkers = Math.Max(0, Math.Min(10, ExtraWorkers));
            PriorityBoost = Math.Max(0, Math.Min(3, PriorityBoost));
        }
    }

    public sealed class CircumstanceInfo
    {
        public readonly CircumstanceType Type;
        public readonly string LabelKey;
        public readonly string DescriptionKey;

        public CircumstanceInfo(CircumstanceType type, string labelKey, string descriptionKey)
        {
            Type = type;
            LabelKey = labelKey;
            DescriptionKey = descriptionKey;
        }
    }

    public static class CircumstanceCatalog
    {
        public static readonly IReadOnlyList<CircumstanceInfo> All = new List<CircumstanceInfo>
        {
            new CircumstanceInfo(CircumstanceType.MedicalEmergency, "AutoPriority.Circumstance.Medical", "AutoPriority.Circumstance.Medical.Desc"),
            new CircumstanceInfo(CircumstanceType.Fire, "AutoPriority.Circumstance.Fire", "AutoPriority.Circumstance.Fire.Desc"),
            new CircumstanceInfo(CircumstanceType.FoodShortage, "AutoPriority.Circumstance.Food", "AutoPriority.Circumstance.Food.Desc"),
            new CircumstanceInfo(CircumstanceType.LowMedicine, "AutoPriority.Circumstance.Medicine", "AutoPriority.Circumstance.Medicine.Desc"),
            new CircumstanceInfo(CircumstanceType.PrisonerLoad, "AutoPriority.Circumstance.Prisoners", "AutoPriority.Circumstance.Prisoners.Desc"),
            new CircumstanceInfo(CircumstanceType.ConstructionBacklog, "AutoPriority.Circumstance.Construction", "AutoPriority.Circumstance.Construction.Desc"),
            new CircumstanceInfo(CircumstanceType.HaulingBacklog, "AutoPriority.Circumstance.Hauling", "AutoPriority.Circumstance.Hauling.Desc"),
            new CircumstanceInfo(CircumstanceType.CleaningBacklog, "AutoPriority.Circumstance.Cleaning", "AutoPriority.Circumstance.Cleaning.Desc"),
            new CircumstanceInfo(CircumstanceType.PlantBacklog, "AutoPriority.Circumstance.Plants", "AutoPriority.Circumstance.Plants.Desc"),
            new CircumstanceInfo(CircumstanceType.AnimalBacklog, "AutoPriority.Circumstance.Animals", "AutoPriority.Circumstance.Animals.Desc"),
            new CircumstanceInfo(CircumstanceType.HuntingBacklog, "AutoPriority.Circumstance.Hunting", "AutoPriority.Circumstance.Hunting.Desc")
        };

        private static readonly Dictionary<string, CircumstanceType[]> Defaults =
            new Dictionary<string, CircumstanceType[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "Doctor", new[] { CircumstanceType.MedicalEmergency, CircumstanceType.LowMedicine } },
                { "Firefighter", new[] { CircumstanceType.Fire } },
                { "Warden", new[] { CircumstanceType.PrisonerLoad } },
                { "Cooking", new[] { CircumstanceType.FoodShortage } },
                { "Hunting", new[] { CircumstanceType.FoodShortage, CircumstanceType.HuntingBacklog } },
                { "Handling", new[] { CircumstanceType.AnimalBacklog } },
                { "Construction", new[] { CircumstanceType.ConstructionBacklog } },
                { "Growing", new[] { CircumstanceType.FoodShortage, CircumstanceType.PlantBacklog } },
                { "PlantCutting", new[] { CircumstanceType.FoodShortage, CircumstanceType.PlantBacklog } },
                { "Hauling", new[] { CircumstanceType.HaulingBacklog, CircumstanceType.FoodShortage } },
                { "Cleaning", new[] { CircumstanceType.CleaningBacklog } },
                { "Fishing", new[] { CircumstanceType.FoodShortage } }
            };

        public static List<CircumstanceRule> CreateRules(string workTypeDefName)
        {
            CircumstanceType[] enabledTypes;
            if (!Defaults.TryGetValue(workTypeDefName, out enabledTypes))
            {
                enabledTypes = new CircumstanceType[0];
            }

            var rules = new List<CircumstanceRule>(All.Count);
            for (int infoIndex = 0; infoIndex < All.Count; infoIndex++)
            {
                CircumstanceInfo info = All[infoIndex];
                bool enabled = false;
                for (int typeIndex = 0; typeIndex < enabledTypes.Length; typeIndex++)
                {
                    if (enabledTypes[typeIndex] == info.Type)
                    {
                        enabled = true;
                        break;
                    }
                }

                rules.Add(new CircumstanceRule(info.Type, enabled));
            }

            return rules;
        }

        public static void EnsureAllRules(List<CircumstanceRule> rules)
        {
            var existing = new HashSet<CircumstanceType>();
            for (int ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
            {
                existing.Add(rules[ruleIndex].Type);
            }

            foreach (CircumstanceInfo info in All)
            {
                if (!existing.Contains(info.Type))
                {
                    rules.Add(new CircumstanceRule(info.Type, false));
                }
            }
        }
    }

    public sealed class ColonyCircumstanceSnapshot
    {
        private readonly Dictionary<CircumstanceType, int> counts = new Dictionary<CircumstanceType, int>();

        public bool IsActive(CircumstanceType type)
        {
            int count;
            return counts.TryGetValue(type, out count) && count > 0;
        }

        public int Count(CircumstanceType type)
        {
            int count;
            return counts.TryGetValue(type, out count) ? count : 0;
        }

        public static ColonyCircumstanceSnapshot Capture(Map map)
        {
            return Capture(map, null);
        }

        public static ColonyCircumstanceSnapshot Capture(Map map, ISet<CircumstanceType> requested)
        {
            var snapshot = new ColonyCircumstanceSnapshot();
            List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            int colonistCount = Math.Max(1, colonists.Count);

            if (Needs(requested, CircumstanceType.MedicalEmergency))
            {
                int patients = CountPatientsNeedingTend(colonists);
                patients += CountPatientsNeedingTend(map.mapPawns.PrisonersOfColonySpawned);
                snapshot.counts[CircumstanceType.MedicalEmergency] = patients;
            }

            if (Needs(requested, CircumstanceType.Fire))
            {
                snapshot.counts[CircumstanceType.Fire] = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;
            }

            if (Needs(requested, CircumstanceType.PrisonerLoad))
            {
                snapshot.counts[CircumstanceType.PrisonerLoad] = map.mapPawns.PrisonersOfColonySpawnedCount;
            }

            float availableNutrition = 0f;
            int medicine = 0;
            int construction = 0;
            int filth = 0;
            bool needsFood = Needs(requested, CircumstanceType.FoodShortage);
            bool needsMedicine = Needs(requested, CircumstanceType.LowMedicine);
            bool needsConstruction = Needs(requested, CircumstanceType.ConstructionBacklog);
            bool needsFilth = Needs(requested, CircumstanceType.CleaningBacklog);
            if (needsFood)
            {
                List<Thing> food = map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSource);
                for (int index = 0; index < food.Count; index++)
                {
                    Thing thing = food[index];
                    if (!thing.Destroyed && thing.def.IsNutritionGivingIngestible &&
                        thing.def.ingestible.HumanEdible && !thing.def.IsDrug && !(thing is Corpse))
                    {
                        availableNutrition += thing.GetStatValue(StatDefOf.Nutrition) * thing.stackCount;
                    }
                }
            }

            if (needsMedicine)
            {
                List<Thing> medicines = map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine);
                for (int index = 0; index < medicines.Count; index++)
                {
                    medicine += medicines[index].stackCount;
                }
            }

            if (needsConstruction)
            {
                construction = map.listerThings.ThingsInGroup(ThingRequestGroup.Construction).Count;
            }

            if (needsFilth)
            {
                filth = map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).Count;
            }

            // Roughly two days of food. The value intentionally ignores future crops.
            if (needsFood)
            {
                snapshot.counts[CircumstanceType.FoodShortage] = availableNutrition < colonistCount * 3.2f
                    ? Math.Max(1, (int)Math.Ceiling(colonistCount * 3.2f - availableNutrition))
                    : 0;
            }

            if (needsMedicine)
            {
                snapshot.counts[CircumstanceType.LowMedicine] = medicine < colonistCount
                    ? colonistCount - medicine
                    : 0;
            }

            if (needsConstruction)
            {
                snapshot.counts[CircumstanceType.ConstructionBacklog] = construction >= 5 ? construction : 0;
            }

            if (needsFilth)
            {
                snapshot.counts[CircumstanceType.CleaningBacklog] = filth >= 50 ? filth : 0;
            }

            if (Needs(requested, CircumstanceType.HaulingBacklog))
            {
                int haul = CountDesignations(map, DesignationDefOf.Haul);
                snapshot.counts[CircumstanceType.HaulingBacklog] = haul >= 10 ? haul : 0;
            }

            if (Needs(requested, CircumstanceType.PlantBacklog))
            {
                int plants = CountDesignations(map, DesignationDefOf.CutPlant) + CountDesignations(map, DesignationDefOf.HarvestPlant);
                snapshot.counts[CircumstanceType.PlantBacklog] = plants >= 10 ? plants : 0;
            }

            if (Needs(requested, CircumstanceType.AnimalBacklog))
            {
                int animals = CountDesignations(map, DesignationDefOf.Tame) + CountDesignations(map, DesignationDefOf.Slaughter);
                snapshot.counts[CircumstanceType.AnimalBacklog] = animals;
            }

            if (Needs(requested, CircumstanceType.HuntingBacklog))
            {
                int hunts = CountDesignations(map, DesignationDefOf.Hunt);
                snapshot.counts[CircumstanceType.HuntingBacklog] = hunts >= 3 ? hunts : 0;
            }

            return snapshot;
        }

        private static bool Needs(ISet<CircumstanceType> requested, CircumstanceType type)
        {
            return requested == null || requested.Contains(type);
        }

        private static int CountDesignations(Map map, DesignationDef def)
        {
            if (def == null)
            {
                return 0;
            }

            int count = 0;
            foreach (Designation designation in map.designationManager.SpawnedDesignationsOfDef(def))
            {
                count++;
            }

            return count;
        }

        private static int CountPatientsNeedingTend(List<Pawn> pawns)
        {
            int count = 0;
            for (int index = 0; index < pawns.Count; index++)
            {
                Pawn pawn = pawns[index];
                if (pawn.health != null && pawn.health.HasHediffsNeedingTendByPlayer())
                {
                    count++;
                }
            }

            return count;
        }
    }
}
