using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CKBusbarBridge.Drivers;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;

namespace CKBusbarBridge
{
    /// <summary>
    /// 读取柜体装配体中的关键尺寸值，供后续基架与母排定位计算使用。
    /// </summary>
    public class CabinetAssemblyDimensionReader
    {
        private readonly IModelDoc2 _cabinetAssemblyModel;

        private static readonly CabinetSideReadSpec ACabinetReadSpec = new CabinetSideReadSpec
        {
            DisplayName = "A柜",
            GroundHeightDimensionName = "I@距离6",
            SubAssemblyComponentPath = new[] { "A柜-子装配体" },
            CabinetComponentPath = new[] { "A柜-子装配体", "A柜体" },
            CabinetHeightVariableName = "柜高",
            CabinetDepthVariableName = "B柜深",
            FrontBusbarCenterDistanceDimensionNames = new[] { "铜排中心至柜前距离@距离3" },
            RearBusbarCenterDistanceDimensionNames = new[] { "铜排中心距柜后距离@距离4" },
            // 面对面独立基座后，绝缘子定位改从宿主基座草图读取，不再读柜体子装配旧尺寸。
            ReadInsulatorHeightFromStandaloneBase = true,
            FrontStandaloneBasePositionName = "A柜前",
            RearStandaloneBasePositionName = "A柜后",
            FrontInsulatorHeightDimensionNames = new[] { "基座绝缘子高度（3垫块）-A柜前@距离8" },
            RearInsulatorHeightDimensionNames = new[] { "基座绝缘子高度（3垫块）-A柜后@距离10" },
            FrontBaseAssemblyPath = new[] { "A柜-子装配体", "5GX.355.020-A柜前" },
            RearBaseAssemblyPath = new[] { "A柜-子装配体", "5GX.355.020-A柜后" },
            FrontBaseHeightVariableName = "A柜前-基座高",
            RearBaseHeightVariableName = "A柜后-基座高",
            FrontBaseDepthVariableName = "A柜前-基座深",
            RearBaseDepthVariableName = "A柜后-基座深",
            FrontBaseWidthVariableName = "A柜前-基座宽",
            RearBaseWidthVariableName = "A柜后-基座宽",
            FrontExtensionBaseAssemblyPath = new[] { "A柜-子装配体", "5GX.355.021-基架A柜前" },
            RearExtensionBaseAssemblyPath = new[] { "A柜-子装配体", "5GX.355.021-基架A柜后" }
        };

        private static readonly CabinetSideReadSpec BCabinetReadSpec = new CabinetSideReadSpec
        {
            DisplayName = "B柜",
            GroundHeightDimensionName = "I@距离5",
            SubAssemblyComponentPath = new[] { "B柜-子装配体" },
            CabinetComponentPath = new[] { "B柜-子装配体", "B柜体" },
            CabinetHeightVariableName = "柜高",
            CabinetDepthVariableName = "B柜深",
            FrontBusbarCenterDistanceDimensionNames = new[] { "铜排中心至柜前距离@距离3" },
            RearBusbarCenterDistanceDimensionNames = new[] { "铜排中心距柜后距离@距离4" },
            ReadInsulatorHeightFromStandaloneBase = true,
            FrontStandaloneBasePositionName = "B柜前",
            RearStandaloneBasePositionName = "B柜后",
            FrontInsulatorHeightDimensionNames = new[] { "基座绝缘子高度（3垫块）-B柜前@距离8" },
            RearInsulatorHeightDimensionNames = new[] { "基座绝缘子高度（3垫块）-B柜后@距离10" },
            FrontBaseAssemblyPath = new[] { "B柜-子装配体", "5GX.355.020-B柜前" },
            RearBaseAssemblyPath = new[] { "B柜-子装配体", "5GX.355.020-B柜后" },
            FrontBaseHeightVariableName = "B柜前-基座高",
            RearBaseHeightVariableName = "B柜后-基座高",
            FrontBaseDepthVariableName = "B柜前-基座深",
            RearBaseDepthVariableName = "B柜后-基座深",
            FrontBaseWidthVariableName = "B柜前-基座宽",
            RearBaseWidthVariableName = "B柜后-基座宽",
            FrontExtensionBaseAssemblyPath = new[] { "B柜-子装配体", "5GX.355.021-基架B柜前" },
            RearExtensionBaseAssemblyPath = new[] { "B柜-子装配体", "5GX.355.021-基架B柜后" }
        };

        private static readonly CabinetSideReadSpec FaceToWallCabinetReadSpec = new CabinetSideReadSpec
        {
            DisplayName = "柜",
            GroundHeightDimensionName = "D1@距离7",
            SubAssemblyComponentPath = new[] { "柜子装配体" },
            CabinetComponentPath = new[] { "柜子装配体", "柜体" },
            CabinetHeightVariableName = "柜高",
            CabinetDepthVariableName = "B柜深",
            FrontBusbarCenterDistanceDimensionNames = new[] { "铜排中心至柜前距离@距离3" },
            RearBusbarCenterDistanceDimensionNames = new[] { "铜排中心距柜后距离@距离4" },
            ReadInsulatorHeightFromStandaloneBase = true,
            FrontStandaloneBasePositionName = FaceToWallBaseDrivePlanner.FrontPositionName,
            RearStandaloneBasePositionName = FaceToWallBaseDrivePlanner.RearPositionName
        };

        public CabinetAssemblyDimensionReader(IModelDoc2 cabinetAssemblyModel)
        {
            _cabinetAssemblyModel = EnsureAssemblyModel(cabinetAssemblyModel);
        }

        /// <summary>
        /// 判断用户选定的项目目录在选择时是否为空。
        /// SolidWorks 锁文件（~$）和 Windows 资源管理器隐藏杂项不视为项目内容。
        /// </summary>
        public static bool IsProjectDirectoryEmpty(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                throw new DirectoryNotFoundException($"项目目录不存在：{projectPath}");
            }

            return !Directory.EnumerateFileSystemEntries(projectPath)
                .Any(path => !IsIgnorableProjectDirectoryEntry(path));
        }

        /// <summary>
        /// Explorer 默认不显示的锁文件/杂项：用户会把这类目录当成空文件夹。
        /// 子目录和真实模型文件仍视为非空。
        /// </summary>
        internal static bool IsIgnorableProjectDirectoryEntry(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
            {
                return false;
            }

            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            if (name.StartsWith("~$", StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 解析旧项目的柜体总装配体。优先使用场景约定路径，其次识别重命名后的“5+图号”装配体。
        /// </summary>
        public static string ResolveProjectMainAssemblyPath(string projectPath, DesignScenarioId scenarioId)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                throw new DirectoryNotFoundException($"项目目录不存在：{projectPath}");
            }

            var expectedPath = Path.Combine(projectPath, DesignScenarioDefaults.GetMainAssemblyRelativePath(scenarioId));

            // 两种场景的根总装配体都位于项目根目录；旧“柜体基座”布局不再参与解析。
            var assemblySearchDirectory = projectPath;

            var renamedCandidates = Directory
                .EnumerateFiles(assemblySearchDirectory, "*.SLDASM", SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    var baseName = Path.GetFileNameWithoutExtension(path);
                    return !string.IsNullOrWhiteSpace(baseName)
                        && baseName.Length > 1
                        && baseName[0] == '5'
                        && DrawingNumberHelper.TryParse(baseName.Substring(1), out _, out _);
                })
                .ToList();

            if (renamedCandidates.Count == 1)
            {
                return renamedCandidates[0];
            }

            if (renamedCandidates.Count > 1)
            {
                throw new InvalidOperationException(
                    $"项目中找到多个重命名后的总装配体，无法确定应打开哪一个：{string.Join("、", renamedCandidates.Select(Path.GetFileName))}");
            }

            if (File.Exists(expectedPath))
            {
                return expectedPath;
            }

            throw new FileNotFoundException(
                $"项目目录非空，但未找到当前场景的总装配体：{expectedPath}");
        }

        internal static bool IsComponentAvailable(IModelDoc2 rootModel, params string[] componentPath)
        {
            var component = GetComponentByPath(rootModel, componentPath);
            if (component == null)
            {
                return false;
            }

            try { return !component.IsSuppressed(); }
            catch { return false; }
        }

        internal static IComponent2 FindFirstDescendantComponentContaining(
            IModelDoc2 rootModel,
            string nameFragment,
            int requiredDocumentType = 0)
        {
            if (rootModel == null || string.IsNullOrWhiteSpace(nameFragment))
            {
                return null;
            }

            var rootComponent = rootModel.ConfigurationManager?.ActiveConfiguration?.GetRootComponent3(true) as IComponent2;
            return FindFirstDescendantComponentContaining(rootComponent, nameFragment, requiredDocumentType);
        }

        /// <summary>
        /// 获取构造函数传入的柜体装配体中的柜间距 A。
        /// </summary>
        /// <returns>柜间距 A。</returns>
        public double GetCabinetSpacing()
        {
            return GetCabinetSpacing(_cabinetAssemblyModel);
        }

        /// <summary>
        /// 获取指定柜体装配体中的柜间距 A。
        /// </summary>
        /// <param name="cabinetAssemblyModel">柜体总装配体模型。</param>
        /// <returns>柜间距 A。</returns>
        public double GetCabinetSpacing(IModelDoc2 cabinetAssemblyModel)
        {
            var assemblyModel = EnsureAssemblyModel(cabinetAssemblyModel);
            return GetRequiredDimensionValue(assemblyModel, "A@距离1", "柜间距A");
        }

        /// <summary>
        /// 获取构造函数传入的柜体装配体中的 A 柜相关尺寸。
        /// </summary>
        /// <param name="frontHoleEnabled">A 柜前孔是否启用。</param>
        /// <param name="backHoleEnabled">A 柜后孔是否启用。</param>
        /// <returns>A 柜相关尺寸结果。</returns>
        public CabinetSideDimensions GetACabinetDimensions(bool frontHoleEnabled, bool backHoleEnabled)
        {
            return GetACabinetDimensions(_cabinetAssemblyModel, frontHoleEnabled, backHoleEnabled);
        }

        /// <summary>
        /// 获取指定柜体装配体中的 A 柜相关尺寸。
        /// </summary>
        /// <param name="cabinetAssemblyModel">柜体总装配体模型。</param>
        /// <param name="frontHoleEnabled">A 柜前孔是否启用。</param>
        /// <param name="backHoleEnabled">A 柜后孔是否启用。</param>
        /// <returns>A 柜相关尺寸结果。</returns>
        public CabinetSideDimensions GetACabinetDimensions(IModelDoc2 cabinetAssemblyModel, bool frontHoleEnabled, bool backHoleEnabled)
        {
            return GetCabinetSideDimensions(cabinetAssemblyModel, ACabinetReadSpec, frontHoleEnabled, backHoleEnabled);
        }

        /// <summary>
        /// 获取构造函数传入的柜体装配体中的 B 柜相关尺寸。
        /// </summary>
        /// <param name="frontHoleEnabled">B 柜前孔是否启用。</param>
        /// <param name="backHoleEnabled">B 柜后孔是否启用。</param>
        /// <returns>B 柜相关尺寸结果。</returns>
        public CabinetSideDimensions GetBCabinetDimensions(bool frontHoleEnabled, bool backHoleEnabled)
        {
            return GetBCabinetDimensions(_cabinetAssemblyModel, frontHoleEnabled, backHoleEnabled);
        }

        /// <summary>
        /// 获取指定柜体装配体中的 B 柜相关尺寸。
        /// </summary>
        /// <param name="cabinetAssemblyModel">柜体总装配体模型。</param>
        /// <param name="frontHoleEnabled">B 柜前孔是否启用。</param>
        /// <param name="backHoleEnabled">B 柜后孔是否启用。</param>
        /// <returns>B 柜相关尺寸结果。</returns>
        public CabinetSideDimensions GetBCabinetDimensions(IModelDoc2 cabinetAssemblyModel, bool frontHoleEnabled, bool backHoleEnabled)
        {
            return GetCabinetSideDimensions(cabinetAssemblyModel, BCabinetReadSpec, frontHoleEnabled, backHoleEnabled);
        }

        /// <summary>
        /// 获取面对墙场景单柜（柜子装配体）相关尺寸，供母线桥 B 端（柜体侧）计算使用。
        /// </summary>
        public CabinetSideDimensions GetFaceToWallCabinetDimensions(bool frontHoleEnabled, bool backHoleEnabled)
        {
            return GetFaceToWallCabinetDimensions(_cabinetAssemblyModel, frontHoleEnabled, backHoleEnabled);
        }

        /// <summary>
        /// 获取指定装配体中面对墙场景单柜相关尺寸。
        /// </summary>
        public CabinetSideDimensions GetFaceToWallCabinetDimensions(IModelDoc2 cabinetAssemblyModel, bool frontHoleEnabled, bool backHoleEnabled)
        {
            return GetCabinetSideDimensions(cabinetAssemblyModel, FaceToWallCabinetReadSpec, frontHoleEnabled, backHoleEnabled);
        }

        /// <summary>
        /// 获取面对墙场景中柜体至墙体的桥接跨度（朝向对应的距墙距离）。
        /// </summary>
        public double GetFaceToWallCabinetToWallSpacing(FaceToWallCabinetOrientation orientation)
        {
            return GetFaceToWallCabinetToWallSpacing(_cabinetAssemblyModel, orientation);
        }

        /// <summary>
        /// 获取指定装配体中面对墙场景的柜体至墙体桥接跨度。
        /// </summary>
        public double GetFaceToWallCabinetToWallSpacing(
            IModelDoc2 cabinetAssemblyModel,
            FaceToWallCabinetOrientation orientation)
        {
            var assemblyModel = EnsureAssemblyModel(cabinetAssemblyModel);
            var variableName = FaceToWallCabinetOrientationDefaults.GetWallDistanceGlobalVariableName(orientation);
            if (TryGetGlobalVariableValue(assemblyModel, variableName, out var spacingValue)
                && spacingValue > 0)
            {
                return Math.Round(spacingValue, 1);
            }

            throw new InvalidOperationException(
                $"读取面对墙场景{FaceToWallCabinetOrientationDefaults.GetWallDistanceDisplayName(orientation)}失败，未找到全局变量“{variableName}”。");
        }

        private CabinetSideDimensions GetCabinetSideDimensions(IModelDoc2 cabinetAssemblyModel, CabinetSideReadSpec readSpec, bool frontHoleEnabled, bool backHoleEnabled)
        {
            if (readSpec == null)
            {
                throw new ArgumentNullException(nameof(readSpec));
            }

            var assemblyModel = EnsureAssemblyModel(cabinetAssemblyModel);
            var subAssemblyModel = ReferencedModelValueReader.FromPath(assemblyModel, readSpec.SubAssemblyComponentPath);
            var cabinetModel = ReferencedModelValueReader.FromPath(assemblyModel, readSpec.CabinetComponentPath);

            var cabinetDepth = GetRequiredGlobalVariableValue(cabinetModel, readSpec.CabinetDepthVariableName, $"{readSpec.DisplayName}柜深");
            var cabinetWidth = GetRequiredGlobalVariableValue(cabinetModel, "柜宽", $"{readSpec.DisplayName}柜宽");
            var useFrontHole = frontHoleEnabled || !backHoleEnabled;
            if (!frontHoleEnabled && !backHoleEnabled)
            {
                throw new InvalidOperationException($"{readSpec.DisplayName}前孔和后孔均未启用，无法确定定位尺寸来源。");
            }

            double baseToCabinetFrontDistance;
            double baseToCabinetBackDistance;
            double busbarCenterToCabinetFrontDistance;
            double busbarCenterToCabinetBackDistance;
            double insulatorHeight;
            double baseHeight;
            double baseDepth;
            double baseWidth;
            CabinetHolePosition holePosition;

            if (useFrontHole)
            {
                var toCabinetEdgeDistance = GetRequiredDimensionValue(
                    cabinetModel,
                    FrontHoleToCabinetEdgeDimensionName,
                    $"{readSpec.DisplayName}距柜前距离");
                var fixedHoleSpacing = GetRequiredDimensionValue(
                    cabinetModel,
                    FrontHoleFixedSpacingDimensionName,
                    $"{readSpec.DisplayName}柜前接口固定孔间距");
                CabinetBaseEdgeDistanceHelper.Calculate(
                    true,
                    toCabinetEdgeDistance,
                    fixedHoleSpacing,
                    cabinetDepth,
                    out baseToCabinetFrontDistance,
                    out baseToCabinetBackDistance);
                busbarCenterToCabinetFrontDistance = GetRequiredDimensionValue(subAssemblyModel, readSpec.FrontBusbarCenterDistanceDimensionNames, $"{readSpec.DisplayName}伸出排中心距柜前距离");
                insulatorHeight = ReadInsulatorAbsoluteHeight(
                    assemblyModel,
                    subAssemblyModel.Model,
                    readSpec,
                    isFront: true);
                busbarCenterToCabinetBackDistance = Math.Round(cabinetDepth - busbarCenterToCabinetFrontDistance, 1);

                ReadBaseSizeDimensions(
                    assemblyModel,
                    subAssemblyModel.Model,
                    readSpec,
                    isFront: true,
                    out baseHeight,
                    out baseDepth,
                    out baseWidth, cabinetWidth);
                holePosition = CabinetHolePosition.Front;
            }
            else
            {
                var toCabinetEdgeDistance = GetRequiredDimensionValue(
                    cabinetModel,
                    RearHoleToCabinetEdgeDimensionName,
                    $"{readSpec.DisplayName}距柜后距离");
                var fixedHoleSpacing = GetRequiredDimensionValue(
                    cabinetModel,
                    RearHoleFixedSpacingDimensionName,
                    $"{readSpec.DisplayName}柜后接口固定孔间距");
                CabinetBaseEdgeDistanceHelper.Calculate(
                    false,
                    toCabinetEdgeDistance,
                    fixedHoleSpacing,
                    cabinetDepth,
                    out baseToCabinetFrontDistance,
                    out baseToCabinetBackDistance);
                busbarCenterToCabinetBackDistance = GetRequiredDimensionValue(subAssemblyModel, readSpec.RearBusbarCenterDistanceDimensionNames, $"{readSpec.DisplayName}伸出排中心距柜后距离");
                insulatorHeight = ReadInsulatorAbsoluteHeight(
                    assemblyModel,
                    subAssemblyModel.Model,
                    readSpec,
                    isFront: false);

                busbarCenterToCabinetFrontDistance = Math.Round(cabinetDepth - busbarCenterToCabinetBackDistance, 1);

                ReadBaseSizeDimensions(
                    assemblyModel,
                    subAssemblyModel.Model,
                    readSpec,
                    isFront: false,
                    out baseHeight,
                    out baseDepth,
                    out baseWidth, cabinetWidth);
                holePosition = CabinetHolePosition.Back;
            }

            var baseOpeningHeight = ReadRequiredStandaloneBaseOpeningHeight(
                assemblyModel,
                readSpec,
                holePosition == CabinetHolePosition.Front);

            return new CabinetSideDimensions
            {
                CabinetGroundHeight = GetRequiredDimensionValue(assemblyModel, readSpec.GroundHeightDimensionName, $"{readSpec.DisplayName}离地高度I"),
                CabinetHeight = GetRequiredGlobalVariableValue(cabinetModel, readSpec.CabinetHeightVariableName, $"{readSpec.DisplayName}柜高"),
                CabinetDepth = cabinetDepth,
                BaseToCabinetFrontDistance = baseToCabinetFrontDistance,
                BaseToCabinetBackDistance = baseToCabinetBackDistance,
                BusbarCenterToCabinetFrontDistance = busbarCenterToCabinetFrontDistance,
                BusbarCenterToCabinetBackDistance = busbarCenterToCabinetBackDistance,
                InsulatorHeight = insulatorHeight,
                BaseHeight = baseHeight,
                BaseDepth = baseDepth,
                BaseWidth = baseWidth,
                BaseOpeningHeight = baseOpeningHeight,
                HolePosition = holePosition
            };
        }

        private static IModelDoc2 EnsureAssemblyModel(IModelDoc2 cabinetAssemblyModel)
        {
            if (cabinetAssemblyModel == null)
            {
                throw new InvalidOperationException("当前没有可用的柜体装配体模型。");
            }

            if (cabinetAssemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException("当前模型不是装配体，无法读取柜体装配尺寸。");
            }

            return cabinetAssemblyModel;
        }

        /// <summary>
        /// 读取绝缘子绝对定位高度。
        /// 面对面/面对墙独立基座：从宿主基座草图局部尺寸换算，宿主由总装「模型对照映射」定位。
        /// </summary>
        private static double ReadInsulatorAbsoluteHeight(
            IModelDoc2 assemblyModel,
            IModelDoc2 subAssemblyModel,
            CabinetSideReadSpec readSpec,
            bool isFront)
        {
            var displayName = isFront
                ? $"{readSpec.DisplayName}前基座绝缘子高度"
                : $"{readSpec.DisplayName}后基座绝缘子高度";

            if (readSpec.ReadInsulatorHeightFromStandaloneBase)
            {
                var positionName = isFront
                    ? readSpec.FrontStandaloneBasePositionName
                    : readSpec.RearStandaloneBasePositionName;
                return ReadStandaloneBaseInsulatorAbsoluteHeight(
                    assemblyModel,
                    positionName,
                    displayName);
            }

            var legacyNames = isFront
                ? readSpec.FrontInsulatorHeightDimensionNames
                : readSpec.RearInsulatorHeightDimensionNames;
            return GetRequiredDimensionValue(subAssemblyModel, legacyNames, displayName);
        }

        /// <summary>
        /// 从当前启用孔对应的独立普通基座读取「基座开孔高度」。原驱动变量缺失或不可读时明确报错。
        /// </summary>
        private static double ReadRequiredStandaloneBaseOpeningHeight(
            IModelDoc2 assemblyModel, CabinetSideReadSpec readSpec, bool isFront)
        {
            var position = isFront ? readSpec.FrontStandaloneBasePositionName : readSpec.RearStandaloneBasePositionName;
            TryReadStandaloneBaseOpeningHeight(assemblyModel, position, out var height);
            return height;
        }

        /// <summary>
        /// 读取基座高/深/宽。
        /// 面对面/面对墙独立基座：按「模型对照映射」定位普通基座，读「基座高/深/宽」，再叠加增高基座「高度」。
        /// </summary>
        private static void ReadBaseSizeDimensions(
            IModelDoc2 assemblyModel,
            IModelDoc2 subAssemblyModel,
            CabinetSideReadSpec readSpec,
            bool isFront,
            out double baseHeight,
            out double baseDepth,
            out double baseWidth,
            double cabinetWidth)
        {
            if (readSpec.ReadInsulatorHeightFromStandaloneBase)
            {
                var positionName = isFront
                    ? readSpec.FrontStandaloneBasePositionName
                    : readSpec.RearStandaloneBasePositionName;
                var sideLabel = isFront ? "前" : "后";
                ReadStandaloneBaseSizeDimensions(
                    assemblyModel,
                    positionName,
                    $"{readSpec.DisplayName}{sideLabel}",
                    out baseHeight,
                    out baseDepth,
                    out baseWidth, cabinetWidth);
                return;
            }

            var baseAssemblyPath = isFront
                ? readSpec.FrontBaseAssemblyPath
                : readSpec.RearBaseAssemblyPath;
            var heightVariable = isFront
                ? readSpec.FrontBaseHeightVariableName
                : readSpec.RearBaseHeightVariableName;
            var depthVariable = isFront
                ? readSpec.FrontBaseDepthVariableName
                : readSpec.RearBaseDepthVariableName;
            var widthVariable = isFront
                ? readSpec.FrontBaseWidthVariableName
                : readSpec.RearBaseWidthVariableName;
            var extensionPath = isFront
                ? readSpec.FrontExtensionBaseAssemblyPath
                : readSpec.RearExtensionBaseAssemblyPath;
            var sideName = isFront ? "前" : "后";

            var baseAssemblyModel = GetBaseAssemblyModel(
                assemblyModel,
                subAssemblyModel,
                baseAssemblyPath,
                readSpec.ResolveBaseAssemblyByComponentName);
            // 基础基座只承担“基础基座高”；若存在延长基座则需加上延长基座高度，得到总基座高度。
            baseHeight = GetRequiredGlobalVariableValue(
                    baseAssemblyModel,
                    heightVariable,
                    $"{readSpec.DisplayName}{sideName}基架高度")
                + GetExtensionBaseHeight(assemblyModel, extensionPath);
            baseDepth = GetRequiredGlobalVariableValue(
                baseAssemblyModel,
                depthVariable,
                $"{readSpec.DisplayName}{sideName}基架深度");
            baseWidth = GetRequiredGlobalVariableValue(
                baseAssemblyModel,
                widthVariable,
                $"{readSpec.DisplayName}{sideName}基架宽度");
        }

        /// <summary>
        /// 从总装根级独立普通/增高基座读取总基座高、深、宽。
        /// 总高 = 普通基座「基座高」+ 增高基座「高度」（无增高则为 0）。
        /// </summary>
        internal static void ReadStandaloneBaseSizeDimensions(
            IModelDoc2 assemblyModel,
            string positionName,
            string displayPrefix,
            out double totalBaseHeight,
            out double baseDepth,
            out double baseWidth,
            double? cabinetWidth = null)
        {
            if (string.IsNullOrWhiteSpace(positionName))
            {
                throw new ArgumentException("基座位置名称不能为空。", nameof(positionName));
            }

            var baseComponent = FindStandaloneBaseComponent(
                assemblyModel,
                positionName,
                elevated: false);
            if (baseComponent == null)
            {
                throw new InvalidOperationException(
                    $"读取{displayPrefix}基架尺寸失败，未找到位置“{positionName}”对应的独立普通基座。");
            }

            var baseModel = ReferencedModelValueReader.FromComponent(baseComponent);
            if (baseModel == null)
            {
                throw new InvalidOperationException(
                    $"读取{displayPrefix}基架高度失败，目标模型为空。");
            }

            var basicHeight = GetRequiredGlobalVariableValue(
                baseModel,
                StandaloneBaseHeightVariableName,
                $"{displayPrefix}基架高度");
            baseDepth = GetRequiredGlobalVariableValue(
                baseModel,
                StandaloneBaseDepthVariableName,
                $"{displayPrefix}基架深度");
            baseWidth = GetRequiredGlobalVariableValue(
                baseModel,
                StandaloneBaseWidthVariableName,
                $"{displayPrefix}基架宽度");
            if (cabinetWidth.HasValue)
                CabinetBaseDimensionRules.RequireMatch(positionName, "普通基座宽", cabinetWidth, baseWidth);

            var elevatedHeight = 0d;
            var elevatedComponent = FindStandaloneBaseComponent(
                assemblyModel,
                positionName,
                elevated: true);
            if (elevatedComponent != null)
            {
                var elevatedModel = ReferencedModelValueReader.FromComponent(elevatedComponent);
                elevatedHeight = GetRequiredGlobalVariableValue(
                    elevatedModel,
                    ExtensionBaseHeightVariableName,
                    $"{displayPrefix}增高基座高度");
                if (cabinetWidth.HasValue)
                    CabinetBaseDimensionRules.RequireMatch(positionName, "增高基座宽", cabinetWidth,
                        GetRequiredGlobalVariableValue(elevatedModel, "宽度", $"{displayPrefix}增高基座宽度"));
            }

            totalBaseHeight = ComposeTotalBaseHeight(basicHeight, elevatedHeight);
        }

        /// <summary>
        /// 从独立普通基座读取「基座开孔高度」。旧模型或变量缺失时返回 false，由调用方保留界面默认/随动值。
        /// </summary>
        internal static bool TryReadStandaloneBaseOpeningHeight(
            IModelDoc2 assemblyModel, string positionName, out double openingHeight)
        {
            var component = FindStandaloneBaseComponent(assemblyModel, positionName, elevated: false);
            openingHeight = Math.Round(ReferencedModelValueReader.FromComponent(component)
                .Global(CabinetHoleParameters.BaseOpeningHeightVariableName), 1);
            return true;
        }

        /// <summary>
        /// 读取增高基座「高度」。无增高实例时返回 0，供界面延长基座高还原上次写入值。
        /// </summary>
        internal static double ReadStandaloneElevatedBaseHeight(
            IModelDoc2 assemblyModel,
            string positionName)
        {
            if (string.IsNullOrWhiteSpace(positionName))
            {
                throw new ArgumentException("基座位置名称不能为空。", nameof(positionName));
            }

            var elevatedComponent = FindStandaloneBaseComponent(
                assemblyModel,
                positionName,
                elevated: true);
            if (elevatedComponent == null)
            {
                return 0;
            }

            var elevatedModel = ReferencedModelValueReader.FromComponent(elevatedComponent);
            return GetRequiredGlobalVariableValue(
                elevatedModel,
                ExtensionBaseHeightVariableName,
                $"{positionName}增高基座高度");
        }

        /// <summary>
        /// 从独立基座内部的 3 垫块绝缘子读取「绝缘子高度」。
        /// 只在宿主基座装配体内查找，避免读到柜前/柜后文件夹中被压缩的残留件。
        /// </summary>
        internal static bool TryReadInsulatorHeightFromStandaloneBase(
            IModelDoc2 assemblyModel,
            string positionName,
            out double insulatorHeight)
        {
            if (assemblyModel == null || string.IsNullOrWhiteSpace(positionName))
            {
                insulatorHeight = 0;
                return false;
            }

            return TryReadInsulatorHeightFromStandaloneBase(
                () => FindStandaloneBaseComponent(
                    assemblyModel,
                    positionName,
                    elevated: true),
                () => FindStandaloneBaseComponent(
                    assemblyModel,
                    positionName,
                    elevated: false),
                out insulatorHeight);
        }

        internal static bool TryReadInsulatorHeightFromStandaloneBase(
            IModelDoc2 assemblyModel,
            string positionName,
            SideToSideBaseModelKind baseKind,
            out double insulatorHeight)
        {
            if (assemblyModel == null || string.IsNullOrWhiteSpace(positionName))
            {
                insulatorHeight = 0;
                return false;
            }

            return TryReadInsulatorHeightFromStandaloneBase(
                () => baseKind == SideToSideBaseModelKind.Base2
                    ? FindStandaloneBaseComponent(
                        assemblyModel,
                        positionName,
                        SideToSideBaseModelKind.ElevatedBase)
                    : null,
                () => FindStandaloneBaseComponent(
                    assemblyModel,
                    positionName,
                    baseKind),
                out insulatorHeight);
        }

        private static bool TryReadInsulatorHeightFromStandaloneBase(
            Func<IComponent2> elevatedBaseResolver,
            Func<IComponent2> ordinaryBaseResolver,
            out double insulatorHeight)
        {
            insulatorHeight = 0;
            var elevatedComponent = elevatedBaseResolver();
            var baseComponent = ordinaryBaseResolver();
            var elevatedHostsInsulator = elevatedComponent != null && ComponentContainsInsulator(elevatedComponent);
            var baseHostsInsulator = baseComponent != null && ComponentContainsInsulator(baseComponent);
            if (elevatedHostsInsulator && baseHostsInsulator)
                throw new InvalidOperationException("绝缘子反读失败：普通基座与增高基座同时存在有效绝缘子，宿主不唯一。");
            var host = elevatedHostsInsulator ? elevatedComponent : baseHostsInsulator ? baseComponent : null;
            if (host == null) return false;
            var insulator = FindFirstDescendantComponentContaining(host, "绝缘子+垫片+支架",
                (int)swDocumentTypes_e.swDocASSEMBLY);
            var values = ReferencedModelValueReader.FromComponent(insulator);
            // 普通模板高度在装配体；L型模板高度在一级垫块装配体，两者均读取原驱动值。
            if (!values.TryGlobal("绝缘子高度", out insulatorHeight)
                && !TryReadInsulatorHeightFromPadSubAssembly(insulator, values.Model, out insulatorHeight)) return false;
            if (insulatorHeight <= 0d) throw new InvalidOperationException("绝缘子反读失败：原模型高度必须为正数：" + values.Identity);
            insulatorHeight = Math.Round(insulatorHeight, 3);
            return true;
        }

        private static bool TryReadInsulatorHeightFromPadSubAssembly(
            IComponent2 insulatorComponent,
            IModelDoc2 insulatorModel,
            out double insulatorHeight)
        {
            insulatorHeight = 0;
            var heights = new List<double>();
            foreach (var child in GetDirectChildComponents(insulatorComponent, insulatorModel))
            {
                if (!IsComponentUnsuppressed(child) || !IsInsulatorPadSubAssemblyComponent(child)) continue;
                var values = ReferencedModelValueReader.FromComponent(child);
                if (values.Model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                    throw new InvalidOperationException("绝缘子垫块反读失败：组件不是装配体：" + values.Identity);
                heights.Add(values.Global("绝缘子高度"));
            }
            if (heights.Count == 0) return false;
            if (heights.Any(height => Math.Abs(height - heights[0]) > .001d))
                throw new InvalidOperationException("绝缘子反读失败：未压缩垫块的实际高度不一致。");
            insulatorHeight = heights[0];
            return true;
        }

        private static IReadOnlyList<IComponent2> GetDirectChildComponents(
            IComponent2 insulatorComponent, IModelDoc2 insulatorModel)
        {
            if (insulatorComponent == null || insulatorModel == null)
                throw new InvalidOperationException("绝缘子子组件反读失败：原模型不可访问。");
            var children = insulatorComponent.GetChildren() as object[];
            if (children == null) throw new InvalidOperationException("绝缘子子组件反读失败：" + insulatorComponent.Name2);
            return children.OfType<IComponent2>().ToArray();
        }

        private static bool IsComponentUnsuppressed(IComponent2 component)
        {
            if (component == null) return false;
            return !component.IsSuppressed();
        }

        private static bool IsInsulatorPadSubAssemblyComponent(IComponent2 component)
        {
            if (component == null)
            {
                return false;
            }

            string componentName = null;
            string selectId = null;
            string pathName = null;
            try { componentName = component.Name2; } catch { }
            try { selectId = component.GetSelectByIDString(); } catch { }
            try { pathName = component.GetPathName(); } catch { }

            return IsInsulatorPadSubAssemblyComponentName(componentName, pathName)
                || IsInsulatorPadSubAssemblyComponentName(selectId, pathName);
        }

        internal static bool IsInsulatorPadSubAssemblyComponentName(
            string componentName,
            string pathName)
        {
            const string prefix = "绝缘子垫块";
            var normalizedComponentName = NormalizeComponentName(componentName);
            string normalizedFileName;
            try
            {
                normalizedFileName = NormalizeComponentName(
                    Path.GetFileNameWithoutExtension(pathName));
            }
            catch
            {
                normalizedFileName = string.Empty;
            }

            return normalizedComponentName.StartsWith(
                       prefix,
                       StringComparison.OrdinalIgnoreCase)
                || normalizedFileName.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 总基座高 = 普通基座高 + 增高基座高。
        /// </summary>
        internal static double ComposeTotalBaseHeight(double basicBaseHeight, double elevatedBaseHeight)
        {
            if (basicBaseHeight <= 0
                || double.IsNaN(basicBaseHeight)
                || double.IsInfinity(basicBaseHeight))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(basicBaseHeight),
                    basicBaseHeight,
                    "普通基座高度必须为有限正数。");
            }

            if (elevatedBaseHeight < 0
                || double.IsNaN(elevatedBaseHeight)
                || double.IsInfinity(elevatedBaseHeight))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(elevatedBaseHeight),
                    elevatedBaseHeight,
                    "增高基座高度必须为有限非负数。");
            }

            return Math.Round(basicBaseHeight + elevatedBaseHeight, 1);
        }

        /// <summary>
        /// 从独立普通/增高基座读取绝缘子绝对定位高度：
        /// 绝缘子在增高基座内 => D1@草图2；
        /// 绝缘子在普通基座内 => D1@绝缘子安装坐标系草图（有增高时再加增高高）。
        /// </summary>
        internal static double ReadStandaloneBaseInsulatorAbsoluteHeight(
            IModelDoc2 assemblyModel,
            string positionName,
            string displayName)
        {
            if (string.IsNullOrWhiteSpace(positionName))
            {
                throw new ArgumentException("基座位置名称不能为空。", nameof(positionName));
            }

            var elevatedComponent = FindStandaloneBaseComponent(
                assemblyModel,
                positionName,
                elevated: true);
            var baseComponent = FindStandaloneBaseComponent(
                assemblyModel,
                positionName,
                elevated: false);

            var elevatedHostsInsulator = elevatedComponent != null && ComponentContainsInsulator(elevatedComponent);
            var baseHostsInsulator = baseComponent != null && ComponentContainsInsulator(baseComponent);
            if (elevatedHostsInsulator && baseHostsInsulator)
                throw new InvalidOperationException("绝缘子定位反读失败：普通基座与增高基座的宿主不唯一：" + positionName);
            if (!elevatedHostsInsulator && !baseHostsInsulator)
                throw new InvalidOperationException("绝缘子定位反读失败：基座内没有有效绝缘子：" + positionName);
            if (elevatedHostsInsulator)
            {
                var elevatedModel = ReferencedModelValueReader.FromComponent(elevatedComponent);
                var localHeight = GetRequiredDimensionValue(
                    elevatedModel,
                    StandaloneElevatedInsulatorPlacementDimensionName,
                    displayName + "（增高基座 D1@草图2）");
                return ComposeInsulatorAbsoluteHeight(
                    hasElevatedBase: true,
                    insulatorInElevatedBase: true,
                    localPlacementHeight: localHeight,
                    elevatedBaseHeight: 0);
            }

            if (baseComponent == null)
            {
                throw new InvalidOperationException(
                    $"读取{displayName}失败，未找到位置“{positionName}”对应的独立普通基座。");
            }

            var baseModel = ReferencedModelValueReader.FromComponent(baseComponent);
            var baseLocalHeight = GetRequiredDimensionValue(
                baseModel,
                StandaloneBaseInsulatorPlacementDimensionName,
                displayName + "（普通基座 D1@绝缘子安装坐标系草图）");

            var elevatedHeight = 0d;
            if (elevatedComponent != null)
            {
                var elevatedModel = ReferencedModelValueReader.FromComponent(elevatedComponent);
                elevatedHeight = GetRequiredGlobalVariableValue(
                    elevatedModel,
                    ExtensionBaseHeightVariableName,
                    $"{positionName}增高基座高度");
            }

            return ComposeInsulatorAbsoluteHeight(
                hasElevatedBase: elevatedComponent != null,
                insulatorInElevatedBase: false,
                localPlacementHeight: baseLocalHeight,
                elevatedBaseHeight: elevatedHeight);
        }

        /// <summary>
        /// 将宿主基座内的局部定位高度换算为总装配绝对高度。
        /// </summary>
        internal static double ComposeInsulatorAbsoluteHeight(
            bool hasElevatedBase,
            bool insulatorInElevatedBase,
            double localPlacementHeight,
            double elevatedBaseHeight)
        {
            if (localPlacementHeight < 0
                || double.IsNaN(localPlacementHeight)
                || double.IsInfinity(localPlacementHeight))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(localPlacementHeight),
                    localPlacementHeight,
                    "绝缘子局部定位高度必须为有限非负数。");
            }

            if (insulatorInElevatedBase)
            {
                return Math.Round(localPlacementHeight, 1);
            }

            if (!hasElevatedBase)
            {
                return Math.Round(localPlacementHeight, 1);
            }

            if (elevatedBaseHeight < 0
                || double.IsNaN(elevatedBaseHeight)
                || double.IsInfinity(elevatedBaseHeight))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(elevatedBaseHeight),
                    elevatedBaseHeight,
                    "增高基座高度必须为有限非负数。");
            }

            return Math.Round(localPlacementHeight + elevatedBaseHeight, 1);
        }

        internal static IComponent2 FindStandaloneBaseComponent(
            IModelDoc2 assemblyModel,
            string positionName,
            bool elevated)
        {
            return FindStandaloneBaseComponent(
                assemblyModel,
                positionName,
                elevated,
                elevated
                    ? StandaloneElevatedBaseFileNamePrefix
                    : StandaloneBaseFileNamePrefix,
                requireMappedPrefix: false);
        }

        internal static IComponent2 FindStandaloneBaseComponent(
            IModelDoc2 assemblyModel,
            string positionName,
            SideToSideBaseModelKind baseKind)
        {
            switch (baseKind)
            {
                case SideToSideBaseModelKind.Base1:
                    return FindStandaloneBaseComponent(
                        assemblyModel,
                        positionName,
                        elevated: false,
                        SideToSideBase1FileNamePrefix,
                        requireMappedPrefix: true);
                case SideToSideBaseModelKind.Base2:
                    return FindStandaloneBaseComponent(
                        assemblyModel,
                        positionName,
                        elevated: false,
                        SideToSideBase2FileNamePrefix,
                        requireMappedPrefix: true);
                case SideToSideBaseModelKind.ElevatedBase:
                    return FindStandaloneBaseComponent(
                        assemblyModel,
                        positionName,
                        elevated: true,
                        StandaloneElevatedBaseFileNamePrefix,
                        requireMappedPrefix: true);
                default:
                    throw new ArgumentOutOfRangeException(nameof(baseKind));
            }
        }

        private static IComponent2 FindStandaloneBaseComponent(
            IModelDoc2 assemblyModel,
            string positionName,
            bool elevated,
            string expectedFileNamePrefix,
            bool requireMappedPrefix)
        {
            // 优先按总装「模型对照映射」中的零部件全名（含实例号）精确定位。
            var mappingJson = TryReadCustomPropertyText(
                assemblyModel,
                FaceToFaceBaseModelMappingHelper.PropertyName);
            string mappedFullName;
            if (FaceToFaceBaseModelMappingHelper.TryGetComponentFullName(
                mappingJson,
                positionName,
                elevated,
                out mappedFullName))
            {
                var mappedComponents = EnumerateRootAssemblyComponents(assemblyModel)
                    .Where(component => FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(component?.Name2, mappedFullName))
                    .ToArray();
                if (mappedComponents.Length > 1)
                    throw new InvalidOperationException("独立基座反读失败：映射组件在总装顶层不唯一：" + mappedFullName);
                var byMapping = mappedComponents.SingleOrDefault();
                if (byMapping != null)
                {
                    if (!requireMappedPrefix
                        || IsStandaloneBaseComponent(
                            byMapping,
                            expectedFileNamePrefix))
                    {
                        return byMapping;
                    }

                    throw new InvalidOperationException(
                        $"读取位置“{positionName}”独立{(elevated ? "增高" : "普通")}基座失败："
                        + $"映射组件“{mappedFullName}”不匹配期望图号 {expectedFileNamePrefix}。");
                }

                throw new InvalidOperationException(
                    $"读取位置“{positionName}”独立{(elevated ? "增高" : "普通")}基座失败："
                    + $"模型对照映射中的零部件全名“{mappedFullName}”在总装配体中未找到。");
            }

            if (string.IsNullOrWhiteSpace(mappingJson))
                throw new InvalidOperationException("独立基座反读失败：总装缺少模型对照映射。");
            Newtonsoft.Json.Linq.JObject mapping;
            try { mapping = Newtonsoft.Json.Linq.JObject.Parse(mappingJson); }
            catch (Newtonsoft.Json.JsonException ex)
            { throw new InvalidOperationException("独立基座反读失败：模型对照映射JSON无效。", ex); }
            var entry = mapping["基座"]?[positionName] as Newtonsoft.Json.Linq.JObject;
            if (elevated && entry != null && entry["增高基座"] != null
                && entry["增高基座"].Type == Newtonsoft.Json.Linq.JTokenType.Null) return null;
            throw new InvalidOperationException("独立基座反读失败：" + positionName + "缺少有效的模型对照映射，禁止按目录或默认实例推断。");
        }

        /// <summary>
        /// 读取文档级/活动配置自定义属性文本；优先返回原始表达式，避免 JSON 被求值破坏。
        /// </summary>
        private static string TryReadCustomPropertyText(IModelDoc2 model, string propertyName)
        {
            if (model?.Extension == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            var documentManager = model.Extension.CustomPropertyManager[string.Empty];
            var value = TryReadCustomPropertyFromManager(documentManager, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var activeConfigurationName = model.ConfigurationManager?.ActiveConfiguration?.Name;
            if (string.IsNullOrWhiteSpace(activeConfigurationName))
            {
                return null;
            }

            return TryReadCustomPropertyFromManager(
                model.Extension.CustomPropertyManager[activeConfigurationName],
                propertyName);
        }

        private static string TryReadCustomPropertyFromManager(
            CustomPropertyManager customPropertyManager,
            string propertyName)
        {
            if (customPropertyManager == null)
            {
                return null;
            }

            try
            {
                string rawValue;
                string resolvedValue;
                bool wasResolved;
                bool linkToProperty;
                customPropertyManager.Get6(
                    propertyName,
                    false,
                    out rawValue,
                    out resolvedValue,
                    out wasResolved,
                    out linkToProperty);
                if (!string.IsNullOrWhiteSpace(rawValue))
                {
                    return rawValue;
                }

                return string.IsNullOrWhiteSpace(resolvedValue) ? null : resolvedValue;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<IComponent2> EnumerateRootAssemblyComponents(IModelDoc2 assemblyModel)
        {
            var configuration = assemblyModel?.ConfigurationManager?.ActiveConfiguration;
            var rootComponent = configuration?.GetRootComponent3(true);
            var children = rootComponent?.GetChildren() as object[];
            if (children == null || children.Length == 0)
            {
                yield break;
            }

            foreach (var childObject in children)
            {
                if (childObject is IComponent2 component)
                {
                    yield return component;
                }
            }
        }

        private static bool IsStandaloneBaseComponent(
            IComponent2 component,
            string fileNamePrefix)
        {
            if (component == null)
            {
                return false;
            }

            try
            {
                if (component.IsSuppressed())
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            var path = component.GetPathName() ?? string.Empty;
            var fileName = Path.GetFileName(path) ?? string.Empty;
            if (fileName.StartsWith(
                fileNamePrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 兼容未 PackAndGo 的模板原名。
            var normalizedName = NormalizeComponentName(component.Name2);
            return normalizedName.StartsWith(
                fileNamePrefix,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool ComponentPathContainsPositionFolder(
            IComponent2 component,
            string positionName,
            bool elevated)
        {
            var path = (component?.GetPathName() ?? string.Empty).Replace('/', '\\');
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(positionName))
            {
                return false;
            }

            var typeFolder = elevated ? "增高基座" : "基座";
            var marker = "\\基座\\" + positionName + "\\" + typeFolder + "\\";
            return path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ComponentContainsInsulator(IComponent2 hostComponent)
        {
            if (hostComponent == null) return false;
            var children = hostComponent.GetChildren() as object[];
            if (children == null) throw new InvalidOperationException("基座内部组件反读失败：" + hostComponent.Name2);
            return children.OfType<IComponent2>().Any(child => IsComponentUnsuppressed(child)
                && ((Path.GetFileNameWithoutExtension(child.GetPathName()) ?? string.Empty)
                    .IndexOf("绝缘子", StringComparison.OrdinalIgnoreCase) >= 0
                    || NormalizeComponentName(child.Name2).IndexOf("绝缘子", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        internal static double ReadRequiredInsulatorBodyHeight(IModelDoc2 model, IReadOnlyList<string> positions)
        {
            if (positions == null || positions.Count == 0)
                throw new InvalidOperationException("绝缘子高度反读失败：没有启用的基座位置。");
            double? actual = null;
            foreach (var position in positions)
            {
                if (!TryReadInsulatorHeightFromStandaloneBase(model, position, out var height))
                    throw new InvalidOperationException("绝缘子高度反读失败：" + position + "缺少可读取的原驱动高度。");
                if (actual.HasValue && Math.Abs(actual.Value - height) > .01d)
                    throw new InvalidOperationException("绝缘子高度反读失败：已启用基座的实际绝缘子高度不一致。");
                actual = height;
            }
            return actual.Value;
        }

        private static double GetRequiredDimensionValue(ReferencedModelValueReader model, string name, string displayName) =>
            model.Dimension(name);

        private static double GetRequiredDimensionValue(ReferencedModelValueReader model, string[] names, string displayName)
        {
            foreach (var name in names)
                if (model.TryDimension(name, out var value)) return value;
            throw new InvalidOperationException("尺寸反读失败：" + model.Identity + "/" + displayName);
        }

        private static double GetRequiredGlobalVariableValue(ReferencedModelValueReader model, string name, string displayName) =>
            model.Global(name);

        private static double GetRequiredDimensionValue(IModelDoc2 model, string dimensionName, string displayName)
        {
            if (model == null)
            {
                throw new InvalidOperationException($"读取{displayName}失败，目标模型为空。");
            }

            if (!TryGetDimensionValue(model, dimensionName, out var value))
            {
                throw new InvalidOperationException($"读取{displayName}失败，未找到尺寸：{dimensionName}");
            }

            return Math.Round(value, 1);
        }

        private static double GetRequiredDimensionValue(IModelDoc2 model, string[] dimensionNames, string displayName)
        {
            if (TryGetDimensionValue(model, dimensionNames, out var value))
            {
                return Math.Round(value, 1);
            }

            throw new InvalidOperationException($"读取{displayName}失败，未找到以下任一尺寸：{string.Join("、", dimensionNames)}");
        }

        internal static bool TryGetDimensionValue(IModelDoc2 model, string dimensionName, out double value)
        {
            value = 0;
            if (model == null || string.IsNullOrWhiteSpace(dimensionName))
            {
                return false;
            }

            return new ReferencedModelValueReader(model).TryDimension(dimensionName, out value);
        }

        private static bool TryGetDimensionValue(IModelDoc2 model, string[] dimensionNames, out double value)
        {
            value = 0;
            if (dimensionNames == null || dimensionNames.Length == 0)
            {
                return false;
            }

            foreach (var dimensionName in dimensionNames)
            {
                if (TryGetDimensionValue(model, dimensionName, out value))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 读取延长基座（5GX.355.023-基架X柜X）的高度；当组件不存在或已压缩时返回 0。
        /// </summary>
        /// <param name="assemblyModel">柜体总装配体模型。</param>
        /// <param name="extensionBaseAssemblyPath">延长基座组件层级路径。</param>
        /// <returns>延长基座高度；不存在或被压缩时为 0。</returns>
        internal static double GetExtensionBaseHeight(IModelDoc2 assemblyModel, string[] extensionBaseAssemblyPath)
        {
            if (assemblyModel == null || extensionBaseAssemblyPath == null || extensionBaseAssemblyPath.Length == 0)
                throw new InvalidOperationException("延长基座反读失败：缺少原模型或组件路径。");
            var component = GetComponentByPath(assemblyModel, extensionBaseAssemblyPath);
            // 可选组件确认不存在或已压缩时，实际高度为零；存在的有效组件必须读取成功。
            if (component == null || component.IsSuppressed()) return 0;
            return Math.Round(ReferencedModelValueReader.FromComponent(component).Global(ExtensionBaseHeightVariableName), 1);
        }

        private static double GetRequiredGlobalVariableValue(IModelDoc2 model, string variableName, string displayName)
        {
            if (model == null)
            {
                throw new InvalidOperationException($"读取{displayName}失败，目标模型为空。");
            }

            if (!TryGetGlobalVariableValue(model, variableName, out var value))
            {
                throw new InvalidOperationException(CabinetHoleParameters.GetParameterDisplayName($"读取{displayName}失败，未找到全局变量：{variableName}"));
            }

            return Math.Round(value, 1);
        }

        internal static bool TryGetGlobalVariableValue(IModelDoc2 model, string variableName, out double value)
        {
            value = 0;
            if (model == null || string.IsNullOrWhiteSpace(variableName))
            {
                return false;
            }

            return new ReferencedModelValueReader(model).TryGlobal(variableName, out value);
        }

        internal static bool HasGlobalVariable(IModelDoc2 model, string variableName)
        {
            if (model == null || string.IsNullOrWhiteSpace(variableName))
            {
                return false;
            }

            var equationManager = model.GetEquationMgr() as IEquationMgr;
            if (equationManager == null)
            {
                return false;
            }

            var equationCount = equationManager.GetCount();
            for (var index = 0; index < equationCount; index++)
            {
                if (EquationDefinesGlobalVariable(
                    equationManager.Equation[index] as string,
                    variableName))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool EquationDefinesGlobalVariable(string equation, string globalVariableName)
        {
            if (string.IsNullOrWhiteSpace(equation)
                || string.IsNullOrWhiteSpace(globalVariableName))
            {
                return false;
            }

            var left = equation;
            var equalSignIndex = equation.IndexOf('=');
            if (equalSignIndex >= 0)
            {
                left = equation.Substring(0, equalSignIndex);
            }

            left = left.Trim();
            if (left.Length >= 2 && left[0] == '"' && left[left.Length - 1] == '"')
            {
                left = left.Substring(1, left.Length - 2);
            }

            return string.Equals(left, globalVariableName, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryParseEquationNumericValue(string equation, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(equation))
            {
                return false;
            }

            var equalSignIndex = equation.IndexOf('=');
            if (equalSignIndex < 0 || equalSignIndex >= equation.Length - 1)
            {
                return false;
            }

            var right = equation.Substring(equalSignIndex + 1).Trim();
            var match = Regex.Match(right,
                @"^(?<value>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[Ee][+-]?\d+)?)\s*(?:mm)?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success && double.TryParse(match.Groups["value"].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static IModelDoc2 GetBaseAssemblyModel(
            IModelDoc2 assemblyModel,
            IModelDoc2 subAssemblyModel,
            string[] baseAssemblyPath,
            bool resolveByComponentName)
        {
            var baseAssemblyModel = GetComponentModelByPath(assemblyModel, baseAssemblyPath);
            if (baseAssemblyModel != null || !resolveByComponentName)
            {
                return baseAssemblyModel;
            }

            var baseComponentName = GetLastPathSegment(baseAssemblyPath);
            return GetComponentModelByDescendantName(subAssemblyModel, baseComponentName)
                ?? GetComponentModelByDescendantName(assemblyModel, baseComponentName);
        }

        private static string GetLastPathSegment(string[] componentPath)
        {
            if (componentPath == null || componentPath.Length == 0)
            {
                return null;
            }

            return componentPath[componentPath.Length - 1];
        }

        private static IModelDoc2 GetComponentModelByDescendantName(IModelDoc2 rootModel, string componentName)
        {
            var component = FindFirstDescendantComponent(rootModel, componentName);
            return component?.GetModelDoc2() as IModelDoc2;
        }

        internal static IComponent2 FindFirstDescendantComponent(IModelDoc2 rootModel, string componentName)
        {
            if (rootModel == null || string.IsNullOrWhiteSpace(componentName))
            {
                return null;
            }

            var configuration = rootModel.ConfigurationManager?.ActiveConfiguration;
            var rootComponent = configuration?.GetRootComponent3(true) as IComponent2;
            if (rootComponent == null)
            {
                return null;
            }

            return FindFirstDescendantComponent(rootComponent, componentName);
        }

        private static IComponent2 FindFirstDescendantComponent(IComponent2 parentComponent, string componentName)
        {
            if (parentComponent == null || string.IsNullOrWhiteSpace(componentName))
            {
                return null;
            }

            if (ComponentNameMatches(parentComponent, componentName))
            {
                return parentComponent;
            }

            var children = parentComponent.GetChildren() as object[];
            if (children == null || children.Length == 0)
            {
                return null;
            }

            foreach (var childObject in children)
            {
                var childComponent = childObject as IComponent2;
                var matchedComponent = childComponent == null
                    ? null
                    : FindFirstDescendantComponent(childComponent, componentName);
                if (matchedComponent != null)
                {
                    return matchedComponent;
                }
            }

            return null;
        }

        private static IComponent2 FindFirstDescendantComponentContaining(
            IComponent2 parentComponent, string nameFragment, int requiredDocumentType)
        {
            if (parentComponent == null || string.IsNullOrWhiteSpace(nameFragment)) return null;
            if (parentComponent.IsSuppressed()) return null;
            if (NormalizeComponentName(parentComponent.Name2).IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (requiredDocumentType == 0) return parentComponent;
                var values = ReferencedModelValueReader.FromComponent(parentComponent);
                if (values.Model.GetType() == requiredDocumentType) return parentComponent;
            }
            var children = parentComponent.GetChildren() as object[];
            if (children == null) return null;
            foreach (var child in children.OfType<IComponent2>())
            {
                var matched = FindFirstDescendantComponentContaining(child, nameFragment, requiredDocumentType);
                if (matched != null) return matched;
            }
            return null;
        }

        internal static IModelDoc2 GetComponentModelByPath(IModelDoc2 rootModel, string[] componentPath)
        {
            var targetComponent = GetComponentByPath(rootModel, componentPath);
            return targetComponent?.GetModelDoc2() as IModelDoc2;
        }

        internal static IComponent2 GetComponentByPath(IModelDoc2 rootModel, string[] componentPath)
        {
            if (rootModel == null || componentPath == null || componentPath.Length == 0)
            {
                return null;
            }

            var configuration = rootModel.ConfigurationManager?.ActiveConfiguration;
            var rootComponent = configuration?.GetRootComponent3(true);
            if (rootComponent == null)
            {
                return null;
            }

            return FindChildComponentByPath(rootComponent, componentPath, 0);
        }

        private static IComponent2 FindChildComponentByPath(IComponent2 parentComponent, string[] componentPath, int pathIndex)
        {
            if (parentComponent == null || componentPath == null || pathIndex < 0 || pathIndex >= componentPath.Length) return null;
            if (parentComponent.IsSuppressed()) return null;
            var children = parentComponent.GetChildren() as object[];
            if (children == null) throw new InvalidOperationException("组件路径反读失败：" + parentComponent.Name2);
            var matches = children.OfType<IComponent2>()
                .Where(component => !component.IsSuppressed() && ComponentNameMatches(component, componentPath[pathIndex])).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("组件路径不唯一：" + string.Join("/", componentPath));
            if (matches.Length == 0) return null;
            return pathIndex == componentPath.Length - 1 ? matches[0]
                : FindChildComponentByPath(matches[0], componentPath, pathIndex + 1);
        }

        private static bool ComponentNameMatches(IComponent2 component, string expectedName)
        {
            if (component == null || string.IsNullOrWhiteSpace(expectedName))
            {
                return false;
            }

            var normalizedExpectedName = NormalizeComponentName(expectedName);
            var candidateNames = new[]
            {
                NormalizeComponentName(component.Name2),
                NormalizeComponentName(Path.GetFileNameWithoutExtension(component.GetPathName()))
            };

            return candidateNames.Any(name => string.Equals(name, normalizedExpectedName, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeComponentName(string componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName))
            {
                return string.Empty;
            }

            var normalizedName = componentName.Trim();
            var atIndex = normalizedName.IndexOf('@');
            if (atIndex >= 0)
            {
                normalizedName = normalizedName.Substring(0, atIndex);
            }

            var slashIndex = normalizedName.LastIndexOf('/');
            if (slashIndex >= 0)
            {
                normalizedName = normalizedName.Substring(slashIndex + 1);
            }

            normalizedName = Regex.Replace(normalizedName, @"<\d+>$", string.Empty);
            normalizedName = Regex.Replace(normalizedName, @"-\d+$", string.Empty);

            return normalizedName.Trim();
        }

        private sealed class CabinetSideReadSpec
        {
            public string DisplayName { get; set; }

            public string GroundHeightDimensionName { get; set; }

            public string[] SubAssemblyComponentPath { get; set; }

            public string[] CabinetComponentPath { get; set; }

            public string CabinetHeightVariableName { get; set; }

            public string CabinetDepthVariableName { get; set; }

            public string[] FrontBusbarCenterDistanceDimensionNames { get; set; }

            public string[] RearBusbarCenterDistanceDimensionNames { get; set; }

            public string[] FrontInsulatorHeightDimensionNames { get; set; }

            public string[] RearInsulatorHeightDimensionNames { get; set; }

            /// <summary>
            /// 为 true 时，从独立普通/增高基座读取绝缘子绝对定位高度，以及基座高/深/宽。
            /// </summary>
            public bool ReadInsulatorHeightFromStandaloneBase { get; set; }

            public string FrontStandaloneBasePositionName { get; set; }

            public string RearStandaloneBasePositionName { get; set; }

            public string[] FrontBaseAssemblyPath { get; set; }

            public string[] RearBaseAssemblyPath { get; set; }

            public string FrontBaseHeightVariableName { get; set; }

            public string RearBaseHeightVariableName { get; set; }

            public string FrontBaseDepthVariableName { get; set; }

            public string RearBaseDepthVariableName { get; set; }

            public string FrontBaseWidthVariableName { get; set; }

            public string RearBaseWidthVariableName { get; set; }

            public string[] FrontExtensionBaseAssemblyPath { get; set; }

            public string[] RearExtensionBaseAssemblyPath { get; set; }

            /// <summary>
            /// 为 true 时，基座装配体路径解析失败将按组件名在子装配体中递归查找。
            /// </summary>
            public bool ResolveBaseAssemblyByComponentName { get; set; }
        }

        /// <summary>
        /// 延长基座装配体（5GX.355.023-基架X柜X）内的高度全局变量名（无前缀）。
        /// </summary>
        private const string ExtensionBaseHeightVariableName = "高度";

        private const string StandaloneBaseFileNamePrefix = "5GX.355.020";

        private const string StandaloneElevatedBaseFileNamePrefix = "5GX.355.021";

        private const string SideToSideBase1FileNamePrefix = "5GX.355.030";

        private const string SideToSideBase2FileNamePrefix = "5GX.355.031";
        private const string FrontHoleToCabinetEdgeDimensionName = "距柜前距离@草图6";
        private const string FrontHoleFixedSpacingDimensionName = "接口固定孔间距@草图6";
        private const string RearHoleToCabinetEdgeDimensionName = "距柜后距离@草图8";
        private const string RearHoleFixedSpacingDimensionName = "接口固定孔间距@草图8";

        private const string StandaloneBaseHeightVariableName = "基座高";

        private const string StandaloneBaseWidthVariableName = "基座宽";

        private const string StandaloneBaseDepthVariableName = "基座深";

        private const string StandaloneBaseInsulatorPlacementDimensionName = "D1@绝缘子安装坐标系草图";

        private const string StandaloneElevatedInsulatorPlacementDimensionName = "D1@草图2";

        /// <summary>
        /// 柜体侧边读取结果。
        /// </summary>
        public sealed class CabinetSideDimensions
        {
            public double CabinetGroundHeight { get; set; }

            public double CabinetHeight { get; set; }

            public double CabinetDepth { get; set; }

            public double BaseToCabinetFrontDistance { get; set; }

            public double BaseToCabinetBackDistance { get; set; }

            public double BusbarCenterToCabinetFrontDistance { get; set; }

            public double BusbarCenterToCabinetBackDistance { get; set; }

            public double InsulatorHeight { get; set; }

            public double BaseHeight { get; set; }

            public double BaseDepth { get; set; }

            public double BaseWidth { get; set; }

            /// <summary>
            /// 当前启用孔对应独立基座的「基座开孔高度」。缺失时为 0。
            /// </summary>
            public double BaseOpeningHeight { get; set; }

            public CabinetHolePosition HolePosition { get; set; }
        }

        /// <summary>
        /// 当前采用的柜顶孔方向。
        /// </summary>
        public enum CabinetHolePosition
        {
            Front = 1,
            Back = 2
        }
    }
}
