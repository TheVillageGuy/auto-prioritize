using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoPriority
{
    public enum ScoreFactorKind
    {
        Stat,
        RelevantSkill,
        RelevantPassion
    }

    public sealed class ScoreFactor
    {
        private StatDef resolvedStat;
        private bool statResolved;

        public readonly ScoreFactorKind Kind;
        public readonly string DefName;
        public readonly float Weight;
        public readonly bool LowerIsBetter;

        public ScoreFactor(ScoreFactorKind kind, string defName, float weight, bool lowerIsBetter = false)
        {
            Kind = kind;
            DefName = defName;
            Weight = weight;
            LowerIsBetter = lowerIsBetter;
        }

        public string Label
        {
            get
            {
                if (Kind == ScoreFactorKind.RelevantSkill)
                {
                    return "AutoPriority.Factor.RelevantSkill".Translate();
                }

                if (Kind == ScoreFactorKind.RelevantPassion)
                {
                    return "AutoPriority.Factor.Passion".Translate();
                }

                StatDef stat = Stat;
                return stat != null ? stat.LabelCap.ToString() : DefName;
            }
        }

        public StatDef Stat
        {
            get
            {
                if (!statResolved)
                {
                    resolvedStat = DefDatabase<StatDef>.GetNamedSilentFail(DefName);
                    statResolved = true;
                }

                return resolvedStat;
            }
        }
    }

    public sealed class ScoredPawn
    {
        public readonly Pawn Pawn;
        public readonly float Score;

        public ScoredPawn(Pawn pawn, float score)
        {
            Pawn = pawn;
            Score = score;
        }
    }

    /// <summary>
    /// Short-lived cache shared by every work type in one update pass. It prevents
    /// common stats such as MoveSpeed and WorkSpeedGlobal from being recalculated
    /// repeatedly, but is deliberately discarded after the pass so health,
    /// equipment, genes and hediff changes are observed on the next update.
    /// </summary>
    public sealed class WorkScoreCache
    {
        private struct CachedStatValue
        {
            public readonly float Value;
            public readonly bool Disabled;

            public CachedStatValue(float value, bool disabled)
            {
                Value = value;
                Disabled = disabled;
            }
        }

        private readonly Dictionary<Pawn, Dictionary<StatDef, CachedStatValue>> statValues =
            new Dictionary<Pawn, Dictionary<StatDef, CachedStatValue>>();
        private readonly Dictionary<Pawn, Dictionary<WorkTypeDef, float>> skillValues =
            new Dictionary<Pawn, Dictionary<WorkTypeDef, float>>();
        private readonly Dictionary<Pawn, Dictionary<WorkTypeDef, float>> passionValues =
            new Dictionary<Pawn, Dictionary<WorkTypeDef, float>>();

        public float StatValue(Pawn pawn, StatDef stat, bool lowerIsBetter)
        {
            Dictionary<StatDef, CachedStatValue> values;
            if (!statValues.TryGetValue(pawn, out values))
            {
                values = new Dictionary<StatDef, CachedStatValue>();
                statValues.Add(pawn, values);
            }

            CachedStatValue cached;
            if (!values.TryGetValue(stat, out cached))
            {
                bool disabled = stat.Worker.IsDisabledFor(pawn);
                float value = disabled ? 0f : pawn.GetStatValue(stat);
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    value = 0f;
                }

                cached = new CachedStatValue(value, disabled);
                values.Add(stat, cached);
            }

            return cached.Disabled
                ? (lowerIsBetter ? 1000000000f : 0f)
                : cached.Value;
        }

        public float SkillValue(Pawn pawn, WorkTypeDef workType)
        {
            Dictionary<WorkTypeDef, float> values = WorkValues(skillValues, pawn);
            float value;
            if (!values.TryGetValue(workType, out value))
            {
                value = pawn.skills == null ? 0f : pawn.skills.AverageOfRelevantSkillsFor(workType);
                values.Add(workType, value);
            }

            return value;
        }

        public float PassionValue(Pawn pawn, WorkTypeDef workType)
        {
            Dictionary<WorkTypeDef, float> values = WorkValues(passionValues, pawn);
            float value;
            if (!values.TryGetValue(workType, out value))
            {
                value = pawn.skills == null ? 0f : (float)pawn.skills.MaxPassionOfRelevantSkillsFor(workType);
                values.Add(workType, value);
            }

            return value;
        }

        private static Dictionary<WorkTypeDef, float> WorkValues(
            Dictionary<Pawn, Dictionary<WorkTypeDef, float>> cache,
            Pawn pawn)
        {
            Dictionary<WorkTypeDef, float> values;
            if (!cache.TryGetValue(pawn, out values))
            {
                values = new Dictionary<WorkTypeDef, float>();
                cache.Add(pawn, values);
            }

            return values;
        }
    }

    public static class WorkScoring
    {
        private static readonly Dictionary<string, IReadOnlyList<ScoreFactor>> ResolvedCatalog =
            new Dictionary<string, IReadOnlyList<ScoreFactor>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, ScoreFactor[]> Catalog =
            new Dictionary<string, ScoreFactor[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "Firefighter", Factors(Stat("MoveSpeed", 0.55f), Stat("GeneralLaborSpeed", 0.45f)) },
                { "Patient", Factors(Stat("ImmunityGainSpeed", 0.55f), Stat("InjuryHealingFactor", 0.35f), Stat("MoveSpeed", 0.10f)) },
                { "Doctor", Factors(Stat("MedicalTendQuality", 0.35f), Stat("MedicalSurgerySuccessChance", 0.25f), Stat("MedicalTendSpeed", 0.25f), Stat("MoveSpeed", 0.10f), Passion(0.05f)) },
                { "PatientBedRest", Factors(Stat("ImmunityGainSpeed", 0.60f), Stat("InjuryHealingFactor", 0.40f)) },
                { "BasicWorker", Factors(Stat("GeneralLaborSpeed", 0.65f), Stat("MoveSpeed", 0.35f)) },
                { "Childcare", Factors(Stat("BabyPlayGainFactor", 0.35f), Stat("SocialImpact", 0.20f), Stat("GeneralLaborSpeed", 0.20f), Stat("MoveSpeed", 0.15f), Skill(0.10f)) },
                { "Warden", Factors(Stat("NegotiationAbility", 0.50f), Stat("ArrestSuccessChance", 0.15f), Stat("SocialImpact", 0.20f), Stat("MoveSpeed", 0.10f), Passion(0.05f)) },
                { "Handling", Factors(Stat("TameAnimalChance", 0.25f), Stat("TrainAnimalChance", 0.25f), Stat("AnimalGatherYield", 0.20f), Stat("AnimalGatherSpeed", 0.15f), Stat("MoveSpeed", 0.10f), Passion(0.05f)) },
                { "Cooking", Factors(Stat("GeneralLaborSpeed", 0.40f), Stat("FoodPoisonChance", 0.25f, true), Skill(0.25f), Stat("MoveSpeed", 0.05f), Passion(0.05f)) },
                { "Hunting", Factors(Stat("ShootingAccuracyPawn", 0.30f), Stat("AimingDelayFactor", 0.15f, true), Stat("HuntingStealth", 0.20f), Stat("MoveSpeed", 0.15f), Skill(0.15f), Passion(0.05f)) },
                { "Construction", Factors(Stat("ConstructionSpeed", 0.40f), Stat("ConstructSuccessChance", 0.30f), Stat("FixBrokenDownBuildingSuccessChance", 0.15f), Stat("MoveSpeed", 0.10f), Passion(0.05f)) },
                { "Growing", Factors(Stat("PlantWorkSpeed", 0.40f), Stat("PlantHarvestYield", 0.30f), Stat("DrugHarvestYield", 0.15f), Stat("MoveSpeed", 0.10f), Passion(0.05f)) },
                { "Mining", Factors(Stat("MiningSpeed", 0.40f), Stat("DeepDrillingSpeed", 0.15f), Stat("MiningYield", 0.30f), Stat("MoveSpeed", 0.10f), Passion(0.05f)) },
                { "PlantCutting", Factors(Stat("PlantWorkSpeed", 0.45f), Stat("PlantHarvestYield", 0.35f), Stat("MoveSpeed", 0.15f), Passion(0.05f)) },
                { "Smithing", Factors(Stat("GeneralLaborSpeed", 0.45f), Skill(0.40f), Passion(0.10f), Stat("MoveSpeed", 0.05f)) },
                { "Tailoring", Factors(Stat("GeneralLaborSpeed", 0.45f), Skill(0.40f), Passion(0.10f), Stat("MoveSpeed", 0.05f)) },
                { "Art", Factors(Stat("GeneralLaborSpeed", 0.35f), Skill(0.50f), Passion(0.10f), Stat("MoveSpeed", 0.05f)) },
                { "Crafting", Factors(Stat("GeneralLaborSpeed", 0.60f), Skill(0.25f), Passion(0.10f), Stat("MoveSpeed", 0.05f)) },
                { "Hauling", Factors(Stat("MoveSpeed", 0.55f), Stat("CarryingCapacity", 0.45f)) },
                { "Cleaning", Factors(Stat("CleaningSpeed", 0.65f), Stat("MoveSpeed", 0.35f)) },
                { "Research", Factors(Stat("ResearchSpeed", 0.85f), Passion(0.15f)) },
                { "DarkStudy", Factors(Stat("EntityStudyRate", 0.40f), Stat("StudyEfficiency", 0.35f), Stat("ActivitySuppressionRate", 0.15f), Stat("MoveSpeed", 0.05f), Passion(0.05f)) },
                { "Fishing", Factors(Stat("FishingSpeed", 0.45f), Stat("FishingYield", 0.40f), Stat("MoveSpeed", 0.10f), Passion(0.05f)) }
            };

        public static IReadOnlyList<ScoreFactor> FactorsFor(WorkTypeDef workType)
        {
            IReadOnlyList<ScoreFactor> resolved;
            if (ResolvedCatalog.TryGetValue(workType.defName, out resolved))
            {
                return resolved;
            }

            ScoreFactor[] factors;
            if (Catalog.TryGetValue(workType.defName, out factors))
            {
                resolved = factors.Where(IsAvailable).ToList();
                ResolvedCatalog[workType.defName] = resolved;
                return resolved;
            }

            // Mod-added work types still receive a sensible, automatic profile.
            resolved = Factors(Skill(0.60f), Stat("WorkSpeedGlobal", 0.20f), Stat("MoveSpeed", 0.10f), Passion(0.10f))
                .Where(IsAvailable)
                .ToList();
            ResolvedCatalog[workType.defName] = resolved;
            return resolved;
        }

        public static List<ScoredPawn> Rank(WorkTypeDef workType, List<Pawn> pawns, WorkScoreCache cache)
        {
            IReadOnlyList<ScoreFactor> factors = FactorsFor(workType);
            if (pawns.Count == 0)
            {
                return new List<ScoredPawn>();
            }

            var raw = new float[pawns.Count, factors.Count];
            for (int pawnIndex = 0; pawnIndex < pawns.Count; pawnIndex++)
            {
                for (int factorIndex = 0; factorIndex < factors.Count; factorIndex++)
                {
                    raw[pawnIndex, factorIndex] = RawValue(pawns[pawnIndex], workType, factors[factorIndex], cache);
                }
            }

            var minimums = new float[factors.Count];
            var maximums = new float[factors.Count];
            for (int factorIndex = 0; factorIndex < factors.Count; factorIndex++)
            {
                minimums[factorIndex] = float.MaxValue;
                maximums[factorIndex] = float.MinValue;
                for (int pawnIndex = 0; pawnIndex < pawns.Count; pawnIndex++)
                {
                    minimums[factorIndex] = Math.Min(minimums[factorIndex], raw[pawnIndex, factorIndex]);
                    maximums[factorIndex] = Math.Max(maximums[factorIndex], raw[pawnIndex, factorIndex]);
                }
            }

            var scores = new List<ScoredPawn>(pawns.Count);
            for (int pawnIndex = 0; pawnIndex < pawns.Count; pawnIndex++)
            {
                float score = 0f;
                float totalWeight = 0f;
                for (int factorIndex = 0; factorIndex < factors.Count; factorIndex++)
                {
                    float min = minimums[factorIndex];
                    float max = maximums[factorIndex];
                    float normalized = max - min < 0.0001f ? 0.5f : Mathf.InverseLerp(min, max, raw[pawnIndex, factorIndex]);
                    if (factors[factorIndex].LowerIsBetter)
                    {
                        normalized = 1f - normalized;
                    }

                    score += normalized * factors[factorIndex].Weight;
                    totalWeight += factors[factorIndex].Weight;
                }

                if (totalWeight > 0f)
                {
                    score /= totalWeight;
                }

                scores.Add(new ScoredPawn(pawns[pawnIndex], score * 100f));
            }

            scores.Sort(delegate(ScoredPawn left, ScoredPawn right)
            {
                int scoreComparison = right.Score.CompareTo(left.Score);
                if (scoreComparison != 0)
                {
                    return scoreComparison;
                }

                int skillComparison = cache.SkillValue(right.Pawn, workType)
                    .CompareTo(cache.SkillValue(left.Pawn, workType));
                return skillComparison != 0
                    ? skillComparison
                    : left.Pawn.thingIDNumber.CompareTo(right.Pawn.thingIDNumber);
            });
            return scores;
        }

        private static float RawValue(Pawn pawn, WorkTypeDef workType, ScoreFactor factor, WorkScoreCache cache)
        {
            if (factor.Kind == ScoreFactorKind.RelevantSkill)
            {
                return cache.SkillValue(pawn, workType);
            }

            if (factor.Kind == ScoreFactorKind.RelevantPassion)
            {
                return cache.PassionValue(pawn, workType);
            }

            StatDef stat = factor.Stat;
            if (stat == null)
            {
                return 0f;
            }

            return cache.StatValue(pawn, stat, factor.LowerIsBetter);
        }

        private static bool IsAvailable(ScoreFactor factor)
        {
            return factor.Kind != ScoreFactorKind.Stat || factor.Stat != null;
        }

        private static ScoreFactor Stat(string defName, float weight, bool lowerIsBetter = false)
        {
            return new ScoreFactor(ScoreFactorKind.Stat, defName, weight, lowerIsBetter);
        }

        private static ScoreFactor Skill(float weight)
        {
            return new ScoreFactor(ScoreFactorKind.RelevantSkill, null, weight);
        }

        private static ScoreFactor Passion(float weight)
        {
            return new ScoreFactor(ScoreFactorKind.RelevantPassion, null, weight);
        }

        private static ScoreFactor[] Factors(params ScoreFactor[] factors)
        {
            return factors;
        }
    }
}
