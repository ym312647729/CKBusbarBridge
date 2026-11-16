using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CKBusbarBridgePreview;
using GeometRi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.Services;
using CKBusbarBridge.Models;

namespace CKBusbarBridge.Prototype
{
    // 仅保存生成数据；反读先核对实际模型，再恢复编辑参数，不能拿JSON冒充模型验收。
    internal sealed class BridgeGenerationRecipeCodec
    {
        internal sealed class WallPlanningData
        {
            public bool IsPhaseSwitched { get; set; }
            public double? SpecialStartX { get; set; }
            public double? MinimumSpecialSpan { get; set; }
            public LinkageRouteWorkerRequestDto Request { get; set; }
            public LinkageRouteWorkerSolutionDto Solution { get; set; }
            public AlgorithmBusbarGenerationGroup[] Groups { get; set; }
        }
        internal string EncodeWallPlanning(LinkageRouteWorkerRequestDto request, LinkageRouteWorkerSolutionDto solution,
            IReadOnlyList<AlgorithmBusbarGenerationGroup> groups, double? specialStartX = null, bool isPhaseSwitched = false)
            => new BridgeWallPlanningCodec().EncodeWallPlanning(request, solution, groups, specialStartX,
                isPhaseSwitched: isPhaseSwitched);
        internal WallPlanningData DecodeWallPlanning(string json)
        {
            var stored = new BridgeWallPlanningCodec().DecodeWallPlanning(json);
            return new WallPlanningData { Request = stored.Request, Solution = stored.Solution, Groups = stored.Groups,
                SpecialStartX = stored.SpecialStartX, IsPhaseSwitched = stored.IsPhaseSwitched,
                MinimumSpecialSpan = stored.MinimumSpecialSpan };
        }
        internal BridgeDesignSample CreateWallBaseline(string json, BridgeUpstreamSnapshot upstream)
        {
            var data = JsonConvert.DeserializeObject<Recipe>(json, Settings());
            if (data?.Schema != 3 || data.Scenario != "FaceToWall" || data.WallPlanning == null || data.Objects == null)
                throw new InvalidOperationException("面对墙正式生成记录版本或方案数据缺失。");
            return new PrototypeBusbarBridgeDesignProvider().RestoreFaceToWallPlanning(upstream, data.WallPlanning,
                data.Objects.Select(Object).Where(o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator).ToArray());
        }
        private JsonSerializerSettings Settings() => BridgeWallPlanningCodec.CreateSettings();

        internal IReadOnlyList<BridgeDesignObject> ReadSavedObjects(string recipe)
        {
            var data = JsonConvert.DeserializeObject<Recipe>(recipe, Settings());
            if (data?.Objects == null) throw new InvalidOperationException("正式记录缺少桥体对象。");
            return data.Objects.Select(Object).ToArray();
        }

        internal BridgeDesignSample RestoreStored(string recipe, BridgeUpstreamSnapshot upstream)
        {
            var data = JsonConvert.DeserializeObject<Recipe>(recipe, Settings());
            if (data == null || data.Schema < 1 || data.Schema > 3 || data.Objects == null || data.Routes == null || data.EditRoutes == null)
                throw new InvalidOperationException("生成记录缺少可恢复的业务对象或原生路线。");
            if (data.Scenario == "FaceToWall") return Decode(recipe, CreateWallBaseline(recipe, upstream));
            var coordinates = Coordinates(data);
            var native = data.Routes.FirstOrDefault(r => r.Native != null)?.Native;
            if (native == null && (data.CopperWidth<=0 || data.CopperThickness<=0)) throw new InvalidOperationException("生成记录缺少铜排规格基准。");
            var context = new PrototypeBusbarBridgeDesignProvider().CreateStoredContext(upstream, coordinates,
                data.Schema == 1 ? "FaceToFace" : data.Scenario, native?.BusBarWidth??data.CopperWidth, native?.BusBarThickness??data.CopperThickness);
            // 仅在用户明确增删断点时创建规划器，打开窗口不运行规划。
            context = context.WithCopperEditing(context.CopperEditRoutes, proposed=>
            {
                var result=new PrototypeBusbarBridgeDesignProvider().TryCreatePlannedPreview(upstream,out var planned);
                if(!result.Success || planned.ReplanCopper==null) throw new InvalidOperationException("无法为断点编辑创建铜排规划："+result.Message);
                return planned.ReplanCopper(proposed);
            });
            return Decode(recipe, context);
        }

        internal string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }

        internal string CopperFingerprint(string recipe, string material)
        {
            var data = JObject.Parse(recipe);
            if (!(data["Routes"] is JArray routes) ||
                routes.Any(route => route["Native"] == null || route["Native"].Type == JTokenType.Null) ||
                string.IsNullOrWhiteSpace(material))
                throw new InvalidOperationException("缺少可核验的原生铜排计划或材质，不能复用已有铜排。");
            // 只纳入冻结的铜排与坐标输入；支撑编辑和Worker搜索信息不应使相同铜排失效。
            var input = new JObject
            {
                ["Scenario"] = data["Scenario"] ?? new JValue("FaceToFace"),
                ["CoordinateData"] = data["CoordinateData"],
                ["OriginX"] = data["OriginX"], ["OriginZ"] = data["OriginZ"],
                ["Overlap"] = data["Overlap"], ["Routes"] = routes, ["Material"] = material
            };
            return Hash(Canonical(input).ToString(Formatting.None));
        }

        internal BridgeSecondaryDrivePlan SecondaryPlan(BridgeGenerationPlan plan, string material,
            string previousRecipe, string previousMaterial, string previousUpstream,
            IReadOnlyDictionary<string, string> previousFrames)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var position in plan.Frames)
            {
                var fingerprint = FrameFingerprint(position.ModelPlan, plan.Insulators);
                var notchStates = (plan.Objects ?? Array.Empty<BridgeDesignObject>()).Where(o => o.Kind == DesignObjectKind.Frame &&
                    string.Equals(o.SourceName, position.SourceName, StringComparison.OrdinalIgnoreCase) && o.SuppressTemplateAngleNotches.HasValue)
                    .Select(o => o.SuppressTemplateAngleNotches.Value).Distinct().ToArray();
                if (notchStates.Length > 1) throw new InvalidOperationException("同源基架的角钢豁口状态不同，请先独立化：" + position.SourceName);
                if (notchStates.Length == 1) fingerprint = Hash(fingerprint + (notchStates[0] ? "|NoTemplateNotch" : "|TemplateNotch22"));
                var notch = position.ModelPlan.AngleNotches;
                if (notch?.DepthMm != null || notch?.OuterDepthMm != null)
                    fingerprint = Hash(fingerprint + FormattableString.Invariant($"|NotchDepth={notch.DepthMm:R}|OuterNotchDepth={notch.OuterDepthMm:R}"));
                if (notch?.InstanceDepthsMm != null)
                    fingerprint = Hash(fingerprint + "|InstanceNotchDepths=" + string.Join(",",
                        notch.InstanceDepthsMm.Select(depth => depth.ToString("R", CultureInfo.InvariantCulture))));
                if (current.TryGetValue(position.SourceName, out var shared) && shared != fingerprint)
                    throw new InvalidOperationException("同源基架的完整物理参数不同，请先独立化：" + position.SourceName);
                current[position.SourceName] = fingerprint;
            }
            var copper = CopperFingerprint(plan.Recipe, material);
            return new BridgeSecondaryDrivePlan
            {
                CopperFingerprint = copper,
                GenerateCopper = string.IsNullOrWhiteSpace(previousRecipe) ||
                    !BridgeDraftPreviewSnapshot.UpstreamFingerprintsMatch(previousUpstream, plan.UpstreamFingerprint) ||
                    copper != CopperFingerprint(previousRecipe, previousMaterial),
                FrameFingerprints = current,
                DirtyFrameSources = current.Where(pair => previousFrames == null ||
                    !previousFrames.TryGetValue(pair.Key, out var old) || old != pair.Value)
                    .Select(pair => pair.Key).OrderBy(source => source, StringComparer.Ordinal).ToArray()
            };
        }

        private string FrameFingerprint(FaceToFaceFrameModelPlan frame, FaceToFaceFrameInsulatorParameters common)
        {
            var input = new JObject
            {
                ["Template"] = (int)frame.TemplateIdentity, ["Kind"] = (int)frame.FrameTemplateKind,
                ["Width"] = frame.Width, ["Height"] = frame.Height, ["Length"] = frame.Length,
                ["Positions"] = JArray.FromObject(frame.InsulatorPositions),
                ["Variables"] = JObject.FromObject(frame.GlobalVariables),
                ["WallEntryDistance"] = frame.WallEntryDistance
            };
            if (frame.InsulatorCount > 0)
            {
                var parameters = frame.InternalInsulators ?? common ??
                    throw new InvalidOperationException("缺少架内绝缘子完整驱动参数：" + frame.SourceName);
                var drive = parameters.CreateDrivePlan(frame);
                input["Support"] = new JObject
                {
                    ["Spec"] = drive.BusbarSpec, ["Thickness"] = drive.BusbarThickness,
                    ["PhaseSpacing"] = drive.PhaseSpacing, ["BodyHeight"] = drive.InsulatorHeight,
                    ["Roots"] = drive.SinglePhaseRootCount, ["LBracket"] = drive.UseLBracketTemplate,
                    ["SpanSource"] = (int)parameters.BracketSpanSource
                };
                if (drive.UseOffsetTemplate)
                {
                    var support = (JObject)input["Support"];
                    support["OffsetTemplate"] = true;
                    support["TransverseOffsets"] = JArray.FromObject(drive.TransverseOffsets);
                }
                if (drive.Mounts != null) ((JObject)input["Support"])["Mounts"] = JArray.FromObject(drive.Mounts);
            }
            return Hash(Canonical(input).ToString(Formatting.None));
        }

        private JToken Canonical(JToken token)
        {
            if (token is JObject obj)
                return new JObject(obj.Properties().OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => new JProperty(property.Name, Canonical(property.Value))));
            if (token is JArray array) return new JArray(array.Select(Canonical));
            return token.DeepClone();
        }

        internal string Encode(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, IEnumerable<string> manualIds = null,
            IEnumerable<string> lockedIds = null)
            => EncodeCore(sample, objects, manualIds, lockedIds, CaptureEditingForObjects(sample, objects));

        private BridgeFrameLengthEditing CaptureEditingForObjects(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects)
        {
            var editing = sample.SavedFrameLengthEditing ?? new BridgeFrameLengthEditing(sample.FrameLengthBaseline,
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
            var owner = sample.ScenarioKey == "FaceToWall" ? sample.Objects.FirstOrDefault(o => o.FrameTemplateKey == "FaceToWallLarge1") : null;
            var current = owner == null ? null : objects.FirstOrDefault(o => o.Id == owner.Id);
            if (current == null) return editing;
            var vertical = current.Value(DesignField.WallVerticalClearance) - owner.Value(DesignField.WallVerticalClearance);
            var entry = current.Value(DesignField.WallEntryDistance) - owner.Value(DesignField.WallEntryDistance);
            var horizontal = current.Value(DesignField.WallHorizontalClearance) - owner.Value(DesignField.WallHorizontalClearance);
            var baseline = editing.BaselineFrames.Select(frame =>
            {
                var values = frame.Values.ToDictionary(p => p.Key, p => p.Value); values[DesignField.Height] = frame.Height - 2 * vertical;
                if (frame.Id == owner.Id)
                {
                    values[DesignField.X] = frame.X + entry / 2; values[DesignField.Length] = frame.Length + entry;
                    values[DesignField.Width] = frame.Width - 2 * horizontal;
                }
                var adjusted = frame.WithValues(values);
                return frame.WallFramePreview == null || Math.Abs(vertical) < .000001 ? adjusted : adjusted.WithWallFramePreview(
                    new BridgeWallFramePreview(frame.WallFramePreview.InletWidth, frame.WallFramePreview.InletHeight - 2 * vertical, frame.WallFramePreview.InletOffset));
            });
            return new BridgeFrameLengthEditing(baseline, editing.IndependentFrames, editing.FixedEndFrames, editing.LengthAdjustedFrames, editing.OffsetCopperMinimumLengthMm);
        }

        internal string Encode(BusbarBridgeDraftEditor editor)
        {
            var sample = editor.CaptureSavedSample();
            return EncodeCore(sample, sample.Objects, null, null, sample.SavedFrameLengthEditing);
        }

        private string EncodeCore(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, IEnumerable<string> manualIds,
            IEnumerable<string> lockedIds, BridgeFrameLengthEditing editing)
        {
            var ids = new HashSet<string>(objects.Where(o => o.Kind == DesignObjectKind.Frame).Select(o => o.Id));
            var data = new Recipe
            {
                Schema = sample.ScenarioKey == "FaceToWall" ? 3 : 2, WallPlanning = sample.WallPlanningRecipe,
                OriginX = sample.PreviewOriginX.Value, OriginZ = sample.PreviewOriginZ.Value,
                Scenario = sample.ScenarioKey,
                HangerLayout = sample.SavedHangers,
                CopperWidth=sample.Source.BusbarWidth, CopperThickness=sample.Source.BusbarThickness,
                CoordinateData = CoordinateData(sample.Coordinates ?? BridgeCoordinateFrame.Facing(sample.PreviewOriginX.Value, sample.PreviewOriginZ.Value)),
                Overlap = sample.Parameters.OverlapLengthMm,
                InsulationClearance = Math.Abs(sample.Parameters.InsulationClearanceMm - 125d) <= 1e-8d
                    ? (double?)null : sample.Parameters.InsulationClearanceMm,
                SpecialCopperSectionIds = sample.SpecialCopperSectionIds?.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                OffsetCopperSafetyShift = Math.Abs(sample.OffsetCopperSafetyShift) < .000001 ? (double?)null : sample.OffsetCopperSafetyShift,
                CopperLengthBaseline = sample.CopperLengthBaseline?.Select(StoreObject).ToArray(),
                ManualInsulators = (manualIds ?? sample.SavedManualInsulatorIds).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                ManualJoints = sample.SavedManualJointIds.ToArray(),
                LockedObjects = (lockedIds ?? sample.SavedLockedObjectIds).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                FrameLengthEditing = new FrameEditingData { BaselineFrames = editing.BaselineFrames.Select(StoreObject).ToArray(),
                    OffsetCopperMinimumLengthMm = editing.OffsetCopperMinimumLengthMm,
                    IndependentFrames = editing.IndependentFrames.Where(ids.Contains).ToArray(),
                    FixedEndFrames = editing.FixedEndFrames.Where(id => ids.Contains(id) && editing.IndependentFrames.Contains(id)).ToArray(),
                    LengthAdjustedFrames = editing.LengthAdjustedFrames.Where(ids.Contains).ToArray() },
                Objects = objects.Where(o => !IsUpstream(o)).OrderBy(o => o.Id, StringComparer.Ordinal).Select(StoreObject).ToArray(),
                Routes = sample.Routes.Select(Route).ToArray(), EditRoutes = sample.CopperEditRoutes.Select(Route).ToArray()
            };
            return JsonConvert.SerializeObject(data, Settings());
        }

        private ObjectData StoreObject(BridgeDesignObject o) => new ObjectData
        {
            Id = o.Id, Name = o.Name, Kind = o.Kind, Role = o.Role, Parent = o.ParentId, Phase = o.Phase, Binding = o.ComponentBinding,
            Template = o.ExplicitFrameTemplateKey, Values = o.Values.OrderBy(v => v.Key).ToDictionary(v => v.Key, v => v.Value),
            Editable = o.EditableFields.ToArray(), Joint = o.JointGroupId, Insulator = o.InsulatorGroupId,
            Position = o.FramePositionIndex, PadCount = o.InsulatorPadCount, PadThickness = o.InsulatorPadThickness,
            PhaseSpacing = o.InsulatorPhaseSpacing, AssemblyHeight = o.InsulatorAssemblyHeight, Automatic = o.HasAutomaticInsulatorPlacement,
            OffsetInsulators = o.UseOffsetInsulators, SuppressTemplateAngleNotches = o.SuppressTemplateAngleNotches,
            AngleNotchDepthMm = o.AngleNotches?.DepthMm, OuterAngleNotchDepthMm = o.AngleNotches?.OuterDepthMm,
            InstanceNotchDepthsMm = o.AngleNotches?.InstanceDepthsMm?.ToArray(),
            CornerMinimumSafeLengthMm = o.CornerPreview?.MinimumSafeLengthMm,
            Mount = o.MountPlan, MountGroup = o.MountGroupIndex,
            Wall = o.WallFramePreview == null ? null : new[] { o.WallFramePreview.InletWidth, o.WallFramePreview.InletHeight, o.WallFramePreview.InletOffset },
            Small = o.SmallOffsetPreview == null ? null : new[] { (double)o.SmallOffsetPreview.InletDirection, o.SmallOffsetPreview.InletSize,
                o.SmallOffsetPreview.OutletDirection, o.SmallOffsetPreview.OutletSize },
            Corner = o.CornerPreview == null ? null : new[] { o.CornerPreview.ClosedEnd, o.CornerPreview.ClosedSide }
        };

        private BridgeFrameLengthEditing RestoreFrameEditing(FrameEditingData data, IReadOnlyList<BridgeDesignObject> objects)
        {
            if (data == null) return null; // 旧记录没有编辑状态，沿用原有基线语义。
            var ids = new HashSet<string>(objects.Where(o => o.Kind == DesignObjectKind.Frame).Select(o => o.Id));
            if (data.BaselineFrames == null || data.IndependentFrames == null || data.FixedEndFrames == null || data.LengthAdjustedFrames == null ||
                ids.Count > 0 && data.BaselineFrames.Length == 0 || data.BaselineFrames.Any(o => o == null || o.Kind != DesignObjectKind.Frame || o.Values == null || o.Editable == null) ||
                data.BaselineFrames.Select(o => o.Id).Distinct().Count() != data.BaselineFrames.Length ||
                data.IndependentFrames.Concat(data.LengthAdjustedFrames).Any(id => !ids.Contains(id)) ||
                data.FixedEndFrames.Any(id => !data.IndependentFrames.Contains(id)))
                throw new InvalidOperationException("生成记录的基架长度基线或固定端编辑状态不完整，不能恢复。");
            var baseline = data.BaselineFrames.Select(Object).ToArray();
            if (baseline.Any(o => new[] { o.X, o.Y, o.Z, o.Length, o.Width, o.Height, o.Direction }.Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                o.Length <= 0 || o.Width <= 0 || o.Height <= 0))
                throw new InvalidOperationException("生成记录的基架长度基线尺寸无效，不能恢复。");
            return new BridgeFrameLengthEditing(baseline, data.IndependentFrames, data.FixedEndFrames, data.LengthAdjustedFrames, data.OffsetCopperMinimumLengthMm);
        }

        internal BridgeDesignSample Decode(string json, BridgeDesignSample fresh)
        {
            var data = JsonConvert.DeserializeObject<Recipe>(json, Settings());
            if (data == null || (data.Schema != 1 && data.Schema != 2 && data.Schema != 3) || data.Objects == null || data.Routes == null || data.EditRoutes == null ||
                Math.Abs(data.OriginX - fresh.PreviewOriginX.Value) > .01 || Math.Abs(data.OriginZ - fresh.PreviewOriginZ.Value) > .01)
                throw new InvalidOperationException("正式模型生成记录版本或坐标基准不匹配。");
            var coordinates = Coordinates(data);
            if ((data.Schema == 1 ? "FaceToFace" : data.Scenario) != fresh.ScenarioKey ||
                !coordinates.Matches(fresh.Coordinates ?? BridgeCoordinateFrame.Facing(fresh.PreviewOriginX.Value, fresh.PreviewOriginZ.Value)))
                throw new InvalidOperationException("正式模型生成记录的场景或完整坐标基准不匹配。");
            var objects = fresh.Objects.Where(IsUpstream).Concat(data.Objects.Select(Object)).ToArray();
            var frameEditing = RestoreFrameEditing(data.FrameLengthEditing, objects);
            var restored = new BridgeDesignSample(fresh.Id, fresh.Source, objects,
                data.Routes.Select(r => RestoreRoute(r, data)), Array.Empty<BridgeDesignIssue>(),
                    new BridgeDesignParameters(data.Overlap, data.InsulationClearance ?? fresh.Parameters.InsulationClearanceMm,
                        preserveOverlapPrecision: fresh.IsSideToSide))
                .WithCopperEditing(data.EditRoutes.Select(r => RestoreRoute(r, data)), fresh.ReplanCopper)
                .WithSpecialCopperSectionIds(data.SpecialCopperSectionIds ?? fresh.SpecialCopperSectionIds)
                .WithBaseJointConstraints(fresh.BaseJointConstraints).WithCoordinates(coordinates, fresh.ScenarioKey)
                .WithWallPlanningRecipe(data.WallPlanning)
                .WithSavedManualInsulators(data.ManualInsulators ?? Array.Empty<string>())
                .WithSavedManualJoints(data.ManualJoints ?? Array.Empty<string>())
                .WithSavedLocks(data.LockedObjects ?? Array.Empty<string>()).WithFrameLengthEditing(frameEditing)
                .WithCopperLengthBaseline(data.CopperLengthBaseline == null ? fresh.CopperLengthBaseline : data.CopperLengthBaseline.Select(Object))
                .WithOffsetCopperSafetyShift(data.OffsetCopperSafetyShift ?? 0d)
                .WithHangers(data.HangerLayout)
                .WithHangerRoof(fresh.HangerRoof, fresh.HangerRoofProblem);
            return PreserveSupportReplanning(restored);
        }

        internal BridgeDesignSample PreserveSupportReplanning(BridgeDesignSample saved)
        {
            if (saved.ReplanCopper == null) return saved;
            var planner = saved.ReplanCopper;
            var objects = saved.Objects;
            var frameEditing = saved.SavedFrameLengthEditing;
            // 对比规划器前后结果，只替换受断点编辑影响的支撑；实物尺寸和端部缺口仍由反读基线保留。
            BridgeDesignSample reference = null;
            Func<IReadOnlyList<BridgeDesignObject>, BridgeDesignSample> replan = current =>
                {
                    reference = reference ?? planner(objects);
                    var next = planner(current);
                    var beforeFrames = reference.Objects.Where(o => o.Kind == DesignObjectKind.Frame).ToDictionary(o => o.Id);
                    var afterFrames = next.Objects.Where(o => o.Kind == DesignObjectKind.Frame).ToDictionary(o => o.Id);
                    var fields = new[] { DesignField.X, DesignField.Y, DesignField.Z, DesignField.Length,
                        DesignField.Width, DesignField.Height, DesignField.Direction };
                    var changed = new HashSet<string>(beforeFrames.Keys.Union(afterFrames.Keys).Where(id =>
                        !beforeFrames.ContainsKey(id) || !afterFrames.ContainsKey(id) ||
                        beforeFrames[id].FrameTemplateKey != afterFrames[id].FrameTemplateKey ||
                        fields.Any(f => Math.Abs(beforeFrames[id].Value(f) - afterFrames[id].Value(f)) >= .01)));
                    Func<BridgeDesignObject, string> owner = o => o.Kind == DesignObjectKind.Frame ? o.Id : o.ParentId;
                    Func<BridgeDesignObject, bool> support = o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator;
                    var merged = next.Objects.Where(o => !support(o) || changed.Contains(owner(o)))
                        .Concat(objects.Where(o => support(o) && !changed.Contains(owner(o)))).ToArray();
                    var result = new BridgeDesignSample(next.Id, next.Source, merged, next.Routes, next.Issues, next.Parameters)
                        .WithCopperEditing(next.CopperEditRoutes, next.ReplanCopper).WithBaseJointConstraints(next.BaseJointConstraints)
                        .WithSpecialCopperSectionIds(next.SpecialCopperSectionIds)
                        .WithCoordinates(saved.Coordinates, saved.ScenarioKey)
                        .WithWallPlanningRecipe(saved.WallPlanningRecipe)
                        .WithReadbackBaseline(saved.ReadbackBaseline)
                        .WithOffsetCopperSafetyShift(next.OffsetCopperSafetyShift)
                        .WithCopperLengthBaseline(saved.CopperLengthBaseline)
                        .WithSavedLocks(saved.SavedLockedObjectIds.Where(id => merged.Any(o => o.Id == id)))
                        .WithSavedManualJoints(saved.SavedManualJointIds)
                        .WithSavedManualInsulators(saved.SavedManualInsulatorIds.Where(id =>
                            merged.Any(o => o.Id == id && !changed.Contains(o.ParentId))))
                        .WithFrameLengthEditing(frameEditing == null ? null : new BridgeFrameLengthEditing(
                            frameEditing.BaselineFrames.Where(o => !changed.Contains(o.Id))
                                .Concat(next.FrameLengthBaseline.Where(o => changed.Contains(o.Id))),
                            frameEditing.IndependentFrames, frameEditing.FixedEndFrames, frameEditing.LengthAdjustedFrames, frameEditing.OffsetCopperMinimumLengthMm).RetainFor(merged));
                    // 物理路径节点会在编辑后重建；后续修改必须从本次铜排结果继续，不能复用旧路线入口。
                    return PreserveSupportReplanning(result);
                };
            return saved.WithCopperEditing(saved.CopperEditRoutes, replan);
        }

        private bool IsUpstream(BridgeDesignObject o) => o.Kind == DesignObjectKind.Cabinet || o.Kind == DesignObjectKind.Base ||
            o.Kind == DesignObjectKind.FixedBusbar || o.Kind == DesignObjectKind.FixedInsulator ||
            o.Kind == DesignObjectKind.Wall || o.Kind == DesignObjectKind.WallBushing;

        internal IReadOnlyList<AlgorithmBusbarGenerationGroup> ReadCopperGroups(string json)
        {
            var data = JsonConvert.DeserializeObject<Recipe>(json, Settings());
            if (data?.Routes == null) throw new InvalidOperationException("旧方案缺少铜排计划。");
            var objects = data.Objects.Select(Object).ToArray();
            var sample = new BridgeDesignSample("registered-copper", new BridgeDesignSource("已登记方案", "", 1, 1),
                objects, data.Routes.Select(r => RestoreRoute(r, data)), Array.Empty<BridgeDesignIssue>(),
                new BridgeDesignParameters(data.Overlap, data.InsulationClearance ?? 125d)).WithCoordinates(Coordinates(data), data.Scenario ?? "FaceToFace");
            return new BridgeDraftGenerationPlanFactory().CopperGroups(sample);
        }

        private RouteData Route(BridgeDesignRoute route) => new RouteData
        {
            Id = route.Id, Phase = route.Phase, Points = route.PointIds.ToArray(), L = route.LSegmentStartIds.ToArray(),
            Straight = route.StraightLapStartIds.ToArray(), Native = route.CopyNativePreview(), Section = route.CopperSectionId,
            GenerationGroup = route.GenerationGroup, IsBridge = route.IsBridge, Binding = route.ComponentBinding,
            RaisedLapBaselineNative = route.CopyRaisedLapBaselineNative(),
            RaisedLapBaselinePointIds = route.HasRaisedLapBaseline ? route.RaisedLapBaselinePointIds.ToArray() : null
        };

        private BridgeDesignRoute RestoreRoute(RouteData route, Recipe data)
        {
            var result = new BridgeDesignRoute(route.Id, route.Phase, route.Points, route.L, route.Straight);
            if (route.Native != null)
            {
                var coordinates = Coordinates(data);
                var local = coordinates.ToPlanning(route.Native);
                result = result.WithPhysicalGeometry(local.BusBarWidth, local.BusBarThickness,
                    FaceToFaceModelBusbarPlanner.CreateFixedInterfacePreviewSolids(local)
                        .Select(s => s.Select(p => new[] { p[0], p[2], p[1] }).ToArray()))
                    .WithNativePreview(local, coordinates);
                if (route.GenerationGroup != null) result = result.WithGenerationGroup(route.GenerationGroup, route.IsBridge);
                if (route.Section != null)
                {
                    var indexText = route.Section.Substring(route.Section.LastIndexOf('-') + 1);
                    if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var sectionIndex) ||
                        sectionIndex < 1 || BridgeCopperSection.IdFor(sectionIndex) != route.Section)
                        throw new InvalidOperationException("生成记录的铜排分件标识无效：" + route.Section);
                    result = result.WithCopperSection(sectionIndex);
                }
                if (route.Binding != null) result = result.WithComponentBinding(route.Binding);
                if (route.RaisedLapBaselineNative != null)
                    result = result.WithRaisedLapBaseline(route.RaisedLapBaselineNative,
                        route.RaisedLapBaselinePointIds ?? throw new InvalidOperationException("抬高搭接记录缺少原始加工点身份。"));
                else if (route.RaisedLapBaselinePointIds != null)
                    throw new InvalidOperationException("抬高搭接记录缺少原始加工参数。");
            }
            return result;
        }

        private BridgeDesignObject Object(ObjectData o)
        {
            var result = new BridgeDesignObject(o.Id, o.Name, o.Kind, o.Values, o.Editable, o.Role, o.Parent, o.Phase);
            if (o.Binding != null) result = result.WithComponentBinding(o.Binding);
            if (o.Joint != null) result = result.WithJointGroup(o.Joint);
            if (o.Insulator != null) result = result.WithInsulatorGroup(o.Insulator);
            if (o.Mount != null) result = result.WithMountPlan(o.Mount, o.MountGroup);
            if (o.Position > 0) result = result.WithFramePosition(o.Position);
            if (o.Kind == DesignObjectKind.Frame)
            {
                if (o.Template != null) result = result.WithFrameTemplate(o.Template);
                if (o.InstanceNotchDepthsMm != null)
                    result = result.WithAngleNotches(new BridgeAngleNotchPlan(o.InstanceNotchDepthsMm));
                else if (o.SuppressTemplateAngleNotches.HasValue)
                    result = result.WithAngleNotches(new BridgeAngleNotchPlan(o.SuppressTemplateAngleNotches.Value,
                        o.AngleNotchDepthMm, o.OuterAngleNotchDepthMm));
                if (o.PadThickness > 0 && o.PhaseSpacing > 0)
                    result = result.WithInsulatorStack(o.PadCount, o.PadThickness, o.PhaseSpacing, o.AssemblyHeight);
                if (o.OffsetInsulators) result = result.WithOffsetInsulators();
                if (o.Automatic) result = result.WithAutomaticInsulatorPlacement();
                if (o.Small != null) result = result.WithSmallOffsetPreview(new BridgeSmallOffsetPreview((int)o.Small[0], o.Small[1], (int)o.Small[2], o.Small[3]));
                if (o.Corner != null) result = result.WithCornerPreview(new BridgeCornerPreview(o.Corner[0], o.Corner[1], o.CornerMinimumSafeLengthMm));
                if (o.Wall != null)
                {
                    if (o.Wall.Length != 3) throw new InvalidOperationException("生成记录的面对墙孔口参数无效。");
                    result = result.WithWallFramePreview(new BridgeWallFramePreview(o.Wall[0], o.Wall[1], o.Wall[2]));
                }
            }
            return result;
        }

        private sealed class Recipe
        {
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public BridgeHangerLayout HangerLayout { get; set; }
            public string WallPlanning { get; set; }
            public int Schema { get; set; }
            public string Scenario { get; set; }
            public double[][] CoordinateData { get; set; }
            public double OriginX { get; set; }
            public double OriginZ { get; set; }
            public double Overlap { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public double? InsulationClearance { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string[] SpecialCopperSectionIds { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public double? OffsetCopperSafetyShift { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public ObjectData[] CopperLengthBaseline { get; set; }
            public double CopperWidth { get; set; }
            public double CopperThickness { get; set; }
            public string[] ManualInsulators { get; set; }
            public string[] ManualJoints { get; set; }
            public string[] LockedObjects { get; set; }
            public FrameEditingData FrameLengthEditing { get; set; }
            public ObjectData[] Objects { get; set; }
            public RouteData[] Routes { get; set; }
            public RouteData[] EditRoutes { get; set; }
        }
        private sealed class FrameEditingData
        {
            [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
            public double? OffsetCopperMinimumLengthMm { get; set; }
            public ObjectData[] BaselineFrames { get; set; }
            public string[] IndependentFrames { get; set; }
            public string[] FixedEndFrames { get; set; }
            public string[] LengthAdjustedFrames { get; set; }
        }
        private sealed class ObjectData
        {
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public bool? SuppressTemplateAngleNotches { get; set; }
            public double? AngleNotchDepthMm { get; set; }
            public double? OuterAngleNotchDepthMm { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public double[] InstanceNotchDepthsMm { get; set; }
            public double? CornerMinimumSafeLengthMm { get; set; }
            public BridgeComponentBinding Binding { get; set; }
            public double[] Wall { get; set; }
            public string Template { get; set; }
            public string Id { get; set; }
            public string Name { get; set; }
            public DesignObjectKind Kind { get; set; }
            public string Role { get; set; }
            public string Parent { get; set; }
            public string Phase { get; set; }
            public Dictionary<DesignField, double> Values { get; set; }
            public DesignField[] Editable { get; set; }
            public string Joint { get; set; }
            public string Insulator { get; set; }
            public int Position { get; set; }
            public int PadCount { get; set; }
            public double PadThickness { get; set; }
            public double PhaseSpacing { get; set; }
            public double AssemblyHeight { get; set; }
            public bool Automatic { get; set; }
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool OffsetInsulators { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public BridgeInsulatorMountPlan Mount { get; set; }
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int MountGroup { get; set; }
            public double[] Small { get; set; }
            public int[] Corner { get; set; }
        }
        private sealed class RouteData
        {
            public BridgeComponentBinding Binding { get; set; }
            public string GenerationGroup { get; set; }
            public bool IsBridge { get; set; }
            public string Id { get; set; }
            public string Phase { get; set; }
            public string[] Points { get; set; }
            public string[] L { get; set; }
            public string[] Straight { get; set; }
            public string Section { get; set; }
            public PlanarBendBusbarSetting Native { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public PlanarBendBusbarSetting RaisedLapBaselineNative { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string[] RaisedLapBaselinePointIds { get; set; }
        }

        private double[][] CoordinateData(BridgeCoordinateFrame frame) => new[]
            { frame.Origin.ToArray(), frame.Along.ToArray(), frame.Across.ToArray(), frame.Up.ToArray() };
        private BridgeCoordinateFrame Coordinates(Recipe data)
        {
            if (data.Schema == 1) return BridgeCoordinateFrame.Facing(data.OriginX, data.OriginZ);
            if (data.CoordinateData == null || data.CoordinateData.Length != 4)
                throw new InvalidOperationException("生成记录缺少完整坐标基准。");
            return new BridgeCoordinateFrame(data.CoordinateData[0], data.CoordinateData[1], data.CoordinateData[2], data.CoordinateData[3]);
        }
    }
}
