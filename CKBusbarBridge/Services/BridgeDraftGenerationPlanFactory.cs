using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Drivers;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridge.Services;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Prototype
{
    internal sealed class BridgeDraftGenerationPlanFactory
    {
        internal string ReadMaterial(BridgeUpstreamSnapshot upstream)
        {
            if (upstream == null) throw new ArgumentNullException(nameof(upstream));
            var fields = (upstream.FaceToWall == null ? new[] { "A柜始端", "B柜始端" } : new[] { "柜端始端" }).SelectMany(prefix =>
            {
                var side = upstream.Fields.Where(f => f.Name != null &&
                    f.Name.StartsWith(prefix, StringComparison.Ordinal) &&
                    f.Name.EndsWith(" · 铜排材质", StringComparison.Ordinal)).ToArray();
                if (side.Length == 0)
                    throw new InvalidOperationException("缺少" + prefix + "零件引用配置中的实际铜排材质，请重读上游。");
                return side;
            }).ToArray();
            foreach (var field in fields)
                if (field.IsMissing || (field.Value != SolidWorksMaterialHelper.DefaultBusbarMaterial &&
                    field.Value != SolidWorksMaterialHelper.FullRoundEdgeBusbarMaterial))
                    throw new InvalidOperationException(field.Name + "无效或未读取：" + field.Value + "；来源：" + field.Source);
            var values = fields.Select(f => f.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length != 1)
                throw new InvalidOperationException("A/B柜始端排实际铜排材质不一致：" +
                    string.Join("；", fields.Select(f => f.Name + "=" + f.Value)));
            return values[0];
        }

        internal BridgeGenerationPlan Create(BridgeUpstreamSnapshot upstream, BusbarBridgeDraftEditor editor, bool forceDrivePlan = false, bool actualBaseline = false)
        {
            if (upstream == null || editor?.CurrentSample?.Source.IsModelDerived != true)
                throw new InvalidOperationException("正式生成需要真实总装规划草稿，请重读并规划。");
            var baselineSample = editor.CurrentSample;
            if (!actualBaseline && baselineSample.ReadbackBaseline?.RequiresBridgeRepair == true && !baselineSample.ReadbackBaseline.RepairPlanned)
                throw new InvalidOperationException("实际母线桥未贯通，请先修复贯通并核验预览。");
            if (baselineSample.ReadbackBaseline?.RequiresWallCopperReplan == true)
                throw new InvalidOperationException("柜体接口已变化，请重新规划面对墙铜排后生成；不能接纳旧024桥体作为同步结果。");
            if (!actualBaseline)
            {
                var baseIssues = new BusbarBridgeFrameJointClearance().ValidateBaseJoints(baselineSample, editor.EffectivePlan);
                if (baseIssues.Count > 0)
                    throw new InvalidOperationException("正式生成存在基座接缝避让问题：" + string.Join("；", baseIssues.Select(issue => issue.Message)));
            }
            var revision = editor.Revision;
            var frozenSample = editor.CaptureSavedSample();
            if (!actualBaseline)
            {
                var supportIssues = new BridgeSmallOffsetSupportPlanner().Validate(frozenSample, frozenSample.Objects);
                if (supportIssues.Count > 0)
                    throw new InvalidOperationException("正式生成存在小偏移支撑问题：" + string.Join("；", supportIssues.Select(issue => issue.Message)));
                var checkedGeometry = new BridgeRaisedLapPlanner().Apply(frozenSample, frozenSample.Objects, allowLift: false, keepNotchPlan: true);
                var lapIssues = checkedGeometry.Issues;
                if (lapIssues.Count > 0)
                    throw new InvalidOperationException("正式生成存在搭接/基架净距问题：" + string.Join("；", lapIssues.Select(issue => issue.Message)));
                frozenSample = checkedGeometry.Sample.WithGeometry(
                    new PrototypeBusbarBridgeDesignProvider().SplitDivergentFrameSources(checkedGeometry.Sample.Objects), checkedGeometry.Sample.Routes);
            }
            if (!actualBaseline && upstream.FaceToWall != null)
            {
                var wallEndProblem = new FaceToWallWallEndRule().FindProblem(upstream.FaceToWall, frozenSample);
                if (wallEndProblem != null) throw new InvalidOperationException(wallEndProblem);
            }
            var frozenRecipe = new BridgeGenerationRecipeCodec().Encode(frozenSample, frozenSample.Objects);
            if (editor.Revision != revision) throw new InvalidOperationException("冻结期间草稿发生变化，请重试。");
            if (!forceDrivePlan && baselineSample.ReadbackBaseline != null &&
                !baselineSample.ReadbackBaseline.RequiresBridgeRepair && !baselineSample.ReadbackBaseline.RequiresCabinetSynchronization &&
                (actualBaseline || !editor.HasBlockingIssues) && frozenRecipe == baselineSample.ReadbackBaseline.Recipe)
                return new BridgeGenerationPlan { Upstream=upstream, UpstreamFingerprint=BridgeDraftPreviewSnapshot.UpstreamFingerprint(upstream),
                    Sample=frozenSample, Objects=frozenSample.Objects.ToArray(), Recipe=frozenRecipe,
                    Fingerprint=new BridgeGenerationRecipeCodec().Hash(frozenRecipe), Revision=revision,
                    WallAppliedVerticalClearance=upstream.FaceToWall?.AppliedWallVerticalClearance??0 };
            EnsureCompleteUpstream(upstream);
            if (!actualBaseline && editor.HasBlockingIssues)
                throw new InvalidOperationException("正式生成存在阻断问题：" + string.Join("；",
                    editor.PlanningIssues.Concat(editor.ClearanceIssues).Concat(editor.FrameConnectionIssues).Concat(editor.HangerIssues)
                        .Where(issue => issue.BlocksGeneration).Select(issue => issue.Message).Distinct()));
            var sample = frozenSample;
            var objects = sample.Objects.ToArray();
            var frames = objects.Where(o => o.Kind == DesignObjectKind.Frame).OrderBy(o => o.FramePositionIndex).ToArray();
            if (objects.Any(o => o.Kind == DesignObjectKind.Insulator && !frames.Any(f => f.Id == o.ParentId)))
                throw new InvalidOperationException("有效绝缘子缺少所属基架。");
            if (frames.Length == 0 && sample.ReadbackBaseline == null || frames.Where((f, i) => f.FramePositionIndex != i + 1).Any())
                throw new InvalidOperationException("有效基架位置必须连续且不得为空。");
            if (!actualBaseline && upstream.FaceToWall == null)
                ValidateOffsetStructure(objects, frames, upstream.SideToSide == null, baselineSample.ReadbackBaseline != null);
            var inputs = upstream.FaceToWall == null ? frames.Select(f => FrameInput(f, objects)).ToArray() : null;
            var counts = upstream.FaceToWall != null ? new[] { upstream.FaceToWall.RootCount } : upstream.SideToSide == null
                ? new[] { "A", "B" }.SelectMany(side => new[] { "A", "B", "C" }
                    .Select(phase => upstream.Endpoints.Count(e => e.Side == side && e.Phase == phase))).ToArray()
                : new[] { upstream.SideToSide.Copper.RootCount };
            if (counts.Distinct().Count() != 1 || counts[0] < 1 || counts[0] > 4)
                throw new InvalidOperationException("生成端排的三相根数不一致或不在1～4根范围内。");
            FaceToFaceFrameInsulatorParameters insulators = null;
            var perFrame = new Dictionary<string, FaceToFaceFrameInsulatorParameters>(StringComparer.OrdinalIgnoreCase);
            foreach (var frame in frames.Where(f => objects.Any(o => o.ParentId == f.Id && o.Kind == DesignObjectKind.Insulator)))
            {
                if (frame.InsulatorPadCount != counts[0] - 1 || frame.InsulatorAssemblyHeight <= 25 || frame.InsulatorPhaseSpacing <= 0)
                    throw new InvalidOperationException("基架绝缘子缺少已核验的规格、高度或N−1垫块参数：" + frame.Name);
                // 草稿高度含25mm支架，原驱动入口接收本体高。
                var parameters = new FaceToFaceFrameInsulatorParameters(
                    sample.Source.BusbarWidth.ToString("R", CultureInfo.InvariantCulture) + "*" +
                    sample.Source.BusbarThickness.ToString("R", CultureInfo.InvariantCulture),
                    frame.InsulatorPhaseSpacing, frame.InsulatorAssemblyHeight - 25d, counts[0],
                    frame.FrameTemplateKey == "SideToSidePhaseChange029"
                        ? FrameInsulatorBracketSpanSource.FrameHeight : FrameInsulatorBracketSpanSource.FrameWidth,
                    frame.UseOffsetInsulators ? TransverseOffsets(frame, objects) : null, MountPlans(frame, objects));
                insulators = parameters;
                if (perFrame.TryGetValue(frame.SourceName, out var previous) &&
                    !previous.HasSameParameters(parameters))
                    throw new InvalidOperationException("同源基架的内部绝缘子参数不同，请先独立化：" + frame.SourceName);
                perFrame[frame.SourceName] = parameters;
            }
            var groups = CopperGroups(sample, sample.ReadbackBaseline == null || sample.ReadbackBaseline.RequiresCabinetSynchronization);
            if (upstream.FaceToWall != null && sample.ReadbackBaseline?.RequiresCabinetSynchronization == true)
                new FaceToWallPreviewFrameLayoutPlanner().ValidateFrames(upstream.FaceToWall, frames);
            var recipe = frozenRecipe;
            if (editor.Revision != revision) throw new InvalidOperationException("冻结期间草稿发生变化，请重试。");
            var appliedVertical = upstream.FaceToWall?.AppliedWallVerticalClearance ?? 0;
            var positionPlans = frames.Length==0 ? Array.Empty<FaceToFaceFramePositionPlan>() : upstream.FaceToWall == null ? FaceToFaceFrameDrivePlanner.CreatePlans(inputs) :
                WallPlans(upstream.FaceToWall, frames, objects, appliedVerticalClearance: appliedVertical,actualDimensions:sample.ReadbackBaseline!=null);
            FaceToWallBaseVerticalCompensationPlan baseCompensation = null;
            if (upstream.FaceToWall != null && upstream.FaceToWall.WallInstallation == null && frames.Any(f=>f.FrameTemplateKey=="FaceToWallLarge1"))
            {
                var vertical = frames.Last(f=>f.FrameTemplateKey=="FaceToWallLarge1").Value(DesignField.WallVerticalClearance);
                var actualBase = upstream.FaceToWall.FixedObjects.SingleOrDefault(o => o.Id == "wall-base" && o.Kind == DesignObjectKind.Base);
                if (Math.Abs(vertical - appliedVertical) >= .01)
                {
                    if (actualBase == null) throw new InvalidOperationException("面对墙上下墙距缺少已反读普通基座基准。");
                    baseCompensation = FaceToWallBaseVerticalCompensationPlanner.CreatePlan(actualBase.Height + appliedVertical,
                        upstream.FaceToWall.Cabinet.OpeningHeight + 2 * appliedVertical, vertical);
                }
            }
            foreach (var position in positionPlans)
            {
                if (perFrame.TryGetValue(position.SourceName, out var parameters))
                    position.ModelPlan.InternalInsulators = parameters;
                if (position.ModelPlan.InsulatorCount > 0)
                    (position.ModelPlan.InternalInsulators ?? insulators ??
                        throw new InvalidOperationException("基架缺少绝缘子驱动参数：" + position.SourceName))
                        .CreateDrivePlan(position.ModelPlan);
            }
            ApplyTemplateAngleNotchPlans(positionPlans, frames);
            return new BridgeGenerationPlan
            {
                Upstream = upstream, Revision = revision, Sample = sample, Objects = Array.AsReadOnly(objects),
                UpstreamFingerprint = BridgeDraftPreviewSnapshot.UpstreamFingerprint(upstream),
                Copper = groups, Frames = positionPlans, Insulators = insulators,
                Recipe = recipe, Fingerprint = new BridgeGenerationRecipeCodec().Hash(recipe),
                WallAppliedVerticalClearance = appliedVertical, WallBaseCompensation = baseCompensation
            };
        }

        internal void ApplyTemplateAngleNotchPlans(IReadOnlyList<FaceToFaceFramePositionPlan> positions,
            IReadOnlyList<BridgeDesignObject> frames)
        {
            var states = new Dictionary<string, BridgeAngleNotchPlan>(StringComparer.OrdinalIgnoreCase);
            foreach (var frame in frames.Where(f => f.AngleNotches != null &&
                (f.CornerPreview != null || BusbarBridgeDraftPreviewSceneFactory.HasTemplateAngleNotches(f))))
            {
                if (states.TryGetValue(frame.SourceName, out var previous) && !BridgeAngleNotchPlan.Same(previous, frame.AngleNotches))
                    throw new InvalidOperationException("同源基架的角钢豁口状态不同，请先独立化：" + frame.SourceName);
                states[frame.SourceName] = frame.AngleNotches;
            }
            foreach (var position in positions)
            {
                states.TryGetValue(position.SourceName, out var state);
                position.ModelPlan.AngleNotches = state;
                position.ModelPlan.SuppressTemplateAngleNotches = state?.Suppress;
            }
        }

        internal void EnsureCompleteUpstream(BridgeUpstreamSnapshot upstream)
        {
            if (upstream == null) throw new ArgumentNullException(nameof(upstream));
            var missing = upstream.Fields.Where(field => field.IsMissing).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException("正式生成上游信息缺失：" + string.Join("；",
                    missing.Select(field => field.Name + "（来源：" + field.Source + "）")) + "。请修正后重读。");
        }

        // 只接纳本次明确写入的两个基座尺寸；连接几何、映射和铜排仍须与原快照一致。
        internal void ValidateWallBaseCompensation(BridgeUpstreamSnapshot before, BridgeUpstreamSnapshot current,
            FaceToWallBaseVerticalCompensationPlan target)
        {
            EnsureCompleteUpstream(current);
            var oldWall = before?.FaceToWall;
            var wall = current?.FaceToWall;
            var oldBase = oldWall?.FixedObjects.SingleOrDefault(o => o.Id == "wall-base" && o.Kind == DesignObjectKind.Base);
            var actualBase = wall?.FixedObjects.SingleOrDefault(o => o.Id == "wall-base" && o.Kind == DesignObjectKind.Base);
            if (oldBase == null || actualBase == null || target == null ||
                Math.Abs(actualBase.Height - target.BaseHeight) > .01 ||
                Math.Abs(wall.Cabinet.OpeningHeight - target.BaseOpeningHeight) > .01 ||
                Math.Abs(wall.AppliedWallVerticalClearance - target.VerticalClearance) > .0005 ||
                Math.Abs(actualBase.Length - oldBase.Length) > .01 || Math.Abs(actualBase.Width - oldBase.Width) > .01 ||
                Math.Abs(actualBase.X - oldBase.X) > .2 || Math.Abs(actualBase.Y - oldBase.Y) > .2 ||
                Math.Abs(actualBase.Z - oldBase.Z + (target.VerticalClearance - oldWall.AppliedWallVerticalClearance) / 2) > .2 ||
                wall.RootCount != oldWall.RootCount || wall.Position != oldWall.Position ||
                Math.Abs(wall.ModelClearance - oldWall.ModelClearance) > .0005 ||
                Math.Abs(wall.OverlapLength - oldWall.OverlapLength) > .0005 ||
                Math.Abs(wall.Cabinet.BaseWidth - oldWall.Cabinet.BaseWidth) > .0005 ||
                Math.Abs(wall.Cabinet.InsulatorWithBracketHeight - oldWall.Cabinet.InsulatorWithBracketHeight) > .0005)
                throw new InvalidOperationException("上下墙距普通基座补偿结果与目标尺寸或安装位置不一致。");
            Func<BridgeUpstreamSnapshot, string> unchanged = snapshot => string.Join("\n",
                BridgeDraftPreviewSnapshot.UpstreamFingerprint(snapshot).Split('\n').Where(line =>
                    !line.StartsWith("面对墙基座开孔高度\t", StringComparison.Ordinal) &&
                    !line.StartsWith("wall/parameters=", StringComparison.Ordinal)));
            if (unchanged(before) != unchanged(current))
                throw new InvalidOperationException("上下墙距补偿改变了连接几何、映射或其他上游参数，已拒绝生成。");
        }

        internal IReadOnlyList<FaceToFaceFramePositionPlan> WallPlans(FaceToWallUpstreamGeometry input,
            IReadOnlyList<BridgeDesignObject> frames, IReadOnlyList<BridgeDesignObject> objects,
            IReadOnlyList<string> instances = null, IReadOnlyList<string> sources = null, double? appliedVerticalClearance = null,bool actualDimensions=false)
        {
            var applied = appliedVerticalClearance ?? input?.AppliedWallVerticalClearance ?? 0;
            if (frames.Count == 0) return Array.Empty<FaceToFaceFramePositionPlan>();
            var actual = actualDimensions || frames.All(f => f.ComponentBinding != null);
            if (!actual) new FaceToWallPreviewFrameLayoutPlanner().ValidateFrames(input, frames, applied);
            var owner = frames.Last();
            if (input.WallInstallation != null)
            {
                new FaceToWallPreviewFrameLayoutPlanner().ValidateFrames(input, frames, 0);
                var installationInputs = frames.Select(frame =>
                {
                    var validated = FrameInput(frame, objects);
                    var kind = frame.FrameTemplateKey == "Regular" ? FaceToWallFrameTemplateKind.Regular :
                        frame.FrameTemplateKey == "FaceToWallLarge1" ? FaceToWallFrameTemplateKind.Large1 :
                        frame.FrameTemplateKey == "FaceToWallLarge2" ? FaceToWallFrameTemplateKind.Large2 :
                        throw new InvalidOperationException("面对墙基架模板身份无效：" + frame.Name);
                    var entryDistance = kind == FaceToWallFrameTemplateKind.Large1 ? input.WallInstallation.EntryDistance : 0;
                    var drivenLength = kind == FaceToWallFrameTemplateKind.Large1
                        ? input.TargetWallEnd - input.Cabinet.Opening.X - frames.Take(frames.Count - 1).Sum(f => f.DriveValue(DesignField.Length))
                        : frame.DriveValue(DesignField.Length);
                    // 规划尺寸已经是四边距作用后的驱动值；仅还原旧规划器会再加一次的轴向伸入量。
                    return new FaceToWallFramePlanInput(frame.SourceName, kind == FaceToWallFrameTemplateKind.Large1 ? input.TargetWallWidth : frame.DriveValue(DesignField.Width),
                        drivenLength - entryDistance, kind == FaceToWallFrameTemplateKind.Regular ? frame.DriveValue(DesignField.Height) : input.TargetWallHeight,
                        frame.IsReversed, kind, validated.InsulatorCount, validated.InsulatorPositions, 0, 0, entryDistance);
                }).ToArray();
                return FaceToWallFrameDrivePlanner.CreatePlans(installationInputs, input.Cabinet.BaseWidth,
                    input.EffectiveWallEccentricity, input.Cabinet.OpeningHeight, instances, sources);
            }
            var vertical = owner.Value(DesignField.WallVerticalClearance);
            var horizontal = owner.Value(DesignField.WallHorizontalClearance);
            var entry = owner.Value(DesignField.WallEntryDistance);
            var inputs = frames.Select(frame =>
            {
                var template = frame.FrameTemplateKey == "Regular" ? FaceToWallFrameTemplateKind.Regular :
                    frame.FrameTemplateKey == "FaceToWallLarge1" ? FaceToWallFrameTemplateKind.Large1 :
                    frame.FrameTemplateKey == "FaceToWallLarge2" ? FaceToWallFrameTemplateKind.Large2 :
                    throw new InvalidOperationException("面对墙基架模板身份无效：" + frame.Name);
                // 检查每套三相、支架底面和本体高度。模板仅支持居中的一体三相支架；
                // 未提供可驱动的横向定位尺寸时明确拒绝，不能生成近似布局。
                var validated = FrameInput(frame, objects);
                var large1 = template == FaceToWallFrameTemplateKind.Large1;
                return new FaceToWallFramePlanInput(frame.SourceName, frame.DriveValue(DesignField.Width) + (!actual && large1 ? 2 * horizontal : 0),
                    frame.DriveValue(DesignField.Length) - (!actual && large1 ? entry : 0), frame.DriveValue(DesignField.Height) + (actual ? 0 : 2 * vertical),
                    frame.IsReversed, template, validated.InsulatorCount, validated.InsulatorPositions,
                    large1 ? horizontal : 0, vertical, large1 ? entry : 0);
            }).ToArray();
            return FaceToWallFrameDrivePlanner.CreatePlans(inputs, input.Cabinet.BaseWidth,
                input.WallCabinetEccentricity, input.Cabinet.OpeningHeight - 2 * (vertical - applied), instances, sources);
        }

        internal FaceToFaceFramePlanInput FrameInput(BridgeDesignObject frame, IReadOnlyList<BridgeDesignObject> objects)
        {
            var vertical = frame.FrameTemplateKey == "SideToSidePhaseChange029";
            var groups = objects.Where(o => o.Kind == DesignObjectKind.Insulator && o.ParentId == frame.Id)
                .GroupBy(o => o.InsulatorGroupId).OrderBy(g => g.First().X).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
            if (groups.Length > 3 || Math.Abs(frame.Value(DesignField.InsulatorCount) - groups.Length) > .01)
                throw new InvalidOperationException("基架绝缘子套数与有效对象不一致：" + frame.Name);
            foreach (var group in groups)
            {
                var floorSupport = group.Any(BridgeSmallOffsetSupportPlanner.IsSupport);
                var members = floorSupport ? group.OrderByDescending(o => o.MountGroupIndex).ToArray() :
                    group.OrderBy(o => vertical ? o.Z : o.Y).ToArray();
                if (members.Any(o => o.InsulatorMountProblem != null))
                    throw new InvalidOperationException("换相吊装安装位无效：" + members.First(o => o.InsulatorMountProblem != null).InsulatorMountProblem);
                if (string.IsNullOrWhiteSpace(group.Key) || members.Length != 3)
                    throw new InvalidOperationException("绝缘子套不完整：" + group.Key + "；需要3个相位对象，实际" + members.Length + "个。");
                var centerZ = -(vertical ? frame.Width : frame.Height) / 2 + BusbarBridgeDraftPreviewSceneFactory.FrameFloorThicknessMm +
                    frame.InsulatorAssemblyHeight / 2;
                // 草稿Values统一保留0.1mm，驱动元数据保留原精度。只允许半个量化步长的误差，
                // 加微小浮点余量以兼容全局/局部坐标换算；不改动正式驱动值或实际模型校验容差。
                const double draftRoundingTolerance = .05 + 1e-7;
                var transverseOffset = frame.UseOffsetInsulators ? members.Average(o => o.Y) : 0d;
                for (var i = 0; i < members.Length; i++)
                {
                    var member = members[i];
                    ValidateInsulatorDimension(group.Key, member.Id, "桥向X", member.X, members[0].X, .01);
                    ValidateInsulatorDimension(group.Key, member.Id, "装配高度", member.Height,
                        frame.InsulatorAssemblyHeight, draftRoundingTolerance);
                    ValidateInsulatorDimension(group.Key, member.Id, "本体轴向位置", vertical ? member.Y : member.Z, centerZ, draftRoundingTolerance);
                    ValidateInsulatorDimension(group.Key, member.Id, "三相排列位置", vertical ? member.Z : member.Y,
                        floorSupport ? member.MountPlan.Transverse[member.MountGroupIndex] :
                        (i - 1) * frame.InsulatorPhaseSpacing + transverseOffset, draftRoundingTolerance);
                }
                if (floorSupport && (frame.FrameTemplateKey != "SmallOffset" || members.Count(o => !o.IsSuppressedInsulator) != 1))
                    throw new InvalidOperationException("小偏移搭接支架必须每套只安装一个绝缘子：" + group.Key);
            }
            var positions = groups.Select((g, i) => frame.ComponentBinding?.InsulatorPositions != null &&
                i < frame.ComponentBinding.InsulatorPositions.Length &&
                Math.Abs(g.First().X - BridgeDesignPrecision.OneDecimal(frame.ComponentBinding.InsulatorPositions[i] - frame.DriveValue(DesignField.Length) / 2)) < .000001
                    ? frame.ComponentBinding.InsulatorPositions[i] : g.First().X + frame.DriveValue(DesignField.Length) / 2d).ToArray();
            if (positions.Any(p => p < 0 || p > frame.Length))
                throw new InvalidOperationException("绝缘子定位超出所属基架：" + frame.Name);
            var small = frame.SmallOffsetPreview;
            var smallPlan = small == null ? null : new FaceToFaceSmallOffsetFrameDrivePlan(frame.Width,
                small.InletDirection, small.InletSize, small.OutletDirection, small.OutletSize);
            var corner = frame.FrameTemplateKey == "LargeOffsetCorner";
            if ((frame.FrameTemplateKey == "SmallOffset" && small == null) || (corner && frame.CornerPreview == null))
                throw new InvalidOperationException("特殊基架缺少模板坐标参数：" + frame.Name);
            var coordinates = FrameInstallationCoordinates(frame);
            return new FaceToFaceFramePlanInput(frame.SourceName, frame.DriveValue(DesignField.Width), frame.DriveValue(DesignField.Length), frame.DriveValue(DesignField.Height),
                frame.IsReversed, positions.Length, positions,
                corner ? FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner : FaceToWallFrameTemplateKind.Regular,
                coordinates.Inlet, coordinates.Outlet, smallPlan,
                vertical ? (FrameTemplateIdentity?)FrameTemplateIdentity.SideToSidePhaseChange029 : null);
        }

        internal static (string Inlet, string Outlet) FrameInstallationCoordinates(BridgeDesignObject frame)
        {
            var corner = frame.FrameTemplateKey == "LargeOffsetCorner";
            if (corner && frame.CornerPreview == null)
                throw new InvalidOperationException("大偏移拐角缺少原安装方向：" + frame.Name);
            var coordinate = corner ? (frame.CornerPreview.ClosedSide < 0 ? 1 : 2) : (frame.IsReversed ? 2 : 1);
            var retainedPorts = frame.ComponentBinding != null && (corner || frame.ComponentBinding.DisplayValues != null &&
                frame.ComponentBinding.DisplayValues.TryGetValue(DesignField.Installation, out var installation) &&
                installation == frame.Value(DesignField.Installation));
            return retainedPorts ? (frame.ComponentBinding.Inlet, frame.ComponentBinding.Outlet)
                : ("坐标系-进" + coordinate, "坐标系-出" + coordinate);
        }

        internal IReadOnlyList<BridgeInsulatorMountPlan> MountPlans(BridgeDesignObject frame, IReadOnlyList<BridgeDesignObject> objects)
        {
            var plans = objects.Where(o => o.Kind == DesignObjectKind.Insulator && o.ParentId == frame.Id)
                .GroupBy(o => o.InsulatorGroupId).OrderBy(g => g.First().X).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    if (g.Select(o => o.MountPlan?.Identity).Distinct().Count() != 1 ||
                        g.Any(o => o.MountPlan != null) && g.Select(o => o.MountGroupIndex).Distinct().Count() != 3)
                        throw new InvalidOperationException("换相吊装的组状态不一致：" + g.Key);
                    return g.First().MountPlan;
                }).ToArray();
            return plans.Any(p => p != null) ? plans : null;
        }

        internal double[] TransverseOffsets(BridgeDesignObject frame, IReadOnlyList<BridgeDesignObject> objects)
        {
            var groups = objects.Where(o => o.Kind == DesignObjectKind.Insulator && o.ParentId == frame.Id)
                .GroupBy(o => o.InsulatorGroupId).OrderBy(g => g.First().X).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
            var original = frame.ComponentBinding?.InsulatorTransverseOffsets;
            return groups.Select((g, index) => original != null && original.Length == groups.Length &&
                g.OrderBy(o => o.Y).Select((o, phase) => Math.Abs(o.Y - BridgeDesignPrecision.OneDecimal(
                    original[index] + (phase - 1) * frame.InsulatorPhaseSpacing)) < .000001).All(equal => equal)
                ? original[index] : g.Average(o => o.Y)).ToArray();
        }

        private void ValidateOffsetStructure(IReadOnlyList<BridgeDesignObject> objects, IReadOnlyList<BridgeDesignObject> frames,
            bool faceToFace, bool hasReadback)
        {
            if (frames.Count == 0 || frames.Any(f => f.FrameTemplateKey == "SideToSidePhaseChange029")) return;
            var a = objects.SingleOrDefault(o => o.Kind == DesignObjectKind.Base && o.Id == "base-a");
            var b = objects.SingleOrDefault(o => o.Kind == DesignObjectKind.Base && o.Id == "base-b");
            if (a == null || b == null) throw new InvalidOperationException("偏移场景缺少实际连接的A/B端基座，不能判定结构。");
            var signed = a.Y - b.Y;
            var large = Models.RdTypeMetadata.IsLargeOffset(signed, a.Width, b.Width);
            if (frames.Count(f => f.FrameTemplateKey == "LargeOffsetCorner") != (large ? 2 : 0))
                throw new InvalidOperationException("基架结构与两端连接基座平均宽加200 mm的偏移分界不一致，请同步或重新规划。");
            if (!CKBusbarBridge.DesignGeometry.BridgeOffsetRules.IsWidened(signed)) return;
            var layout = FaceToFaceSmallOffsetFramePlanner.CreateLayout(frames.Count, 0, signed, a.Width, b.Width);
            var tolerance = faceToFace ? BridgeDesignPrecision.LengthComparisonToleranceMm :
                CKBusbarBridge.DesignGeometry.BridgeOffsetRules.LengthTolerance;
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index]; var expected = layout.GetOffsetFrame(index); var actual = frame.SmallOffsetPreview;
                var reverse = faceToFace && index > 0 && index == frames.Count - 1;
                // 旧双正装实例可继续编辑；只有实际反读的正装尾节保留原姿态。
                if (reverse && hasReadback && frame.ComponentBinding?.Inlet == "坐标系-进1" && !frame.IsReversed)
                    reverse = false;
                expected = expected?.InInstallationOrder(reverse);
                if (!(Math.Abs(frame.DriveValue(DesignField.Width) - layout.FrameWidths[index]) <= tolerance)
                    || (expected == null) != (actual == null)
                    || (expected != null && (frame.IsReversed != reverse || actual.InletDirection != expected.InletOffsetDirection
                        || actual.OutletDirection != expected.OutletOffsetDirection
                        || !(Math.Abs(actual.InletSize - expected.InletOffsetSize) <= tolerance)
                        || !(Math.Abs(actual.OutletSize - expected.OutletOffsetSize) <= tolerance))))
                    throw new InvalidOperationException("整段扩宽基架宽度或两端单面封板接口不一致：" + frame.Name);
            }
        }

        private void ValidateInsulatorDimension(string groupId, string memberId, string dimension,
            double actual, double expected, double tolerance)
        {
            // 用正向比较一并拒绝NaN/Infinity，避免无效数值绕过校验。
            if (Math.Abs(actual - expected) <= tolerance) return;
            throw new InvalidOperationException("绝缘子三相定位不一致：" + groupId + "；" + memberId + "的" + dimension +
                "实际=" + actual.ToString("0.###", CultureInfo.InvariantCulture) + " mm，期望=" +
                expected.ToString("0.###", CultureInfo.InvariantCulture) + " mm。");
        }

        internal IReadOnlyList<AlgorithmBusbarGenerationGroup> CopperGroups(BridgeDesignSample sample, bool validateForGeneration = true)
        {
            BridgeCopperManufacturingValidation.RequireValid(sample, validateForGeneration);
            var copper = sample.Routes.Select(r => new { Route = r, Setting = r.CopyNativePreview() }).ToArray();
            if(copper.Length==0 && !validateForGeneration) return Array.Empty<AlgorithmBusbarGenerationGroup>();
            // 同一铣排的三段保留原顺序；A/B端组显式隔离，不能与中间排共享端排文件。
            return copper.GroupBy(p => new { Name = p.Route.GenerationGroup ?? (p.Route.CopperSectionId == null
                    ? ((p.Setting.GroupKey ?? "").EndsWith("A基座L排", StringComparison.Ordinal) ? "A基座L排" :
                       (p.Setting.GroupKey ?? "").EndsWith("B基座L排", StringComparison.Ordinal) ? "B基座L排" :
                       throw new InvalidOperationException("端排缺少明确生成归属：" + p.Route.Id))
                    : "中间排"), p.Route.IsBridge })
                .Select(g => new AlgorithmBusbarGenerationGroup
                {
                    DisplayName = g.Key.Name, IsBridge = g.Key.IsBridge, RestrictSourceReuseToCurrentGroup = g.Key.Name != "中间排",
                    Busbars = Array.AsReadOnly(g.Select(p => p.Setting.Copy()).ToArray())
                }).ToArray();
        }

        internal double[][] InterfacePoints(BridgeDesignObject frame, BridgeCoordinateFrame coordinates)
        {
            if (coordinates == null) throw new ArgumentNullException(nameof(coordinates));
            return new BusbarBridgeDraftPreviewSceneFactory().FrameInterfacePoints(frame).Select(p => coordinates.ToAssembly(p)).ToArray();
        }

        // 面对面兼容入口；完整姿态转换使用上方坐标契约。
        // 抽取转发壳：模板接口正本为 BusbarBridgeDraftPreviewSceneFactory.FacingFrameInterfacePoints。
        internal double[][] InterfacePoints(BridgeDesignObject frame, double originX, double originZ)
        {
            return new BusbarBridgeDraftPreviewSceneFactory().FacingFrameInterfacePoints(frame)
                .Select(p => new[] { originX + p[0], p[1], originZ + p[2] }).ToArray();
        }
    }
}
