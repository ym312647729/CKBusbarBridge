using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace CKBusbarBridge.Drivers
{
    internal static class FaceToFaceFrameDrivePlanner
    {
        private const double TemplateDefaultWidth = 800;
        private const double TemplateDefaultLength = 1000;
        private const double TemplateDefaultHeight = 500;

        internal static IReadOnlyList<FaceToFaceFramePositionPlan> CreatePlans(
            IEnumerable<FaceToFaceFramePlanInput> inputs)
        {
            return CreatePlans(inputs, null, null);
        }

        /// <summary>
        /// 创建目标计划，支持传递组件实例ID和原始源名称，用于二次驱动时的实例级匹配。
        /// </summary>
        /// <param name="inputs">计划输入。</param>
        /// <param name="componentInstanceIds">每个位置对应的组件实例ID列表（与inputs顺序对应）。可为null。</param>
        /// <param name="originalSourceNames">每个位置对应的原始基架名称列表（与inputs顺序对应）。可为null。</param>
        internal static IReadOnlyList<FaceToFaceFramePositionPlan> CreatePlans(
            IEnumerable<FaceToFaceFramePlanInput> inputs,
            IReadOnlyList<string> componentInstanceIds,
            IReadOnlyList<string> originalSourceNames)
        {
            if (inputs == null)
            {
                throw new ArgumentNullException(nameof(inputs));
            }

            // SourceName 是基架物理模型的身份：同名位置共享一份模型，
            // 不同名称即使参数相同也必须保留独立文件夹和装配体。
            var modelPlansBySource = new Dictionary<string, FaceToFaceFrameModelPlan>(
                StringComparer.OrdinalIgnoreCase);
            var sourceNamesByFolder = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            var positionPlans = new List<FaceToFaceFramePositionPlan>();
            var inputIndex = 0;
            foreach (var input in inputs)
            {
                if (input == null)
                {
                    throw new ArgumentException("Frame plan input cannot be null.", nameof(inputs));
                }

                var sourceName = NormalizeSourceName(input.SourceName);
                ValidateFaceToFaceTemplateKind(input.FrameTemplateKind);
                ValidateExplicitCoordinateRoute(
                    input.InletCoordinateName,
                    input.OutletCoordinateName);
                if (input.SmallOffsetFrame != null
                    && input.FrameTemplateKind != FaceToWallFrameTemplateKind.Regular)
                {
                    throw new NotSupportedException("小偏移模型只能使用面对面常规 024 模板身份。");
                }

                ValidatePositiveFinite(nameof(input.Width), input.Width);
                ValidatePositiveFinite(nameof(input.Length), input.Length);
                ValidatePositiveFinite(nameof(input.Height), input.Height);
                var plannedWidth = input.SmallOffsetFrame?.FrameWidth ?? input.Width;
                ValidatePositiveFinite(nameof(input.SmallOffsetFrame), plannedWidth);
                var normalizedDimensions = Tuple.Create(plannedWidth, input.Length, input.Height);
                var normalizedPositions = ValidateAndNormalizeInsulatorLayout(
                    sourceName,
                    input.InsulatorCount,
                    input.InsulatorPositions,
                    normalizedDimensions.Item2);
                var folderName = BuildFolderName(sourceName);
                if (sourceNamesByFolder.TryGetValue(folderName, out var folderOwner)
                    && !string.Equals(folderOwner, sourceName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"基架名称“{folderOwner}”与“{sourceName}”会映射到同一文件夹“{folderName}”。",
                        nameof(inputs));
                }

                sourceNamesByFolder[folderName] = sourceName;
                if (modelPlansBySource.TryGetValue(sourceName, out var modelPlan))
                {
                    if (!HasSameModelParameters(
                        modelPlan,
                        normalizedDimensions.Item1,
                        normalizedDimensions.Item2,
                        normalizedDimensions.Item3,
                        input.InsulatorCount,
                        normalizedPositions,
                        input.FrameTemplateKind,
                        input.TemplateIdentity,
                        input.SmallOffsetFrame))
                    {
                        throw new ArgumentException(
                            $"同一基架来源名称的尺寸或绝缘子布置不一致：{modelPlan.SourceName}。",
                            nameof(inputs));
                    }

                    // Windows 路径不区分大小写，位置数据统一回写首次出现的标准名称。
                    sourceName = modelPlan.SourceName;
                }
                else
                {
                    modelPlan = CreateModelPlan(
                        sourceName,
                        folderName,
                        normalizedDimensions.Item1,
                        normalizedDimensions.Item2,
                        normalizedDimensions.Item3,
                        input.InsulatorCount,
                        normalizedPositions,
                        input.FrameTemplateKind,
                        input.TemplateIdentity,
                        input.SmallOffsetFrame);
                    modelPlansBySource.Add(sourceName, modelPlan);
                }

                // 提取实例身份信息
                var componentInstanceId = componentInstanceIds != null && inputIndex < componentInstanceIds.Count
                    ? componentInstanceIds[inputIndex]
                    : null;
                var originalSourceName = originalSourceNames != null && inputIndex < originalSourceNames.Count
                    ? originalSourceNames[inputIndex]
                    : null;

                positionPlans.Add(new FaceToFaceFramePositionPlan(
                    positionPlans.Count + 1,
                    sourceName,
                    modelPlan,
                    input.IsReversed,
                    input.InletCoordinateName,
                    input.OutletCoordinateName,
                    originalSourceName,
                    componentInstanceId));
                inputIndex++;
            }

            return positionPlans;
        }

        private static FaceToFaceFrameModelPlan CreateModelPlan(
            string sourceName,
            string folderName,
            double width,
            double length,
            double height,
            int insulatorCount,
            IReadOnlyList<double> insulatorPositions,
            FaceToWallFrameTemplateKind frameTemplateKind,
            FrameTemplateIdentity templateIdentity,
            FaceToFaceSmallOffsetFrameDrivePlan smallOffsetFrame)
        {
            var normalizedWidth = width;
            var normalizedLength = length;
            var normalizedHeight = height;
            FaceToFaceSmallOffsetFrameDrivePlan normalizedSmallOffsetFrame = null;
            if (smallOffsetFrame != null)
            {
                ValidateSmallOffsetFrame(smallOffsetFrame);
                normalizedSmallOffsetFrame = new FaceToFaceSmallOffsetFrameDrivePlan(
                    normalizedWidth,
                    smallOffsetFrame.InletOffsetDirection,
                    Normalize(smallOffsetFrame.InletOffsetSize),
                    smallOffsetFrame.OutletOffsetDirection,
                    Normalize(smallOffsetFrame.OutletOffsetSize));
            }

            var isTemplateDefault = frameTemplateKind == FaceToWallFrameTemplateKind.Regular
                && smallOffsetFrame == null
                && normalizedWidth == TemplateDefaultWidth
                && normalizedLength == TemplateDefaultLength
                && normalizedHeight == TemplateDefaultHeight;
            IReadOnlyDictionary<string, object> globalVariables;
            if (smallOffsetFrame != null)
            {
                globalVariables = new Dictionary<string, object>
                {
                    ["基架宽度"] = Math.Abs(normalizedWidth),
                    ["基架长度"] = Math.Abs(normalizedLength),
                    ["基架高度"] = Math.Abs(normalizedHeight),
                    ["进孔偏移方向"] = normalizedSmallOffsetFrame.InletOffsetDirection,
                    ["进孔偏移尺寸"] = normalizedSmallOffsetFrame.InletOffsetSize,
                    ["出孔偏移方向"] = normalizedSmallOffsetFrame.OutletOffsetDirection,
                    ["出孔偏移尺寸"] = normalizedSmallOffsetFrame.OutletOffsetSize
                };
            }
            else if (FaceToFaceFrameTemplateRules.IsFaceToFaceLargeOffsetCorner(frameTemplateKind))
            {
                // 028 的三项尺寸变量始终显式写入，即使数值刚好等于常规 024 默认值。
                globalVariables = new Dictionary<string, object>
                {
                    ["基架宽度"] = Math.Abs(normalizedWidth),
                    ["基架长度"] = Math.Abs(normalizedLength),
                    ["基架高度"] = Math.Abs(normalizedHeight)
                };
            }
            else
            {
                globalVariables = isTemplateDefault
                    ? new Dictionary<string, object>()
                    : new Dictionary<string, object>
                    {
                        ["基架宽度"] = Math.Abs(normalizedWidth),
                        ["基架长度"] = Math.Abs(normalizedLength),
                        ["基架高度"] = Math.Abs(normalizedHeight)
                    };
            }

            return new FaceToFaceFrameModelPlan(
                sourceName,
                folderName,
                normalizedWidth,
                normalizedLength,
                normalizedHeight,
                insulatorCount,
                insulatorPositions,
                isTemplateDefault,
                globalVariables,
                frameTemplateKind,
                smallOffsetFrame != null,
                normalizedSmallOffsetFrame,
                templateIdentity);
        }

        internal static IReadOnlyList<double> ValidateAndNormalizeInsulatorLayout(
            string sourceName,
            int insulatorCount,
            IReadOnlyList<double> positions,
            double normalizedLength)
        {
            if (insulatorCount < 0 || insulatorCount > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(insulatorCount),
                    insulatorCount,
                    $"基架“{sourceName}”的绝缘子数量必须为 0～3。");
            }

            positions = positions ?? Array.Empty<double>();
            if (positions.Count != insulatorCount)
            {
                throw new ArgumentException(
                    $"基架“{sourceName}”的绝缘子数量与定位值数量不一致："
                    + $"数量={insulatorCount}，定位值={positions.Count}。",
                    nameof(positions));
            }

            var normalizedPositions = new List<double>(positions.Count);
            var uniquePositions = new HashSet<double>();
            for (var index = 0; index < positions.Count; index++)
            {
                var position = positions[index];
                if (double.IsNaN(position) || double.IsInfinity(position))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(positions),
                        position,
                        $"基架“{sourceName}”的绝缘子{index + 1}定位必须为有限数值。");
                }

                var normalizedPosition = Normalize(position);
                if (normalizedPosition < 0 || normalizedPosition > normalizedLength)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(positions),
                        position,
                        $"基架“{sourceName}”的绝缘子{index + 1}定位必须在 0～{normalizedLength:0.###} mm 内。");
                }

                if (!uniquePositions.Add(normalizedPosition))
                {
                    throw new ArgumentException(
                        $"基架“{sourceName}”存在重复的绝缘子定位值：{normalizedPosition:0.###} mm。",
                        nameof(positions));
                }

                normalizedPositions.Add(normalizedPosition);
            }

            return normalizedPositions;
        }

        private static bool HasSameModelParameters(
            FaceToFaceFrameModelPlan modelPlan,
            double width,
            double length,
            double height,
            int insulatorCount,
            IReadOnlyList<double> insulatorPositions,
            FaceToWallFrameTemplateKind frameTemplateKind,
            FrameTemplateIdentity templateIdentity,
            FaceToFaceSmallOffsetFrameDrivePlan smallOffsetFrame)
        {
            var candidate = CreateModelPlan(modelPlan.SourceName, modelPlan.FolderName,
                width, length, height, insulatorCount, insulatorPositions,
                frameTemplateKind, templateIdentity, smallOffsetFrame);
            return FaceToFaceFramePhysicalIdentity.ModelParametersEqual(modelPlan, candidate);
        }

        /// <summary>
        /// 在任何磁盘或 SolidWorks 操作前拒绝面对墙模板或未知枚举值。
        /// </summary>
        private static void ValidateFaceToFaceTemplateKind(
            FaceToWallFrameTemplateKind frameTemplateKind)
        {
            if (!FaceToFaceFrameTemplateRules.IsFaceToFaceSupported(frameTemplateKind))
            {
                throw new NotSupportedException(
                    $"面对面基架不支持模板类型：{frameTemplateKind}。");
            }
        }

        /// <summary>
        /// 面对面物理件只能使用同编号的进/出坐标系组合。
        /// </summary>
        private static void ValidateExplicitCoordinateRoute(
            string inletCoordinateName,
            string outletCoordinateName)
        {
            var usesRoute1 = string.Equals(
                    inletCoordinateName,
                    "坐标系-进1",
                    StringComparison.Ordinal)
                && string.Equals(
                    outletCoordinateName,
                    "坐标系-出1",
                    StringComparison.Ordinal);
            var usesRoute2 = string.Equals(
                    inletCoordinateName,
                    "坐标系-进2",
                    StringComparison.Ordinal)
                && string.Equals(
                    outletCoordinateName,
                    "坐标系-出2",
                    StringComparison.Ordinal);
            if (!usesRoute1 && !usesRoute2)
            {
                throw new ArgumentException(
                    $"面对面基架坐标系路由无效：{inletCoordinateName ?? "<空>"}/{outletCoordinateName ?? "<空>"}。");
            }
        }

        private static void ValidateSmallOffsetFrame(FaceToFaceSmallOffsetFrameDrivePlan plan)
        {
            if ((plan.InletOffsetDirection != 0 && plan.InletOffsetDirection != 1)
                || (plan.OutletOffsetDirection != 0 && plan.OutletOffsetDirection != 1))
            {
                throw new ArgumentOutOfRangeException(nameof(plan), "偏移方向只能为 0 或 1。");
            }

            ValidateNonNegativeFinite(nameof(plan.InletOffsetSize), plan.InletOffsetSize);
            ValidateNonNegativeFinite(nameof(plan.OutletOffsetSize), plan.OutletOffsetSize);
        }

        private static void ValidateNonNegativeFinite(string parameterName, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(parameterName, value, "偏移尺寸必须是有限非负值。");
            }
        }

        private static string NormalizeSourceName(string sourceName)
        {
            return FaceToFaceFrameSourceIdentity.NormalizeAndValidate(sourceName);
        }

        private static string BuildFolderName(string sourceName)
        {
            // 反读时以直属目录名恢复 SourceName，因此逻辑名称与磁盘目录必须可无损往返。
            return sourceName;
        }

        private static double Normalize(double value)
        {
            return Math.Round(value, 3, MidpointRounding.AwayFromZero);
        }

        private static void ValidatePositiveFinite(string parameterName, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    "Frame dimensions must be finite positive values.");
            }
        }
    }

    /// <summary>
    /// 统一约束面对面基架的逻辑身份和 Windows 直属目录身份。
    /// 不对非法字符做静默替换，避免重开界面后 SourceName 发生变化。
    /// </summary>
    internal static class FaceToFaceFrameSourceIdentity
    {
        private static readonly HashSet<string> WindowsReservedNames
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };

        internal static string NormalizeAndValidate(string sourceName)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                throw new ArgumentException("Source name cannot be blank.", nameof(sourceName));
            }

            var normalized = sourceName.Trim();
            var reservedStem = normalized.Split('.')[0];
            if (normalized == "."
                || normalized == ".."
                || normalized.EndsWith(".", StringComparison.Ordinal)
                || normalized.EndsWith(" ", StringComparison.Ordinal)
                || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || WindowsReservedNames.Contains(reservedStem))
            {
                throw new ArgumentException(
                    $"基架名称“{normalized}”不能作为 Windows 基架文件夹名称。",
                    nameof(sourceName));
            }

            return normalized;
        }

        internal static void ValidateModelPlanIdentity(FaceToFaceFrameModelPlan modelPlan)
        {
            if (modelPlan == null)
            {
                throw new ArgumentNullException(nameof(modelPlan));
            }

            var normalizedSourceName = NormalizeAndValidate(modelPlan.SourceName);
            if (!string.Equals(modelPlan.SourceName, normalizedSourceName, StringComparison.Ordinal)
                || !string.Equals(modelPlan.FolderName, normalizedSourceName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"基架“{modelPlan.SourceName}”的逻辑名称与直属文件夹身份不一致：{modelPlan.FolderName}");
            }
        }
    }
}
