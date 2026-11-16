using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Prototype
{
    public sealed partial class PrototypeBusbarBridgeDesignProvider
    {
        private BridgeEditResult TryCreateSideToSidePreview(BridgeUpstreamSnapshot upstream, out BridgeDesignSample sample, bool validateCabinetHeights = true)
        {
            sample = null;
            if (upstream.SideToSide == null)
                return new BridgeEditResult(false, "侧对侧柜端接口反读未完成：" +
                    string.Join("；", upstream.Fields.Where(f => f.IsMissing).Select(f => f.Name + "：" + f.Source)));
            try
            {
                var plan = SideToSideIndependentCopperPlanner.CreateDiagnosticPreview(upstream.SideToSide.Copper);
                sample = CreateSideToSideSample(upstream, plan, null, validateCabinetHeights: validateCabinetHeights);
                return new BridgeEditResult(true);
            }
            catch (InvalidOperationException ex) { return new BridgeEditResult(false, ex.Message); }
            catch (ArgumentException ex) { return new BridgeEditResult(false, ex.Message); }
        }

        private BridgeDesignSample CreateSideToSideSample(BridgeUpstreamSnapshot upstream, SideToSideCopperPlan plan,
            IReadOnlyList<BridgeDesignObject> retainedSupports, IReadOnlyList<BridgeDesignObject> retainedControls = null,
            bool validateCabinetHeights = true, bool allowCopperFallback = true)
        {
            var side = upstream.SideToSide; var input = side.Copper;
            var objects = side.FixedObjects.ToList();
            var routes = new List<BridgeDesignRoute>();
            var issues = new List<BridgeDesignIssue>();
            for (var pieceIndex = 0; pieceIndex < plan.Pieces.Count; pieceIndex++)
            {
                var piece = plan.Pieces[pieceIndex]; var shape = piece.Shape;
                var ids = new List<string>();
                for (var i = 0; i < shape.PointList.Count; i++)
                {
                    var p = shape.PointList[i]; var id = "side-piece-" + pieceIndex + "-" + i;
                    objects.Add(Create(id, piece.Phase + "相" + (piece.Side == "Middle" ? "中间排" : piece.Side + "柜连接排"),
                        DesignObjectKind.FixedRoutePoint, p.X, p.Z, p.Y, "实际加工点列；柜端几何先规划", phase: piece.Phase));
                    ids.Add(id);
                }
                var route = new BridgeDesignRoute("side-route-" + pieceIndex, piece.Phase, ids)
                    .WithPhysicalGeometry(shape.BusBarWidth, shape.BusBarThickness,
                        FaceToFaceModelBusbarPlanner.CreateFixedInterfacePreviewSolids(shape)
                            .Select(s => s.Select(p => new[] { p[0], p[2], p[1] }).ToArray()))
                    .WithNativePreview(shape, side.Coordinates)
                    .WithGenerationGroup(piece.Side == "Middle" ? "中间排" : piece.Side + "柜连接段", piece.IsBridge);
                if (piece.Side == "Middle") route = route.WithCopperSection(shape.PlanningSegmentIndex + 1);
                routes.Add(route);
            }
            var problem = plan.ClearanceProblem;
            if (problem != null)
            {
                var first = plan.Pieces[problem.FirstPieceIndex];
                var second = plan.Pieces[problem.SecondPieceIndex];
                Func<SideToSideCopperPiece, string, string> label = (piece, key) =>
                    (piece.Side == "Middle" ? "桥体中间排" : piece.Side + "柜连接排") + " · " +
                    System.Text.RegularExpressions.Regex.Replace(key, @"^([ABC])-(\d+)-段(\d+)$", "$1相第$2根 · 第$3直段");
                var firstLabel = label(first, problem.FirstLabel);
                var secondLabel = label(second, problem.SecondLabel);
                issues.Add(new BridgeDesignIssue(firstLabel + " 与 " + secondLabel + " 净距不足。"
                    + string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "实测{0:0.0} mm，要求≥{1:0.0} mm。", problem.ActualDistance, problem.RequiredDistance)
                    + "请检查换相连接排走向、伸出排相间距及柜顶孔L3；调整后重读并规划。",
                    routes[problem.FirstPieceIndex].Id, "跨相净距", true)
                    .WithLocation(new BridgeIssueLocation(routes[problem.SecondPieceIndex].Id,
                        problem.ActualDistance, problem.RequiredDistance,
                        problem.FirstCorners.Select(p => new[] { p[0], p[2], p[1] }),
                        problem.SecondCorners.Select(p => new[] { p[0], p[2], p[1] }), firstLabel, secondLabel)));
            }
            // 支撑失败只增加问题，不丢弃已经完成的铜排。
            if (retainedSupports != null) objects.AddRange(retainedSupports);
            else
            {
                var supports = new List<BridgeDesignObject>();
                try
                {
                    if (validateCabinetHeights && !input.FixedPhaseChange &&
                        (Math.Abs(input.A.OpeningHeight - input.B.OpeningHeight) > .5d ||
                        Math.Abs(input.A.Opening.Y - input.B.Opening.Y) > .5d))
                        throw new InvalidOperationException("两侧孔口不等高，无法布置当前等高基架。");
                    AddPlannedSupports(supports, plan.Path, Models.RdTypeMetadata.IsLargeOffset(input.A.Opening.Z - input.B.Opening.Z, input.A.BaseWidth, input.B.BaseWidth),
                        input.B.Opening.Y, input.B.OpeningHeight, input.A.BaseWidth, input.B.BaseWidth,
                        input.B.Opening.X, input.A.Opening.X, input.PhaseSpacing, input.Width, input.Clearance,
                        reverseRearFrames: !input.FixedPhaseChange, reverseWidenedTail: false);
                    supports.RemoveAll(o => o.Kind == DesignObjectKind.Insulator);
                    foreach (var original in supports.Where(o => o.Kind == DesignObjectKind.Frame).ToArray())
                    {
                        var frame = input.FixedPhaseChange ? original.WithFrameTemplate("SideToSidePhaseChange029") : original;
                        frame = frame.WithInsulatorStack(input.RootCount - 1, input.Thickness, input.PhaseSpacing,
                            Models.InsulatorHeightRules.ToInsulatorWithBracketHeight(side.FrameInsulatorBodyHeight));
                        supports[supports.IndexOf(original)] = frame;
                        if (frame.Value(DesignField.InsulatorCount) > 0)
                            for (var phase = 0; phase < 3; phase++)
                                supports.Add(CreateInsulator(frame, phase + 1, (phase - 1) * input.PhaseSpacing));
                    }
                    objects.AddRange(AssignInitialFrameSources(supports));
                }
                catch (InvalidOperationException ex) { issues.Add(new BridgeDesignIssue(ex.Message, null, "支撑规划")); }
                catch (ArgumentException ex) { issues.Add(new BridgeDesignIssue(ex.Message, null, "支撑规划")); }
            }
            if (!objects.Any(o => o.Kind == DesignObjectKind.Frame))
                issues.Add(new BridgeDesignIssue("完整方案至少需要一节基架；铜排已保留。", null, "支撑规划"));
            var edits = new List<BridgeDesignRoute>();
            var joints = new List<BridgeDesignObject>();
            foreach (var phase in new[] { "A", "B", "C" })
            {
                var ids = new List<string>();
                var middle = plan.Pieces.Where(p => p.Side == "Middle" && p.Phase == phase).Select(p => p.Shape).ToArray();
                var retainedPhase = retainedControls?.Where(o => o.Phase == phase).OrderBy(o => o.X).ToArray();
                for (var i = 0; i < plan.Path.Points.Count; i++)
                {
                    var p = plan.Path.Points[i];
                    var retained = retainedPhase?[i];
                    var id = retained?.Id ?? "side-edit-" + phase + "-" + i;
                    var fixedEnd = i == 0 || i == plan.Path.Points.Count - 1;
                    var x = fixedEnd ? BridgeSideToSideCopperEditor.FixedJointX(
                        middle.Where(piece => piece.PlanningSegmentIndex == (i == 0 ? 0 : i - 1)),
                        i == 0, input.Width) : p.X + input.Width / 2d;
                    joints.Add(new BridgeDesignObject(id, fixedEnd ? "柜端固定接口" : "断点" + i,
                        fixedEnd ? DesignObjectKind.FixedJoint : DesignObjectKind.Breakpoint,
                        new Dictionary<DesignField, double> { [DesignField.X] = x,
                            [DesignField.Y] = p.Y, [DesignField.Z] = input.B.Opening.Y },
                        fixedEnd ? Array.Empty<DesignField>() : new[] { DesignField.X },
                        "以后截面为基准，ABC三相联动", phase: phase).WithJointGroup(retained?.JointGroupId ?? "side-joint-" + i));
                    ids.Add(id);
                }
                edits.Add(new BridgeDesignRoute("side-edit-" + phase, phase, ids));
            }
            objects.AddRange(joints);
            var source = new BridgeDesignSource(upstream.Scenario,
                "实际柜端铜排先规划；基架和绝缘子后置。允许一架，完整方案禁止零架。\n支撑强度：未校核。",
                input.Width, input.Thickness, "当前总装只读输入 · 侧对侧规划", true);
            var sample = new BridgeDesignSample("model-plan-" + upstream.ModelName, source, objects, routes, issues,
                new BridgeDesignParameters(input.Width, input.Clearance, preserveOverlapPrecision: true))
                .WithCoordinates(side.Coordinates, input.FixedPhaseChange ? "SideToSideBase1" : "SideToSideBase2");
            // 失败方案保留参与校验的原始铜排；不再执行可能改变位置的支撑避让或铜排兜底。
            if (problem != null) return sample;
            if (retainedSupports == null)
            {
                var copperMinimum = BridgeOffsetCopperChange.CaptureMinimum(sample);
                var connections = new BusbarBridgeFrameJointClearance().Plan(sample);
                var placement = new BusbarBridgeInsulatorClearance().PlaceIndependently(sample, connections.Objects, null);
                sample = new BridgeDesignSample(sample.Id, source, AssignInitialFrameSources(placement.Objects), routes,
                    issues.Concat(connections.Issues).Concat(placement.Issues), sample.Parameters)
                    .WithCoordinates(side.Coordinates, sample.ScenarioKey);
                sample = sample.WithFrameLengthEditing(new BridgeFrameLengthEditing(sample.FrameLengthBaseline,
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), copperMinimum));
            }
            sample = sample.WithSpecialCopperSections(plan.Path.Segments.Where(s => s.Role != MiddleBusbarSegmentRole.Ordinary).Select(s => s.Index + 1));
            var interactive = BridgeSideToSideCopperEditor.Create(sample.WithCopperEditing(edits, null), input);
            Func<IReadOnlyList<BridgeDesignObject>, BridgeDesignSample> replan = null;
            replan = current =>
            {
                var controls = EditedCopperControls(current, joints);
                var groupProblem = BridgeSideToSideCopperEditor.JointGroupProblem(controls);
                if (groupProblem != null) throw new InvalidOperationException(groupProblem);
                if (interactive != null && interactive.TryReplan(current, out var edited))
                    return edited.WithCopperEditing(edits, replan);
                var adjusted = ReplanMiddleCopperPath(plan.Path, joints, controls, input.Width, false,
                    input.PhaseSpacing, input.Thickness, input.Clearance, deferOrdinaryLengthLimitToPhysicalGeometry: true);
                var copper = SideToSideIndependentCopperPlanner.Create(input, adjusted);
                var changed = adjusted.OffsetFrameStartX.HasValue &&
                    (Math.Abs(adjusted.OffsetFrameStartX.Value - plan.Path.OffsetFrameStartX.Value) > .001d ||
                     Math.Abs(adjusted.OffsetFrameEndX.Value - plan.Path.OffsetFrameEndX.Value) > .001d);
                return CreateSideToSideSample(upstream, copper, changed ? null : current
                    .Where(o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator).ToArray(), controls, validateCabinetHeights, false);
            };
            sample = sample.WithCopperEditing(edits, replan);
            if (retainedSupports == null && allowCopperFallback) sample = FinishFrameJointPlanning(sample);
            // 与面对面共用偏移支撑规则；最终铜排和接缝确定后，重算三相搭接排的落地支撑。
            return sample.WithGeometry(new BridgeSmallOffsetSupportPlanner().Apply(sample, sample.Objects), sample.Routes);
        }
    }
}
