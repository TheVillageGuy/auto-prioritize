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
            var snapshot = new ColonyCircumstanceSnapshot();
            List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            int colonistCount = Math.Max(1, colonists.Count);

            int patients = colonists.Count(pawn => pawn.health != null && pawn.health.HasHediffsNeedingTendByPlayer());
            patients += map.mapPawns.PrisonersOfColonySpawned.Count(pawn => pawn.health != null && pawn.health.HasHediffsNeedingTendByPlayer());
            snapshot.counts[CircumstanceType.MedicalEmergency] = patients;

            snapshot.counts[CircumstanceType.Fire] = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;
            snapshot.counts[CircumstanceType.PrisonerLoad] = map.mapPawns.PrisonersOfColonySpawnedCount;

            float availableNutrition = 0f;
            int medicine = 0;
            int construction = 0;
            int filth = 0;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing.Destroyed)
                {
                    continue;
                }

                if (thing.def.IsNutritionGivingIngestible && thing.def.ingestible.HumanEdible && !thing.def.IsDrug && !(thing is Corpse))
                {
                    availableNutrition += thing.GetStatValue(StatDefOf.Nutrition) * thing.stackCount;
                }

                if (thing.def.IsMedicine)
                {
                    medicine += thing.stackCount;
                }

                if (thing is Blueprint || thing is Frame)
                {
                    construction++;
                }

                if (thing is Filth)
                {
                    filth++;
                }
            }

            // Roughly two days of food. The value intentionally ignores future crops.
            snapshot.counts[CircumstanceType.FoodShortage] = availableNutrition < colonistCount * 3.2f
                ? Math.Max(1, (int)Math.Ceiling(colonistCount * 3.2f - availableNutrition))
                : 0;
            snapshot.counts[CircumstanceType.LowMedicine] = medicine < colonistCount
                ? colonistCount - medicine
                : 0;
            snapshot.counts[CircumstanceType.ConstructionBacklog] = construction >= 5 ? construction : 0;
            snapshot.counts[CircumstanceType.CleaningBacklog] = filth >= 50 ? filth : 0;

            int haul = CountDesignations(map, DesignationDefOf.Haul);
            int plants = CountDesignations(map, DesignationDefOf.CutPlant) + CountDesignations(map, DesignationDefOf.HarvestPlant);
            int animals = CountDesignations(map, DesignationDefOf.Tame) + CountDesignations(map, DesignationDefOf.Slaughter);
            int hunts = CountDesignations(map, DesignationDefOf.Hunt);
            snapshot.counts[CircumstanceType.HaulingBacklog] = haul >= 10 ? haul : 0;
            snapshot.counts[CircumstanceType.PlantBacklog] = plants >= 10 ? plants : 0;
            snapshot.counts[CircumstanceType.AnimalBacklog] = animals > 0 ? animals : 0;
            snapshot.counts[CircumstanceType.HuntingBacklog] = hunts >= 3 ? hunts : 0;

            return snapshot;
        }

        private static int CountDesignations(Map map, DesignationDef def)
        {
            return def == null ? 0 : map.designationManager.SpawnedDesignationsOfDef(def).Count();
        }
    }
}
