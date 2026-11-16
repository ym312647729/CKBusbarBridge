using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Drivers;
using CKBusbarBridge.Models;
using CKBusbarBridgePreview;
using GeometRi;
using Newtonsoft.Json;

namespace CKBusbarBridge.Prototype
{
    internal sealed class BridgeBusinessReadPolicy
    {
        private readonly Func<string, bool> _allowUnsavedDocument;

        internal BridgeBusinessReadPolicy(Func<string, bool> allowUnsavedDocument = null)
        { _allowUnsavedDocument = allowUnsavedDocument; }

        internal void RequireSaved(bool pendingSave, string path)
        {
            // 入口读取必须落盘；事务校验只能放行已经纳入本次恢复范围的文档。
            if (pendingSave && _allowUnsavedDocument?.Invoke(path) != true)
                throw new InvalidOperationException("请先保存业务模型：" + path);
        }

        internal void RequireFeatureConfiguration(string active, string referenced, string context)
        {
            // GetDefinition没有配置参数；不得把活动配置的特征定义当作实例引用配置。
            if (string.IsNullOrWhiteSpace(referenced) || active != referenced)
                throw new InvalidOperationException("原驱动特征定义的配置上下文不符：" + context +
                    "；引用配置=" + referenced + "；活动配置=" + active);
        }
    }

    internal sealed class BridgeReadbackFrame
    {
        internal FaceToFaceFrameReadItem Physical;
        internal BridgeComponentBinding Binding;
        internal double[] Inlet, Outlet;
        internal BridgeWallFramePreview Wall;
        internal bool? SuppressTemplateAngleNotches;
        internal BridgeAngleNotchPlan AngleNotches;
        internal double PhaseSpacing, AssemblyHeight, PadThickness;
        internal int PadCount;
        internal double[] TransverseOffsets;
        internal BridgeInsulatorMountPlan[] Mounts;
    }

    internal sealed class BridgeReadbackCopper
    {
        internal BridgeComponentBinding Binding;
        internal double[][] Path;
        internal double Width, Thickness;
        internal double? Radius;
        internal double? VerticalRadius;
        internal string Kind;
        internal double[] Milling;
        internal MilledBarFeatureSide_e MilledSide;
        internal Func<List<BridgeContactFace>> ReadFaces;
        internal List<BridgeContactFace> Faces;
    }

    internal sealed class BridgeReadbackCabinetCopper
    {
        internal string Side, Phase;
        internal BridgeReadbackCopper Copper;
    }

    internal sealed class BridgeCopperBaseFlangeParameters
    {
        internal double Length, Thickness, Radius;
    }

    internal sealed class BridgeCopperSheetMetalParameters
    {
        internal double Thickness, Radius;
    }

    internal interface IBridgeCopperParameterSource
    {
        IReadOnlyList<string> BaseFlangeNames();
        BridgeCopperBaseFlangeParameters BaseFlange(string name);
        BridgeCopperSheetMetalParameters SheetMetal(string name);
        double Dimension(params string[] names);
        bool Suppressed(string name);
    }

    internal sealed class BridgeCopperParameterReadback
    {
        internal void Read(IBridgeCopperParameterSource source, BridgeReadbackCopper item,
            bool vertical, bool milled, bool bent, string context)
        {
            item.Kind = vertical ? "立弯" : milled ? "铣排" : "平弯";
            if (vertical) ReadVertical(source, item, context);
            else
            {
                // 与ApplyBendParameters逐项对应，包含生成器支持的模板尺寸别名。
                item.Width = source.Dimension("W@铜排基体", "D2@铜排基体");
                item.Thickness = source.Dimension("Thickness@钣金", "厚度@钣金");
                item.Radius = bent ? source.Dimension("R@钣金", "D1@钣金") : (double?)null;
            }
            if (!milled) return;
            item.Milling = new[] { source.Dimension("排宽W@草图1"), source.Dimension("增加宽@草图1"), source.Dimension("SL@草图1") };
            var left = source.Suppressed("左铣排");
            var right = source.Suppressed("右铣排");
            if (left == right) throw new InvalidOperationException("铣排左右加工特征不能唯一识别：" + context);
            item.MilledSide = left ? MilledBarFeatureSide_e.Right : MilledBarFeatureSide_e.Left;
        }

        private void ReadVertical(IBridgeCopperParameterSource source, BridgeReadbackCopper item, string context)
        {
            var segments = source.BaseFlangeNames().Select(name => new {
                Name = name,
                Match = System.Text.RegularExpressions.Regex.Match(name, @"^(立弯)?铜排基体([1-9]\d*)$")
            }).Where(s => s.Match.Success).Select(s => new {
                s.Name,
                Vertical = s.Match.Groups[1].Success,
                Index = int.Parse(s.Match.Groups[2].Value, CultureInfo.InvariantCulture)
            }).OrderBy(s => s.Index).ToArray();
            if (segments.Length == 0 || !segments.Any(s => s.Vertical) || segments.Any(s => source.Suppressed(s.Name)) ||
                segments.Where((s, i) => s.Index != i + 1).Any())
                throw new InvalidOperationException("立弯铜排的原驱动分段缺失、重复或被压缩：" + context);

            double? width = null, thickness = null, radius = null, verticalRadius = null;
            foreach (var segment in segments)
            {
                var flange = source.BaseFlange(segment.Name);
                if (segment.Vertical)
                {
                    // SetBusBarBendBody把法兰长度写为排厚；ResetThicknessOverride把钣金厚度写为排宽。
                    var sheetMetal = source.SheetMetal("立弯钣金" + segment.Index.ToString(CultureInfo.InvariantCulture));
                    Merge(ref width, sheetMetal.Thickness, true, "排宽", context);
                    Merge(ref thickness, flange.Length, true, "排厚", context);
                    Merge(ref verticalRadius, sheetMetal.Radius, false, "立弯半径", context);
                }
                else
                {
                    Merge(ref width, flange.Length, true, "排宽", context);
                    Merge(ref thickness, flange.Thickness, true, "排厚", context);
                    Merge(ref radius, flange.Radius, false, "平弯半径", context);
                }
            }
            item.Width = width.Value;
            item.Thickness = thickness.Value;
            item.Radius = radius;
            item.VerticalRadius = verticalRadius;
        }

        private void Merge(ref double? current, double value, bool positive, string field, string context)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || (positive ? value <= 0 : value < 0))
                throw new InvalidOperationException("原驱动特征参数无效：" + context + "/" + field);
            if (current.HasValue && Math.Abs(current.Value - value) > .01)
                throw new InvalidOperationException("立弯铜排各段原驱动参数不一致：" + context + "/" + field);
            current = current ?? value;
        }
    }

    internal sealed class BridgeBusinessObservation
    {
        internal List<BridgeReadbackFrame> Frames = new List<BridgeReadbackFrame>();
        internal List<BridgeReadbackCopper> Copper = new List<BridgeReadbackCopper>();
        internal List<BridgeReadbackCabinetCopper> CabinetCopper = new List<BridgeReadbackCabinetCopper>();
        internal List<string> Suppressed = new List<string>();
        internal string Signature;
        internal static string Sign(BridgeBusinessObservation value)
            => Sign(value, null);

        internal static string Sign(BridgeBusinessObservation value, Func<string, string> mapPath, bool legacyNotches = false)
        {
            Func<BridgeComponentBinding, BridgeComponentBinding> binding = source =>
                source == null || mapPath == null ? source : source.WithPath(mapPath(source.Path));
            var json = JsonConvert.SerializeObject(new {
                Frames = value.Frames.Select(f => new { Binding = binding(f.Binding), f.Inlet, f.Outlet, f.Physical.Width, f.Physical.Height,
                    f.Physical.Length, f.Physical.InsulatorPositions, f.Physical.SmallOffsetFrame, f.Physical.TemplateIdentity,
                    f.Physical.WallEntryDistance, f.Wall, f.PhaseSpacing, f.AssemblyHeight, f.PadCount, f.PadThickness }),
                Copper = value.Copper.OrderBy(c => c.Binding.Instance, StringComparer.Ordinal).Select(c => new { Binding = binding(c.Binding), c.Width, c.Thickness, c.Radius, c.VerticalRadius, c.Kind, c.Milling, c.MilledSide }),
                Suppressed = value.Suppressed.OrderBy(s => s, StringComparer.Ordinal)
            });
            if (value.Frames.Any(f => f.TransverseOffsets != null))
                json += JsonConvert.SerializeObject(value.Frames.Where(f => f.TransverseOffsets != null)
                    .Select(f => new { f.Binding.Instance, f.TransverseOffsets }));
            // 其他场景的签名字节保持原样；特殊换相额外绑定真实柜端来源。
            if (value.Frames.Any(f => f.Mounts != null))
                json += "\nHanging=" + JsonConvert.SerializeObject(value.Frames.Where(f => f.Mounts != null)
                    .Select(f => new { f.Binding.Instance, f.Mounts }));
            if (value.CabinetCopper.Count > 0)
                json += "\nCabinetCopper=" + JsonConvert.SerializeObject(value.CabinetCopper.OrderBy(c => c.Copper.Binding.Instance, StringComparer.Ordinal)
                    .Select(c => new { c.Side, c.Phase, Binding = binding(c.Copper.Binding), c.Copper.Width, c.Copper.Thickness }));
            var notches = value.Frames.Select(f => new { Instance = f.Binding?.Instance, Frame = f,
                Plan = legacyNotches ? LegacyAngleNotches(f) : f.AngleNotches }).ToArray();
            if (legacyNotches && notches.Any(n => n.Frame.AngleNotches?.InstanceDepthsMm != null && n.Plan == null))
                return null;
            var states = notches.Select(n => new { n.Instance,
                SuppressTemplateAngleNotches = n.Plan?.Suppress ?? n.Frame.SuppressTemplateAngleNotches })
                .Where(n => n.SuppressTemplateAngleNotches.HasValue).ToArray();
            if (states.Length > 0) json += "\nAngleNotches=" + JsonConvert.SerializeObject(states);
            if (notches.Any(n => n.Plan?.DepthMm != null))
                json += "\nAngleNotchDepths=" + JsonConvert.SerializeObject(notches.Where(n => n.Plan?.DepthMm != null)
                    .Select(n => new { n.Instance, n.Plan.DepthMm, n.Plan.OuterDepthMm }));
            if (notches.Any(n => n.Plan?.InstanceDepthsMm != null))
                json += "\nInstanceNotchDepths=" + JsonConvert.SerializeObject(notches.Where(n => n.Plan?.InstanceDepthsMm != null)
                    .Select(n => new { n.Instance, n.Plan.InstanceDepthsMm }));
            return new BridgeGenerationRecipeCodec().Hash(json);
        }

        private static BridgeAngleNotchPlan LegacyAngleNotches(BridgeReadbackFrame frame)
        {
            var depths = frame.AngleNotches?.InstanceDepthsMm;
            if (depths == null) return frame.AngleNotches;
            var physical = frame.Physical;
            var corner = physical.TemplateIdentity == FrameTemplateIdentity.FaceToFaceLargeOffsetCorner028;
            var selected = corner ? depths.Take(2) : depths;
            if (physical.SmallOffsetFrame != null)
            {
                var seals = FaceToFaceSmallOffsetSealPlatePlanner.CreatePlan(physical.SmallOffsetFrame, physical.Height);
                selected = depths.Where((_, slot) => BusbarBridgeDraftPreviewSceneFactory.SmallOffsetAngle(seals, slot) == null);
            }
            var depth = UniformDepth(selected);
            var outer = corner ? UniformDepth(depths.Skip(2)) : null;
            if (!depth.HasValue || corner && !outer.HasValue) return null;
            return new BridgeAngleNotchPlan(depth == 0d,
                corner || physical.TemplateIdentity == FrameTemplateIdentity.Regular024 ? depth : null, outer);

            double? UniformDepth(IEnumerable<double> values)
            {
                var ordered = values.OrderBy(d => d).ToArray();
                return ordered.Length > 0 &&
                    SolidWorksFaceToFaceFrameModelOperations.NotchDepthMatches(ordered.Last(), ordered[0]) ? ordered[0] : (double?)null;
            }
        }
    }

    internal sealed class BridgeBusinessReconciler
    {
        internal BridgeDesignSample RestoreBaseline(BridgeReadbackBaseline baseline,
            BridgeBusinessObservation actual, BridgeUpstreamSnapshot upstream)
        {
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (actual == null) throw new ArgumentNullException(nameof(actual));
            if (actual.Signature != baseline.Signature)
                throw new InvalidOperationException("母线桥业务对象在反读后发生变化，请重新读取后生成。");

            var saved = new BridgeGenerationRecipeCodec().RestoreStored(baseline.Recipe, upstream);
            // 基线配方已经绑定反读时的实际实例；新方案重新分节后，同名逻辑ID可能属于另一节。
            var restored = Reconcile(saved, actual, null, allowDisconnected: true, upstream: upstream);
            restored.ReadbackBaseline.Material = baseline.Material;
            return restored;
        }

        internal IReadOnlyDictionary<string, string> ResolveTargetFrameInstances(
            IEnumerable<BridgeDesignObject> targets, BridgeDesignSample baseline)
        {
            if (targets == null) throw new ArgumentNullException(nameof(targets));
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            var actual = new Dictionary<string, BridgeDesignObject>(StringComparer.Ordinal);
            foreach (var frame in baseline.Objects.Where(o => o.Kind == DesignObjectKind.Frame))
            {
                var binding = frame.ComponentBinding;
                if (string.IsNullOrWhiteSpace(binding?.Instance) || actual.ContainsKey(binding.Instance))
                    throw new InvalidOperationException("实际基架实例身份缺失或重复，不能建立目标安装映射：" + DescribeFrame(frame));
                actual.Add(binding.Instance, frame);
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var claimed = new Dictionary<string, BridgeDesignObject>(StringComparer.Ordinal);
            foreach (var frame in targets.Where(o => o.Kind == DesignObjectKind.Frame))
            {
                var binding = frame.ComponentBinding;
                // 没有实物绑定的目标是新增安装；禁止按复用的逻辑ID或位置号认领旧实例。
                if (binding == null) continue;
                if (string.IsNullOrWhiteSpace(binding.Instance) || !actual.TryGetValue(binding.Instance, out var original))
                    throw new InvalidOperationException("目标基架绑定的实际实例已失效：" + DescribeFrame(frame));
                if (string.IsNullOrWhiteSpace(binding.Path) || string.IsNullOrWhiteSpace(binding.Configuration) ||
                    binding.DocumentKey != original.ComponentBinding.DocumentKey)
                    throw new InvalidOperationException("目标基架的引用文件或配置与实物基线不一致：目标=" +
                        DescribeFrame(frame) + "；基线=" + DescribeFrame(original));
                if (claimed.TryGetValue(binding.Instance, out var conflict))
                    throw new InvalidOperationException("多个目标基架指向同一实际实例：" + binding.Instance +
                        "；对象1=" + DescribeFrame(conflict) + "；对象2=" + DescribeFrame(frame));
                claimed.Add(binding.Instance, frame);
                result.Add(frame.Id, binding.Instance);
            }
            return result;
        }

        private static string DescribeFrame(BridgeDesignObject frame)
            => frame.Id + "（" + frame.DisplayName + "，模板=" + frame.FrameTemplateKey +
                "，实例=" + frame.ComponentBinding?.Instance + "，路径=" + frame.ComponentBinding?.Path +
                "，配置=" + frame.ComponentBinding?.Configuration + "）";

        internal BridgeDesignSample Reconcile(BridgeDesignSample saved, BridgeBusinessObservation actual,
            IReadOnlyDictionary<string, string> previousFrames, bool allowDisconnected = false, BridgeUpstreamSnapshot upstream = null)
        {
            var objects = saved.Objects.Where(o => o.Kind != DesignObjectKind.Frame && o.Kind != DesignObjectKind.Insulator).ToList();
            var previous = saved.Objects.Where(o => o.Kind == DesignObjectKind.Frame).ToArray();
            var instances = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in actual.Frames)
                if (string.IsNullOrWhiteSpace(item.Binding?.Instance) || !instances.Add(item.Binding.Instance))
                    throw new InvalidOperationException("实际基架实例身份缺失或重复，不能恢复安装顺序和编辑状态。");
            var direct = new Dictionary<string, BridgeDesignObject>(StringComparer.Ordinal);
            foreach (var frame in previous)
            {
                // 本次提交的实例映射优先；替换、增删之后，旧绑定中的实例名可能已被另一节复用。
                var instance = previousFrames != null && previousFrames.TryGetValue(frame.Id, out var mapped)
                    ? mapped : frame.ComponentBinding?.Instance;
                if (instance == null || !instances.Contains(instance)) continue;
                if (direct.TryGetValue(instance, out var conflict))
                    throw new InvalidOperationException("多个基架指向同一实际实例，不能恢复安装身份：" + instance +
                        "；对象1=" + DescribeFrame(conflict) + "；映射来源1=" +
                        (previousFrames?.ContainsKey(conflict.Id) == true ? "提交映射" : "配方绑定") +
                        "；对象2=" + DescribeFrame(frame) + "；映射来源2=" +
                        (previousFrames?.ContainsKey(frame.Id) == true ? "提交映射" : "配方绑定"));
                direct.Add(instance, frame);
            }
            var reserved = new HashSet<string>(direct.Values.Select(o => o.Id));
            var claimed = new HashSet<string>();
            for (var index = 0; index < actual.Frames.Count; index++)
            {
                var item = actual.Frames[index]; var physical = item.Physical;
                direct.TryGetValue(item.Binding.Instance, out var old);
                if (old == null)
                {
                    // 先保留全部仍在场的实例，再做双向唯一的文档匹配；新增共享实例不能抢走旧节的长度锁和固定端。
                    var sameDocument = previous.Where(o => !claimed.Contains(o.Id) && !reserved.Contains(o.Id) && o.ComponentBinding != null &&
                        o.ComponentBinding.DocumentKey == item.Binding.DocumentKey).ToArray();
                    if (sameDocument.Length == 1 && actual.Frames.Count(f => !direct.ContainsKey(f.Binding.Instance) &&
                            f.Binding.DocumentKey == item.Binding.DocumentKey) == 1) old = sameDocument[0];
                }
                if (old != null) claimed.Add(old.Id);
                var key = physical.TemplateIdentity == FrameTemplateIdentity.SideToSidePhaseChange029 ? "SideToSidePhaseChange029" :
                    physical.IsSmallOffsetFrame ? "SmallOffset" : physical.FrameTemplateKind == FaceToWallFrameTemplateKind.Large1 ? "FaceToWallLarge1" :
                    physical.FrameTemplateKind == FaceToWallFrameTemplateKind.Large2 ? "FaceToWallLarge2" :
                    physical.FrameTemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner ? "LargeOffsetCorner" : "Regular";
                var id = old?.Id ?? "actual-frame-" + new BridgeGenerationRecipeCodec().Hash(item.Binding.Instance).Substring(0, 16);
                var shared = previous.FirstOrDefault(o => o.ComponentBinding?.DocumentKey == item.Binding.DocumentKey);
                var frame = old ?? new PrototypeBusbarBridgeDesignProvider().Create(id,
                    shared?.Name ?? physical.SourceName, DesignObjectKind.Frame, 0, 0, 0, "实际基架", physical.Length, physical.Width, height: physical.Height);
                var values = frame.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.Length] = physical.Length; values[DesignField.Width] = physical.Width; values[DesignField.Height] = physical.Height;
                if (key == "LargeOffsetCorner")
                {
                    values.Remove(DesignField.Installation);
                    item.Binding = item.Binding.WithoutFrameInstallation();
                }
                else values[DesignField.Installation] = physical.IsReversed ? 1 : 0;
                values[DesignField.InsulatorCount] = physical.InsulatorPositions.Count;
                values[DesignField.X] = 0; values[DesignField.Y] = 0; values[DesignField.Z] = 0; values[DesignField.Direction] = 0;
                item.Binding.DrivenValues = new Dictionary<DesignField, double>(values);
                frame = frame.WithValues(values).WithSourceName(physical.SourceName).WithComponentBinding(item.Binding).WithFrameTemplate(key).WithFramePosition(index + 1);
                if (item.AngleNotches != null) frame = frame.WithAngleNotches(item.AngleNotches);
                else if (item.SuppressTemplateAngleNotches.HasValue) frame = frame.WithTemplateAngleNotches(item.SuppressTemplateAngleNotches.Value);
                if (physical.SmallOffsetFrame != null)
                    frame = frame.WithSmallOffsetPreview(new BridgeSmallOffsetPreview(physical.SmallOffsetFrame.InletOffsetDirection,
                        physical.SmallOffsetFrame.InletOffsetSize, physical.SmallOffsetFrame.OutletOffsetDirection, physical.SmallOffsetFrame.OutletOffsetSize));
                if (item.Wall != null) frame = frame.WithWallFramePreview(item.Wall);
                if (key == "LargeOffsetCorner" && frame.CornerPreview == null)
                {
                    var prototype = objects.Concat(previous).FirstOrDefault(o => o.ComponentBinding?.DocumentKey == item.Binding.DocumentKey && o.CornerPreview != null &&
                        o.ComponentBinding.Inlet==item.Binding.Inlet);
                    if(prototype==null && previousFrames!=null)
                        prototype=previous.FirstOrDefault(o=>o.CornerPreview!=null && previousFrames.TryGetValue(o.Id,out var originalInstance) &&
                            actual.Frames.Any(f=>f.Binding.Instance==originalInstance && f.Binding.DocumentKey==item.Binding.DocumentKey &&
                                f.Binding.Inlet==item.Binding.Inlet));
                    if (prototype == null) throw new InvalidOperationException("新增028基架缺少可核验的封闭端模板关系：" + item.Binding.Instance);
                    frame = frame.WithCornerPreview(prototype.CornerPreview);
                }
                var nominal = new BridgeDraftGenerationPlanFactory().InterfacePoints(frame, saved.Coordinates).Select(saved.Coordinates.ToDraft).ToArray();
                var inlet = saved.Coordinates.ToDraft(item.Inlet); var outlet = saved.Coordinates.ToDraft(item.Outlet);
                var direction = Math.Atan2(outlet[1] - inlet[1], outlet[0] - inlet[0]) -
                    Math.Atan2(nominal[1][1] - nominal[0][1], nominal[1][0] - nominal[0][0]);
                values[DesignField.Direction] = direction * 180 / Math.PI;
                values[DesignField.X] = inlet[0] - nominal[0][0] * Math.Cos(direction) + nominal[0][1] * Math.Sin(direction);
                values[DesignField.Y] = inlet[1] - nominal[0][0] * Math.Sin(direction) - nominal[0][1] * Math.Cos(direction);
                values[DesignField.Z] = inlet[2] - nominal[0][2];
                item.Binding.DrivenValues = new Dictionary<DesignField, double>(values);
                frame = frame.WithValues(values).WithComponentBinding(item.Binding);
                item.Binding.DisplayValues = frame.Values.ToDictionary(p => p.Key, p => p.Value);
                var restoredPorts = new BridgeDraftGenerationPlanFactory().InterfacePoints(frame, saved.Coordinates);
                if (Enumerable.Range(0, 3).Any(axis =>
                    Math.Abs(restoredPorts[0][axis] - item.Inlet[axis]) > BridgeDesignPrecision.LengthComparisonToleranceMm ||
                    Math.Abs(restoredPorts[1][axis] - item.Outlet[axis]) > BridgeDesignPrecision.LengthComparisonToleranceMm))
                    throw new InvalidOperationException("实际基架接口姿态无法由当前业务参数表达：" + item.Binding.Instance);
                item.Binding.InsulatorPositions = physical.InsulatorPositions.ToArray();
                item.Binding.InsulatorTransverseOffsets = item.TransverseOffsets?.ToArray();
                if (physical.InsulatorCount > 0)
                    frame = frame.WithInsulatorStack(item.PadCount, item.PadThickness, item.PhaseSpacing, item.AssemblyHeight);
                if (item.TransverseOffsets != null && item.TransverseOffsets.Length != physical.InsulatorCount)
                    throw new InvalidOperationException("实际可偏移绝缘子横移参数与安装套数不一致：" + item.Binding.Instance);
                if (item.Mounts != null && item.Mounts.Length != physical.InsulatorCount)
                    throw new InvalidOperationException("实际逐相落地/吊装计划与安装套数不一致：基架=" + item.Binding.Instance +
                        "；安装位数量=" + physical.InsulatorCount + "；逐相安装计划数量=" + item.Mounts.Length);
                frame = frame.WithOffsetInsulators(item.TransverseOffsets != null);
                objects.Add(frame);
                for (var position = 0; position < physical.InsulatorPositions.Count; position++)
                    for (var phase = 0; phase < 3; phase++)
                    {
                        var child = new PrototypeBusbarBridgeDesignProvider().CreateInsulator(frame, position * 3 + phase + 1,
                            (phase - 1) * item.PhaseSpacing + (item.TransverseOffsets?[position] ?? 0));
                        var local = child.Values.ToDictionary(p => p.Key, p => p.Value);
                        local[DesignField.X] = physical.InsulatorPositions[position] - physical.Length / 2;
                        if (item.Mounts?[position]?.IsFloorStanding == true)
                            local[DesignField.Y] = item.Mounts[position].Transverse[2 - phase];
                        child = child.WithValues(local).WithFramePosition(index + 1);
                        if (item.Mounts?[position] != null) child = child.WithMountPlan(item.Mounts[position], 2 - phase);
                        objects.Add(child);
                    }
            }
            var editing = saved.SavedFrameLengthEditing ?? new BridgeFrameLengthEditing(saved.FrameLengthBaseline,
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
            var result = saved.WithGeometry(objects, saved.Routes).WithFrameLengthEditing(editing.RetainFor(objects));
            var hangerLayout = saved.SavedHangers;
            if (hangerLayout?.StartFrameId != null && !direct.Values.Any(frame => frame.Id == hangerLayout.StartFrameId))
                result = result.WithHangers(new BridgeHangerLayout(null, hangerLayout.Direction, hangerLayout.Settings));
            result = new BridgeCopperBusinessRecovery().Recover(result, actual.Copper, allowDisconnected, upstream);
            var baseline = new BridgeReadbackBaseline { Signature = actual.Signature, CopperCount = actual.Copper.Count, SuppressedInstances = actual.Suppressed.ToArray() };
            result = result.WithReadbackBaseline(baseline);
            var ids = new HashSet<string>(result.Objects.Select(o => o.Id));
            result = result.WithSavedManualInsulators(result.SavedManualInsulatorIds.Where(ids.Contains))
                .WithSavedLocks(result.SavedLockedObjectIds.Where(ids.Contains));
            var editor = new BusbarBridgeDraftEditor(); editor.Load(result);
            result = result.WithGeometry(editor.EffectivePlan, result.Routes);
            baseline.Recipe = new BridgeGenerationRecipeCodec().Encode(result, result.Objects,
                result.SavedManualInsulatorIds, result.SavedLockedObjectIds);
            return new BridgeGenerationRecipeCodec().PreserveSupportReplanning(result);
        }
    }
}
