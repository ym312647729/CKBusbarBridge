using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 读取时校验旧基架自身的尺寸、结构与模板身份，提交时再对照当前柜体/基座目标。
    /// 该校验只接受总装配合、模型全局变量和模板身份的真实反读结果。
    /// </summary>
    internal static class SideToSideFrameReadbackValidator
    {
        private const double Tolerance = 1.0;

        internal static void Validate(
            SideToSideFrameInitialization initialization,
            FaceToFaceFrameReadResult result,
            IReadOnlyDictionary<string, FrameTemplateIdentity>
                mappedIdentitiesByComponentFullName = null,
            bool requireExpectedHeight = true,
            bool requireExpectedEccentricity = true)
        {
            if (initialization == null)
            {
                throw new ArgumentNullException(nameof(initialization));
            }

            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            if (!result.HasFrames)
            {
                return;
            }

            if (!requireExpectedEccentricity)
            {
                // 偏心改变可能同时切换 029/024/032/028，旧模型只能按自身结构校验。
                initialization = CreatePhysicalInitialization(initialization, result);
            }

            EnsureSame(
                "总基架长",
                initialization.TotalFrameLength,
                result.TotalLength);
            foreach (var frame in result.Frames)
            {
                if (double.IsNaN(frame.Height) || double.IsInfinity(frame.Height)
                    || frame.Height <= 0d)
                {
                    throw new InvalidOperationException(
                        "基架高度（" + frame.ComponentInstanceId + "）必须为有限正数。");
                }

                // 基座已更新而基架尚未二次驱动时，旧高度仍是有效的物理快照。
                // 只有最终提交校验才要求实际高度等于当前基座推导的目标高度。
                if (requireExpectedHeight)
                {
                    EnsureSame(
                        "基架高度（" + frame.ComponentInstanceId + "）",
                        initialization.FrameHeight,
                        frame.Height);
                }
                ValidateMappedIdentity(frame, mappedIdentitiesByComponentFullName);
            }

            if (initialization.BaseKind == SideToSideFrameBaseKind.Base1)
            {
                ValidateBase1(initialization, result);
                return;
            }

            ValidateBase2(initialization, result, requireExpectedEccentricity);
        }

        private static SideToSideFrameInitialization CreatePhysicalInitialization(
            SideToSideFrameInitialization target,
            FaceToFaceFrameReadResult result)
        {
            if (result.Frames.Any(frame => frame == null
                || !IsPositiveFinite(frame.Width) || !IsPositiveFinite(frame.Length)))
            {
                throw new InvalidOperationException("旧基架的宽度和长度必须为有限正数。");
            }

            var physical = new SideToSideFrameInitialization
            {
                BaseKind = result.Frames.Any(frame =>
                    frame.TemplateIdentity == FrameTemplateIdentity.SideToSidePhaseChange029)
                        ? SideToSideFrameBaseKind.Base1 : SideToSideFrameBaseKind.Base2,
                OffsetKind = result.HasLargeOffsetGroup ? SideToSideFrameOffsetKind.Large
                    : result.Frames.Any(frame => frame.IsSmallOffsetFrame)
                        ? SideToSideFrameOffsetKind.Small : SideToSideFrameOffsetKind.None,
                TotalFrameLength = result.TotalLength,
                FrameHeight = target.FrameHeight,
                FrameWidth = result.Frames[0].Width,
                CabinetEccentricity = result.SignedEccentricity
            };
            if (physical.OffsetKind == SideToSideFrameOffsetKind.Large)
            {
                physical.ACabinetBaseWidth = result.Frames.Single(frame =>
                    frame.LargeOffsetRole == FaceToFaceLargeOffsetFrameRole.ASideCorner).Width;
                physical.BCabinetBaseWidth = result.Frames.Single(frame =>
                    frame.LargeOffsetRole == FaceToFaceLargeOffsetFrameRole.BSideCorner).Width;
            }
            else if (physical.OffsetKind == SideToSideFrameOffsetKind.Small)
            {
                if (FaceToFaceSmallOffsetFramePlanner.TryReadWidened(result.Frames,
                    out var signed, out var aWidth, out var bWidth))
                {
                    physical.CabinetEccentricity = signed;
                    physical.ACabinetBaseWidth = aWidth;
                    physical.BCabinetBaseWidth = bWidth;
                    return physical;
                }
                physical.IsLegacySmallOffsetStructure = true;
                var offset = result.Frames.Single(frame => frame.IsSmallOffsetFrame);
                var plan = offset.SmallOffsetFrame;
                physical.ACabinetBaseWidth = offset.Width - 2d * plan.OutletOffsetSize;
                physical.BCabinetBaseWidth = offset.Width - 2d * plan.InletOffsetSize;
                physical.CabinetEccentricity = (plan.InletOffsetDirection == 0 ? 1d : -1d)
                    * (plan.InletOffsetSize + plan.OutletOffsetSize);
                if (!IsPositiveFinite(physical.ACabinetBaseWidth)
                    || !IsPositiveFinite(physical.BCabinetBaseWidth)
                    || !IsPositiveFinite(Math.Abs(physical.CabinetEccentricity)))
                {
                    throw new InvalidOperationException("小偏移基架的物理孔偏移尺寸无效。");
                }
            }
            return physical;
        }

        private static bool IsPositiveFinite(double value)
        {
            return value > 0d && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static void ValidateBase1(
            SideToSideFrameInitialization initialization,
            FaceToFaceFrameReadResult result)
        {
            if (result.Frames.Count < 2)
            {
                throw new InvalidOperationException(
                    "侧对侧第一种固定换相至少需要2节基架，当前仅"
                    + result.Frames.Count + "节。");
            }

            foreach (var frame in result.Frames)
            {
                if (!frame.HasExplicitTemplateIdentity
                    || frame.TemplateIdentity
                        != FrameTemplateIdentity.SideToSidePhaseChange029)
                {
                    throw new InvalidOperationException(
                        "侧对侧基座1基架模板身份无效：位置="
                        + frame.ComponentInstanceId
                        + "；路径=" + frame.ModelPath + "。");
                }

                if (frame.IsSmallOffsetFrame || frame.IsLargeOffsetMember)
                {
                    throw new InvalidOperationException(
                        "侧对侧基座1不能包含偏移基架：位置="
                        + frame.ComponentInstanceId + "。");
                }

                EnsureSame(
                    "基座1基架宽度（" + frame.ComponentInstanceId + "）",
                    initialization.FrameWidth,
                    frame.Width);
            }
        }

        private static void ValidateBase2(
            SideToSideFrameInitialization initialization,
            FaceToFaceFrameReadResult result,
            bool requireExpectedEccentricity)
        {
            foreach (var frame in result.Frames)
            {
                var expectedIdentity = FrameTemplateIdentityMetadata
                    .FromLegacyStructure(
                        frame.FrameTemplateKind,
                        frame.IsSmallOffsetFrame);
                if (frame.HasExplicitTemplateIdentity
                    && frame.TemplateIdentity != expectedIdentity)
                {
                    throw new InvalidOperationException(
                        "侧对侧基座2基架模板身份与物理结构不一致：位置="
                        + frame.ComponentInstanceId + "；路径=" + frame.ModelPath + "。");
                }
            }

            switch (initialization.OffsetKind)
            {
                case SideToSideFrameOffsetKind.None:
                    ValidateBase2Regular(initialization, result);
                    break;
                case SideToSideFrameOffsetKind.Small:
                    ValidateBase2SmallOffset(initialization, result);
                    break;
                case SideToSideFrameOffsetKind.Large:
                    ValidateBase2LargeOffset(initialization, result, requireExpectedEccentricity);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(initialization.OffsetKind));
            }
        }

        private static void ValidateBase2Regular(
            SideToSideFrameInitialization initialization,
            FaceToFaceFrameReadResult result)
        {
            if (result.Frames.Any(frame =>
                frame.IsSmallOffsetFrame || frame.IsLargeOffsetMember))
            {
                throw new InvalidOperationException(
                    "侧对侧基座2无偏移场景不能包含偏移基架结构。");
            }

            foreach (var frame in result.Frames)
            {
                EnsureSame(
                    "基座2无偏移基架宽度（" + frame.ComponentInstanceId + "）",
                    initialization.FrameWidth,
                    frame.Width);
            }
        }

        private static void ValidateBase2SmallOffset(
            SideToSideFrameInitialization initialization,
            FaceToFaceFrameReadResult result)
        {
            if (result.HasLargeOffsetGroup)
                throw new InvalidOperationException("侧对侧基座2小偏移场景不能包含 028 大偏移组。");
            if (!initialization.IsLegacySmallOffsetStructure
                && CKBusbarBridge.DesignGeometry.BridgeOffsetRules.IsWidened(initialization.CabinetEccentricity))
            {
                if (!FaceToFaceSmallOffsetFramePlanner.TryReadWidened(result.Frames,
                    out var signed, out var aWidth, out var bWidth))
                    throw new InvalidOperationException("整段扩宽场景必须为同宽基架链，两端使用单端封板小偏移模板。");
                EnsureSame("整段扩宽偏移", initialization.CabinetEccentricity, signed);
                EnsureSame("A端连接基座宽", initialization.ACabinetBaseWidth, aWidth);
                EnsureSame("B端连接基座宽", initialization.BCabinetBaseWidth, bWidth);
                EnsureSame("整段扩宽基架宽", (aWidth + bWidth) / 2d + Math.Abs(signed), result.Frames[0].Width);
                return;
            }

            var offsetFrames = result.Frames
                .Select((frame, index) => new { Frame = frame, Index = index })
                .Where(item => item.Frame.IsSmallOffsetFrame)
                .ToList();
            if (offsetFrames.Count != 1)
            {
                throw new InvalidOperationException(
                    "侧对侧基座2小偏移场景必须且只能有一段小偏移基架。");
            }

            var offsetItem = offsetFrames[0];
            var expectedWidth =
                (initialization.ACabinetBaseWidth
                    + initialization.BCabinetBaseWidth) / 2d
                + Math.Abs(initialization.CabinetEccentricity);
            EnsureSame(
                "小偏移基架宽度（" + offsetItem.Frame.ComponentInstanceId + "）",
                expectedWidth,
                offsetItem.Frame.Width);

            for (var index = 0; index < result.Frames.Count; index++)
            {
                if (index == offsetItem.Index)
                {
                    continue;
                }

                var expectedSideWidth = index < offsetItem.Index
                    ? initialization.BCabinetBaseWidth
                    : initialization.ACabinetBaseWidth;
                EnsureSame(
                    "小偏移柜侧基架宽度（"
                        + result.Frames[index].ComponentInstanceId + "）",
                    expectedSideWidth,
                    result.Frames[index].Width);
            }

            ValidateSmallOffsetDirectionsAndSizes(initialization, offsetItem.Frame);
        }

        private static void ValidateSmallOffsetDirectionsAndSizes(
            SideToSideFrameInitialization initialization,
            FaceToFaceFrameReadItem offsetFrame)
        {
            var plan = offsetFrame.SmallOffsetFrame;
            if (plan == null || double.IsNaN(plan.InletOffsetSize) || double.IsInfinity(plan.InletOffsetSize)
                || double.IsNaN(plan.OutletOffsetSize) || double.IsInfinity(plan.OutletOffsetSize)
                || plan.InletOffsetSize < 0d || plan.OutletOffsetSize < 0d)
            {
                throw new InvalidOperationException("小偏移基架的进/出孔偏移尺寸必须为有限非负数。");
            }
            EnsureSame("小偏移模型宽度", offsetFrame.Width, plan.FrameWidth);
            if (Models.RdTypeMetadata.IsLargeOffset(
                initialization.CabinetEccentricity, initialization.ACabinetBaseWidth, initialization.BCabinetBaseWidth))
            {
                throw new InvalidOperationException("小偏移物理结构的偏心值不能达到大偏移阈值。");
            }
            var rightOffset = initialization.CabinetEccentricity > 0d;
            var expectedInletDirection = rightOffset ? 0 : 1;
            var expectedOutletDirection = rightOffset ? 1 : 0;
            if (plan.InletOffsetDirection != expectedInletDirection
                || plan.OutletOffsetDirection != expectedOutletDirection)
            {
                throw new InvalidOperationException(
                    "侧对侧小偏移基架进/出孔方向与偏移符号不一致：位置="
                    + offsetFrame.ComponentInstanceId + "。");
            }

            EnsureSame(
                "小偏移进孔偏移尺寸",
                (offsetFrame.Width - initialization.BCabinetBaseWidth) / 2d,
                plan.InletOffsetSize);
            EnsureSame(
                "小偏移出孔偏移尺寸",
                (offsetFrame.Width - initialization.ACabinetBaseWidth) / 2d,
                plan.OutletOffsetSize);
        }

        private static void ValidateBase2LargeOffset(
            SideToSideFrameInitialization initialization,
            FaceToFaceFrameReadResult result,
            bool requireExpectedEccentricity)
        {
            if (!result.HasLargeOffsetGroup
                || result.Frames.Any(frame => frame.IsSmallOffsetFrame))
            {
                throw new InvalidOperationException(
                    "侧对侧基座2大偏移场景必须包含唯一 028 大偏移组。");
            }

            // 柜体已驱动而基架待同步时，旧偏心仍是有效快照；目标比较留到最终提交。
            if (requireExpectedEccentricity)
            {
                EnsureSame(
                    "大偏移带符号偏心值",
                    initialization.CabinetEccentricity,
                    result.SignedEccentricity);
            }

            var group = result.Frames.Where(frame => frame.IsLargeOffsetMember).ToList();
            var bCorners = group.Where(frame =>
                frame.LargeOffsetRole == FaceToFaceLargeOffsetFrameRole.BSideCorner).ToList();
            var aCorners = group.Where(frame =>
                frame.LargeOffsetRole == FaceToFaceLargeOffsetFrameRole.ASideCorner).ToList();
            var connectors = group.Where(frame =>
                frame.LargeOffsetRole == FaceToFaceLargeOffsetFrameRole.Connector).ToList();
            if (bCorners.Count != 1 || aCorners.Count != 1 || connectors.Count == 0)
            {
                throw new InvalidOperationException(
                    "侧对侧基座2大偏移组必须包含 B 侧拐角、连接段和 A 侧拐角。");
            }

            // 基座宽度较小值只用于初始化；界面允许同步修改拐角长度和连接基架宽度。
            // 回读以物理共享尺寸为准，仍须保证整组一致，不能把默认值当成固定约束。
            var sharedLength = bCorners[0].Length;
            if (double.IsNaN(sharedLength)
                || double.IsInfinity(sharedLength)
                || sharedLength <= 0d)
            {
                throw new InvalidOperationException(
                    "大偏移拐角共享长度必须为有限正数：B 侧拐角="
                    + bCorners[0].ComponentInstanceId + "。");
            }

            EnsureSame("B 侧拐角宽度", initialization.BCabinetBaseWidth, bCorners[0].Width);
            EnsureSame("A 侧拐角宽度", initialization.ACabinetBaseWidth, aCorners[0].Width);
            EnsureSame("A 侧拐角共享长度", sharedLength, aCorners[0].Length);
            foreach (var connector in connectors)
            {
                EnsureSame("大偏移连接基架宽度", sharedLength, connector.Width);
            }

            var expectedConnectorSpan = Math.Abs(result.SignedEccentricity)
                - (initialization.ACabinetBaseWidth
                    + initialization.BCabinetBaseWidth) / 2d;
            if (double.IsNaN(expectedConnectorSpan)
                || double.IsInfinity(expectedConnectorSpan)
                || expectedConnectorSpan < FaceToFaceLargeOffsetFramePlanner.MinimumConnectorSpan)
            {
                throw new InvalidOperationException(
                    "大偏移物理结构的连接空间不足，无法容纳最短 200 mm 连接基架。");
            }
            EnsureSame(
                "大偏移连接段总长",
                expectedConnectorSpan,
                connectors.Sum(frame => frame.Length));

            var firstGroupIndex = result.Frames.ToList().FindIndex(frame =>
                frame.IsLargeOffsetMember);
            var lastGroupIndex = result.Frames.ToList().FindLastIndex(frame =>
                frame.IsLargeOffsetMember);
            for (var index = 0; index < result.Frames.Count; index++)
            {
                if (result.Frames[index].IsLargeOffsetMember)
                {
                    continue;
                }

                EnsureSame(
                    "大偏移柜侧基架宽度（"
                        + result.Frames[index].ComponentInstanceId + "）",
                    index < firstGroupIndex
                        ? initialization.BCabinetBaseWidth
                        : initialization.ACabinetBaseWidth,
                    result.Frames[index].Width);
            }
        }

        private static void ValidateMappedIdentity(
            FaceToFaceFrameReadItem frame,
            IReadOnlyDictionary<string, FrameTemplateIdentity> mappedIdentities)
        {
            if (mappedIdentities == null
                || !mappedIdentities.TryGetValue(
                    frame.ComponentInstanceId,
                    out var mappedIdentity))
            {
                return;
            }

            if (!frame.HasExplicitTemplateIdentity
                || frame.TemplateIdentity != mappedIdentity)
            {
                throw new InvalidOperationException(
                    "基架模型属性与模型对照映射中的模板身份冲突：位置="
                    + frame.ComponentInstanceId + "；路径=" + frame.ModelPath + "。");
            }
        }

        private static void EnsureSame(
            string parameterName,
            double expected,
            double actual)
        {
            if (double.IsNaN(expected) || double.IsInfinity(expected)
                || double.IsNaN(actual)
                || double.IsInfinity(actual)
                || Math.Abs(expected - actual) > Tolerance)
            {
                throw new InvalidOperationException(
                    parameterName + "与当前柜体/基座物理输入不一致：期望="
                    + expected.ToString("0.###") + " mm；实际="
                    + actual.ToString("0.###") + " mm。");
            }
        }
    }
}
