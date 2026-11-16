using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 面对墙基架纯计算规划。同源看宽、长、高、基架种类，以及常规基架的有序绝缘子布置。
    /// 正反装只决定进出坐标系；024/026/027均按有效数量装配内部绝缘子。
    /// </summary>
    internal static class FaceToWallFrameDrivePlanner
    {
        internal const double RegularTemplateDefaultWidth = 800;
        internal const double RegularTemplateDefaultLength = 1000;
        internal const double RegularTemplateDefaultHeight = 500;
        internal const double LargeTemplateDefaultWidth = 1950;
        internal const double LargeTemplateDefaultLength = 1300;
        internal const double LargeTemplateDefaultHeight = 810;

        internal const string RegularFrameWidthVariableName = "基架宽度";
        internal const string RegularFrameLengthVariableName = "基架长度";
        internal const string RegularFrameHeightVariableName = "基架高度";
        // 大基架1/2模板已与常规基架统一尺寸全局变量名，
        // 驱动与模型反读必须共用同一组名称。
        internal const string LargeFrameWidthVariableName = RegularFrameWidthVariableName;
        internal const string LargeFrameLengthVariableName = RegularFrameLengthVariableName;
        internal const string LargeFrameHeightVariableName = RegularFrameHeightVariableName;
        internal const string UpperSealingPlateWidthVariableName = "上侧封板宽度";
        internal const string LowerSealingPlateWidthVariableName = "下侧封板宽度";
        internal const string SideCoverPlateWidthVariableName = "侧盖板宽度";
        internal const string WallEntryDistancePropertyName = "进墙距离";

        internal static IReadOnlyList<FaceToFaceFramePositionPlan> CreatePlans(
            IEnumerable<FaceToWallFramePlanInput> inputs,
            double regularFrameWidth,
            double wallCabinetEccentricity)
        {
            return CreatePlans(
                inputs,
                regularFrameWidth,
                wallCabinetEccentricity,
                0d);
        }

        internal static IReadOnlyList<FaceToFaceFramePositionPlan> CreatePlans(
            IEnumerable<FaceToWallFramePlanInput> inputs,
            double regularFrameWidth,
            double wallCabinetEccentricity,
            double baseOpeningHeight)
        {
            return CreatePlans(inputs, regularFrameWidth, wallCabinetEccentricity, baseOpeningHeight, null, null);
        }

        /// <summary>
        /// 创建面对墙基架目标计划，支持传递组件实例ID和原始源名称，用于二次驱动时的实例级匹配。
        /// </summary>
        internal static IReadOnlyList<FaceToFaceFramePositionPlan> CreatePlans(
            IEnumerable<FaceToWallFramePlanInput> inputs,
            double regularFrameWidth,
            double wallCabinetEccentricity,
            double baseOpeningHeight,
            IReadOnlyList<string> componentInstanceIds,
            IReadOnlyList<string> originalSourceNames)
        {
            if (inputs == null)
            {
                throw new ArgumentNullException(nameof(inputs));
            }

            // Windows 目录和二次快照都把来源名按大小写不敏感处理；
            // 首次规划必须保持同样语义，防止 FrameA/framea 绕过同源模板冲突校验。
            var identityBySource = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            var modelPlans = new Dictionary<string, FaceToFaceFrameModelPlan>(StringComparer.OrdinalIgnoreCase);
            var positionPlans = new List<FaceToFaceFramePositionPlan>();
            var inputIndex = 0;
            var normalizedRegularWidth = Normalize(regularFrameWidth);
            var normalizedEccentricity = Normalize(wallCabinetEccentricity);
            foreach (var input in inputs)
            {
                if (input == null)
                {
                    throw new ArgumentException("基架计划输入不能为空。", nameof(inputs));
                }

                var sourceName = NormalizeSourceName(input.SourceName);
                if (!Enum.IsDefined(typeof(FaceToWallFrameTemplateKind), input.TemplateKind)
                    || !FaceToFaceFrameTemplateRules.IsFaceToWallSupported(input.TemplateKind))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(inputs),
                        input.TemplateKind,
                        $"基架“{sourceName}”的模板类型无效。");
                }

                // 面对墙大基架模板只有一套进/出坐标系，不能像常规基架一样切换到反装坐标系。
                // 在计算模型身份和执行任何文件/SolidWorks 操作前拒绝非法状态。
                if (input.TemplateKind != FaceToWallFrameTemplateKind.Regular
                    && input.IsReversed)
                {
                    throw new ArgumentException(
                        $"{sourceName}使用{input.TemplateKind}模板，不支持反装。",
                        nameof(inputs));
                }

                ValidatePositiveFinite("宽度", input.Width);
                ValidatePositiveFinite("长度", input.Length);
                ValidatePositiveFinite("高度", input.Height);
                var width = Normalize(input.Width);
                var length = Normalize(input.Length);
                var height = Normalize(input.Height);
                var wallHorizontalClearance = input.TemplateKind == FaceToWallFrameTemplateKind.Large1
                    ? Normalize(input.WallHorizontalClearance)
                    : 0d;
                var wallVerticalClearance = Normalize(input.WallVerticalClearance);
                var wallEntryDistance = input.TemplateKind == FaceToWallFrameTemplateKind.Large1
                    ? Normalize(input.WallEntryDistance)
                    : 0d;
                ValidateFinite(nameof(input.WallVerticalClearance), wallVerticalClearance);
                height = Normalize(height - wallVerticalClearance * 2d);
                ValidatePositiveFinite("补偿后高度", height);
                if (input.TemplateKind == FaceToWallFrameTemplateKind.Large1)
                {
                    ValidateFinite(nameof(input.WallHorizontalClearance), wallHorizontalClearance);
                    ValidateFinite(nameof(input.WallEntryDistance), wallEntryDistance);

                    // 左右间隙和进墙距离只补偿大基架1；上下间隙由全部面对墙基架统一补偿。
                    // 三项补偿都只参与尺寸计算，不写入同名模型全局变量。
                    width = Normalize(width - wallHorizontalClearance * 2d);
                    length = Normalize(length + wallEntryDistance);
                    ValidatePositiveFinite("补偿后宽度", width);
                    ValidatePositiveFinite("补偿后长度", length);
                }
                ValidateMinimumLength(input.TemplateKind, length);
                // 026/027与常规基架共用内部绝缘子驱动路由；无合法支撑时数量为0。
                var supportsInternalInsulators =
                    FaceToFaceFrameTemplateRules.SupportsInternalInsulators(input.TemplateKind);
                var insulatorCount = supportsInternalInsulators ? input.InsulatorCount : 0;
                var insulatorPositions = supportsInternalInsulators
                    ? FaceToFaceFrameDrivePlanner.ValidateAndNormalizeInsulatorLayout(
                        sourceName,
                        insulatorCount,
                        input.InsulatorPositions,
                        length)
                    : Array.Empty<double>();
                var modelIdentity = BuildModelIdentity(
                    width,
                    length,
                    height,
                    input.TemplateKind,
                    insulatorCount,
                    insulatorPositions) + "|entry=" + wallEntryDistance.ToString("0.###", CultureInfo.InvariantCulture);

                if (identityBySource.TryGetValue(sourceName, out var existingIdentity)
                    && !string.Equals(existingIdentity, modelIdentity, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"同一基架来源名称对应不同的模型身份：{sourceName}。",
                        nameof(inputs));
                }

                identityBySource[sourceName] = modelIdentity;
                var candidatePlan = CreateModelPlan(sourceName, width, length, height,
                    insulatorCount, insulatorPositions, input.TemplateKind, normalizedRegularWidth,
                    normalizedEccentricity, baseOpeningHeight, wallEntryDistance);
                if (!modelPlans.TryGetValue(sourceName, out var modelPlan))
                {
                    modelPlan = candidatePlan;
                    modelPlans.Add(sourceName, modelPlan);
                }
                else if (!FaceToFaceFramePhysicalIdentity.ModelParametersEqual(modelPlan, candidatePlan))
                {
                    throw new ArgumentException("同一基架名称对应不同的模型参数：" + sourceName, nameof(inputs));
                }
                sourceName = modelPlan.SourceName;

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
                    input.IsReversed ? "坐标系-进2" : "坐标系-进1",
                    input.IsReversed ? "坐标系-出2" : "坐标系-出1",
                    originalSourceName,
                    componentInstanceId));
                inputIndex++;
            }

            return positionPlans;
        }

        internal static IReadOnlyDictionary<string, object> BuildLargeFrameOpeningVariables(
            double largeFrameWidth,
            double regularFrameWidth,
            double wallCabinetEccentricity)
        {
            var halfWidthDifference = (Normalize(largeFrameWidth) - Normalize(regularFrameWidth)) / 2.0;
            var eccentricity = Normalize(wallCabinetEccentricity);
            return new Dictionary<string, object>
            {
                [UpperSealingPlateWidthVariableName] = Normalize(halfWidthDifference + eccentricity),
                [LowerSealingPlateWidthVariableName] = Normalize(halfWidthDifference - eccentricity)
            };
        }

        private static FaceToFaceFrameModelPlan CreateModelPlan(
            string sourceName,
            double width,
            double length,
            double height,
            int insulatorCount,
            IReadOnlyList<double> insulatorPositions,
            FaceToWallFrameTemplateKind templateKind,
            double regularFrameWidth,
            double wallCabinetEccentricity,
            double baseOpeningHeight,
            double wallEntryDistance)
        {
            var isTemplateDefault = templateKind == FaceToWallFrameTemplateKind.Regular
                && width == RegularTemplateDefaultWidth
                && length == RegularTemplateDefaultLength
                && height == RegularTemplateDefaultHeight;
            var globalVariables = isTemplateDefault
                ? new Dictionary<string, object>()
                : CreateDriveVariables(
                    width,
                    length,
                    height,
                    templateKind,
                    regularFrameWidth,
                    wallCabinetEccentricity,
                    baseOpeningHeight);

            return new FaceToFaceFrameModelPlan(
                sourceName,
                BuildFolderName(sourceName),
                width,
                length,
                height,
                insulatorCount,
                insulatorPositions,
                isTemplateDefault,
                globalVariables,
                templateKind,
                wallEntryDistance: wallEntryDistance);
        }

        private static Dictionary<string, object> CreateDriveVariables(
            double width,
            double length,
            double height,
            FaceToWallFrameTemplateKind templateKind,
            double regularFrameWidth,
            double wallCabinetEccentricity,
            double baseOpeningHeight)
        {
            if (templateKind == FaceToWallFrameTemplateKind.Large1)
            {
                ValidatePositiveFinite("常规基架宽度", regularFrameWidth);
                var variables = new Dictionary<string, object>
                {
                    [LargeFrameWidthVariableName] = Math.Abs(width),
                    [LargeFrameLengthVariableName] = Math.Abs(length),
                    [LargeFrameHeightVariableName] = Math.Abs(height)
                };
                foreach (var openingVariable in BuildLargeFrameOpeningVariables(
                    width,
                    regularFrameWidth,
                    wallCabinetEccentricity))
                {
                    variables[openingVariable.Key] = openingVariable.Value;
                }

                return variables;
            }

            if (templateKind == FaceToWallFrameTemplateKind.Large2)
            {
                ValidatePositiveFinite("常规基架宽度", regularFrameWidth);
                ValidatePositiveFinite("基架高度", baseOpeningHeight);
                FaceToWallFrameLengthPlanner.RequiresLargeFrame2(baseOpeningHeight, height);
                return new Dictionary<string, object>
                {
                    [LargeFrameLengthVariableName] = Math.Abs(length),
                    [LargeFrameWidthVariableName] = Math.Abs(width),
                    [LargeFrameHeightVariableName] = Math.Abs(height),
                    [SideCoverPlateWidthVariableName] = Normalize(
                        Math.Max(0d, height - Normalize(baseOpeningHeight)))
                };

            }

            return new Dictionary<string, object>
            {
                [RegularFrameWidthVariableName] = Math.Abs(width),
                [RegularFrameLengthVariableName] = Math.Abs(length),
                [RegularFrameHeightVariableName] = Math.Abs(height)
            };
        }

        private static string BuildModelIdentity(
            double width,
            double length,
            double height,
            FaceToWallFrameTemplateKind templateKind,
            int insulatorCount,
            IEnumerable<double> insulatorPositions)
        {
            // 常规基架必须把有序绝缘子布置纳入身份，否则相同外形、不同定位会错误复用。
            return string.Join(
                "|",
                new[]
                {
                    width.ToString("R", CultureInfo.InvariantCulture),
                    length.ToString("R", CultureInfo.InvariantCulture),
                    height.ToString("R", CultureInfo.InvariantCulture),
                    templateKind.ToString(),
                    insulatorCount.ToString(CultureInfo.InvariantCulture),
                    string.Join(
                        ",",
                        insulatorPositions.Select(position =>
                            position.ToString("R", CultureInfo.InvariantCulture)))
                });
        }

        private static string NormalizeSourceName(string sourceName)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                throw new ArgumentException("基架来源名称不能为空。", nameof(sourceName));
            }

            return sourceName.Trim();
        }

        private static string BuildFolderName(string sourceName)
        {
            var folderName = sourceName;
            foreach (var invalidFileNameCharacter in Path.GetInvalidFileNameChars())
            {
                folderName = folderName.Replace(invalidFileNameCharacter, '_');
            }

            return folderName;
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
                    "基架尺寸必须为有限正数。");
            }
        }

        private static void ValidateFinite(string parameterName, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    "墙体间隙参数必须为有限数值。");
            }
        }

        private static void ValidateMinimumLength(
            FaceToWallFrameTemplateKind templateKind,
            double length)
        {
            var minimumLength = templateKind == FaceToWallFrameTemplateKind.Regular
                ? FaceToWallFrameLengthPlanner.RegularMinimumLength
                : FaceToWallFrameLengthPlanner.LargeMinimumLength;
            if (length < minimumLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(length),
                    length,
                    $"{templateKind} 基架长度不能小于 {minimumLength:0.##} mm。");
            }
        }
    }

    /// <summary>
    /// 面对墙大基架1的上下间隙对柜体侧普通基座的补偿结果。
    /// 界面保留零补偿基准值，只把计算后的物理尺寸写入基座模型。
    /// </summary>
    internal sealed class FaceToWallBaseVerticalCompensationPlan
    {
        internal FaceToWallBaseVerticalCompensationPlan(
            double verticalClearance,
            double baseHeight,
            double baseOpeningHeight)
        {
            VerticalClearance = verticalClearance;
            BaseHeight = baseHeight;
            BaseOpeningHeight = baseOpeningHeight;
            GlobalVariables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["基座高"] = baseHeight,
                ["基座开孔高度"] = baseOpeningHeight
            };
        }

        internal double VerticalClearance { get; }

        internal double BaseHeight { get; }

        internal double BaseOpeningHeight { get; }

        internal IReadOnlyDictionary<string, object> GlobalVariables { get; }

        internal bool RequiresDrive(double actualBaseHeight, double actualBaseOpeningHeight)
        {
            return !AreEqual(BaseHeight, actualBaseHeight)
                || !AreEqual(BaseOpeningHeight, actualBaseOpeningHeight);
        }

        private static bool AreEqual(double left, double right)
        {
            return Math.Abs(left - right) < 0.0005d;
        }
    }

    internal static class FaceToWallBaseVerticalCompensationPlanner
    {
        internal static FaceToWallBaseVerticalCompensationPlan CreatePlan(
            double baselineBaseHeight,
            double baselineBaseOpeningHeight,
            double verticalClearance)
        {
            ValidateFinite("与墙上下间隙", verticalClearance);
            ValidatePositiveFinite("零补偿基座高", baselineBaseHeight);
            ValidatePositiveFinite("零补偿基架高度", baselineBaseOpeningHeight);

            var normalizedClearance = Normalize(verticalClearance);
            var compensatedBaseHeight = Normalize(
                Normalize(baselineBaseHeight) - normalizedClearance);
            var compensatedBaseOpeningHeight = Normalize(
                Normalize(baselineBaseOpeningHeight) - normalizedClearance * 2d);
            ValidatePositiveFinite("补偿后基座高", compensatedBaseHeight);
            ValidatePositiveFinite("补偿后基架高度", compensatedBaseOpeningHeight);

            return new FaceToWallBaseVerticalCompensationPlan(
                normalizedClearance,
                compensatedBaseHeight,
                compensatedBaseOpeningHeight);
        }

        private static double Normalize(double value)
        {
            return Math.Round(value, 3, MidpointRounding.AwayFromZero);
        }

        private static void ValidateFinite(string parameterName, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    parameterName + "必须为有限数值。");
            }
        }

        private static void ValidatePositiveFinite(string parameterName, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    parameterName + "必须为有限正数。");
            }
        }
    }
}
