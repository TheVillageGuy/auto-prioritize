using System;
using System.Collections.Generic;
using System.Linq;
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

            return All.Select(info => new CircumstanceRule(info.Type, enabledTypes.Contains(info.Type))).ToList();
        }

        public static void EnsureAllRules(List<CircumstanceRule> rules)
        {
            foreach (CircumstanceInfo info in All)
            {
                if (rules.All(rule => rule.Type != info.Type))
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
                int patients = colonists.Count(pawn => pawn.health != null && pawn.health.HasHediffsNeedingTendByPlayer());
                patients += map.mapPawns.PrisonersOfColonySpawned.Count(pawn => pawn.health != null && pawn.health.HasHediffsNeedingTendByPlayer());
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
            if (needsFood || needsMedicine || needsConstruction || needsFilth)
            {
                foreach (Thing thing in map.listerThings.AllThings)
                {
                    if (thing.Destroyed)
                    {
                        continue;
                    }

                    if (needsFood && thing.def.IsNutritionGivingIngestible && thing.def.ingestible.HumanEdible && !thing.def.IsDrug && !(thing is Corpse))
                    {
                        availableNutrition += thing.GetStatValue(StatDefOf.Nutrition) * thing.stackCount;
                    }

                    if (needsMedicine && thing.def.IsMedicine)
                    {
                        medicine += thing.stackCount;
                    }

                    if (needsConstruction && (thing is Blueprint || thing is Frame))
                    {
                        construction++;
                    }

                    if (needsFilth && thing is Filth)
                    {
                        filth++;
                    }
                }
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
            return def == null ? 0 : map.designationManager.SpawnedDesignationsOfDef(def).Count();
        }
    }
}
