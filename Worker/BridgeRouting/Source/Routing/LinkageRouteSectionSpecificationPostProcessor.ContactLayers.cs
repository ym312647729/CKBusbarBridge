using System;
using System.Collections.Generic;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteSectionSpecificationPostProcessor
    {
        private static bool TrySelectNonIntersectingContactLayers(
            IReadOnlyList<double> stationaryLayerCoordinates,
            IReadOnlyList<double> desiredMovingCoordinates,
            double contactCenterDistance,
            double movingLayerThickness,
            double[] preferredUniformCoordinates,
            out double[] selectedCoordinates)
        {
            selectedCoordinates = null;
            // 已合法的整组同侧层位必须逐值保留。这里只选择构造层位，
            // 不替代后续真实有限实体、I链、固定端和最终绝缘校验。
            if (AreContactLayersSeparated(stationaryLayerCoordinates,
                    preferredUniformCoordinates, contactCenterDistance,
                    movingLayerThickness))
            {
                selectedCoordinates = preferredUniformCoordinates;
                return true;
            }

            var memberCount = stationaryLayerCoordinates.Count;
            var alternativeUniformCoordinates = new double[memberCount];
            for (var layerIndex = 0; layerIndex < memberCount; layerIndex++)
                alternativeUniformCoordinates[layerIndex] =
                    2d * stationaryLayerCoordinates[layerIndex] -
                    preferredUniformCoordinates[layerIndex];
            if (AreContactLayersSeparated(stationaryLayerCoordinates,
                    alternativeUniformCoordinates, contactCenterDistance,
                    movingLayerThickness))
            {
                selectedCoordinates = alternativeUniformCoordinates;
                return true;
            }

            // 每个成员只接触自身对应层的正/负厚面。二态动态规划保留
            // 身份和层序，避免2^N枚举；跨静止层检查总复杂度O(N²)。
            var contactCoordinates = new double[memberCount, 2];
            var contactCosts = new double[memberCount, 2];
            var precedingSides = new int[memberCount, 2];
            for (var layerIndex = 0; layerIndex < memberCount; layerIndex++)
            {
                for (var sideIndex = 0; sideIndex < 2; sideIndex++)
                {
                    var contactCoordinate = stationaryLayerCoordinates[layerIndex] +
                        (sideIndex == 0 ? -contactCenterDistance : contactCenterDistance);
                    contactCoordinates[layerIndex, sideIndex] = contactCoordinate;
                    contactCosts[layerIndex, sideIndex] = double.PositiveInfinity;
                    precedingSides[layerIndex, sideIndex] = -1;
                    if (!IsContactLayerClearOfStationaryLayers(
                            stationaryLayerCoordinates, contactCoordinate,
                            contactCenterDistance))
                        continue;

                    var deviation = contactCoordinate - desiredMovingCoordinates[layerIndex];
                    var localCost = deviation * deviation;
                    if (layerIndex == 0)
                    {
                        contactCosts[layerIndex, sideIndex] = localCost;
                        continue;
                    }

                    for (var precedingSideIndex = 0; precedingSideIndex < 2; precedingSideIndex++)
                    {
                        if (contactCoordinate - contactCoordinates[layerIndex - 1, precedingSideIndex] <
                                movingLayerThickness - GeometryTolerance)
                            continue;
                        var candidateCost = contactCosts[layerIndex - 1, precedingSideIndex] + localCost;
                        if (candidateCost < contactCosts[layerIndex, sideIndex])
                        {
                            contactCosts[layerIndex, sideIndex] = candidateCost;
                            precedingSides[layerIndex, sideIndex] = precedingSideIndex;
                        }
                    }
                }
            }

            var finalSideIndex = contactCosts[memberCount - 1, 0] <=
                contactCosts[memberCount - 1, 1] ? 0 : 1;
            if (double.IsPositiveInfinity(contactCosts[memberCount - 1, finalSideIndex]))
                return false;

            selectedCoordinates = new double[memberCount];
            for (var layerIndex = memberCount - 1; layerIndex >= 0; layerIndex--)
            {
                selectedCoordinates[layerIndex] = contactCoordinates[layerIndex, finalSideIndex];
                finalSideIndex = precedingSides[layerIndex, finalSideIndex];
            }
            return true;
        }

        private static bool AreContactLayersSeparated(
            IReadOnlyList<double> stationaryLayerCoordinates,
            IReadOnlyList<double> movingCoordinates,
            double contactCenterDistance,
            double movingLayerThickness)
        {
            for (var layerIndex = 0; layerIndex < movingCoordinates.Count; layerIndex++)
            {
                if (!IsContactLayerClearOfStationaryLayers(stationaryLayerCoordinates,
                        movingCoordinates[layerIndex], contactCenterDistance) ||
                    (layerIndex > 0 && movingCoordinates[layerIndex] - movingCoordinates[layerIndex - 1] <
                        movingLayerThickness - GeometryTolerance))
                    return false;
            }
            return true;
        }

        private static bool IsContactLayerClearOfStationaryLayers(
            IReadOnlyList<double> stationaryLayerCoordinates,
            double movingCoordinate,
            double contactCenterDistance)
        {
            for (var stationaryLayerIndex = 0; stationaryLayerIndex < stationaryLayerCoordinates.Count;
                stationaryLayerIndex++)
                if (Math.Abs(movingCoordinate - stationaryLayerCoordinates[stationaryLayerIndex]) <
                    contactCenterDistance - GeometryTolerance)
                    return false;
            return true;
        }
    }
}
