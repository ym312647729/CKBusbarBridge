using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CKBusbarBridgePreview;
using CKBusbarBridge.Models;

namespace CKBusbarBridge.Prototype
{
    /// <summary>提供成套演示方案，并将只读模型输入适配为路径优先的界面预览方案。</summary>
    public sealed partial class PrototypeBusbarBridgeDesignProvider
    {
        public const int InsulatorsPerAssembly = 3;
        public IReadOnlyList<string> SampleNames { get; } = Array.AsReadOnly(new[]
        {
            "面对面基础", "面对面小偏移", "面对面大偏移", "面对墙", "侧对侧", "演示问题", "空方案", "加载失败演示"
        });

        public BridgeEditResult TryLoad(string name, out BridgeDesignSample sample)
        {
            sample = null;
            if (!SampleNames.Contains(name)) return new BridgeEditResult(false, "未找到所选示例。");
            if (name == "加载失败演示") return new BridgeEditResult(false, "演示加载失败：未取得示例方案。请重新选择示例；未读取或改写模型。");
            var offset = name == "面对面小偏移" ? 350d : name == "面对面大偏移" ? 1600d : 0d;
            var span = name == "面对面大偏移" ? 6000d : 4400d;
            var side = name == "侧对侧";
            var wall = name == "面对墙";
            var endOffset = offset + (side ? 500 : 0);
            var source = new BridgeDesignSource(name,
                "仅演示，全部上游信息只读\nA柜（宽×深×高）：1000.0 × 1400.0 × 2200.0 mm\n" +
                (wall ? "B端：墙洞固定接口\n" : "B柜（宽×深×高）：1000.0 × 1400.0 × 2200.0 mm\n") +
                "偏移Y：" + offset.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " mm\n示例方向：" + (side ? "侧对侧" : wall ? "面对墙" : "面对面") +
                "\n铜排：80.0 × 10.0 mm，三相A/B/C\n相别、相序及规格由柜体驱动确定\n始端排和始端箱绝缘子只读；基座L排断点可调，墙洞固定接口只读\n" +
                (wall ? "路径方向：A柜 → 墙洞。\n" : "路径方向：左侧B柜出排 → 右侧A柜进排。\n") +
                "A/B基座为总装独立装配体；中间基架不归属柜体。\n正式阶段需从模型读取；修改请回柜体驱动模块。", 80, BusbarBridgeDraftPreviewSceneFactory.DemoCopperThicknessMm);
            var objects = new List<BridgeDesignObject>();
            var routes = new List<BridgeDesignRoute>();
            var issues = new List<BridgeDesignIssue>();
            if (name != "空方案")
            {
                // 按参考图组织演示空间：柜体落地、基座在柜顶、桥体跨在两端之间；不是截图量测尺寸。
                // 两柜场景统一按左B出排、右A进排。相别A/B/C与柜号无关；单柜面对墙仍保留A柜端。
                objects.Add(Create(wall ? "cabinet-a" : "cabinet-b", wall ? "A柜" : "B柜 · 出排", DesignObjectKind.Cabinet, 0, 0, 1100, "柜体（只读）", 1400, 1000, height: 2200));
                objects.Add(Create(wall ? "wall-interface" : "cabinet-a", wall ? "墙洞接口参照" : "A柜 · 进排", DesignObjectKind.Cabinet,
                    span, endOffset, wall ? 2750 : 1100, wall ? "墙洞参照（只读）" : "柜体（只读）", wall ? 220 : 1400, 1000, height: wall ? 800 : 2200));
                // 基座深度沿X，横向宽度沿Y；示意深度独立于柜深和基架单节长度，不从截图量测。
                const double baseDepth = 500;
                objects.Add(Create(wall ? "base-a" : "base-b", wall ? "A侧独立基座" : "B侧独立基座 · 出排", DesignObjectKind.Base, -baseDepth / 2, 0, 2600, "总装独立基座（只读；深度为演示值）", baseDepth, 1000, height: 800));
                if (!wall) objects.Add(Create("base-a", "A侧独立基座 · 进排", DesignObjectKind.Base, span + baseDepth / 2, endOffset, 2600, "总装独立基座（只读；深度为演示值）", baseDepth, 1000, height: 800));
                AddRoutes(objects, routes, span, endOffset, wall);
                var count = name == "面对面大偏移" ? 7 : 5;
                var sectionWidth = span / count;
                for (var i = 0; i < count; i++)
                {
                    var x = sectionWidth * (i + .5);
                    var frame = Create("frame-" + (i + 1), "基架" + (i + 1), DesignObjectKind.Frame,
                        x, endOffset * x / span, 2750,
                        name == "面对面小偏移" ? "小偏移基架（模板示意）" : "分节槽形基架（示意）",
                        sectionWidth * Math.Sqrt(1 + endOffset * endOffset / (span * span)), 1000);
                    var values = frame.Values.ToDictionary(p => p.Key, p => p.Value);
                    values[DesignField.Direction] = Math.Atan2(endOffset, span) * 180 / Math.PI;
                    frame = frame.WithValues(values);
                    if (name == "面对面小偏移")
                        frame = frame.WithSmallOffsetPreview(new BridgeSmallOffsetPreview(0, offset / 2, 1, offset / 2));
                    objects.Add(frame);
                    for (var j = 0; j < 3; j++) objects.Add(CreateInsulator(frame.Id, j + 1, -200 + j * 200, frame.Height, frame.Z));
                }
                if (name == "演示问题")
                {
                    issues.Add(new BridgeDesignIssue("示意：断点处净距需复核（未实际计算）。", "B-lap", sideView: true));
                    issues.Add(new BridgeDesignIssue("示意：基架支撑及孔位待核对。", "frame-1"));
                    issues.Add(new BridgeDesignIssue("示意：目标已移除，定位应提示不存在。", "removed-frame"));
                }
            }
            sample = new BridgeDesignSample(name, source, AssignInitialFrameSources(objects), routes, issues);
            var placement = new BusbarBridgeInsulatorClearance().Place(sample, sample.Objects, null);
            sample = new BridgeDesignSample(name, source, AssignInitialFrameSources(placement.Objects), routes,
                issues.Concat(placement.Issues), sample.Parameters);
            return new BridgeEditResult(true);
        }

        /// <summary>
        /// 将柜体驱动只读快照转换为“先铜排、后支撑”的界面方案。这里只生成预览草稿，
        /// 不写回模型；路径分段不读取既有基架坐标。
        /// </summary>
        public BridgeEditResult TryCreatePlannedPreview(BridgeUpstreamSnapshot upstream,
            out BridgeDesignSample sample)
            => TryCreatePlannedPreview(upstream, out sample, true);

        internal BridgeDesignSample CreateStoredContext(BridgeUpstreamSnapshot upstream, BridgeCoordinateFrame coordinates,
            string scenario, double width, double thickness)
        {
            var objects = new List<BridgeDesignObject>();
            if (upstream.SideToSide != null) objects.AddRange(upstream.SideToSide.FixedObjects);
            else
            {
                foreach (var item in new[] { new[] { "A柜", "cabinet-a", "A柜" }, new[] { "B柜", "cabinet-b", "B柜" },
                    new[] { "A柜基座", "base-a", "A侧独立基座" }, new[] { "B柜基座", "base-b", "B侧独立基座" } })
                {
                    if (!TryReadNominalGeometry(upstream, item[0], out var shape))
                        throw new InvalidOperationException(item[0] + "缺少原驱动尺寸和模板定位关系。");
                    var p = coordinates.ToDraft(new[] { shape.CenterX, shape.CenterY, shape.CenterZ });
                    objects.Add(Create(item[1], item[2], item[1].StartsWith("base-") ? DesignObjectKind.Base : DesignObjectKind.Cabinet,
                        p[0], p[1], p[2], "模型只读", shape.Length, shape.Width, height: shape.Height));
                }
                AddFaceToFaceElevatedBases(objects, upstream, coordinates);
                AddUpstreamPreviewParts(objects, upstream.PreviewParts, coordinates.Origin[0], coordinates.Origin[2]);
            }
            var insulationDistance = upstream.SideToSide?.Copper.Clearance ?? upstream.FaceToWall?.Clearance ??
                (TryReadMillimetres(upstream, InsulationDistanceMetadata.PropertyName, out var readDistance) && readDistance > 0d
                    ? readDistance : InsulationDistanceMetadata.DefaultMillimetres);
            return new BridgeDesignSample("saved-model", new BridgeDesignSource(upstream.Scenario,
                upstream.ModelName, width, thickness, "已保存业务方案", true), objects, Array.Empty<BridgeDesignRoute>(),
                Array.Empty<BridgeDesignIssue>(), new BridgeDesignParameters(width, insulationDistance)).WithCoordinates(coordinates, scenario);
        }

        internal BridgeEditResult TryCreateSynchronizedPreview(BridgeUpstreamSnapshot upstream,
            out BridgeDesignSample sample)
            => TryCreatePlannedPreview(upstream, out sample, false);

        private BridgeEditResult TryCreatePlannedPreview(BridgeUpstreamSnapshot upstream,
            out BridgeDesignSample sample, bool validateCabinetHeights, bool preferEqualWidenedEnds = false)
        {
            sample = null;
            if (upstream?.HasModel != true)
                return new BridgeEditResult(false, "当前没有可用于路径规划的模型上游信息。");
            if (upstream.Scenario?.Contains("侧对侧") == true)
                return TryCreateSideToSidePreview(upstream, out sample, validateCabinetHeights);
            if (string.IsNullOrWhiteSpace(upstream.Scenario) ||
                !upstream.Scenario.Contains("面对面"))
                return new BridgeEditResult(false, "当前阶段只接入面对面场景的模型方案预览。");

            if (!TryReadNominalGeometry(upstream, "A柜", out var aCabinet) ||
                !TryReadNominalGeometry(upstream, "B柜", out var bCabinet) ||
                !TryReadNominalGeometry(upstream, "A柜基座", out var aBase) ||
                !TryReadNominalGeometry(upstream, "B柜基座", out var bBase))
                return new BridgeEditResult(false, "A/B柜及两侧独立基座的名义外形坐标和原驱动尺寸输入缺失或无效，不能生成模型预览。");
            // 总装坐标为 X=桥向、Y=竖直、Z=横向；预览坐标为 X=桥向、Y=横向、Z=竖直。
            var span = aCabinet.CenterX - bCabinet.CenterX;
            var offset = aCabinet.CenterZ - bCabinet.CenterZ;
            double pathStartX;
            double pathEndX;
            double startY;
            double endY;
            double bridgeElevation;
            double frameHeight;
            double frameElevation;
            try
            {
                ValidateModelEndpoints(upstream);
                var bEnds = upstream.Endpoints.Where(e => e.Side == "B").ToArray();
                var aEnds = upstream.Endpoints.Where(e => e.Side == "A").ToArray();
                pathStartX = bEnds.Average(e => e.Interface[0]) - bCabinet.CenterX;
                pathEndX = aEnds.Average(e => e.Interface[0]) - bCabinet.CenterX;
                startY = bEnds.Average(e => e.Interface[2]) - bCabinet.CenterZ;
                endY = aEnds.Average(e => e.Interface[2]) - bCabinet.CenterZ;
                bridgeElevation = bEnds.Average(e => e.Interface[1]);
                if (validateCabinetHeights && Math.Abs(bridgeElevation - aEnds.Average(e => e.Interface[1])) > .5d)
                    throw new InvalidOperationException("两端接口高度不同，需要竖向过渡规划；不能以平均高度绘制水平母排。");
                frameHeight = ReadRequiredMillimetres(upstream, "B柜基座 · ", "基座开孔高度");
                frameElevation = ReadRequiredMillimetres(upstream, "B柜基座孔口中心", "Y");
                if (validateCabinetHeights && (Math.Abs(frameHeight - ReadRequiredMillimetres(upstream, "A柜基座 · ", "基座开孔高度")) > .5d ||
                    Math.Abs(frameElevation - ReadRequiredMillimetres(upstream, "A柜基座孔口中心", "Y")) > .5d))
                    throw new InvalidOperationException("两端孔口高度或中心高度不同，不能生成等高桥体预览。");
            }
            catch (InvalidOperationException ex)
            {
                return new BridgeEditResult(false, ex.Message);
            }
            if (span <= 0d)
                return new BridgeEditResult(false, "A柜必须位于B柜的+X方向，不能建立面对面预览坐标系。");
            if (pathEndX <= pathStartX)
                return new BridgeEditResult(false, "A/B基座桥向边界无有效可用跨度，不能生成模型预览。");
            if (!TryReadSpecification(upstream, out var width, out var thickness))
                return new BridgeEditResult(false, "A/B端铜排规格缺失或不一致，不能规划中间铜排。");
            if (!TryReadMatchingPhaseSpacing(upstream, out var phaseSpacing))
                return new BridgeEditResult(false, "A/B端相间距缺失或不一致，不能规划中间铜排。");
            var insulationDistance = TryReadMillimetres(upstream,
                InsulationDistanceMetadata.PropertyName, out var readDistance) && readDistance > 0d
                    ? readDistance : InsulationDistanceMetadata.DefaultMillimetres;

            var aSequence = ReadFieldContaining(upstream, "A柜始端", "相序");
            var bSequence = ReadFieldContaining(upstream, "B柜始端", "相序");
            if (string.IsNullOrWhiteSpace(aSequence) || string.IsNullOrWhiteSpace(bSequence))
                return new BridgeEditResult(false, "A/B柜伸出排相序缺失，不能判断是否需要换相。");

            bool requiresPhaseChange;
            try
            {
                requiresPhaseChange = FaceToFaceModelBusbarPlanner
                    .RequiresPhaseChangeByCabinetPlacement(bSequence, aSequence, false);
            }
            catch (ArgumentException ex)
            {
                return new BridgeEditResult(false, ex.Message);
            }

            // 基架铺排以基座朝桥侧边缘为界，不使用铜排搭接中心。
            var supportStartX = bBase.CenterX + bBase.Length / 2d - bCabinet.CenterX;
            var supportEndX = aBase.CenterX - aBase.Length / 2d - bCabinet.CenterX;
            MiddleBusbarPathPlan pathPlan;
            IReadOnlyList<BridgeUpstreamEndpoint> plannedEndpoints;
            FaceToFaceUnifiedPlanningResult unifiedPlanningResult;
            bool isLargeOffset;
            try
            {
                // 后截面距B基座边为搭接长度+200mm、距A基座边为200mm，
                // 使两端实际搭接区到基座/基架接缝至少200mm；规划器输入仍需减去半排宽。
                var bDefaultX = supportStartX + width + BusbarBridgeFrameJointClearance.MinimumLapDistanceMm - width / 2d;
                var aDefaultX = supportEndX - BusbarBridgeFrameJointClearance.MinimumLapDistanceMm - width / 2d;
                plannedEndpoints = MoveBaseLTails(upstream.Endpoints,
                    bDefaultX - pathStartX, aDefaultX - pathEndX, width, thickness);
                pathStartX = bDefaultX;
                pathEndX = aDefaultX;
                var unifiedPlan = FaceToFaceUnifiedPlanner.Create(
                    new FaceToFaceUnifiedPlanningInput
                    {
                        ACabinetBaseWidth = aBase.Width,
                        BCabinetBaseWidth = bBase.Width,
                        StartX = pathStartX,
                        StartY = startY,
                        EndX = pathEndX,
                        EndY = endY,
                        PhaseSpacing = phaseSpacing,
                        BusbarWidth = width,
                        BusbarThickness = thickness,
                        RequiresPhaseChange = requiresPhaseChange,
                        DeferOffsetLengthLimitToPhysicalGeometry = true,
                        PhaseChangeInsulationClearance = insulationDistance
                    });
                PlanGeometryValidator.ThrowIfErrors(unifiedPlan.Diagnostics);
                pathPlan = unifiedPlan.MiddlePath;
                unifiedPlanningResult = unifiedPlan;
                isLargeOffset = unifiedPlan.IsLargeOffset;
            }
            catch (InvalidOperationException ex)
            {
                return new BridgeEditResult(false, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return new BridgeEditResult(false, ex.Message);
            }

            var objects = new List<BridgeDesignObject>();
            var routes = new List<BridgeDesignRoute>();
            // 总装为 X=桥向、Y=竖直、Z=横向；预览实体为 X=桥向、Y=横向、Z=竖直。
            // 名义外形使用原驱动尺寸；读取端已验证模板轴向，这里仅转换坐标轴。
            objects.Add(Create("cabinet-b", "B柜 · 出排", DesignObjectKind.Cabinet, 0, 0, bCabinet.CenterY,
                "柜体（模型只读）", bCabinet.Length, bCabinet.Width, height: bCabinet.Height));
            objects.Add(Create("cabinet-a", "A柜 · 进排", DesignObjectKind.Cabinet, span, offset, aCabinet.CenterY,
                "柜体（模型只读）", aCabinet.Length, aCabinet.Width, height: aCabinet.Height));
            objects.Add(Create("base-b", "B侧独立基座 · 出排", DesignObjectKind.Base,
                bBase.CenterX - bCabinet.CenterX, bBase.CenterZ - bCabinet.CenterZ, bBase.CenterY,
                "总装顶层独立基座（模型只读）", bBase.Length, bBase.Width, height: bBase.Height));
            objects.Add(Create("base-a", "A侧独立基座 · 进排", DesignObjectKind.Base,
                aBase.CenterX - bCabinet.CenterX, aBase.CenterZ - bCabinet.CenterZ, aBase.CenterY,
                "总装顶层独立基座（模型只读）", aBase.Length, aBase.Width, height: aBase.Height));
            AddUpstreamPreviewParts(objects, upstream.PreviewParts, bCabinet.CenterX, bCabinet.CenterZ);
            try
            {
                AddFaceToFaceElevatedBases(objects, upstream, BridgeCoordinateFrame.Facing(bCabinet.CenterX, bCabinet.CenterZ));
                var bBaseWidth = ReadRequiredMillimetres(upstream, "B柜基座 · ", "基座宽");
                var aBaseWidth = ReadRequiredMillimetres(upstream, "A柜基座 · ", "基座宽");
                pathPlan = AddModelPlannedRoutes(objects, routes, pathPlan, plannedEndpoints,
                    bCabinet.CenterX, bCabinet.CenterZ, width, thickness, requiresPhaseChange, insulationDistance,
                    frameElevation - frameHeight / 2d + BusbarBridgeDraftPreviewSceneFactory.FrameFloorThicknessMm,
                    frameElevation + frameHeight / 2d,
                    Math.Min(bBaseWidth, aBaseWidth) / 2d - (isLargeOffset ? 30d : 40d), expandOffset: true,
                    initialFrameMinimumLength: unifiedPlanningResult?.RawSpecialFrameMinimumLength);
                AddPlannedSupports(objects, pathPlan, isLargeOffset, frameElevation, frameHeight,
                    aBaseWidth, bBaseWidth, supportStartX, supportEndX, phaseSpacing, width, insulationDistance,
                    preferEqualWidenedEnds: preferEqualWidenedEnds);
                AlignPlannedSupportGeometry(objects, phaseSpacing,
                    upstream.Endpoints.Min(e => e.Interface[1]), thickness, upstream.Endpoints);
            }
            catch (InvalidOperationException ex)
            {
                return new BridgeEditResult(false, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return new BridgeEditResult(false, ex.Message);
            }

            var minimumSafeLength = objects.FirstOrDefault(o => o.CornerPreview != null)?.CornerPreview.MinimumSafeLengthMm;
            var summary = "模型只读输入：" + upstream.ModelName + "\n" +
                "路径规则：断点最佳间距2000.0 mm；单根展开长度不超过3000.0 mm（搭接取排宽" +
                width.ToString("0.0", CultureInfo.InvariantCulture) + " mm）。\n" +
                (minimumSafeLength.HasValue ? FormattableString.Invariant(
                    $"大偏移基架：10.0 mm豁口推荐长度{pathPlan.OffsetFrameEndX.Value - pathPlan.OffsetFrameStartX.Value:0.0} mm，25.0 mm豁口最小安全长度{minimumSafeLength.Value:0.0} mm。\n")
                    : unifiedPlanningResult.HasOffset
                    ? "特殊基架：未取整最小值" +
                        unifiedPlanningResult.RawSpecialFrameMinimumLength.Value
                            .ToString("0.0", CultureInfo.InvariantCulture) +
                        " mm，当前规划装配值" +
                        (pathPlan.OffsetFrameEndX.Value - pathPlan.OffsetFrameStartX.Value)
                            .ToString("0.0", CultureInfo.InvariantCulture) +
                        " mm。\n"
                    : string.Empty) +
                "换相：" + (requiresPhaseChange ? "第一段" : "不需要") +
                "；偏移折弯：" + (Math.Abs(offset) > .001 ? "母排中间段" : "无偏移") + "。\n" +
                "中间铜排先规划，基架及架内绝缘子按铜排路径与工艺保护区后置生成。\n" +
                "支撑强度：未校核（当前无载荷、短路工况及验证跨距输入）。";
            var source = new BridgeDesignSource(upstream.Scenario, summary, width, thickness,
                "当前总装只读输入 · 规划结果", true);
            sample = new BridgeDesignSample("model-plan-" + upstream.ModelName, source,
                AssignInitialFrameSources(objects), routes, upstream.Fields
                    .Where(f => f.IsMissing && f.Name.Contains("外形："))
                    .Select(f => new BridgeDesignIssue(f.Name + "未显示：" + f.Source, null, "上游预览")),
                new BridgeDesignParameters(width, insulationDistance));
            // 每节基架独立选合法位置，不能因同源名称让一个无解位置连带删除其他基架支架。
            var copperMinimum = BridgeOffsetCopperChange.CaptureMinimum(sample);
            var connections = new BusbarBridgeFrameJointClearance().Plan(sample);
            var placement = requiresPhaseChange
                ? PlaceInitialInsulators(sample, connections.Objects, pathPlan, plannedEndpoints,
                    bCabinet.CenterX, bCabinet.CenterZ, width, preferSharedWidenedEnds: validateCabinetHeights)
                : new BusbarBridgeInsulatorClearance().PlaceIndependently(sample, connections.Objects, null, null, null,
                    preferSharedWidenedEnds: validateCabinetHeights);
            sample = new BridgeDesignSample(sample.Id, source, AssignInitialFrameSources(placement.Objects), routes,
                sample.Issues.Concat(connections.Issues).Concat(placement.Issues), sample.Parameters);
            sample = sample.WithFrameLengthEditing(new BridgeFrameLengthEditing(sample.FrameLengthBaseline,
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), copperMinimum));
            sample = AttachCopperEditing(sample, pathPlan, plannedEndpoints, bCabinet.CenterX, bCabinet.CenterZ,
                width, thickness, requiresPhaseChange, phaseSpacing, insulationDistance,
                frameElevation - frameHeight / 2d + BusbarBridgeDraftPreviewSceneFactory.FrameFloorThicknessMm,
                frameElevation + frameHeight / 2d,
                Math.Min(ReadRequiredMillimetres(upstream, "B柜基座 · ", "基座宽"),
                    ReadRequiredMillimetres(upstream, "A柜基座 · ", "基座宽")) / 2d - (isLargeOffset ? 30d : 40d),
                supportStartX, supportEndX);
            sample = FinishFrameJointPlanning(sample.WithCoordinates(
                BridgeCoordinateFrame.Facing(bCabinet.CenterX, bCabinet.CenterZ), "FaceToFace"),
                preferSharedWidenedEnds: validateCabinetHeights);
            sample = sample.WithGeometry(new BridgeSmallOffsetSupportPlanner().Apply(sample, sample.Objects), sample.Routes);
            if (validateCabinetHeights && !preferEqualWidenedEnds &&
                Math.Abs(aBase.Width - bBase.Width) <= BridgeDesignPrecision.LengthComparisonToleranceMm)
            {
                var frames = BusbarBridgeDraftEditor.FramesInInstallationOrder(sample.Objects.Where(o => o.Kind == DesignObjectKind.Frame));
                if (frames.Length > 1 && BridgeOffsetCopperChange.IsWidenedTerminal(frames[0]) &&
                    BridgeOffsetCopperChange.IsWidenedTerminal(frames[frames.Length - 1]) &&
                    frames[0].Length != frames[frames.Length - 1].Length &&
                    TryCreatePlannedPreview(upstream, out var equalEnds, validateCabinetHeights, preferEqualWidenedEnds: true).Success)
                {
                    var candidate = new BusbarBridgeDraftEditor();
                    candidate.Load(equalEnds);
                    var ends = BusbarBridgeDraftEditor.FramesInInstallationOrder(candidate.EffectivePlan.Where(o => o.Kind == DesignObjectKind.Frame));
                    if (!candidate.HasBlockingIssues && ends.Length == frames.Length &&
                        ends[0].SharesFrameSourceWith(ends[ends.Length - 1]) &&
                        ends.Zip(frames, (next, original) => next.Value(DesignField.InsulatorCount) >= original.Value(DesignField.InsulatorCount)).All(retained => retained))
                        sample = equalEnds;
                }
            }
            return new BridgeEditResult(true);
        }

        private BridgeDesignSample FinishFrameJointPlanning(BridgeDesignSample sample, bool preferSharedWidenedEnds = false)
        {
            var clearance = new BusbarBridgeFrameJointClearance();
            var planned = clearance.Validate(sample, sample.Objects).Count == 0 ? sample : clearance.PlanWithCopper(sample);
            planned = clearance.PreferWholeCopperLengths(planned);
            if (ReferenceEquals(planned, sample)) return sample;
            var placement = new BusbarBridgeInsulatorClearance().PlaceIndependently(planned, planned.Objects, null, null, null,
                preferSharedWidenedEnds: preferSharedWidenedEnds);
            planned = planned.WithGeometry(AssignInitialFrameSources(placement.Objects), planned.Routes,
                planned.Issues.Where(i => i.Source != "基架连接避让" && i.Source != BridgePhaseChangeSupportPlanner.IssueSource)
                    .Concat(clearance.Validate(planned, placement.Objects)).Concat(placement.Issues));
            var editing = sample.SavedFrameLengthEditing;
            return planned.WithFrameLengthEditing(new BridgeFrameLengthEditing(planned.Objects.Where(o => o.Kind == DesignObjectKind.Frame),
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), editing?.OffsetCopperMinimumLengthMm))
                .WithCopperLengthBaseline(planned.Objects.Where(o => o.IsJointGroup));
        }

        private void AddFaceToFaceElevatedBases(List<BridgeDesignObject> objects, BridgeUpstreamSnapshot upstream,
            BridgeCoordinateFrame coordinates)
        {
            foreach (var side in new[] { "A", "B" })
            {
                var label = side + "柜增高基座";
                if (!upstream.Fields.Any(field => field.Name.StartsWith(label, StringComparison.Ordinal))) continue;
                if (!TryReadNominalGeometry(upstream, label, out var shape))
                    throw new InvalidOperationException(label + "缺少有效的原驱动尺寸和模板定位关系，不能生成预览。");
                var point = coordinates.ToDraft(new[] { shape.CenterX, shape.CenterY, shape.CenterZ });
                objects.Add(Create("elevated-" + side.ToLowerInvariant(), side + "侧增高基座", DesignObjectKind.Base,
                    point[0], point[1], point[2], "总装顶层独立增高基座（模型只读）",
                    shape.Length, shape.Width, height: shape.Height));
            }
        }

        private void AddUpstreamPreviewParts(List<BridgeDesignObject> objects,
            IReadOnlyList<BridgeUpstreamPreviewPart> parts, double originX, double originZ)
        {
            foreach (var part in parts)
            {
                if (part.InsulatorPlacement != null)
                {
                    AddBaseInsulatorPreview(objects, part, originX, originZ);
                    continue;
                }
                var vertices = part.Mesh.Vertices;
                var preview = new double[vertices.Count];
                for (var i = 0; i < vertices.Count; i += 3)
                {
                    preview[i] = vertices[i] - originX;
                    preview[i + 1] = vertices[i + 2] - originZ;
                    preview[i + 2] = vertices[i + 1];
                }
                var parentId = (part.IsInsulator ? "base-" : "cabinet-") + part.Side.ToLowerInvariant();
                var parent = objects.Single(o => o.Id == parentId);
                var node = new BridgeDesignObject("source-" + part.ComponentName,
                    part.Side + (part.IsInsulator ? "基座" : "柜内") + part.Phase + "相" +
                    (part.IsInsulator ? "绝缘子" : "伸出排"),
                    part.IsInsulator ? DesignObjectKind.FixedInsulator : DesignObjectKind.FixedBusbar,
                    new Dictionary<DesignField, double>
                    {
                        [DesignField.X] = preview[0] - parent.X,
                        [DesignField.Y] = preview[1] - parent.Y,
                        [DesignField.Z] = preview[2] - parent.Z
                    }, Array.Empty<DesignField>(), "原模型表面预览（只读）：" + part.ComponentName,
                    parentId, part.Phase).WithModelMesh(new BridgePreviewMesh(preview));
                objects.Add(node);
            }
        }

        private void AddBaseInsulatorPreview(List<BridgeDesignObject> objects,
            BridgeUpstreamPreviewPart part, double originX, double originZ)
        {
            var source = part.InsulatorPlacement;
            var placement = new BridgeInsulatorPose(
                new[] { source.Connection[0] - originX, source.Connection[2] - originZ, source.Connection[1] },
                new[] { source.Axis[0], source.Axis[2], source.Axis[1] }, source.Height,
                source.SinglePhaseRootCount, source.PadThickness,
                source.SpanAxis == null ? null : new[] { source.SpanAxis[0], source.SpanAxis[2], source.SpanAxis[1] }, source.BracketSpan);
            var parentId = "base-" + part.Side.ToLowerInvariant();
            var parent = objects.Single(o => o.Id == parentId);
            var center = Enumerable.Range(0, 3).Select(i => placement.Connection[i] - placement.Axis[i] * placement.Height / 2d).ToArray();
            objects.Add(new BridgeDesignObject("source-" + part.ComponentName,
                part.Side + "基座" + part.Phase + "相绝缘子", DesignObjectKind.FixedInsulator,
                new Dictionary<DesignField, double>
                {
                    [DesignField.X] = center[0] - parent.X, [DesignField.Y] = center[1] - parent.Y,
                    [DesignField.Z] = center[2] - parent.Z, [DesignField.Height] = placement.Height
                }, Array.Empty<DesignField>(), "与基架绝缘子共用预览外形；实际安装位置、方向及高度（只读）",
                parentId, part.Phase).WithInsulatorPlacement(placement));
        }

        private BridgeInsulatorPlacement PlaceInitialInsulators(BridgeDesignSample sample,
            IReadOnlyList<BridgeDesignObject> objects, MiddleBusbarPathPlan path,
            IReadOnlyList<BridgeUpstreamEndpoint> endpoints, double originX, double originZ, double width,
            bool preferSharedWidenedEnds)
        {
            var editRoutes = CreateCopperEditRoutes(path, endpoints, originZ, width, out var joints);
            // 换相禁区和吊装接触面依赖相序路径及总装坐标；首次避让也必须取得与后续编辑相同的上下文。
            var context = sample.WithGeometry(objects.Concat(joints), sample.Routes)
                .WithCopperEditing(editRoutes, null).WithPreviewOrigin(originX, originZ);
            var placement = new BusbarBridgeInsulatorClearance().PlaceIndependently(context, context.Objects, null, null, null,
                preferSharedWidenedEnds: preferSharedWidenedEnds);
            var jointIds = new HashSet<string>(joints.Select(o => o.Id));
            // 编辑回调仍在最终支撑布局确定后绑定，避免回调捕获避让前的绝缘子或基架基线。
            return new BridgeInsulatorPlacement(placement.Objects.Where(o => !jointIds.Contains(o.Id)), placement.Issues);
        }

        private List<BridgeDesignRoute> CreateCopperEditRoutes(MiddleBusbarPathPlan path,
            IReadOnlyList<BridgeUpstreamEndpoint> endpoints, double originZ, double width,
            out List<BridgeDesignObject> joints)
        {
            joints = new List<BridgeDesignObject>();
            var editRoutes = new List<BridgeDesignRoute>();
            foreach (var phase in new[] { "A", "B", "C" })
            {
                var ids = new List<string>();
                for (var i = 0; i < path.Points.Count; i++)
                {
                    var side = i == 0 ? "B" : "A";
                    var phaseEnds = endpoints.Where(e => e.Phase == phase && e.Side == side).ToArray();
                    var reference = i == 0 ? path.Points.First() : path.Points.Last();
                    var fixedEnd = i == 0 || i == path.Points.Count - 1;
                    var id = phase + "-edit-joint-" + i;
                    joints.Add(new BridgeDesignObject(id, fixedEnd ? side + "基座L排断点" : "断点" + i,
                        fixedEnd ? DesignObjectKind.FixedJoint : DesignObjectKind.Breakpoint,
                        new Dictionary<DesignField, double>
                        {
                            // 规划器使用搭接中心；用户断点使用沿B→A的搭接区后截面。
                            [DesignField.X] = path.Points[i].X + width / 2d,
                            [DesignField.Y] = path.Points[i].Y + phaseEnds.Average(e => e.Interface[2]) - originZ - reference.Y,
                            [DesignField.Z] = phaseEnds.Average(e => e.Interface[1])
                        }, new[] { DesignField.X },
                        fixedEnd ? "基座L排断点；距基座边距离：远离为正，进入为负" : "以后截面为基准，ABC三相联动；提交后重新校验铜排几何", phase: phase)
                        .WithJointGroup("model-joint-" + i));
                    ids.Add(id);
                }
                editRoutes.Add(new BridgeDesignRoute("edit-route-" + phase, phase, ids));
            }
            return editRoutes;
        }

        private BridgeDesignSample AttachCopperEditing(BridgeDesignSample baseline, MiddleBusbarPathPlan path,
            IReadOnlyList<BridgeUpstreamEndpoint> endpoints, double originX, double originZ,
            double width, double thickness, bool phaseChange, double phaseSpacing, double insulationDistance,
            double floor, double top, double halfWidth, double supportStartX, double supportEndX)
        {
            var editRoutes = CreateCopperEditRoutes(path, endpoints, originZ, width, out var joints);
            var constraints = new List<BridgeBaseJointConstraint>();
            foreach (var side in new[] { "B", "A" })
            {
                var index = side == "B" ? 0 : path.Points.Count - 1;
                var baseId = side == "B" ? "base-b" : "base-a";
                var sign = side == "B" ? 1d : -1d;
                var sideEnds = endpoints.Where(e => e.Side == side).ToArray();
                // 保留一排宽搭接和原L排末次折弯半径；不可让尾段越过原折弯点。
                var radius = Math.Max(10d, thickness);
                var minimum = side == "B"
                    ? sideEnds.Max(e => e.Points[e.Points.Count - 2][0] - originX) + width + radius
                    : path.Points[0].X + width / 2d + .1d;
                var maximum = side == "A"
                    ? sideEnds.Min(e => e.Points[e.Points.Count - 2][0] - originX) - radius
                    : path.Points.Last().X + width / 2d - .1d;
                // 基座端接缝不参与基架分节调整，只允许铜排断点向桥内避让。
                if (side == "B") minimum = Math.Max(minimum, supportStartX + baseline.Parameters.OverlapLengthMm + BusbarBridgeFrameJointClearance.MinimumLapDistanceMm);
                else maximum = Math.Min(maximum, supportEndX - BusbarBridgeFrameJointClearance.MinimumLapDistanceMm);
                constraints.Add(new BridgeBaseJointConstraint("model-joint-" + index, baseId,
                    side == "B" ? supportStartX : supportEndX, sign, minimum, maximum));
            }
            Func<IReadOnlyList<BridgeDesignObject>, BridgeDesignSample> replan = null;
            replan = current =>
            {
                // 以界面位移量回写原始规划坐标，保留原始精度，同时抵消后截面的半排宽偏置。
                var controls = EditedCopperControls(current, joints);
                var adjusted = ReplanMiddleCopperPath(path, joints, controls, width, true, phaseSpacing, thickness, insulationDistance);
                var coordinates = adjusted.Points.Select(p => p.X).ToArray();
                var currentEditRoutes = CopperControlRoutes(editRoutes, controls);
                var adjustedEnds = MoveBaseLTails(endpoints, coordinates[0] - path.Points[0].X,
                    coordinates.Last() - path.Points.Last().X, width, thickness);
                var copperIds = new HashSet<string>(baseline.Routes.SelectMany(r => r.PointIds));
                var supportsChanged = adjusted.OffsetFrameStartX.HasValue &&
                    (Math.Abs(adjusted.OffsetFrameStartX.Value - path.OffsetFrameStartX.Value) > .001d ||
                     Math.Abs(adjusted.OffsetFrameEndX.Value - path.OffsetFrameEndX.Value) > .001d);
                var supportIds = new HashSet<string>(baseline.Objects.Where(o =>
                    o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator).Select(o => o.Id));
                var objects = baseline.Objects.Where(o => !copperIds.Contains(o.Id) &&
                    (!supportsChanged || !supportIds.Contains(o.Id))).ToList();
                objects.AddRange(controls);
                var routes = new List<BridgeDesignRoute>();
                AddModelPlannedRoutes(objects, routes, adjusted, adjustedEnds, originX, originZ, width, thickness,
                    phaseChange, insulationDistance, floor, top, halfWidth);
                if (supportsChanged)
                {
                    var bBase = baseline.Objects.Single(o => o.Id == "base-b");
                    var aBase = baseline.Objects.Single(o => o.Id == "base-a");
                    var height = top - floor + BusbarBridgeDraftPreviewSceneFactory.FrameFloorThicknessMm;
                    var offset = adjusted.Points.Last().Y - adjusted.Points.First().Y;
                    var large = Models.RdTypeMetadata.IsLargeOffset(offset, aBase.Width, bBase.Width);
                    AddPlannedSupports(objects, adjusted, large, top - height / 2d, height,
                        aBase.Width, bBase.Width, supportStartX, supportEndX, phaseSpacing, width, insulationDistance);
                    AlignPlannedSupportGeometry(objects, phaseSpacing, endpoints.Min(e => e.Interface[1]), thickness, endpoints);
                }
                var replanned = new BridgeDesignSample(baseline.Id, baseline.Source,
                    supportsChanged ? AssignInitialFrameSources(objects) : objects, routes,
                    baseline.Issues.Where(issue => issue.Source != "基架连接避让" && (!supportsChanged || !supportIds.Contains(issue.TargetId))), baseline.Parameters);
                if (supportsChanged)
                {
                    var connections = new BusbarBridgeFrameJointClearance().Plan(replanned);
                    replanned = new BridgeDesignSample(baseline.Id, baseline.Source, AssignInitialFrameSources(connections.Objects),
                        routes, replanned.Issues.Concat(connections.Issues), baseline.Parameters);
                }
                return replanned
                    .WithFrameLengthEditing(baseline.SavedFrameLengthEditing)
                    .WithCopperEditing(currentEditRoutes, replan).WithBaseJointConstraints(constraints)
                    .WithCopperLengthBaseline(joints)
                    .WithSpecialCopperSections(adjusted.Segments.Where(s => s.Role != MiddleBusbarSegmentRole.Ordinary).Select(s => s.Index + 1))
                    .WithPreviewOrigin(originX, originZ);
            };
            return new BridgeDesignSample(baseline.Id, baseline.Source, baseline.Objects.Concat(joints),
                baseline.Routes, baseline.Issues, baseline.Parameters).WithCopperEditing(editRoutes, replan)
                .WithFrameLengthEditing(baseline.SavedFrameLengthEditing)
                .WithBaseJointConstraints(constraints).WithPreviewOrigin(originX, originZ)
                .WithSpecialCopperSections(path.Segments.Where(s => s.Role != MiddleBusbarSegmentRole.Ordinary).Select(s => s.Index + 1));
        }

        private BridgeDesignObject[] EditedCopperControls(IReadOnlyList<BridgeDesignObject> current,
            IReadOnlyList<BridgeDesignObject> baseline)
        {
            var ids = new HashSet<string>(baseline.Select(o => o.Id));
            return current.Where(o => ids.Contains(o.Id) || o.JointGroupId?.StartsWith("user-joint-", StringComparison.Ordinal) == true)
                .OrderBy(o => o.X).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
        }

        private IReadOnlyList<BridgeDesignRoute> CopperControlRoutes(IReadOnlyList<BridgeDesignRoute> routes,
            IReadOnlyList<BridgeDesignObject> controls) => routes.Select(r => new BridgeDesignRoute(r.Id, r.Phase,
                controls.Where(o => o.Phase == r.Phase).OrderBy(o => o.X).Select(o => o.Id))).ToArray();

        private MiddleBusbarPathPlan ReplanMiddleCopperPath(MiddleBusbarPathPlan path,
            IReadOnlyList<BridgeDesignObject> baseline, IReadOnlyList<BridgeDesignObject> controls,
            double width, bool adjustTerminals, double phaseSpacing, double thickness, double clearance,
            bool deferOrdinaryLengthLimitToPhysicalGeometry = false)
        {
            var original = baseline.Where(o => o.Phase == "B").ToArray();
            var middle = controls.Where(o => o.Phase == "B").OrderBy(o => o.X).ToArray();
            var indices = middle.Select(o => Array.FindIndex(original, b => b.Id == o.Id)).ToArray();
            var coordinates = middle.Select((o, i) => indices[i] >= 0
                ? path.Points[indices[i]].X + o.X - original[indices[i]].X : o.X - width / 2d).ToArray();
            var adjusted = MiddleBusbarPathPlanner.RepartitionBreakpoints(path, coordinates, indices, width,
                adjustTerminals, deferOrdinaryLengthLimitToPhysicalGeometry);
            adjusted = FaceToFaceUnifiedPlanner.ReplanOffsetSupportRange(adjusted, phaseSpacing, width, thickness, clearance);
            return path.OffsetFrameStartX.HasValue && path.OffsetFrameEndX.HasValue
                ? FaceToFaceUnifiedPlanner.RecenterOffsetSafetyRange(adjusted, path.OffsetFrameEndX.Value - path.OffsetFrameStartX.Value)
                : adjusted;
        }

        private IReadOnlyList<BridgeUpstreamEndpoint> MoveBaseLTails(
            IReadOnlyList<BridgeUpstreamEndpoint> endpoints, double bShift, double aShift,
            double width, double thickness)
        {
            // 初始化和人工编辑共用尾段调整，原快照、连接起点及折弯点均保留。
            return endpoints.Select(e =>
            {
                var shift = e.Side == "B" ? bShift : aShift;
                var points = e.Points.Select(p => p.ToArray()).ToArray();
                var tail = points.Last();
                tail[0] += shift;
                var previous = points[points.Length - 2];
                var sign = e.Side == "B" ? 1d : -1d;
                if (Math.Abs(tail[1] - previous[1]) > .2d || Math.Abs(tail[2] - previous[2]) > .2d ||
                    sign * (tail[0] - previous[0]) < width + Math.Max(10d, thickness))
                    throw new InvalidOperationException(e.Side + "基座L排末端直段不足以保留搭接及折弯余量。");
                var connection = e.Interface.ToArray(); connection[0] += shift;
                return new BridgeUpstreamEndpoint(e.Side, e.Phase, e.Member, points, connection, e.CopyNativePreview());
            }).ToArray();
        }

        private void AlignPlannedSupportGeometry(List<BridgeDesignObject> objects,
            double phaseSpacing, double copperHeight, double thickness, IReadOnlyList<BridgeUpstreamEndpoint> endpoints)
        {
            // Member是从0开始的标识，不是根数。按两端各相实际接口数量取N，避免将N−1再次减一。
            var counts = new[] { "A", "B" }.SelectMany(side => new[] { "A", "B", "C" }.Select(phase =>
                endpoints.Count(e => e.Side == side && e.Phase == phase))).ToArray();
            if (counts.Distinct().Count() != 1 || counts[0] < 1 || counts[0] > 4)
                throw new InvalidOperationException("基架绝缘子垫块规划要求两柜三相各有相同的1～4根铜排接口。");
            var memberCount = counts[0];
            foreach (var frame in objects.Where(n => n.Kind == DesignObjectKind.Frame).ToArray())
            {
                var floor = frame.Z - frame.Height / 2d + BusbarBridgeDraftPreviewSceneFactory.FrameFloorThicknessMm;
                var height = copperHeight - thickness / 2d - floor;
                if (height <= 40d)
                    throw new InvalidOperationException("基架底板到铜排的空间不足以布置绝缘子支架。");
                objects[objects.IndexOf(frame)] = frame.WithInsulatorStack(memberCount - 1, thickness, phaseSpacing, height);
                var members = objects.Where(n => n.ParentId == frame.Id && n.Kind == DesignObjectKind.Insulator)
                    .OrderBy(n => n.Y).ToArray();
                for (var i = 0; i < members.Length; i++)
                {
                    var values = members[i].Values.ToDictionary(p => p.Key, p => p.Value);
                    values[DesignField.Y] = (i - (members.Length - 1) / 2d) * phaseSpacing;
                    values[DesignField.Z] = floor + height / 2d - frame.Z;
                    values[DesignField.Height] = height;
                    objects[objects.IndexOf(members[i])] = members[i].WithValues(values);
                }
            }
        }

        private void ValidateModelEndpoints(BridgeUpstreamSnapshot upstream)
        {
            var endpoints = upstream.Endpoints;
            if (endpoints.Count == 0)
                throw new InvalidOperationException("固定端排规划未完成：" +
                    (upstream.Fields.LastOrDefault(f => f.Name == "固定端排规划" && f.IsMissing)?.Source ??
                    "缺少真实固定端排几何，请检查伸出排连接几何诊断；不使用示意L排或Box端点。"));
            // 旧测试/纯几何快照没有驱动元数据；真实反读一旦携带根数，就必须两柜完整且逐相满足N。
            if (upstream.Fields.Any(f => f.Name.EndsWith("设计单相根数", StringComparison.Ordinal)))
            {
                foreach (var side in new[] { "A", "B" })
                {
                    var design = upstream.Fields.Where(f => f.Name.StartsWith(side + "柜始端", StringComparison.Ordinal) &&
                        f.Name.EndsWith("设计单相根数", StringComparison.Ordinal)).ToArray();
                    var extension = upstream.Fields.Where(f => f.Name.StartsWith(side + "柜始端", StringComparison.Ordinal) &&
                        f.Name.EndsWith("伸出排每相根数", StringComparison.Ordinal)).ToArray();
                    if (design.Length != 1 || extension.Length != 1 ||
                        !int.TryParse(design[0].Value, out var n) || !int.TryParse(extension[0].Value, out var e) ||
                        e < 1 || e > 3 || n != e + 1)
                        throw new InvalidOperationException(side + "柜设计根数N与伸出排根数E缺失或不满足N=E+1。");
                    foreach (var phase in new[] { "A", "B", "C" })
                        if (!endpoints.Where(p => p.Side == side && p.Phase == phase).Select(p => p.Member)
                            .OrderBy(i => i).SequenceEqual(Enumerable.Range(0, n)))
                            throw new InvalidOperationException(side + "柜" + phase + "相生成接口必须为N=" + n + "根，不能以伸出排E代替。");
                }
            }
            foreach (var phase in new[] { "A", "B", "C" })
            {
                var b = endpoints.Where(e => e.Side == "B" && e.Phase == phase).Select(e => e.Member).OrderBy(i => i).ToArray();
                var a = endpoints.Where(e => e.Side == "A" && e.Phase == phase).Select(e => e.Member).OrderBy(i => i).ToArray();
                if (a.Length == 0 || !a.SequenceEqual(b) || a.Distinct().Count() != a.Length)
                    throw new InvalidOperationException(phase + "相两端排根数不一致或固定端接口重复。");
            }
        }

        private MiddleBusbarPathPlan AddModelPlannedRoutes(List<BridgeDesignObject> objects, List<BridgeDesignRoute> routes,
            MiddleBusbarPathPlan plan, IReadOnlyList<BridgeUpstreamEndpoint> endpoints,
            double originX, double originZ, double width, double thickness, bool requiresPhaseChange,
            double insulationDistance, double floorHeight, double topHeight, double halfWidth,
            bool includeEndpoints = true, bool expandOffset = false, double? initialFrameMinimumLength = null)
        {
            var phases = new[] { "A", "B", "C" };
            Func<string, IReadOnlyList<IReadOnlyList<GeometRi.Point3d>>> interfaces = side =>
                phases.Select(phase => (IReadOnlyList<GeometRi.Point3d>)endpoints
                    .Where(e => e.Side == side && e.Phase == phase).OrderBy(e => e.Member)
                    .Select(e => new GeometRi.Point3d(e.Interface[0] - originX, e.Interface[1], e.Interface[2] - originZ))
                    .ToArray()).ToArray();
            var fixedConnections = endpoints.Select(e =>
            {
                // 旧纯几何快照仍可用于二维；原生预览必须持有真实加工参数，不能补猜半径。
                var physical = e.CopyNativePreview() ?? new PlanarBendBusbarSetting
                {
                    IsCenter = true, BusBarWidth = width, BusBarThickness = thickness,
                    WidthVector = new GeometRi.Vector3d(0d, 0d, 1d),
                    StartBusbarVector = new GeometRi.Vector3d(e.Side == "B" ? 1d : -1d, 0d, 0d)
                };
                physical.GroupKey = e.Phase + "-" + (e.Member + 1) + "-" + e.Side + "基座L排";
                physical.PointList = e.Points.Select(p => new GeometRi.Point3d(p[0] - originX, p[1], p[2] - originZ)).ToList();
                return physical;
            }).ToArray();
            Func<MiddleBusbarPathPlan, IReadOnlyList<PlanarBendBusbarSetting>> create = candidate => FaceToFaceModelBusbarPlanner.CreateFixedInterfaceMiddleGeometry(candidate,
                interfaces("B"), interfaces("A"), width, thickness, requiresPhaseChange, floorHeight, topHeight, halfWidth,
                insulationDistance, fixedConnections);
            var pieces = expandOffset
                ? FaceToFaceUnifiedPlanner.CreateWithAutomaticOffsetExpansion(plan, width, create, out plan,
                    initialFrameMinimumLength)
                : create(plan);
            for (var endpointIndex = 0; includeEndpoints && endpointIndex < endpoints.Count; endpointIndex++)
            {
                var endpoint = endpoints[endpointIndex];
                AddModelEndpoint(objects, routes, endpoint, endpoint.Phase + "-member-" + endpoint.Member + "-" +
                    endpoint.Side.ToLowerInvariant(), originX, originZ, width, thickness, fixedConnections[endpointIndex]);
            }
            for (var pieceIndex = 0; pieceIndex < pieces.Count; pieceIndex++)
            {
                var piece = pieces[pieceIndex];
                var phase = piece.GroupKey.Substring(0, 1);
                var ids = new List<string>();
                for (var i = 0; i < piece.PointList.Count; i++)
                {
                    var point = piece.PointList[i];
                    var id = "physical-piece-" + pieceIndex + "-" + i;
                    objects.Add(Create(id, phase + "相铜排件" + (pieceIndex + 1),
                        i == 0 || i == piece.PointList.Count - 1 ? DesignObjectKind.FixedJoint : DesignObjectKind.FixedRoutePoint,
                        point.X, point.Z, point.Y,
                        (piece.GroupKey.Contains("/split") || piece.GroupKey.Contains("/tail") ? "自动增加断点分段；" : string.Empty) +
                        "正式几何模板；展开长度" +
                            piece.GetDevelopedLength().ToString("0.###", CultureInfo.InvariantCulture) +
                            " mm；跨相净距校核通过", phase: phase));
                    ids.Add(id);
                }
                routes.Add(new BridgeDesignRoute("physical-route-" + pieceIndex, phase, ids)
                    .WithPhysicalGeometry(piece.BusBarWidth, piece.BusBarThickness,
                        FaceToFaceModelBusbarPlanner.CreateFixedInterfacePreviewSolids(piece)
                            .Select(solid => solid.Select(p => new[] { p[0], p[2], p[1] }).ToArray()))
                    .WithNativePreview(piece, originX, originZ)
                    .WithCopperSection(piece.PlanningSegmentIndex + 1));
            }
            return plan;
        }

        private void AddModelEndpoint(List<BridgeDesignObject> objects, List<BridgeDesignRoute> routes,
            BridgeUpstreamEndpoint endpoint, string prefix, double originX, double originZ, double width, double thickness,
            PlanarBendBusbarSetting physical)
        {
            var ids = new List<string>();
            for (var i = 0; i < endpoint.Points.Count; i++)
            {
                var p = endpoint.Points[i];
                var id = prefix + "-" + i;
                objects.Add(Create(id, endpoint.Side + "柜" + endpoint.Phase + "相基座排" + (endpoint.Member + 1),
                    i == 0 ? DesignObjectKind.FixedJoint : DesignObjectKind.FixedRoutePoint,
                    p[0] - originX, p[2] - originZ, p[1], "正式端排算法生成的实际中心线", phase: endpoint.Phase));
                ids.Add(id);
            }
            if (endpoint.Side == "A") ids.Reverse();
            var route = new BridgeDesignRoute("route-" + prefix, endpoint.Phase, ids).WithPhysicalGeometry(width, thickness,
                FaceToFaceModelBusbarPlanner.CreateFixedInterfacePreviewSolids(physical)
                    .Select(solid => solid.Select(p => new[] { p[0], p[2], p[1] }).ToArray()));
            if (endpoint.CopyNativePreview() != null) route = route.WithNativePreview(physical, originX, originZ);
            routes.Add(route);
        }

        private void AddPlannedRoutes(List<BridgeDesignObject> objects,
            List<BridgeDesignRoute> routes, MiddleBusbarPathPlan plan, double pathStartX,
            double pathEndX, double offset, double phaseSpacing, bool requiresPhaseChange,
            bool useOrthogonalOffsetTransition, double elevation)
        {
            for (var phaseIndex = 0; phaseIndex < 3; phaseIndex++)
            {
                var phase = new[] { "A", "B", "C" }[phaseIndex];
                var originalPhaseY = (phaseIndex - 1) * phaseSpacing;
                var changedPhaseY = requiresPhaseChange
                    ? (1 - phaseIndex) * phaseSpacing
                    : originalPhaseY;
                var pointIds = new List<string>();
                for (var pointIndex = 0; pointIndex < plan.Points.Count; pointIndex++)
                {
                    var pathPoint = plan.Points[pointIndex];
                    var id = phase + "-planned-" + pointIndex.ToString(CultureInfo.InvariantCulture);
                    var isStart = pointIndex == 0;
                    var isEnd = pointIndex == plan.Points.Count - 1;
                    var phaseY = pointIndex == 0 ? originalPhaseY : changedPhaseY;
                    var kind = isStart || isEnd ? DesignObjectKind.FixedJoint :
                        pointIndex == 1 ? DesignObjectKind.Lap : DesignObjectKind.Breakpoint;
                    var name = isStart ? "B基座L排断点" : isEnd ? "A基座L排断点" :
                        "规划搭接断点" + pointIndex.ToString(CultureInfo.InvariantCulture);
                    var role = isStart || isEnd
                        ? "基座L排与中间排的固定接口"
                        : "由2000 mm最佳间距和3000 mm最大展开长度自动规划；不使用基架坐标";
                    if (pointIndex > 0 &&
                        (plan.Segments[pointIndex - 1].Role & MiddleBusbarSegmentRole.Offset) != 0)
                    {
                        var previous = plan.Points[pointIndex - 1];
                        var previousPhaseY = pointIndex - 1 == 0
                            ? originalPhaseY
                            : changedPhaseY;
                        var firstControlId = phase + "-planned-offset-" + pointIndex + "-1";
                        var secondControlId = phase + "-planned-offset-" + pointIndex + "-2";
                        var longitudinalSpan = pathPoint.X - previous.X;
                        var firstControlX = useOrthogonalOffsetTransition
                            ? previous.X + longitudinalSpan / 2d
                            : previous.X + longitudinalSpan / 4d;
                        var secondControlX = useOrthogonalOffsetTransition
                            ? firstControlX
                            : pathPoint.X - longitudinalSpan / 4d;
                        objects.Add(Create(firstControlId, phase + "相偏移折弯控制点",
                            DesignObjectKind.FixedRoutePoint,
                            firstControlX,
                            previous.Y + previousPhaseY, elevation,
                            useOrthogonalOffsetTransition
                                ? "大偏移90°转角固定路径点"
                                : "母排中间段偏移折弯固定路径点",
                            phase: phase));
                        objects.Add(Create(secondControlId, phase + "相偏移折弯控制点",
                            DesignObjectKind.FixedRoutePoint,
                            secondControlX,
                            pathPoint.Y + phaseY, elevation,
                            useOrthogonalOffsetTransition
                                ? "大偏移90°转角固定路径点"
                                : "母排中间段偏移折弯固定路径点",
                            phase: phase));
                        pointIds.Add(firstControlId);
                        pointIds.Add(secondControlId);
                    }
                    var node = Create(id, name, kind, pathPoint.X, pathPoint.Y + phaseY, elevation,
                        role, phase: phase, editableX: isStart || isEnd)
                        .WithJointGroup(isStart ? "planned-start" : isEnd ? "planned-end" :
                            "planned-joint-" + pointIndex.ToString(CultureInfo.InvariantCulture));
                    objects.Add(node);
                    pointIds.Add(id);
                }
                routes.Add(new BridgeDesignRoute("planned-route-" + phase, phase, pointIds));
                AddBaseL(objects, routes, phase, "b", pathStartX, originalPhaseY, -1, pointIds[0], elevation);
                AddBaseL(objects, routes, phase, "a", pathEndX, offset + changedPhaseY, 1,
                    pointIds[pointIds.Count - 1], elevation);
            }
        }

        /// <summary>
        /// 铜排路径与分段确定后布置基架。基架不再按铜排段划分：
        /// 除偏移基架必须绑定偏移段断点外，常规基架沿参考路径按固定节拍独立铺排，
        /// 单节长度不超过上限；普通断点变化不影响铺排，偏移区边界变化时重新铺排两侧区间。
        /// </summary>
        private void AddPlannedSupports(List<BridgeDesignObject> objects,
            MiddleBusbarPathPlan plan, bool isLargeOffset, double elevation, double frameHeight,
            double aSideWidth, double bSideWidth, double supportStartX, double supportEndX,
            double phaseSpacing, double busbarWidth, double insulationClearance, bool reverseRearFrames = true,
            bool reverseWidenedTail = true, bool preferEqualWidenedEnds = false)
        {
            var signedOffset = plan.Points.Last().Y - plan.Points.First().Y;
            isLargeOffset = Models.RdTypeMetadata.IsLargeOffset(signedOffset, aSideWidth, bSideWidth);
            if (CKBusbarBridge.DesignGeometry.BridgeOffsetRules.IsWidened(signedOffset))
            {
                var lengths = MiddleBusbarSupportFramePlanner.CreateFrameLengths(supportEndX - supportStartX).ToArray();
                if (preferEqualWidenedEnds && lengths.Length > 1) EqualizeWidenedEndLengths(lengths);
                var layout = Drivers.FaceToFaceSmallOffsetFramePlanner.CreateLayout(
                    lengths.Length, 0, signedOffset, aSideWidth, bSideWidth);
                var cursor = supportStartX;
                var centerY = plan.Points.First().Y + Math.Sign(signedOffset) * layout.GetOffsetFrame(0).InletOffsetSize;
                for (var index = 0; index < lengths.Length; index++)
                {
                    var offset = layout.GetOffsetFrame(index);
                    var reversed = offset != null
                        ? reverseWidenedTail && index > 0 && index == lengths.Length - 1
                        : reverseRearFrames && index >= (lengths.Length + 1) / 2;
                    var id = "frame-" + (index + 1).ToString(CultureInfo.InvariantCulture);
                    var frame = Create(id, "基架" + (index + 1).ToString(CultureInfo.InvariantCulture),
                        DesignObjectKind.Frame, cursor + lengths[index] / 2d, centerY, elevation,
                        "整段扩宽基架", lengths[index], layout.FrameWidths[index], height: frameHeight).WithOffsetInsulators();
                    if (offset != null)
                    {
                        offset = offset.InInstallationOrder(reversed);
                        frame = frame.WithFrameTemplate("SmallOffset")
                            .WithSmallOffsetPreview(new BridgeSmallOffsetPreview(offset.InletOffsetDirection,
                                offset.InletOffsetSize, offset.OutletOffsetDirection, offset.OutletOffsetSize));
                    }
                    if (reversed)
                        frame = frame.WithValues(frame.Values.ToDictionary(p => p.Key,
                            p => p.Key == DesignField.Installation ? 1d : p.Value));
                    objects.Add(frame.WithFramePosition(index + 1));
                    // 扩宽端头032仍承担常规支撑，和中间基架共用绝缘子规划。
                    AddPlannedFrameInsulators(objects, frame, frameHeight);
                    cursor += lengths[index];
                }
                return;
            }
            var frameIndex = 0;
            var offsetSegments = plan.Segments
                .Where(segment => (segment.Role & MiddleBusbarSegmentRole.Offset) != 0)
                .ToArray();
            // 偏移段占据的纵向区间由偏移基架单独覆盖；其余区间为常规基架可铺排区间。
            var offsetStartX = offsetSegments.Length == 0
                ? 0d
                : plan.OffsetFrameStartX ?? offsetSegments[0].Start.X;
            var offsetEndX = offsetSegments.Length == 0
                ? 0d
                : plan.OffsetFrameEndX ?? offsetSegments.Last().End.X;
            var offsetSpan = offsetEndX - offsetStartX;
            // 常规区间 = 两基座朝桥侧边缘之间去掉偏移区间；与铜排搭接中心独立。
            var ordinaryRanges = offsetSegments.Length == 0
                ? new[] { new PlannedSpan(supportStartX, supportEndX, plan.Points[0].Y) }
                : new[]
                {
                    new PlannedSpan(supportStartX, offsetStartX, offsetSegments[0].Start.Y),
                    new PlannedSpan(offsetEndX, supportEndX, offsetSegments.Last().End.Y)
                };

            // 常规基架在各常规区间内按固定节拍独立铺排，布置与铜排断点无关。
            for (var rangeIndex = 0; rangeIndex < ordinaryRanges.Length; rangeIndex++)
            {
                var range = ordinaryRanges[rangeIndex];
                if (range.Length <= .001d) continue;
                if (range.Length < MiddleBusbarSupportFramePlanner.MinimumFrameLength)
                {
                    throw new InvalidOperationException(
                        "特殊基架与端部之间的连续普通基架区间不足200 mm，" +
                        "且不能向特殊工艺区借用空间。");
                }
                var cursorX = range.StartX;
                var lengths = MiddleBusbarSupportFramePlanner.CreateFrameLengths(range.Length);
                for (var index = 0; index < lengths.Count; index++)
                {
                    var length = lengths[index];
                    frameIndex++;
                    var frameWidth = offsetSegments.Length == 0 ? Math.Max(aSideWidth, bSideWidth)
                        : cursorX < offsetStartX ? bSideWidth : aSideWidth;
                    // 默认安装只在创建时确定；偏移场景以偏移结构分界，其余按基架节数从中间分开。
                    var reversed = reverseRearFrames && (offsetSegments.Length > 0
                        ? rangeIndex == 1 : index >= (lengths.Count + 1) / 2);
                    cursorX = AddOrdinaryPlannedFrame(objects, range.CenterY, cursorX, length, frameIndex,
                        elevation, frameHeight, frameWidth, reversed);
                }
            }

            // 偏移基架：必须落在偏移段上，按偏移段断点规划；小偏移为正装加宽箱体。
            if (offsetSegments.Length > 0)
            {
                if (isLargeOffset)
                {
                    // 推荐公式的30.0 mm有效角钢宽度对应10.0 mm豁口。25.0 mm在两侧各再让出15.0 mm。
                    var minimumSafeLength = FaceToFaceUnifiedPlanningRules.CalculateLargeOffsetMinimumSharedLength(
                        phaseSpacing, busbarWidth, insulationClearance) - 2d * (BridgeAngleNotchPlan.MaximumDepthMm - 10d);
                    AddLargeOffsetPlannedFrames(objects, offsetSegments[0],
                        aSideWidth, bSideWidth, offsetStartX, offsetSpan,
                        ref frameIndex, elevation, frameHeight, minimumSafeLength);
                }
                else
                {
                    var offsetCursorX = offsetStartX;
                    foreach (var length in MiddleBusbarSupportFramePlanner
                        .CreateOffsetFrameLengths(offsetSpan))
                    {
                        frameIndex++;
                        offsetCursorX = AddOffsetPlannedFrame(objects, plan, offsetSegments[0],
                            aSideWidth, bSideWidth, offsetSpan, offsetCursorX, length, frameIndex, elevation, frameHeight);
                    }
                }
            }
        }

        private static void EqualizeWidenedEndLengths(double[] lengths)
        {
            var last = lengths.Length - 1;
            var endTotal = lengths[0] + lengths[last];
            var standard = MiddleBusbarSupportFramePlanner.StandardFrameLength;
            var remainder = endTotal - 2d * standard;
            for (var index = 1; index < last; index++)
            {
                var adjusted = lengths[index] + remainder;
                if (adjusted < MiddleBusbarSupportFramePlanner.MinimumFrameLength ||
                    adjusted > MiddleBusbarSupportFramePlanner.MaximumFrameLength) continue;
                lengths[index] = adjusted;
                lengths[0] = lengths[last] = standard;
                return;
            }
            lengths[0] = lengths[last] = endTotal / 2d;
        }

        /// <summary>常规基架可铺排的纵向区间。</summary>
        private readonly struct PlannedSpan
        {
            internal PlannedSpan(double startX, double endX, double centerY)
            {
                StartX = startX;
                EndX = endX;
                CenterY = centerY;
            }

            internal double StartX { get; }

            internal double EndX { get; }

            internal double CenterY { get; }

            internal double Length => EndX - StartX;
        }

        /// <summary>按参考路径铺排一个常规基架；其长度与位置不依赖铜排断点。返回下一节起点。</summary>
        private double AddOrdinaryPlannedFrame(List<BridgeDesignObject> objects,
            double centerY, double startX, double length, int frameIndex,
            double elevation, double frameHeight, double frameWidth, bool reversed)
        {
            var centerX = startX + length / 2d;
            // 铜排过渡区包含特殊基架外的保护长度；普通基架即使进入保护区，
            // 仍须与所在侧直线段共轴，不能沿铜排过渡路径内插横向位置。
            var role = "铜排路径确定后生成的常规支撑：沿参考路径按固定节拍独立铺排，不随断点移动";
            var frame = Create("frame-" + frameIndex.ToString(CultureInfo.InvariantCulture),
                "基架" + frameIndex.ToString(CultureInfo.InvariantCulture), DesignObjectKind.Frame,
                centerX, centerY, elevation, role, length, frameWidth,
                height: frameHeight);
            var values = frame.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
            // 安装方式负责正反装；路径斜率不能改变基架自身的桥向轴。
            values[DesignField.Direction] = 0d;
            values[DesignField.Installation] = reversed ? 1d : 0d;
            frame = frame.WithValues(values);
            objects.Add(frame);
            AddPlannedFrameInsulators(objects, frame, frameHeight);
            return startX + length;
        }

        /// <summary>按偏移段断点铺排偏移基架；返回下一节起点。</summary>
        private double AddOffsetPlannedFrame(List<BridgeDesignObject> objects,
            MiddleBusbarPathPlan plan, MiddleBusbarPathSegment offsetSegment,
            double aSideWidth, double bSideWidth, double offsetSpan,
            double startX, double length, int frameIndex, double elevation, double frameHeight)
        {
            var signedOffset = offsetSegment.End.Y - offsetSegment.Start.Y;
            var isSmallOffset = !Models.RdTypeMetadata.IsLargeOffset(signedOffset, aSideWidth, bSideWidth);
            var centerX = startX + length / 2d;
            var role = isSmallOffset
                ? "小偏移基架：固定正装，沿桥体纵向布置，加宽覆盖两侧偏移"
                : "铜排路径确定后按偏移段断点生成的偏移段支撑";
            // 小偏移模板是正装的加宽矩形箱体，铜排斜线长度和方向不是基架尺寸与姿态。
            var offsetLayout = isSmallOffset
                ? Drivers.FaceToFaceSmallOffsetFramePlanner.CreateLayout(
                    plan.Segments.Count, offsetSegment.Index, signedOffset,
                    aSideWidth, bSideWidth).OffsetFrame
                : null;
            var frameWidth = offsetLayout?.FrameWidth ?? 1000d;
            // 由模板进孔偏移确定箱体中心；两侧宽度不等时，中心不是两条路径中心线的中点。
            var centerY = offsetLayout == null ? (offsetSegment.Start.Y + offsetSegment.End.Y) / 2d :
                offsetSegment.Start.Y + (offsetLayout.InletOffsetDirection == 0 ? 1d : -1d) * offsetLayout.InletOffsetSize;
            var frame = Create("frame-" + frameIndex.ToString(CultureInfo.InvariantCulture),
                "基架" + frameIndex.ToString(CultureInfo.InvariantCulture), DesignObjectKind.Frame,
                centerX, centerY, elevation, role,
                length, frameWidth, height: frameHeight);
            var values = frame.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
            values[DesignField.Direction] = isSmallOffset ? 0d
                : Math.Atan2(signedOffset, offsetSpan) * 180d / Math.PI;
            // 小偏移支撑须待原生中间搭接排和装配坐标确定后逐相创建，此处不套用普通三相支架。
            if (isSmallOffset) values[DesignField.InsulatorCount] = 0d;
            frame = frame.WithValues(values);
            if (offsetLayout != null)
                frame = frame.WithSmallOffsetPreview(new BridgeSmallOffsetPreview(offsetLayout.InletOffsetDirection,
                    offsetLayout.InletOffsetSize, offsetLayout.OutletOffsetDirection, offsetLayout.OutletOffsetSize));
            objects.Add(frame);
            if (!isSmallOffset) AddPlannedFrameInsulators(objects, frame, frameHeight);
            return startX + length;
        }

        /// <summary>
        /// 大偏移支撑由两个X向拐角基架与若干Y向连接基架组成。
        /// 连接跨度按两侧基架宽度扣除，不把整个大偏移区误当成一节斜放基架。
        /// </summary>
        private void AddLargeOffsetPlannedFrames(
            List<BridgeDesignObject> objects,
            MiddleBusbarPathSegment offsetSegment,
            double aSideWidth,
            double bSideWidth,
            double offsetStartX,
            double cornerLength,
            ref int frameIndex,
            double elevation, double frameHeight, double minimumSafeLength)
        {
            FaceToFaceUnifiedPlanningRules.ValidateSpecialFrameLength(
                cornerLength,
                cornerLength,
                nameof(cornerLength));
            var signedOffset = offsetSegment.End.Y - offsetSegment.Start.Y;
            var connectorSpan = FaceToFaceUnifiedPlanningRules
                .CalculateLargeOffsetConnectorSpan(
                    signedOffset,
                    aSideWidth,
                    bSideWidth);
            FaceToFaceUnifiedPlanningRules.ValidateLargeOffsetConnectorSpan(
                signedOffset,
                aSideWidth,
                bSideWidth);

            var centerX = offsetStartX + cornerLength / 2d;
            frameIndex++;
            var bCorner = Create(
                "frame-" + frameIndex.ToString(CultureInfo.InvariantCulture),
                "B侧大偏移拐角基架",
                DesignObjectKind.Frame,
                centerX,
                offsetSegment.Start.Y,
                elevation,
                "大偏移B侧拐角基架：X向，连续承托90°搭接区",
                cornerLength,
                bSideWidth,
                height: frameHeight);
            // 偏移拐角基架暂不添加绝缘子；中间连接基架仍按普通支撑布置。
            var bCornerValues = bCorner.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
            bCornerValues[DesignField.InsulatorCount] = 0d;
            // B端向-X接上游，向偏移方向接中间架；外侧两面封闭。
            bCorner = bCorner.WithValues(bCornerValues)
                .WithCornerPreview(new BridgeCornerPreview(1, -Math.Sign(signedOffset), minimumSafeLength))
                .WithAngleNotchDepth(10d);
            objects.Add(bCorner);

            var direction = Math.Sign(signedOffset);
            var connectorCursor = offsetSegment.Start.Y + direction * bSideWidth / 2d;
            foreach (var connectorLength in MiddleBusbarSupportFramePlanner
                .CreateFrameLengths(connectorSpan))
            {
                frameIndex++;
                var connector = Create(
                    "frame-" + frameIndex.ToString(CultureInfo.InvariantCulture),
                    "大偏移连接基架" + frameIndex.ToString(CultureInfo.InvariantCulture),
                    DesignObjectKind.Frame,
                    centerX,
                    connectorCursor + direction * connectorLength / 2d,
                    elevation,
                    "大偏移Y向连接基架：每节不超过1400 mm",
                    connectorLength,
                    cornerLength,
                    height: frameHeight);
                var connectorValues = connector.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
                connectorValues[DesignField.Direction] = direction > 0d ? 90d : -90d;
                connector = connector.WithValues(connectorValues).WithAngleNotchDepth(10d);
                objects.Add(connector);
                AddPlannedFrameInsulators(objects, connector, frameHeight);
                connectorCursor += direction * connectorLength;
            }

            frameIndex++;
            var aCorner = Create(
                "frame-" + frameIndex.ToString(CultureInfo.InvariantCulture),
                "A侧大偏移拐角基架",
                DesignObjectKind.Frame,
                centerX,
                offsetSegment.End.Y,
                elevation,
                "大偏移A侧拐角基架：X向，连续承托90°搭接区",
                cornerLength,
                aSideWidth,
                height: frameHeight);
            var aCornerValues = aCorner.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
            aCornerValues[DesignField.InsulatorCount] = 0d;
            aCorner = aCorner.WithValues(aCornerValues)
                .WithCornerPreview(new BridgeCornerPreview(-1, Math.Sign(signedOffset), minimumSafeLength))
                .WithAngleNotchDepth(10d);
            objects.Add(aCorner);
        }

        private void AddPlannedFrameInsulators(List<BridgeDesignObject> objects,
            BridgeDesignObject frame, double frameHeight)
        {
            // 局部X=0仅是中点候选，普通基架和扩宽端头均由同一避让器确定最终定位。
            for (var insulatorIndex = 0; insulatorIndex < 3; insulatorIndex++)
            {
                objects.Add(CreateInsulator(frame.Id, insulatorIndex + 1,
                    -200d + insulatorIndex * 200d, frameHeight, frame.Z));
            }
        }

        internal double? GetUpstreamOverlapLength(BridgeUpstreamSnapshot upstream)
        {
            if (upstream == null) return null;
            double value;
            if (upstream.FaceToWall != null) value = upstream.FaceToWall.OverlapLength;
            else if (upstream.SideToSide != null) value = upstream.SideToSide.Copper.Width;
            else if (!TryReadSpecification(upstream, out value, out var thickness)) return null;
            return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value)
                ? (double?)BridgeDesignPrecision.OneDecimal(value) : null;
        }

        internal double? GetTargetFrameLength(BridgeUpstreamSnapshot upstream)
        {
            if (upstream == null) return null;
            double length;
            if (upstream.FaceToWall != null)
            {
                var wall = upstream.FaceToWall;
                length = wall.WallOpeningCorners.Average(point => point.X) - wall.Cabinet.Opening.X;
            }
            else if (upstream.SideToSide != null)
            {
                var input = upstream.SideToSide.Copper;
                length = input.A.Opening.X - input.B.Opening.X;
            }
            else if (upstream.Scenario?.Contains("面对面") == true &&
                TryReadNominalGeometry(upstream, "A柜基座", out var aBase) &&
                TryReadNominalGeometry(upstream, "B柜基座", out var bBase))
            {
                // 与基架铺排共用两侧基座朝桥边界；输入来自原驱动尺寸及已核验的组件变换。
                length = aBase.CenterX - aBase.Length / 2d - (bBase.CenterX + bBase.Length / 2d);
            }
            else return null;
            return double.IsNaN(length) || double.IsInfinity(length) || length <= 0d
                ? (double?)null : BridgeDesignPrecision.OneDecimal(length);
        }

        private readonly struct NominalGeometry
        {
            internal NominalGeometry(double centerX, double centerY, double centerZ,
                double length, double width, double height)
            {
                CenterX = centerX; CenterY = centerY; CenterZ = centerZ;
                Length = length; Width = width; Height = height;
            }
            internal double CenterX { get; }
            internal double CenterY { get; }
            internal double CenterZ { get; }
            internal double Length { get; }
            internal double Width { get; }
            internal double Height { get; }
        }

        private static bool TryReadNominalGeometry(BridgeUpstreamSnapshot upstream, string prefix,
            out NominalGeometry geometry)
        {
            geometry = default(NominalGeometry);
            var values = new double[6];
            var names = new[] { "名义中心X", "名义中心Y", "名义中心Z", "名义X向尺寸", "名义Z向尺寸", "名义Y向尺寸" };
            for (var index = 0; index < names.Length; index++)
            {
                if (!TryReadMillimetres(upstream, prefix + names[index], out values[index]) || values[index] <= 0d && index >= 3)
                    return false;
            }
            geometry = new NominalGeometry(values[0], values[1], values[2], values[3], values[4], values[5]);
            return true;
        }
        private static bool TryReadMillimetres(BridgeUpstreamSnapshot upstream, string name,
            out double value)
        {
            value = 0d;
            var text = upstream.Fields.FirstOrDefault(field => field.Name == name)?.Value;
            var match = Regex.Match(text ?? string.Empty, @"[-+]?\d+(?:\.\d+)?(?:[Ee][-+]?\d+)?");
            return match.Success && double.TryParse(match.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double ReadRequiredMillimetres(BridgeUpstreamSnapshot upstream, string prefix,
            string suffix)
        {
            var matches = upstream.Fields.Where(item =>
                item.Name.StartsWith(prefix, StringComparison.Ordinal) &&
                item.Name.EndsWith(suffix, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(CabinetHoleParameters.GetParameterDisplayName(prefix + suffix + "必须唯一对应启用的柜前/柜后接口，实际数量=" + matches.Length + "，不能按列表顺序选择。"));
            var field = matches[0];
            if (field == null || !TryReadMillimetres(upstream, field.Name, out var value))
                throw new InvalidOperationException(CabinetHoleParameters.GetParameterDisplayName(prefix + suffix + "缺失或无效，不能确定铜排端点。来源：" + field.Source));
            return value;
        }

        private static string ReadFieldContaining(BridgeUpstreamSnapshot upstream, string prefix,
            string suffix)
        {
            return upstream.Fields.FirstOrDefault(field => field.Name.Contains(prefix) &&
                field.Name.EndsWith(suffix, StringComparison.Ordinal))?.Value;
        }

        private static bool TryReadSpecification(BridgeUpstreamSnapshot upstream, out double width,
            out double thickness)
        {
            width = 0d;
            thickness = 0d;
            var specifications = upstream.Fields.Where(field => field.Name.EndsWith("铜排规格",
                    StringComparison.Ordinal) && !field.IsMissing)
                .Select(field => Regex.Match(field.Value, @"(\d+(?:\.\d+)?)\s*[×xX*]\s*(\d+(?:\.\d+)?)"))
                .Where(match => match.Success)
                .Select(match => new
                {
                    Width = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    Thickness = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
                }).Distinct().ToArray();
            if (specifications.Length != 1) return false;
            width = specifications[0].Width;
            thickness = specifications[0].Thickness;
            return width > 0d && thickness > 0d;
        }

        private static bool TryReadMatchingPhaseSpacing(
            BridgeUpstreamSnapshot upstream,
            out double phaseSpacing)
        {
            phaseSpacing = 0d;
            var values = upstream.Fields
                .Where(field => field.Name.EndsWith("相间距", StringComparison.Ordinal)
                    && !field.IsMissing)
                .Select(field => Regex.Match(
                    field.Value ?? string.Empty,
                    @"[-+]?\d+(?:\.\d+)?"))
                .Where(match => match.Success)
                .Select(match => double.Parse(
                    match.Value,
                    CultureInfo.InvariantCulture))
                .Distinct()
                .ToArray();
            if (values.Length != 1 || values[0] <= 0d
                || double.IsNaN(values[0]) || double.IsInfinity(values[0]))
            {
                return false;
            }

            phaseSpacing = values[0];
            return true;
        }

        /// <summary>仅初始化时按实体参数复用名称；不在编辑后自动合并用户主动拆分的模型。</summary>
        public IReadOnlyList<BridgeDesignObject> AssignInitialFrameSources(IReadOnlyList<BridgeDesignObject> objects)
        {
            var sources = new List<BridgeDesignObject>();
            var names = new Dictionary<string, string>();
            foreach (var frame in objects.Where(o => o.Kind == DesignObjectKind.Frame).OrderBy(o => o.X)
                .ThenBy(o => o.Y).ThenBy(o => o.Id, StringComparer.Ordinal))
            {
                var source = sources.FirstOrDefault(o => SameFrameModel(o, frame, objects));
                if (source == null)
                {
                    source = frame.WithSourceName("基架" + (sources.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    sources.Add(source);
                }
                names[frame.Id] = source.SourceName;
            }
            return Array.AsReadOnly(objects.Select(o => names.TryGetValue(o.Id, out var sourceName) ? o.WithSourceName(sourceName) : o).ToArray());
        }

        public bool SameFrameModel(BridgeDesignObject a, BridgeDesignObject b, IReadOnlyList<BridgeDesignObject> objects)
        {
            if (!BridgeAngleNotchPlan.Same(a.AngleNotches, b.AngleNotches) ||
                a.UseOffsetInsulators != b.UseOffsetInsulators || a.InsulatorPadCount != b.InsulatorPadCount ||
                Math.Abs(a.InsulatorPadThickness - b.InsulatorPadThickness) > .001 ||
                Math.Abs(a.InsulatorPhaseSpacing - b.InsulatorPhaseSpacing) > .001 ||
                Math.Abs(a.InsulatorAssemblyHeight - b.InsulatorAssemblyHeight) > .001 ||
                a.FrameTemplateKey != b.FrameTemplateKey || new[] { DesignField.Length, DesignField.Width, DesignField.Height, DesignField.InsulatorCount }
                .Any(f => Math.Abs(a.Value(f) - b.Value(f)) >= .01)) return false;
            if (a.SmallOffsetPreview != null || b.SmallOffsetPreview != null)
            {
                var leftOffset = a.SmallOffsetPreview; var rightOffset = b.SmallOffsetPreview;
                if (leftOffset == null || rightOffset == null
                    || leftOffset.InletDirection != rightOffset.InletDirection
                    || leftOffset.OutletDirection != rightOffset.OutletDirection
                    || Math.Abs(leftOffset.InletSize - rightOffset.InletSize) > .001d
                    || Math.Abs(leftOffset.OutletSize - rightOffset.OutletSize) > .001d) return false;
            }
            if (a.WallFramePreview != null || b.WallFramePreview != null)
            {
                if (a.WallFramePreview == null || b.WallFramePreview == null ||
                    Math.Abs(a.WallFramePreview.InletWidth - b.WallFramePreview.InletWidth) >= .01 ||
                    Math.Abs(a.WallFramePreview.InletHeight - b.WallFramePreview.InletHeight) >= .01 ||
                    Math.Abs(a.WallFramePreview.InletOffset - b.WallFramePreview.InletOffset) >= .01 ||
                    Math.Abs(a.InsulatorAssemblyHeight - b.InsulatorAssemblyHeight) >= .01 ||
                    a.InsulatorPadCount != b.InsulatorPadCount || Math.Abs(a.InsulatorPadThickness - b.InsulatorPadThickness) >= .01)
                    return false;
            }
            // 比较局部布置而非总装位置、朝向或实例ID；不同安装位置可以复用同一实体模型。
            var left = objects.Where(o => o.ParentId == a.Id).OrderBy(o => o.Y).ThenBy(o => o.X).ThenBy(o => o.Z).ToArray();
            var right = objects.Where(o => o.ParentId == b.Id).OrderBy(o => o.Y).ThenBy(o => o.X).ThenBy(o => o.Z).ToArray();
            return left.Length == right.Length && left.Select((o, i) => o.Kind == right[i].Kind &&
                o.MountPlan?.Identity == right[i].MountPlan?.Identity && o.MountGroupIndex == right[i].MountGroupIndex &&
                (o.InsulatorGroupId == null ? null : o.InsulatorGroupId.Substring(a.Id.Length)) ==
                (right[i].InsulatorGroupId == null ? null : right[i].InsulatorGroupId.Substring(b.Id.Length)) &&
                new[] { DesignField.X, DesignField.Y, DesignField.Z, DesignField.Length, DesignField.Width, DesignField.Direction }
                    .All(f => Math.Abs(o.Value(f) - right[i].Value(f)) < .01)).All(equal => equal);
        }

        internal IReadOnlyList<BridgeDesignObject> SplitDivergentFrameSources(IReadOnlyList<BridgeDesignObject> objects)
        {
            var names = new HashSet<string>(objects.Where(o => o.Kind == DesignObjectKind.Frame)
                .Select(o => o.SourceName), StringComparer.OrdinalIgnoreCase);
            var assigned = new Dictionary<string, string>(StringComparer.Ordinal);
            var next = 1;
            foreach (var group in objects.Where(o => o.Kind == DesignObjectKind.Frame && !string.IsNullOrWhiteSpace(o.SourceName))
                .GroupBy(o => o.SourceName, StringComparer.OrdinalIgnoreCase))
            {
                var representatives = new List<BridgeDesignObject>();
                foreach (var frame in group.OrderBy(o => o.FramePositionIndex).ThenBy(o => o.Id, StringComparer.Ordinal))
                {
                    var match = representatives.FirstOrDefault(o => SameFrameModel(o, frame, objects));
                    if (match == null)
                    {
                        var name = frame.SourceName;
                        if (representatives.Count > 0)
                        {
                            do { name = "基架" + (next++).ToString(CultureInfo.InvariantCulture); } while (names.Contains(name));
                            names.Add(name);
                        }
                        match = frame.WithSourceName(name);
                        representatives.Add(match);
                    }
                    assigned[frame.Id] = match.SourceName;
                }
            }
            return objects.Select(o => assigned.TryGetValue(o.Id, out var name) && name != o.SourceName ? o.WithSourceName(name) : o).ToArray();
        }

        private void AddRoutes(List<BridgeDesignObject> objects, List<BridgeDesignRoute> routes, double span, double offset, bool wall)
        {
            for (var p = 0; p < 3; p++)
            {
                var phase = new[] { "A", "B", "C" }[p];
                var y = (p - 1) * 200d;
                var points = new[]
                {
                    Create(phase + "-start", (wall ? "A" : "B") + "基座L排断点", DesignObjectKind.FixedJoint, 0, y, 2750,
                        "基座L排分段断点：L排直头接桥排头；距基座边距离：远离基座为正，进入基座为负", phase: phase, editableX: true).WithJointGroup("bridge-start"),
                    Create(phase + "-lap", "搭接断点1", DesignObjectKind.Lap, 300, y + offset * 300 / span, 2750, "前段直排头搭后段桥排头；无独立搭接板", phase: phase).WithJointGroup("bridge-lap-1"),
                    Create(phase + "-break", "搭接断点2", DesignObjectKind.Breakpoint, span * .7, y + offset * .7, 2750, "前段直排头搭后段桥排头；普通段一桥头一直头（L排除外）", phase: phase).WithJointGroup("bridge-break-2"),
                    Create(phase + "-end", wall ? phase + "相墙洞固定接口" : "A基座L排断点", wall ? DesignObjectKind.FixedEnd : DesignObjectKind.FixedJoint,
                        span, y + offset, 2750, wall ? "墙洞固定接口" : "A基座L排断点：铜排直头搭L排直头；距基座边距离：远离基座为正，进入基座为负",
                        phase: phase, editableX: !wall)
                };
                if (!wall) points[3] = points[3].WithJointGroup("bridge-end");
                objects.AddRange(points);
                routes.Add(new BridgeDesignRoute("route-" + phase, phase, points.Select(point => point.Id)));
                AddBaseL(objects, routes, phase, wall ? "a" : "b", 0, y, -1, phase + "-start");
                if (!wall) AddBaseL(objects, routes, phase, "a", span, y + offset, 1, phase + "-end");
            }
        }

        private void AddBaseL(List<BridgeDesignObject> objects, List<BridgeDesignRoute> routes,
            string phase, string side, double jointX, double y, double sign, string bridgeJointId,
            double elevation = 2750d)
        {
            var prefix = phase + "-base-" + side;
            var ids = new List<string>();
            var cabinetJoint = Create(prefix + "-entry", side.ToUpperInvariant() + (sign < 0 ? "基座出柜固定断点" : "基座入柜固定断点"),
                DesignObjectKind.FixedJoint, jointX + sign * 180, y, elevation - 550d,
                "基座底部固定分段边界；L排直头，柜内连接排未展开（不可移动）", phase: phase).WithJointGroup("base-" + side + "-entry");
            objects.Add(cabinetJoint); ids.Add(cabinetJoint.Id);
            // 固定L排：竖腿、圆弯示意、水平直头。圆弯离散点只描述外形，不是可编辑控制点或新断点。
            for (var i = 0; i <= 6; i++)
            {
                var angle = i * Math.PI / 12;
                var point = Create(prefix + "-bend-" + i, phase + "相基座L排（直排头）", DesignObjectKind.FixedRoutePoint,
                    jointX + sign * (150 + 30 * Math.Cos(angle)), y, elevation - 30d + 30d * Math.Sin(angle),
                    "固定L排外形参照（不可编辑；圆弯为示意）", phase: phase);
                objects.Add(point); ids.Add(point.Id);
            }
            ids.Add(bridgeJointId);
            if (sign > 0) ids.Reverse(); // 沿电气路径顺序：左柜上行出桥，右柜下行进柜。
            routes.Add(new BridgeDesignRoute("route-" + prefix + "-L", phase, ids, new[] { ids[0] },
                sign > 0 ? new[] { ids[0] } : Array.Empty<string>()));
            objects.Add(Create(phase + "-fixed-ins-" + side, side.ToUpperInvariant() + "基座侧装绝缘子组",
                DesignObjectKind.FixedInsulator, jointX + sign * 335, y, elevation - 310d,
                "基座内横向安装，支撑L排竖腿（整组固定只读）", 300, 95, phase: phase).WithInsulatorGroup("base-" + side + "/insulators"));
        }

        public BridgeDesignObject CreateInsulator(string frameId, int ordinal, double localY)
        {
            return CreateInsulator(frameId, ordinal, localY,
                BusbarBridgeDraftPreviewSceneFactory.FrameHeightMm,
                0d);
        }

        public BridgeDesignObject CreateInsulator(BridgeDesignObject frame, int ordinal, double localY)
        {
            if (frame?.Kind != DesignObjectKind.Frame) throw new ArgumentException("绝缘子必须有所属基架。");
            var child = CreateInsulator(frame.Id, ordinal, localY, frame.Height, frame.Z);
            var height = frame.InsulatorAssemblyHeight > 0 ? frame.InsulatorAssemblyHeight :
                frame.Height / 2 - BusbarBridgeDraftPreviewSceneFactory.FrameFloorThicknessMm -
                BusbarBridgeDraftPreviewSceneFactory.DemoCopperThicknessMm / 2;
            var values = child.Values.ToDictionary(p => p.Key, p => p.Value);
            values[DesignField.Height] = height;
            var vertical = frame.FrameTemplateKey == "SideToSidePhaseChange029";
            values[DesignField.Z] = vertical ? localY :
                -frame.Height / 2 + BusbarBridgeDraftPreviewSceneFactory.FrameFloorThicknessMm + height / 2;
            if (vertical) values[DesignField.Y] = -frame.Width / 2 +
                BusbarBridgeDraftPreviewSceneFactory.FrameFloorThicknessMm + height / 2;
            return child.WithValues(values);
        }

        private BridgeDesignObject CreateInsulator(string frameId, int ordinal, double localY,
            double frameHeight, double frameCenterZ)
        {
            var floorTop = frameCenterZ - frameHeight / 2d +
                BusbarBridgeDraftPreviewSceneFactory.FrameFloorThicknessMm;
            var height = frameHeight / 2d -
                BusbarBridgeDraftPreviewSceneFactory.FrameFloorThicknessMm -
                BusbarBridgeDraftPreviewSceneFactory.DemoCopperThicknessMm / 2d;
            var centerZ = floorTop + height / 2d - frameCenterZ;
            var assembly = (ordinal - 1) / InsulatorsPerAssembly + 1;
            return Create(frameId + "/ins-" + ordinal, "绝缘子支架装配体" + assembly, DesignObjectKind.Insulator,
                0, localY, centerZ,
                "绝缘子支架装配体（三相3个绝缘子含垫块＋1个支架，整套计1；安装中心距搭接及桥头区域至少200.0 mm，优选组间距1000.0 mm）",
                length: BusbarBridgeDraftPreviewSceneFactory.InsulatorWidthMm, width: BusbarBridgeDraftPreviewSceneFactory.InsulatorWidthMm,
                parentId: frameId, height: height).WithInsulatorGroup(frameId + (assembly == 1 ? "/insulators" : "/insulators-" + assembly));
        }

        public BridgeDesignObject Create(string id, string name, DesignObjectKind kind, double x, double y, double z,
            string role, double length = 40, double width = 40, string parentId = null, string phase = null, double group = 0,
            bool editableX = false, double height = 0)
        {
            // 兼容旧调用签名；group不再进入数据或权限，同源仅由初始化后的基架名称决定。
            var editable = new List<DesignField>();
            if (kind == DesignObjectKind.Breakpoint || kind == DesignObjectKind.Lap ||
                kind == DesignObjectKind.Frame)
                editable.AddRange(new[] { DesignField.X, DesignField.Y, DesignField.Z });
            if (editableX) editable.Add(DesignField.X);
            if (kind == DesignObjectKind.Insulator) editable.AddRange(new[] { DesignField.X, DesignField.Y });
            if (kind == DesignObjectKind.Frame)
            {
                editable.AddRange(new[] { DesignField.Length, DesignField.Direction, DesignField.InsulatorCount });
                if (!role.Contains("小偏移")) editable.Add(DesignField.Installation);
            }
            return new BridgeDesignObject(id, name, kind, new Dictionary<DesignField, double>
            {
                [DesignField.X] = x, [DesignField.Y] = y, [DesignField.Z] = z,
                [DesignField.Length] = length, [DesignField.Width] = width,
                [DesignField.Direction] = 0, [DesignField.InsulatorCount] = kind == DesignObjectKind.Frame ? 1 : 0,
                [DesignField.Height] = height > 0d ? height : kind == DesignObjectKind.Frame ? BusbarBridgeDraftPreviewSceneFactory.FrameHeightMm : 0,
                [DesignField.Installation] = 0
            }, editable, role, parentId, phase);
        }
    }
}
