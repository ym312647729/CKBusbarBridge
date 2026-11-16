using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;

namespace CKBusbarBridgePreview
{
    public static partial class FaceToFaceModelBusbarPlanner
    {
        internal static IReadOnlyList<SideToSideCopperPiece> CreateIndependentFixedPhaseEnds(SideToSideCopperPlanningInput input)
        {
            if (!RequiresPhaseChangeByCabinetPlacement(input.B.PhaseSequence, input.A.PhaseSequence, true))
                throw new InvalidOperationException("基座1固定换相要求两柜相序互为倒序。");
            if (Math.Abs(input.A.Opening.Y - input.B.Opening.Y) > .5d ||
                Math.Abs(input.A.Opening.Z - input.B.Opening.Z) > .5d)
                throw new InvalidOperationException("基座1固定换相要求两侧孔口等高且无柜间偏心。");
            var bBars = input.B.Extensions.Select(e => e.ToBar()).ToArray();
            var aBars = input.A.Extensions.Select(e => e.ToBar()).ToArray();
            var axis = ResolveSideToSideAxisSetting(bBars, input.B.Opening);
            if (Math.Abs(axis.HeightVector.Y - 1d) > 1e-6)
                throw new InvalidOperationException("基座1实际伸出排的上升方向与孔口竖直方向不一致。");
            var bg = FaceToWallModelBusbarPlanner.GroupExtensionBusbarsByPhase(bBars, axis.WidthVector);
            var ag = FaceToWallModelBusbarPlanner.GroupExtensionBusbarsByPhase(aBars, axis.WidthVector);
            var mapping = ResolveSideToSidePhasePairing(bg, ag);
            var bp = GetPhaseTerminalReferencePoints(bg, "B柜");
            var ap = GetPhaseTerminalReferencePoints(ag, "A柜");
            var nearest = SideToSideFixedPhaseChangeRules.ResolveNearestBCabinetOuterPhaseIndex(
                bp, input.A.Opening, new Vector3d(1, 0, 0), .5d, out var ad, out var cd);
            var result = new List<SideToSideCopperPiece>();
            for (var side = 0; side < 2; side++)
            {
                var cabinet = side == 0 ? input.B : input.A;
                var groups = side == 0 ? bg : ag;
                var references = side == 0 ? bp : ap;
                var candidates = cabinet.OrdinaryConnections.Select(c => c.ToConnection())
                    .Where(c => BusbarGeometryUtility.AreParallel(c.ConnectionPlane.Normal, axis.LengthVector)).ToList();
                var bases = MatchInsulatorsToPhaseReferencePoints(candidates, references, side == 0 ? "B基座" : "A基座");
                for (var logical = 0; logical < 3; logical++)
                {
                    var physical = side == 0 ? logical : mapping[logical];
                    var lane = logical == nearest ? -1 : logical == 1 ? 0 : 1;
                    var height = cabinet.Opening.Y + lane * input.PhaseSpacing;
                    var shapes = FaceToWallModelBusbarPlanner.BuildPhaseStack(groups[physical], bases[physical], null,
                        side == 0 ? axis : CreateLengthReversedAxisSetting(axis), 0d, new[] { 0d, 1d },
                        0d, 1d, logical, input.RootCount, Math.Max(10d, input.Thickness), 20d,
                        false, 0, null, null, null, true, null, null, out var context, true, false, height);
                    foreach (var shape in shapes.Single())
                    {
                        // 旧五点模板的垂直接续使P4/P5重合；预览和正式加工都使用同一有效点列。
                        shape.RemoveInvalidPoints();
                        result.Add(new SideToSideCopperPiece(side == 0 ? "B" : "A", "ABC"[logical].ToString(), shape));
                    }
                }
            }
            return result.AsReadOnly();
        }
    }
}
