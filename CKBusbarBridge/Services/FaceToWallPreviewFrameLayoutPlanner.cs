using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Services
{
    internal sealed class FaceToWallPreviewFrameLayoutPlanner
    {
        // 草稿量化为0.1mm；接口校验与正式模型安装使用同一0.2mm容差。
        private const double InstallationTolerance = .2d;

        internal bool ValidateInput(FaceToWallUpstreamGeometry input, double? appliedVerticalClearance = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var applied = input.WallInstallation == null ? appliedVerticalClearance ?? input.AppliedWallVerticalClearance : 0;
            var cabinet = input.Cabinet;
            var rising = FaceToWallFrameLengthPlanner.RequiresLargeFrame2(cabinet.OpeningHeight + 2 * applied,
                input.WallInstallation == null ? input.WallOpeningHeight : input.TargetWallHeight);
            var opening = cabinet.Opening;
            if (opening == null || new[] { opening.X, opening.Y, opening.Z, cabinet.BaseWidth }
                .Any(v => double.IsNaN(v) || double.IsInfinity(v)) || cabinet.BaseWidth <= 0)
                throw new InvalidOperationException("面对墙基座出口坐标或宽度无效，请重读上游。");
            var bottom = input.WallOpeningCorners.Min(p => p.Y) + (input.WallInstallation?.Bottom ?? 0);
            if (Math.Abs(opening.Y - cabinet.OpeningHeight / 2d - bottom - applied) >
                FaceToWallFrameLengthPlanner.HeightTolerance + 1e-9d)
                throw new InvalidOperationException("实际基座与墙端目标底面不符合024/027共底面安装链；手动基座参数已保留，请调整或重新同步后重读。");
            if (input.TargetWallEnd - opening.X <= .01d)
                throw new InvalidOperationException("实际墙洞不在基座出口正方向，当前模板朝向不支持。");
            return rising;
        }

        internal void ValidateFrames(FaceToWallUpstreamGeometry input, IReadOnlyList<BridgeDesignObject> frames,
            double? appliedVerticalClearance = null)
        {
            var applied = appliedVerticalClearance ?? input?.AppliedWallVerticalClearance ?? 0;
            var rising = ValidateInput(input, applied);
            if (frames == null || frames.Count < (rising ? 2 : 1))
                throw new InvalidOperationException("面对墙缺少完整的必需基架：" + (rising ? "027→026。" : "026。"));
            var wallFrame = frames.Last();
            var vertical = wallFrame.Value(DesignField.WallVerticalClearance);
            var horizontal = wallFrame.Value(DesignField.WallHorizontalClearance);
            var entry = wallFrame.Value(DesignField.WallEntryDistance);
            if (input.WallInstallation != null)
            {
                var installation = input.WallInstallation;
                installation.Validate(input.WallOpeningCorners.Max(p => p.Z) - input.WallOpeningCorners.Min(p => p.Z), input.WallOpeningHeight);
                if (!Equal(entry, installation.EntryDistance) ||
                    !Equal(wallFrame.Value(DesignField.WallTopClearance), installation.Top) ||
                    !Equal(wallFrame.Value(DesignField.WallBottomClearance), installation.Bottom) ||
                    !Equal(wallFrame.Value(DesignField.WallLeftClearance), installation.Left) ||
                    !Equal(wallFrame.Value(DesignField.WallRightClearance), installation.Right))
                    throw new InvalidOperationException("墙端基架与柜体驱动中的墙孔安装参数不一致，请重新规划。");
                vertical = 0; horizontal = 0; applied = 0;
            }
            if (new[] { vertical, horizontal, entry, applied }.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new InvalidOperationException("面对墙安装参数无效。");
            var baseOpeningHeight = input.Cabinet.OpeningHeight - 2 * (vertical - applied);
            var previous = new[] { input.Cabinet.Opening.X, input.Cabinet.Opening.Z, input.Cabinet.Opening.Y };
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                var expectedKey = index == frames.Count - 1 ? "FaceToWallLarge1" :
                    rising && index == frames.Count - 2 ? "FaceToWallLarge2" : "Regular";
                if (frame == null || frame.Kind != DesignObjectKind.Frame || frame.FramePositionIndex != index + 1 ||
                    frame.FrameTemplateKey != expectedKey)
                    throw new InvalidOperationException("面对墙基架必须按连续位置排列为024（可选）→" +
                        (rising ? "027→026。" : "026。"));
                var regular = expectedKey == "Regular";
                var large1 = expectedKey == "FaceToWallLarge1";
                var height = regular ? baseOpeningHeight : input.WallInstallation == null ? input.WallOpeningHeight - 2 * vertical : input.TargetWallHeight;
                var width = large1 ? input.TargetWallWidth - 2 * horizontal : input.Cabinet.BaseWidth;
                var inlet = frame.WallFramePreview;
                if (inlet == null || !Equal(frame.Height, height) || !Equal(frame.Width, width) ||
                    !Equal(inlet.InletWidth, input.Cabinet.BaseWidth) ||
                    !Equal(inlet.InletHeight, large1 ? height : baseOpeningHeight) ||
                    !Equal(inlet.InletOffset, large1 ? input.EffectiveWallEccentricity : 0d))
                    throw new InvalidOperationException(frame.Name + "外形或入口参数与实际基座/墙洞不一致，请重规划。");
                var minimum = regular ? FaceToWallFrameLengthPlanner.RegularMinimumLength : FaceToWallFrameLengthPlanner.LargeMinimumLength;
                if (!(frame.Length >= minimum - .01d) || !Equal(frame.Direction, 0d) ||
                    !regular && frame.IsReversed)
                    throw new InvalidOperationException(frame.Name + "长度或安装方向不符合面对墙模板规则。");
                var point = new[] { frame.X - frame.Length / 2d, frame.Y + inlet.InletOffset,
                    frame.Z - (frame.Height - inlet.InletHeight) / 2d };
                if (point.Where((value, axis) => !Equal(value, previous[axis])).Any())
                    throw new InvalidOperationException(frame.Name + "入口与前段实际出口未闭合，请核验027升高及026偏心安装链。");
                previous = new[] { frame.X + frame.Length / 2d, frame.Y, frame.Z };
            }
            if (!Equal(previous[0], input.WallOpeningCorners.Average(p => p.X) + entry))
                throw new InvalidOperationException("面对墙末端未闭合到墙面加入墙距离的目标位置。");
            if (input.WallInstallation != null && (!Equal(wallFrame.Y, input.TargetWallCenterAcross) ||
                !Equal(wallFrame.Z, input.TargetWallCenterHeight)))
                throw new InvalidOperationException("末端基架驱动定位未满足独立四边距，请核验实际安装接口。");
        }

        private bool Equal(double actual, double expected) => Math.Abs(actual - expected) <= InstallationTolerance;

        internal double Large1CenterAcross(double previousOutlet, double eccentricity, double width)
        {
            if (new[] { previousOutlet, eccentricity, width }.Any(v => double.IsNaN(v) || double.IsInfinity(v)) || width <= 0)
                throw new ArgumentException("026入口安装坐标或驱动参数无效。");
            // 两片驱动封板宽度差为2E，实际开口中心相对外框为E，与外框宽度无关。
            // 旧产物进坐标与封板开口的残差不能外推为宽度补偿。
            // 以封板开口闭合前一节出口；墙洞中心只校验，不参与安装定位。
            return previousOutlet - eccentricity;
        }

        internal IReadOnlyList<FaceToWallFrameLengthPlanItem> Create(FaceToWallUpstreamGeometry input,
            IReadOnlyList<AlgorithmBusbarGenerationGroup> groups, BridgeDesignSample seamCopper = null)
        {
            var start = input.Cabinet.Opening.X;
            var end = input.TargetWallEnd;
            var rising = ValidateInput(input);
            Create(end - start, rising); // 沿用总跨度及必需模板最短长度校验。
            // 只量特殊排的真实实体，不能把柜端G排、普通排或Worker截取的固定直头纳入。
            var special = groups.Where(g => g.DisplayName == "算法中间段" || g.DisplayName == "特殊段入口桥头").SelectMany(g => g.Busbars)
                .SelectMany(b => FaceToFaceModelBusbarPlanner.CreateFixedInterfacePreviewSolids(b))
                .SelectMany(s => s).ToArray();
            if (special.Length == 0) throw new InvalidOperationException("面对墙特殊铜排实体缺失，无法确定大基架绝缘范围。");
            if (special.Any(p => p.Any(v => double.IsNaN(v) || double.IsInfinity(v))))
                throw new InvalidOperationException("面对墙特殊铜排实体包含无效坐标。");
            // 从铜排表面向柜侧预留绝缘净距和40mm入口结构，不能按中心线或断点计算。
            var requiredStart = special.Min(p => p[0]) - input.Clearance - 40d;
            var prefix = groups.TakeWhile(g => g.DisplayName != "算法中间段" && g.DisplayName != "特殊段入口桥头").ToArray();
            if (rising && prefix.Length == 1 && prefix[0].DisplayName == "柜端连接段")
            {
                // 直连L排时，027/026也没有可供通用补偿器收缩的普通节；初次分节须避开完整搭接禁区。
                var lapNear = prefix[0].Busbars.Min(b => b.PointList.Last().X - b.EndLapWidth);
                requiredStart = Math.Min(requiredStart, lapNear - BusbarBridgeFrameJointClearance.MinimumLapDistanceMm);
            }
            var largeLength = Math.Max(FaceToWallFrameLengthPlanner.LargeMinimumLength, end - requiredStart);
            var prefixLength = end - start - largeLength;
            if (!rising && prefixLength > .001d && prefixLength < MiddleBusbarSupportFramePlanner.MinimumFrameLength)
            { largeLength += prefixLength; prefixLength = 0d; }
            if (requiredStart < start - .01d || prefixLength < -.01d ||
                rising && prefixLength < FaceToWallFrameLengthPlanner.LargeMinimumLength - .001d)
                throw new InvalidOperationException("特殊铜排及绝缘距离未给前段支撑留出合法长度，请调整特殊段起点或铜排方案。");
            var result = new List<FaceToWallFrameLengthPlanItem>();
            if (prefixLength > .001d)
            {
                if (rising)
                    result.AddRange(Create(prefixLength, false).Select(p => new FaceToWallFrameLengthPlanItem(
                        p.TemplateKind == FaceToWallFrameTemplateKind.Large1 ? FaceToWallFrameTemplateKind.Large2 : p.TemplateKind, p.Length)));
                else
                    result.AddRange(MiddleBusbarSupportFramePlanner.CreateFrameLengths(prefixLength)
                        .Select(v => new FaceToWallFrameLengthPlanItem(FaceToWallFrameTemplateKind.Regular, v)));
            }
            result.Add(new FaceToWallFrameLengthPlanItem(FaceToWallFrameTemplateKind.Large1, largeLength));
            AvoidBaseLapSeam(input, groups, result);
            if (rising && seamCopper != null) AvoidRisingLapSeam(input, seamCopper, result);
            return result;
        }

        private void AvoidRisingLapSeam(FaceToWallUpstreamGeometry input, BridgeDesignSample copper,
            List<FaceToWallFrameLengthPlanItem> layout)
        {
            var transitionIndex = layout.Count - 2;
            var seam = input.Cabinet.Opening.X + layout.Take(layout.Count - 1).Sum(item => item.Length);
            var earlier = new BusbarBridgeFrameJointClearance().EarlierLapClearSeam(
                copper, seam, input.Cabinet.Opening.Z, input.Cabinet.BaseWidth);
            var shift = seam - earlier;
            var transitionLength = layout[transitionIndex].Length - shift;
            if (shift <= 0.0 || transitionLength < FaceToWallFrameLengthPlanner.LargeMinimumLength) return;

            // 026向柜侧延伸，保留对特殊铜排的包覆和两端安装坐标。
            layout[transitionIndex] = new FaceToWallFrameLengthPlanItem(FaceToWallFrameTemplateKind.Large2, transitionLength);
            layout[transitionIndex + 1] = new FaceToWallFrameLengthPlanItem(FaceToWallFrameTemplateKind.Large1,
                layout[transitionIndex + 1].Length + shift);
        }

        private void AvoidBaseLapSeam(FaceToWallUpstreamGeometry input,
            IReadOnlyList<AlgorithmBusbarGenerationGroup> groups, List<FaceToWallFrameLengthPlanItem> layout)
        {
            if (layout.Count < 2 || layout[0].TemplateKind != FaceToWallFrameTemplateKind.Regular ||
                layout[1].TemplateKind == FaceToWallFrameTemplateKind.Regular) return;
            var cabinet = groups.SingleOrDefault(g => g.DisplayName == "柜端连接段");
            if (cabinet == null || cabinet.Busbars.Count == 0) return;
            var near = cabinet.Busbars.Min(b => b.PointList.Last().X - b.EndLapWidth) - BusbarBridgeFrameJointClearance.MinimumLapDistanceMm;
            var far = cabinet.Busbars.Max(b => b.PointList.Last().X) + BusbarBridgeFrameJointClearance.MinimumLapDistanceMm;
            var seam = input.Cabinet.Opening.X + layout[0].Length;
            if (seam <= near + .000001d || seam >= far - .000001d) return;
            // 自动短余段的接缝落入首搭接禁区时，并入相邻大基架；总长和基座出口均保持原位。
            layout[1] = new FaceToWallFrameLengthPlanItem(layout[1].TemplateKind, layout[0].Length + layout[1].Length);
            layout.RemoveAt(0);
        }

        internal IReadOnlyList<FaceToWallFrameLengthPlanItem> Create(double length, bool rising)
        {
            // 长度与普通基架共用节拍、上限及余段处理；模板角色不反向拉长分节。
            var lengths = MiddleBusbarSupportFramePlanner.CreateFrameLengths(length).ToList();
            var largeCount = rising ? 2 : 1;
            if (length < largeCount * FaceToWallFrameLengthPlanner.LargeMinimumLength - .001d)
                throw new InvalidOperationException("面对墙跨度不足以容纳所需大基架的最低制造长度。");
            if (rising && lengths.Count == 1)
            {
                var first = Math.Round(length / 2d, 3, MidpointRounding.AwayFromZero);
                lengths = new List<double> { first, Math.Round(length - first, 3, MidpointRounding.AwayFromZero) };
            }
            // 普通规划允许200mm余段，大模板至少300mm；从邻节等量调剂，不改变总长。
            for (var i = lengths.Count - largeCount; i < lengths.Count; i++)
            {
                var deficit = FaceToWallFrameLengthPlanner.LargeMinimumLength - lengths[i];
                if (deficit <= .001d) continue;
                foreach (var donor in Enumerable.Range(0, lengths.Count).Where(j => j != i)
                    .OrderBy(j => Math.Abs(j - i)).ThenBy(j => j))
                {
                    var minimum = donor >= lengths.Count - largeCount ? FaceToWallFrameLengthPlanner.LargeMinimumLength :
                        MiddleBusbarSupportFramePlanner.MinimumFrameLength;
                    var transfer = Math.Min(deficit, Math.Max(0d, lengths[donor] - minimum));
                    lengths[donor] -= transfer; lengths[i] += transfer; deficit -= transfer;
                    if (deficit <= .001d) break;
                }
                if (deficit > .001d) throw new InvalidOperationException("面对墙大基架短余段无法满足最低制造长度。");
            }
            return lengths.Select((v, i) => new FaceToWallFrameLengthPlanItem(
                i == lengths.Count - 1 ? FaceToWallFrameTemplateKind.Large1 :
                rising && i == lengths.Count - 2 ? FaceToWallFrameTemplateKind.Large2 : FaceToWallFrameTemplateKind.Regular,
                v)).ToArray();
        }
    }
}
