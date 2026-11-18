using System;

namespace RouteLinkBar.Algorithm.Geometry
{
    public sealed class LinkageRouteClearanceEvaluation
    {
        public bool IsClear { get; internal set; }
        public bool IsSameGroupExcluded { get; internal set; }
        public bool IsLapEnvelopePair { get; internal set; }
        public bool IsExactDistance { get; internal set; }
        public double ActualDistance { get; internal set; }
        public double RequiredDistance { get; internal set; }
        public double ViolationDepth => IsClear || double.IsInfinity(ActualDistance)
            ? 0d
            : Math.Max(0d, RequiredDistance - ActualDistance);
    }

    public static class LinkageRouteClearanceEvaluator
    {
        private const double DistanceTolerance =
            LinkageRouteGeometryTolerances.DistanceComparison;

        public static LinkageRouteClearanceEvaluation EvaluatePair(
            LinkageRouteSolid firstSolid,
            LinkageRouteSolid secondSolid,
            double requiredInsulationClearance,
            double acceptanceTolerance = DistanceTolerance)
        {
            return EvaluatePairCore(
                firstSolid,
                secondSolid,
                requiredInsulationClearance,
                false,
                acceptanceTolerance);
        }

        public static LinkageRouteClearanceEvaluation EvaluatePairWithBroadPhase(
            LinkageRouteSolid firstSolid,
            LinkageRouteSolid secondSolid,
            double insulationClearance)
        {
            return EvaluatePairWithBroadPhase(firstSolid, secondSolid,
                insulationClearance, DistanceTolerance);
        }

        public static LinkageRouteClearanceEvaluation EvaluatePairWithBroadPhase(
            LinkageRouteSolid firstSolid,
            LinkageRouteSolid secondSolid,
            double insulationClearance,
            double acceptanceTolerance)
        {
            return EvaluatePairCore(
                firstSolid,
                secondSolid,
                insulationClearance,
                true,
                acceptanceTolerance);
        }

        private static LinkageRouteClearanceEvaluation EvaluatePairCore(
            LinkageRouteSolid firstSolid,
            LinkageRouteSolid secondSolid,
            double requiredInsulationClearance,
            bool allowAxisAlignedLowerBoundPass,
            double acceptanceTolerance = DistanceTolerance)
        {
            if (requiredInsulationClearance < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(requiredInsulationClearance));
            }

            if (firstSolid == null || secondSolid == null)
            {
                return new LinkageRouteClearanceEvaluation
                {
                    IsClear = false,
                    IsExactDistance = true,
                    ActualDistance = 0d,
                    RequiredDistance = requiredInsulationClearance
                };
            }

            var sameClearanceGroup =
                !string.IsNullOrWhiteSpace(firstSolid.ClearanceGroupKey) &&
                string.Equals(
                    firstSolid.ClearanceGroupKey,
                    secondSolid.ClearanceGroupKey,
                    StringComparison.OrdinalIgnoreCase);
            if (sameClearanceGroup)
            {
                return new LinkageRouteClearanceEvaluation
                {
                    IsClear = true,
                    IsSameGroupExcluded = true,
                    IsExactDistance = true,
                    ActualDistance = double.PositiveInfinity,
                    RequiredDistance = requiredInsulationClearance
                };
            }

            var firstIsLapEnvelope = firstSolid.IsLapClearanceEnvelope;
            var secondIsLapEnvelope = secondSolid.IsLapClearanceEnvelope;
            if (firstIsLapEnvelope || secondIsLapEnvelope)
            {
                // 搭接面区域只允许“一侧绝缘盒 + 一侧原始铜排”。
                // 两个绝缘盒之间不再次计算，避免同一搭接面重复膨胀。
                if (firstIsLapEnvelope && secondIsLapEnvelope)
                {
                    return new LinkageRouteClearanceEvaluation
                    {
                        IsClear = true,
                        IsExactDistance = true,
                        ActualDistance = double.PositiveInfinity,
                        RequiredDistance = 0d,
                        IsLapEnvelopePair = true
                    };
                }

                var hasPositiveVolumeOverlap = LinkageRouteSolidDistance
                    .HasPositiveVolumeIntersection(firstSolid, secondSolid, acceptanceTolerance);
                return new LinkageRouteClearanceEvaluation
                {
                    IsClear = !hasPositiveVolumeOverlap,
                    IsExactDistance = true,
                    ActualDistance = hasPositiveVolumeOverlap
                        ? 0d
                        : LinkageRouteSolidDistance.GetMinimumDistance(
                            firstSolid,
                            secondSolid),
                    RequiredDistance = 0d,
                    IsLapEnvelopePair = true
                };
            }

            if (allowAxisAlignedLowerBoundPass)
            {
                var axisAlignedLowerBound = LinkageRouteSolidDistance
                    .GetAxisAlignedBoundingBoxDistanceLowerBound(
                        firstSolid,
                        secondSolid);
                if (axisAlignedLowerBound + acceptanceTolerance >= requiredInsulationClearance)
                {
                    return new LinkageRouteClearanceEvaluation
                    {
                        IsClear = true,
                        IsExactDistance = false,
                        ActualDistance = axisAlignedLowerBound,
                        RequiredDistance = requiredInsulationClearance
                    };
                }
            }

            var actualDistance = LinkageRouteSolidDistance.GetMinimumDistance(firstSolid, secondSolid);
            return new LinkageRouteClearanceEvaluation
            {
                IsClear = actualDistance + acceptanceTolerance >= requiredInsulationClearance,
                IsExactDistance = true,
                ActualDistance = actualDistance,
                RequiredDistance = requiredInsulationClearance
            };
        }
    }
}
