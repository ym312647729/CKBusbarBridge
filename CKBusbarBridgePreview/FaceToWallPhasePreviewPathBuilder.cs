using GeometRi;
using System;
using System.Collections.Generic;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 根据面对墙某相实际模板尺寸构建预览路径；不调用 SolidWorks COM。
    /// </summary>
    public static class FaceToWallPhasePreviewPathBuilder
    {
        public static IReadOnlyList<BendPreviewSetting> Build(
            FaceToWallPhasePreviewData phase,
            Point3d segmentStart,
            Point3d segmentEnd,
            Vector3d xVector,
            Vector3d lateralVector,
            Vector3d verticalVector,
            BendPreviewSetting source)
        {
            if (phase == null) throw new ArgumentNullException(nameof(phase));
            if (segmentStart == null) throw new ArgumentNullException(nameof(segmentStart));
            if (segmentEnd == null) throw new ArgumentNullException(nameof(segmentEnd));
            if (xVector == null) throw new ArgumentNullException(nameof(xVector));
            if (lateralVector == null) throw new ArgumentNullException(nameof(lateralVector));
            if (verticalVector == null) throw new ArgumentNullException(nameof(verticalVector));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.BusBarWidth <= 0 || source.BusBarThickness <= 0)
            {
                throw new InvalidOperationException("面对墙铜排预览的排宽和排厚必须大于 0。");
            }

            var start = segmentStart.Copy();
            if (phase.HeadKind == FaceToWallPreviewHeadKind.BridgeHead)
            {
                start = start.Move(verticalVector.Negate(), source.BusBarThickness);
            }

            switch (phase.TemplateKind)
            {
                case FaceToWallPreviewTemplateKind.VerticalBend:
                    return BuildVerticalBend(phase, start, xVector, lateralVector, verticalVector, source);
                case FaceToWallPreviewTemplateKind.OffsetConnection:
                    return BuildOffsetConnection(phase, start, xVector, lateralVector, verticalVector, source);
                case FaceToWallPreviewTemplateKind.MilledBar:
                    return BuildMilledBar(phase, start, xVector, lateralVector, verticalVector, source);
                case FaceToWallPreviewTemplateKind.StraightRaiseOnly:
                    return BuildStraightRaiseOnly(phase, start, xVector, verticalVector, source);
                default:
                    throw new InvalidOperationException("不支持的面对墙铜排预览模板类型：" + phase.TemplateKind);
            }
        }

        private static IReadOnlyList<BendPreviewSetting> BuildVerticalBend(
            FaceToWallPhasePreviewData phase,
            Point3d start,
            Vector3d xVector,
            Vector3d lateralVector,
            Vector3d verticalVector,
            BendPreviewSetting source)
        {
            var offsetTotalLength = RequireDimension(phase, "偏移排总长度@控制骨架草图1");
            var raiseLength = RequireDimension(phase, "折弯排抬高长度@控制骨架草图1");
            var firstOffsetLength = RequireDimension(phase, "偏移第一段长度尺寸@控制骨架草图2");
            var secondOffsetLength = RequireDimension(phase, "偏移第二段长度尺寸@控制骨架草图2");
            var middleLength = offsetTotalLength - firstOffsetLength - secondOffsetLength;
            if (middleLength < 0)
            {
                throw new InvalidOperationException(GetPhaseName(phase) + "相立弯预览的偏移分段长度之和超过偏移排总长度。");
            }

            ValidateTotalLength(
                phase,
                offsetTotalLength + raiseLength,
                "偏移排总长度与折弯排抬高长度之和");

            // PreviewBendBody 的路径点是截面基准边，不是截面中心线。立弯段交换排宽/排厚后，
            // 必须先把平放铜排基准边转换到立放铜排基准边；退出立弯段时再反向还原。
            // 否则立弯实体会沿侧向错开半个排宽、沿竖向错开半个排厚。
            var verticalStart = start
                .Move(verticalVector, source.BusBarThickness / 2.0)
                .Move(lateralVector.Negate(), source.BusBarWidth / 2.0);
            var firstEnd = verticalStart.Move(xVector, firstOffsetLength);
            var diagonalEnd = firstEnd
                .Move(xVector, middleLength)
                .Move(lateralVector, phase.DeltaZ);
            var offsetEnd = diagonalEnd.Move(xVector, secondOffsetLength);
            var normalStart = offsetEnd
                .Move(verticalVector.Negate(), source.BusBarThickness / 2.0)
                .Move(lateralVector, source.BusBarWidth / 2.0);

            // 预览内核以第一条路径线段和厚度向量建立截面平面，首段不能沿厚度方向。
            // 当前模板只提供抬高段总长，因此沿用旧预览的折弯余量作为抬高前直段，
            // 同时限制在总长的一半以内，保证抬高前后均保留有效的 X 向线段。
            var firstRaiseLength = Math.Min(
                source.BusBarThickness + source.BarBendingRadius + 3.0,
                raiseLength / 2.0);
            var secondRaiseLength = raiseLength - firstRaiseLength;
            var raiseStart = normalStart.Move(xVector, firstRaiseLength);
            var raisedPoint = raiseStart.Move(verticalVector, phase.DeltaY);
            var target = raisedPoint.Move(xVector, secondRaiseLength);

            var result = new List<BendPreviewSetting>();

            var verticalBend = source.Copy();
            verticalBend.BusBarThickness = source.BusBarWidth;
            verticalBend.BusBarWidth = source.BusBarThickness;
            verticalBend.StartBusbarVector = lateralVector.Copy();
            verticalBend.WidthVector = xVector.Cross(verticalBend.StartBusbarVector).Negate();
            verticalBend.BarBendingRadius = source.BusBarWidth / 4.0;
            verticalBend.PointGather.Clear();
            verticalBend.PointGather.Add(verticalStart);
            verticalBend.PointGather.Add(firstEnd);
            verticalBend.PointGather.Add(diagonalEnd);
            verticalBend.PointGather.Add(offsetEnd);
            verticalBend.SetPointGather();
            AddIfUsable(result, verticalBend);

            var normalPath = source.Copy();
            normalPath.PointGather.Clear();
            normalPath.PointGather.Add(normalStart);
            normalPath.PointGather.Add(raiseStart);
            normalPath.PointGather.Add(raisedPoint);
            normalPath.PointGather.Add(target);
            normalPath.SetPointGather();
            AddIfUsable(result, normalPath);
            return result;
        }

        private static IReadOnlyList<BendPreviewSetting> BuildOffsetConnection(
            FaceToWallPhasePreviewData phase,
            Point3d start,
            Vector3d xVector,
            Vector3d lateralVector,
            Vector3d verticalVector,
            BendPreviewSetting source)
        {
            var firstStraightLength = RequireDimension(phase, "第一节直排长度@控制骨架草图1");
            var firstBendLength = RequireDimension(phase, "折弯排第一段长度@控制骨架草图1");
            var secondBendLength = RequireDimension(phase, "折弯排第二段长度@控制骨架草图1");

            var overlapLength = firstStraightLength + firstBendLength + secondBendLength - phase.DeltaX;
            if (overlapLength < -0.11 || overlapLength > firstStraightLength + 0.11)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相偏移连接排预览的搭接长度无效：{1:0.0}。",
                        GetPhaseName(phase),
                        overlapLength));
            }

            overlapLength = Math.Max(0, overlapLength);

            var firstEnd = start.Move(xVector, firstStraightLength);
            var finalStart = start
                .Move(xVector, firstStraightLength - overlapLength)
                .Move(lateralVector, phase.DeltaZ);
            var raiseStart = finalStart.Move(xVector, firstBendLength);
            var raisedPoint = raiseStart.Move(verticalVector, phase.DeltaY);
            var target = raisedPoint.Move(xVector, secondBendLength);
            ValidateTarget(phase, start, target, xVector, lateralVector, verticalVector);

            var result = new List<BendPreviewSetting>();
            AddIfUsable(result, CreateSetting(source, start, firstEnd));

            // 偏移连接排由三块共面铜排搭接组成。不能把 X/Y/Z 两个折弯平面放入同一
            // BendPreviewSetting；PreviewBendBody 会把它们投影到首个平面并造成路径重合。
            var offsetDirection = phase.DeltaZ > 0
                ? lateralVector.Copy()
                : lateralVector.Negate();
            var bridgeCenter = firstEnd.Move(xVector.Negate(), overlapLength / 2.0);
            var bridgeStart = bridgeCenter.Move(offsetDirection.Negate(), source.BusBarWidth / 2.0);
            var bridgeEnd = bridgeCenter
                .Move(lateralVector, phase.DeltaZ)
                .Move(offsetDirection, source.BusBarWidth / 2.0);
            var bridge = CreateSetting(source, bridgeStart, bridgeEnd);
            bridge.StartBusbarVector = verticalVector.Copy();
            bridge.WidthVector = bridge.StartBusbarVector
                .Cross(new Vector3d(bridgeStart, bridgeEnd))
                .Negate();
            AddIfUsable(result, bridge);

            var finalPath = source.Copy();
            finalPath.PointGather.Clear();
            finalPath.PointGather.Add(finalStart);
            finalPath.PointGather.Add(raiseStart);
            finalPath.PointGather.Add(raisedPoint);
            finalPath.PointGather.Add(target);
            finalPath.SetPointGather();
            AddIfUsable(result, finalPath);
            return result;
        }

        private static IReadOnlyList<BendPreviewSetting> BuildMilledBar(
            FaceToWallPhasePreviewData phase,
            Point3d start,
            Vector3d xVector,
            Vector3d lateralVector,
            Vector3d verticalVector,
            BendPreviewSetting source)
        {
            var firstBarLength = RequireDimension(phase, "第一段排长度尺寸@控制骨架草图1");
            var firstMilledLength = RequireDimension(phase, "第一段铣排尺寸@控制骨架草图1");
            var secondBendLength = RequireDimension(phase, "折弯排第二段长度@控制骨架草图1");
            ValidateTotalLength(
                phase,
                firstBarLength + secondBendLength,
                "第一段排长度与折弯排第二段长度之和");

            if (firstMilledLength + source.BusBarWidth > firstBarLength)
            {
                throw new InvalidOperationException(GetPhaseName(phase) + "相铣排预览的首段长度不足以容纳铣排过渡段。");
            }

            var firstEnd = start.Move(xVector, firstMilledLength);
            var bridgeStart = firstEnd.Move(lateralVector, phase.DeltaZ / 2.0);
            var bridgeEnd = bridgeStart.Move(xVector, source.BusBarWidth);
            var offsetStart = firstEnd.Move(xVector, source.BusBarWidth).Move(lateralVector, phase.DeltaZ);
            var raiseStart = start.Move(xVector, firstBarLength).Move(lateralVector, phase.DeltaZ);
            var raisedPoint = raiseStart.Move(verticalVector, phase.DeltaY);
            var target = raisedPoint.Move(xVector, secondBendLength);

            var result = new List<BendPreviewSetting>();
            AddIfUsable(result, CreateSetting(source, start, firstEnd));

            var milledBridge = source.Copy();
            milledBridge.BusBarWidth = source.BusBarWidth + phase.OffsetSize;
            milledBridge.PointGather.Clear();
            milledBridge.PointGather.Add(bridgeStart);
            milledBridge.PointGather.Add(bridgeEnd);
            milledBridge.SetPointGather();
            AddIfUsable(result, milledBridge);

            var finalPath = source.Copy();
            finalPath.PointGather.Clear();
            finalPath.PointGather.Add(offsetStart);
            finalPath.PointGather.Add(raiseStart);
            finalPath.PointGather.Add(raisedPoint);
            finalPath.PointGather.Add(target);
            finalPath.SetPointGather();
            AddIfUsable(result, finalPath);
            return result;
        }

        private static IReadOnlyList<BendPreviewSetting> BuildStraightRaiseOnly(
            FaceToWallPhasePreviewData phase,
            Point3d start,
            Vector3d xVector,
            Vector3d verticalVector,
            BendPreviewSetting source)
        {
            var firstLength = RequireDimension(phase, "折弯排第一段长度@控制骨架草图1");
            var secondLength = RequireDimension(phase, "折弯排第二段长度@控制骨架草图1");
            ValidateTotalLength(
                phase,
                firstLength + secondLength,
                "折弯排第一段与第二段长度之和");

            var raiseStart = start.Move(xVector, firstLength);
            var raisedPoint = raiseStart.Move(verticalVector, phase.DeltaY);
            var target = raisedPoint.Move(xVector, secondLength);
            var setting = source.Copy();
            setting.PointGather.Clear();
            setting.PointGather.Add(start);
            setting.PointGather.Add(raiseStart);
            setting.PointGather.Add(raisedPoint);
            setting.PointGather.Add(target);
            setting.SetPointGather();
            return new[] { setting };
        }

        private static BendPreviewSetting CreateSetting(
            BendPreviewSetting source,
            Point3d start,
            Point3d end)
        {
            var setting = source.Copy();
            setting.PointGather.Clear();
            setting.PointGather.Add(start);
            setting.PointGather.Add(end);
            setting.SetPointGather();
            return setting;
        }

        private static void AddIfUsable(ICollection<BendPreviewSetting> target, BendPreviewSetting setting)
        {
            if (setting.PointGather.Count >= 2
                && setting.PointGather[0].DistanceTo(setting.PointGather[setting.PointGather.Count - 1]) >= 0.001)
            {
                target.Add(setting);
            }
        }

        private static double RequireDimension(FaceToWallPhasePreviewData phase, string dimensionName)
        {
            if (phase.DrivenDimensions == null
                || !phase.DrivenDimensions.TryGetValue(dimensionName, out var value))
            {
                throw new InvalidOperationException(
                    string.Format("{0}相{1}预览缺少尺寸“{2}”。", GetPhaseName(phase), phase.TemplateKind, dimensionName));
            }

            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                throw new InvalidOperationException(
                    string.Format("{0}相{1}预览尺寸“{2}”无效：{3}。", GetPhaseName(phase), phase.TemplateKind, dimensionName, value));
            }

            return value;
        }

        private static void ValidateTotalLength(
            FaceToWallPhasePreviewData phase,
            double actualLength,
            string description)
        {
            if (Math.Abs(actualLength - phase.DeltaX) > 0.11)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相{1}预览的{2}（{3:0.0}）与铜排总长（{4:0.0}）不一致。",
                        GetPhaseName(phase),
                        phase.TemplateKind,
                        description,
                        actualLength,
                        phase.DeltaX));
            }
        }

        private static void ValidateTarget(
            FaceToWallPhasePreviewData phase,
            Point3d start,
            Point3d target,
            Vector3d xVector,
            Vector3d lateralVector,
            Vector3d verticalVector)
        {
            var expected = start
                .Move(xVector, phase.DeltaX)
                .Move(lateralVector, phase.DeltaZ)
                .Move(verticalVector, phase.DeltaY);
            if (target.DistanceTo(expected) > 0.11)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相{1}预览路径终点与当前铜排驱动尺寸不一致。",
                        GetPhaseName(phase),
                        phase.TemplateKind));
            }
        }

        private static string GetPhaseName(FaceToWallPhasePreviewData phase)
        {
            return phase.PhaseIndex == 0 ? "A" : phase.PhaseIndex == 1 ? "B" : "C";
        }
    }
}
