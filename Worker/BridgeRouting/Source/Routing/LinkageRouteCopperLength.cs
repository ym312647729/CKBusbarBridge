using System;
using System.Collections.Generic;
using System.Linq;
using RouteLinkBar.Algorithm.Contracts;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static class LinkageRouteCopperLength
    {
        internal static double GetGlobalMinimumThickness(LinkageMultiGroupRouteRequest request,
            LinkageRouteSolution solution)
        {
            var copperThicknesses = new List<double>();
            foreach (var group in request?.Groups ?? new List<LinkageRouteGroupRequest>())
                foreach (var terminal in new[] { group?.StartTerminal, group?.EndTerminal })
                    foreach (var member in (terminal?.CabinetBridgeAssembly?.OriginalTerminal ?? terminal)?.Members ?? new List<LinkageTerminalMember>())
                        if (member?.FixedBusbar != null) copperThicknesses.Add(member.FixedBusbar.BusBarThickness);
            foreach (var group in solution?.GroupSolutions ?? new List<LinkageGroupRouteSolution>())
            {
                copperThicknesses.AddRange(group.StackMembers.SelectMany(member => member.PlanarParts)
                    .Where(part => part != null).Select(part => part.BusBarThickness));
                copperThicknesses.AddRange(group.Accessories.Where(accessory =>
                    (accessory?.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge || accessory?.Kind == LinkageRouteAccessoryKind.CabinetTopConnectionBridge) && accessory.Setting != null)
                    .Select(accessory => accessory.Setting.BusBarThickness));
            }
            return copperThicknesses.Where(thickness => thickness > 0 && !double.IsInfinity(thickness))
                .DefaultIfEmpty(0d).Min();
        }

        internal static double GetBridgeLength(IEnumerable<LinkageRouteAccessory> accessories)
        {
            return accessories.Where(accessory =>
                (accessory?.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge ||
                 accessory?.Kind == LinkageRouteAccessoryKind.CabinetTopConnectionBridge) &&
                accessory.Setting != null).Sum(accessory => accessory.Setting.GetDevelopedLength());
        }

        internal static double GetCountedSupportLength(IEnumerable<LinkageRouteAccessory> accessories,
            double globalMinimumThickness)
        {
            return accessories.Where(accessory => accessory?.Kind == LinkageRouteAccessoryKind.SupportBlock &&
                    accessory.Setting != null && accessory.Setting.BusBarThickness > globalMinimumThickness + 1e-6d)
                .Sum(accessory => accessory.Setting.GetDevelopedLength());
        }

        internal static void Recalculate(LinkageMultiGroupRouteRequest request, LinkageRouteSolution solution)
        {
            if (solution?.GroupSolutions == null) return;
            var globalMinimumThickness = GetGlobalMinimumThickness(request, solution);
            foreach (var group in solution.GroupSolutions)
                group.ActualCopperLength = group.StackMembers.SelectMany(member => member.PlanarParts)
                    .Where(part => part != null).Sum(part => part.GetDevelopedLength()) +
                    group.Accessories.Where(accessory => (accessory?.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge || accessory?.Kind == LinkageRouteAccessoryKind.CabinetTopConnectionBridge) &&
                        accessory.Setting != null).Sum(accessory => accessory.Setting.GetDevelopedLength()) +
                    GetCountedSupportLength(group.Accessories, globalMinimumThickness);
            solution.ActualCopperLength = solution.GroupSolutions.Sum(group => group.ActualCopperLength);
        }
    }
}
