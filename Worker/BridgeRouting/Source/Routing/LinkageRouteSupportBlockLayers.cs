using System;
using System.Collections.Generic;
using System.Linq;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static class LinkageRouteSupportBlockLayers
    {
        internal static void SplitOversized(LinkageRouteCenterlineConversionResult conversion)
        {
            var layeredAccessories = new List<LinkageRouteAccessory>();
            foreach (var accessory in conversion.Accessories)
            {
                var block = accessory?.Setting;
                if (accessory?.Kind != LinkageRouteAccessoryKind.SupportBlock ||
                    block?.PointList?.Count < 2 || block?.StartBusbarVector == null ||
                    block.StartBusbarVector.Norm <= 1e-6)
                { layeredAccessories.Add(accessory); continue; }
                var thicknessLimit = GetLapThicknessLimit(conversion, accessory);
                if (thicknessLimit <= 1e-6 || block.BusBarThickness <= thicknessLimit + 1e-6)
                { layeredAccessories.Add(accessory); continue; }
                var consumedThickness = 0d;
                var layerIndex = 0;
                while (block.BusBarThickness - consumedThickness > 1e-6)
                {
                    var layerThickness = Math.Min(thicknessLimit, block.BusBarThickness - consumedThickness);
                    var layer = block.Copy();
                    layer.BusBarThickness = layerThickness;
                    var layerDisplacement = -block.BusBarThickness / 2d + consumedThickness + layerThickness / 2d;
                    layer.PointList = block.PointList.Select(point => LinkageRouteSolidGeometry.Translate(
                        point, block.StartBusbarVector.Normalized, layerDisplacement)).ToList();
                    layeredAccessories.Add(new LinkageRouteAccessory
                    {
                        Kind = accessory.Kind,
                        TerminalSide = accessory.TerminalSide,
                        RelatedGeneratedMemberIndex = accessory.RelatedGeneratedMemberIndex,
                        Setting = layer,
                        SupportBlockKey = string.IsNullOrWhiteSpace(accessory.SupportBlockKey) || layerIndex == 0
                            ? accessory.SupportBlockKey : accessory.SupportBlockKey + "|layer:" + layerIndex
                    });
                    consumedThickness += layerThickness;
                    layerIndex++;
                }
            }
            conversion.Accessories.Clear();
            foreach (var accessory in layeredAccessories) conversion.Accessories.Add(accessory);
        }

        internal static double GetLapThicknessLimit(LinkageRouteCenterlineConversionResult conversion,
            LinkageRouteAccessory accessory)
        {
            var key = accessory.SupportBlockKey ?? string.Empty;
            var thicknesses = new List<double>();
            if (key.StartsWith("boundary:", StringComparison.Ordinal) &&
                int.TryParse(key.Substring("boundary:".Length).Split('|')[0], out var boundaryIndex))
            {
                thicknesses.AddRange(conversion.StackMembers.SelectMany(member => member.PlanarParts)
                    .Where(part => part.PhysicalSectionIndex == boundaryIndex || part.PhysicalSectionIndex == boundaryIndex + 1)
                    .Select(part => part.BusBarThickness));
            }
            else
            {
                foreach (var member in conversion.StackMembers.Where(member =>
                    accessory.RelatedGeneratedMemberIndex < 0 || member.MemberIndex == accessory.RelatedGeneratedMemberIndex))
                {
                    var part = accessory.TerminalSide == LinkageRouteTerminalSide.End
                        ? member.PlanarParts.LastOrDefault() : member.PlanarParts.FirstOrDefault();
                    if (part != null) thicknesses.Add(part.BusBarThickness);
                }
            }
            return thicknesses.Where(thickness => thickness > 0 && !double.IsInfinity(thickness)).DefaultIfEmpty(0d).Min();
        }
    }
}
