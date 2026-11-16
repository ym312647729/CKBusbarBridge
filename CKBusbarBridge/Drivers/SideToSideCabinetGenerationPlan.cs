using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace CKBusbarBridge.Drivers
{
    internal sealed class SideToSideCabinetHoleDrivePlan
    {
        internal SideToSideCabinetHoleDrivePlan(
            bool isEnabled,
            string positionFolderName,
            string activeOpeningFolderName,
            string inactiveOpeningFolderName,
            string featureName,
            string sketchName,
            string assemblyDimensionName,
            IDictionary<string, double> partDimensionMap)
        {
            IsEnabled = isEnabled;
            PositionFolderName = positionFolderName;
            ActiveOpeningFolderName = activeOpeningFolderName;
            InactiveOpeningFolderName = inactiveOpeningFolderName;
            FeatureName = featureName;
            SketchName = sketchName;
            AssemblyDimensionName = assemblyDimensionName;
            PartDimensionMap = partDimensionMap;
        }

        internal bool IsEnabled { get; }

        internal string PositionFolderName { get; }

        internal string ActiveOpeningFolderName { get; }

        internal string InactiveOpeningFolderName { get; }

        internal string FeatureName { get; }

        internal string SketchName { get; }

        internal string AssemblyDimensionName { get; }

        internal IDictionary<string, double> PartDimensionMap { get; }
    }

    /// <summary>
    /// 侧对侧柜体首次生成中的纯计算与固定阶段顺序。
    /// 不访问文件系统或 SolidWorks，便于在修改模型前完成范围校验。
    /// </summary>
    internal static class SideToSideCabinetGenerationPlan
    {
        internal static IDictionary<string, object> BuildRootGlobalVariableMap(
            SideToSideGeneralParameters general)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (general == null)
            {
                return map;
            }

            map["柜间距A"] = general.CabinetSpacing;
            map["楼高D"] = general.FloorHeight;
            map["B"] = general.ParameterB;
            map["柜间偏心值"] = Math.Abs(general.CabinetEccentricity);
            map["偏移方向"] = general.CabinetEccentricity > 0d ? 1 : 0;
            return map;
        }

        internal static IDictionary<string, object> BuildCabinetGlobalVariableMap(
            SideToSideCabinetParameters cabinet,
            string cabinetName)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (cabinet == null)
            {
                return map;
            }

            map["柜高"] = cabinet.CabinetHeight;
            map[cabinetName + "深"] = cabinet.CabinetDepth;
            map["柜宽"] = cabinet.CabinetWidth;
            return map;
        }

        internal static IDictionary<string, double> BuildCabinetAssemblyDimensionMap(
            string cabinetName,
            SideToSideCabinetParameters cabinet)
        {
            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (cabinet == null)
            {
                return map;
            }

            map[GetCabinetGroundHeightDimensionName(cabinetName)] = cabinet.GroundHeight;

            return map;
        }

        internal static string GetCabinetGroundHeightDimensionName(string cabinetName)
        {
            switch (cabinetName)
            {
                case "A柜":
                    return "I@距离7";
                case "B柜":
                    return "I@距离6";
                default:
                    throw new ArgumentException("柜体名称仅支持 A柜 或 B柜。", nameof(cabinetName));
            }
        }

        internal static SideToSideCabinetHoleDrivePlan BuildCabinetHoleDrivePlan(
            string cabinetName,
            SideToSideCabinetHoleParameters hole,
            bool isFront,
            SideToSideCabinetTopOpeningMode openingMode)
        {
            if (string.IsNullOrWhiteSpace(cabinetName))
            {
                throw new ArgumentException("柜体名称不能为空。", nameof(cabinetName));
            }

            if (openingMode != SideToSideCabinetTopOpeningMode.FrontRear)
                throw new InvalidOperationException("侧对侧新模板仅支持前后开孔，请先迁移旧左右开孔模型。");

            const string activeOpeningFolderName = "前后开孔";
            const string inactiveOpeningFolderName = null;
            var positionName = isFront ? "前" : "后";
            var dimensions = CabinetHoleModelContract.ForSideToSide(isFront);
            var sketchName = dimensions.SketchName;
            var assemblyDimensionName = dimensions.AssemblyDimensionName;
            var partDimensionMap = hole == null ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                : dimensions.CreatePartDimensionMap(hole.ToCabinetEdgeDistance, hole.FixedHoleSpacing,
                    hole.L1, hole.L2, hole.L3);

            return new SideToSideCabinetHoleDrivePlan(
                hole != null && hole.IsEnabled,
                cabinetName + positionName,
                activeOpeningFolderName,
                inactiveOpeningFolderName,
                dimensions.FeatureName(cabinetName),
                sketchName,
                assemblyDimensionName,
                partDimensionMap);
        }

        internal static bool TryValidateConfirmedDriveScope(
            SideToSideCabinetDriverParameters parameters,
            out string errorMessage)
        {
            return TryValidateConfirmedDriveScope(parameters, out errorMessage, validateNominalOpeningElevation: true);
        }

        internal static bool TryValidateConfirmedDriveScope(
            SideToSideCabinetDriverParameters parameters,
            out string errorMessage,
            bool validateNominalOpeningElevation)
        {
            if (parameters?.General == null)
            {
                errorMessage = "侧对侧柜体参数不能为空。";
                return false;
            }

            if (!BusbarPhaseSpacingRules.TryValidate(
                    parameters.General.BusbarSpec,
                    parameters.General.ExposedBusbarPhaseSpacing,
                    parameters.General.InsulationDistance,
                    out errorMessage))
            {
                return false;
            }

            try
            {
                // 生成按钮会在复制模板前调用本方法，因此所有可纯计算的基座校验必须在此完成。
                var plans = SideToSideBaseDrivePlanner.CreatePlans(parameters);
                if (validateNominalOpeningElevation)
                    new CabinetOpeningElevationValidator().Validate(parameters, plans);
                if (!SideToSideBase2Rules.TryGetBusbarWidth(
                        parameters.General.BusbarSpec, out var busbarWidth))
                {
                    throw new ArgumentException("无法从铜排规格解析排宽，不能校验基座三相排布跨度。");
                }
                var minimumBaseSpan = CKBusbarBridgePreview.FaceToFaceUnifiedPlanningRules
                    .CalculateMinimumBaseWidth(
                        parameters.General.ExposedBusbarPhaseSpacing,
                        busbarWidth,
                        parameters.General.InsulationDistance);
                var busbarThickness = FaceToFaceBusbarCenterDistanceHelper
                    .ResolveBusbarThickness(parameters.General.BusbarSpec);
                var minimumBaseWidth = NoPhaseChangeBaseOpeningHeightRules
                    .CalculateMinimum(
                        parameters.General.InsulatorHeight,
                        busbarThickness,
                        parameters.General.SinglePhaseRootCount,
                        parameters.General.InsulationDistance);
                foreach (var plan in plans)
                {
                    if (plan.BaseModel.Kind != SideToSideBaseModelKind.Base1)
                    {
                        continue;
                    }
                    // 基座1与其他场景正交：三相沿开孔高度排布，宽度只承载单相排宽。
                    var openingHeight = plan.BaseModel.GlobalVariables["基座开孔高度"];
                    var baseWidth = plan.BaseModel.GlobalVariables["基座宽"];
                    if (openingHeight + 0.001d < minimumBaseSpan)
                    {
                        throw new ArgumentOutOfRangeException(nameof(parameters),
                            $"{plan.PositionName}的基座1开孔高度不能小于"
                            + minimumBaseSpan.ToString("0.###", CultureInfo.InvariantCulture)
                            + " mm（相间距×2+排宽+2×(19.5+绝缘距离+5)）；"
                            + "当前 " + openingHeight.ToString("0.###", CultureInfo.InvariantCulture)
                            + " mm。请调整基架高度。");
                    }
                    if (baseWidth + 0.001d < minimumBaseWidth)
                    {
                        throw new ArgumentOutOfRangeException(nameof(parameters),
                            $"{plan.PositionName}的基座1宽不能小于"
                            + minimumBaseWidth.ToString("0.###", CultureInfo.InvariantCulture)
                            + " mm（41.5+绝缘子及支架高度+(2×单相根数-1)×排厚+绝缘距离+40）；"
                            + "当前 " + baseWidth.ToString("0.###", CultureInfo.InvariantCulture)
                            + " mm。请调整接口固定孔间距。");
                    }
                }
            }
            catch (ArgumentException exception)
            {
                errorMessage = exception.Message;
                return false;
            }

            errorMessage = null;
            return true;
        }

        internal static bool TryValidateConfirmedDriveScope(
            SideToSideCabinetDriverParameters parameters,
            string templateRoot,
            out string errorMessage)
        {
            if (!TryValidateConfirmedDriveScope(parameters, out errorMessage))
            {
                return false;
            }

            try
            {
                // 这个重载由生成按钮在项目模板复制前调用，避免模板缺失时留下半成品。
                SideToSideBaseGenerationPreflight.ValidateTemplates(parameters, templateRoot);
            }
            catch (Exception exception)
            {
                errorMessage = exception.Message;
                return false;
            }

            errorMessage = null;
            return true;
        }

        internal static bool TryValidateThenPrepare(
            SideToSideCabinetDriverParameters parameters,
            string templateRoot,
            Func<bool> prepare,
            out bool preparationSucceeded,
            out string errorMessage)
        {
            if (prepare == null)
            {
                throw new ArgumentNullException(nameof(prepare));
            }

            if (!TryValidateConfirmedDriveScope(parameters, templateRoot, out errorMessage))
            {
                preparationSucceeded = false;
                return false;
            }

            // 通过统一入口强制“参数/模板预检”早于项目复制和模型打开。
            preparationSucceeded = prepare();
            errorMessage = null;
            return true;
        }

        internal static void ExecuteStages(
            Action driveRoot,
            Action driveACabinet,
            Action driveBCabinet,
            Action driveExtensionBusbars,
            Action generateBases,
            Action writeMetadata)
        {
            if (driveRoot == null) throw new ArgumentNullException(nameof(driveRoot));
            if (driveACabinet == null) throw new ArgumentNullException(nameof(driveACabinet));
            if (driveBCabinet == null) throw new ArgumentNullException(nameof(driveBCabinet));
            if (driveExtensionBusbars == null) throw new ArgumentNullException(nameof(driveExtensionBusbars));
            if (generateBases == null) throw new ArgumentNullException(nameof(generateBases));
            if (writeMetadata == null) throw new ArgumentNullException(nameof(writeMetadata));

            driveRoot();
            driveACabinet();
            driveBCabinet();
            driveExtensionBusbars();
            generateBases();
            writeMetadata();
        }

        internal static void ExecuteStagesWithSnapshot(
            Action driveRoot,
            Action driveACabinet,
            Action driveBCabinet,
            Action driveExtensionBusbars,
            Action generateBases,
            Action writeMetadata,
            Action captureSnapshot)
        {
            if (captureSnapshot == null)
            {
                throw new ArgumentNullException(nameof(captureSnapshot));
            }

            ExecuteStages(
                driveRoot,
                driveACabinet,
                driveBCabinet,
                driveExtensionBusbars,
                generateBases,
                writeMetadata);
            captureSnapshot();
        }

        internal static void ExecuteCabinetBodyStages(
            Action<Action> driveInOpenedCabinet,
            Action switchOpeningFolders,
            Action driveFrontHole,
            Action driveBackHole)
        {
            if (driveInOpenedCabinet == null)
            {
                throw new ArgumentNullException(nameof(driveInOpenedCabinet));
            }

            if (switchOpeningFolders == null)
            {
                throw new ArgumentNullException(nameof(switchOpeningFolders));
            }

            if (driveFrontHole == null)
            {
                throw new ArgumentNullException(nameof(driveFrontHole));
            }

            if (driveBackHole == null)
            {
                throw new ArgumentNullException(nameof(driveBackHole));
            }

            driveInOpenedCabinet(() =>
            {
                switchOpeningFolders();
                driveFrontHole();
                driveBackHole();
            });
        }

    }
}
