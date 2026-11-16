using GeometRi;
using Newtonsoft.Json;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 侧对侧固定换相一次真实模型读取的纯数据快照。该类型不持有 COM 对象，
    /// 后续测试可直接反序列化 JSON，并按记录的原始输入和规划结果做离线断言。
    /// </summary>
    public sealed class SideToSideFixedPhaseChangeDebugSnapshot
    {
        public int SchemaVersion { get; set; } = 1;
        public string Scenario { get; set; } = "SideToSideFixedPhaseChange";
        public string CoordinateUnit { get; set; } = "mm";
        public string CaptureId { get; set; }
        public string CapturedAtLocal { get; set; }
        public string CaptureAssemblyVersion { get; set; }
        public string CaptureStatus { get; set; }
        public string Failure { get; set; }
        public string AssemblyTitle { get; set; }
        public string AssemblyPath { get; set; }
        public List<string> CaptureWarnings { get; set; } = new List<string>();
        public SideToSideFixedPhaseChangeDebugInputSnapshot Input { get; set; }
        public List<SideToSideFixedPhaseChangeDebugComponentSnapshot> Components
            { get; set; } = new List<SideToSideFixedPhaseChangeDebugComponentSnapshot>();
        public List<SideToSideFixedPhaseChangeDebugModeledBusbarSnapshot>
            CabinetTopExtensionBusbars { get; set; } =
                new List<SideToSideFixedPhaseChangeDebugModeledBusbarSnapshot>();
        public List<SideToSideFixedPhaseChangeDebugInsulatorSnapshot> Insulators
            { get; set; } = new List<SideToSideFixedPhaseChangeDebugInsulatorSnapshot>();
        public SideToSideFixedPhaseChangePlanningSnapshot Planning { get; set; }
        public List<SideToSideFixedPhaseChangeDebugGeneratedGroupSnapshot>
            GeneratedGroups { get; set; } =
                new List<SideToSideFixedPhaseChangeDebugGeneratedGroupSnapshot>();
    }

    /// <summary>
    /// Debug 测试数据文件。总装配体完整路径直接作为 JSON 字典 Key；
    /// 同一路径后一次采集覆盖前一次采集，不同路径持续追加。
    /// </summary>
    public sealed class SideToSideFixedPhaseChangeDebugDataStore
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, SideToSideFixedPhaseChangeDebugSnapshot>
            SnapshotsByAssemblyPath { get; set; } =
                new Dictionary<string, SideToSideFixedPhaseChangeDebugSnapshot>(
                    StringComparer.OrdinalIgnoreCase);
    }

    public sealed class SideToSideFixedPhaseChangeDebugInputSnapshot
    {
        public string LengthDirection { get; set; }
        public string HeightDirection { get; set; }
        public string WidthDirection { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate LengthVector { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate HeightVector { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate WidthVector { get; set; }
        public int SinglePhaseStackCount { get; set; }
        public double BendingRadius { get; set; }
        public double BendAllowance { get; set; }
        public List<double> BreakpointPositions { get; set; } = new List<double>();
    }

    public sealed class SideToSideFixedPhaseChangeDebugComponentSnapshot
    {
        public string ComponentName { get; set; }
        public string ParentComponentName { get; set; }
        public string ModelPath { get; set; }
        public string SystemPartType { get; set; }
        public bool IsTopLevel { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate BoxMinimum { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate BoxMaximum { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate BoxCenter { get; set; }
    }

    public sealed class SideToSideFixedPhaseChangeDebugModeledBusbarSnapshot
    {
        public string ComponentName { get; set; }
        public string Phase { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate CenterPoint { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate TerminalStart { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate TerminalEnd { get; set; }
        public SideToSideFixedPhaseChangeDebugBusbarSettingSnapshot Centerline
            { get; set; }
    }

    public sealed class SideToSideFixedPhaseChangeDebugInsulatorSnapshot
    {
        public string ComponentName { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate ComponentCenter { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate ConnectionPlanePoint
            { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate ConnectionPlaneNormal
            { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate ConnectionCircleCenter
            { get; set; }
        public double ConnectionCircleRadius { get; set; }
    }

    public sealed class SideToSideFixedPhaseChangePlanningSnapshot
    {
        public double PhysicalAlignmentTolerance { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate LengthVector { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate HeightVector { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate WidthVector { get; set; }
        public string BCabinetComponentName { get; set; }
        public string ACabinetComponentName { get; set; }
        public string BCabinetBaseComponentName { get; set; }
        public string ACabinetBaseComponentName { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate BCabinetBaseCenter
            { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate ACabinetBaseCenter
            { get; set; }
        public double PhaseAPairDistance { get; set; }
        public double PhaseCPairDistance { get; set; }
        public string NearestOuterPhase { get; set; }
        public List<string> ACabinetPhysicalPhaseByLogicalPhase { get; set; } =
            new List<string>();
        public List<SideToSideFixedPhaseChangePhaseAssignmentSnapshot>
            BCabinetPhaseAssignments { get; set; } =
                new List<SideToSideFixedPhaseChangePhaseAssignmentSnapshot>();
        public List<SideToSideFixedPhaseChangePhaseAssignmentSnapshot>
            ACabinetPhaseAssignments { get; set; } =
                new List<SideToSideFixedPhaseChangePhaseAssignmentSnapshot>();
        public List<SideToSideFixedPhaseChangeFrameLaneSnapshot> FrameHeightLanes
            { get; set; } = new List<SideToSideFixedPhaseChangeFrameLaneSnapshot>();
    }

    public sealed class SideToSideFixedPhaseChangePhaseAssignmentSnapshot
    {
        public string Phase { get; set; }
        public string BaseInsulatorComponentName { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate BaseInsulatorCircleCenter
            { get; set; }
        public string FrameInsulatorComponentName { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate FrameInsulatorCircleCenter
            { get; set; }
    }

    public sealed class SideToSideFixedPhaseChangeFrameLaneSnapshot
    {
        public int PhysicalIndex { get; set; }
        public string FrameComponentName { get; set; }
        public List<SideToSideFixedPhaseChangeFrameLaneInsulatorSnapshot>
            HeightLaneInsulators { get; set; } =
                new List<SideToSideFixedPhaseChangeFrameLaneInsulatorSnapshot>();
    }

    public sealed class SideToSideFixedPhaseChangeFrameLaneInsulatorSnapshot
    {
        public int HeightLaneIndex { get; set; }
        public string InsulatorComponentName { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate ConnectionCircleCenter
            { get; set; }
        public double? HeightCoordinate { get; set; }
    }

    public sealed class SideToSideFixedPhaseChangeDebugGeneratedGroupSnapshot
    {
        public string DisplayName { get; set; }
        public string Kind { get; set; }
        public int RouteGroupIndex { get; set; }
        public bool IsBridge { get; set; }
        public List<SideToSideFixedPhaseChangeDebugBusbarSettingSnapshot> Busbars
            { get; set; } =
                new List<SideToSideFixedPhaseChangeDebugBusbarSettingSnapshot>();
    }

    public sealed class SideToSideFixedPhaseChangeDebugBusbarSettingSnapshot
    {
        public bool IsCenter { get; set; }
        public string GroupKey { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate StartBusbarVector
            { get; set; }
        public SideToSideFixedPhaseChangeDebugCoordinate WidthVector { get; set; }
        public List<SideToSideFixedPhaseChangeDebugCoordinate> Points { get; set; } =
            new List<SideToSideFixedPhaseChangeDebugCoordinate>();
        public List<string> PreviewBendPointKinds { get; set; } = new List<string>();
        public double BarBendingRadius { get; set; }
        public double VerticalBendingRadius { get; set; }
        public double BendAllowance { get; set; }
        public double BusbarThickness { get; set; }
        public double BusbarWidth { get; set; }
        public double StartLapWidth { get; set; }
        public double EndLapWidth { get; set; }
    }

    public sealed class SideToSideFixedPhaseChangeDebugCoordinate
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    /// <summary>
    /// Debug 运行时采集会话。Release 中调用方由条件编译完全移除，Save 本身也
    /// 没有文件写入实现，确保发布版本不保留客户模型测试数据。
    /// </summary>
    internal sealed class SideToSideFixedPhaseChangeDebugCaptureSession
    {
        private static readonly object DebugCaptureWriteLock = new object();
        private static readonly Encoding DebugCaptureEncoding = new UTF8Encoding(false);
        private readonly SideToSideFixedPhaseChangeDebugSnapshot _snapshot;

        internal SideToSideFixedPhaseChangeDebugCaptureSession(
            BusbarBridgeAxisSetting axisSetting,
            int singlePhaseStackCount,
            IReadOnlyList<double> breakpointPositions,
            double bendingRadius,
            double bendAllowance)
        {
            _snapshot = new SideToSideFixedPhaseChangeDebugSnapshot
            {
                CaptureId = Guid.NewGuid().ToString("N"),
                CapturedAtLocal = DateTimeOffset.Now.ToString("O"),
                CaptureAssemblyVersion = Assembly.GetExecutingAssembly()
                    .GetName()
                    .Version?.ToString(),
                CaptureStatus = "Started",
                Input = new SideToSideFixedPhaseChangeDebugInputSnapshot
                {
                    LengthDirection = axisSetting?.LengthDirection.ToString(),
                    HeightDirection = axisSetting?.HeightDirection.ToString(),
                    WidthDirection = axisSetting?.WidthDirection.ToString(),
                    LengthVector = CreateCoordinate(axisSetting?.LengthVector),
                    HeightVector = CreateCoordinate(axisSetting?.HeightVector),
                    WidthVector = CreateCoordinate(axisSetting?.WidthVector),
                    SinglePhaseStackCount = singlePhaseStackCount,
                    BendingRadius = bendingRadius,
                    BendAllowance = bendAllowance,
                    BreakpointPositions = (breakpointPositions ??
                        Array.Empty<double>()).ToList()
                }
            };
        }

        internal void AttachCatalog(AssemblyPartCatalog partCatalog)
        {
            if (partCatalog == null)
            {
                return;
            }

            try
            {
                _snapshot.AssemblyTitle = TryReadAssemblyTitle(
                    partCatalog.AssemblyModel);
                _snapshot.AssemblyPath = TryReadAssemblyPath(
                    partCatalog.AssemblyModel);
                _snapshot.Components = partCatalog.Components
                    .Select(CreateComponentSnapshot)
                    .ToList();
            }
            catch (Exception catalogCaptureException)
            {
                AddCaptureWarning("装配体组件快照", catalogCaptureException);
            }
        }

        internal void AttachCabinetTopExtensionBusbars(
            IReadOnlyList<ModeledBusbar> extensionBusbars)
        {
            try
            {
                _snapshot.CabinetTopExtensionBusbars =
                    (extensionBusbars ?? Array.Empty<ModeledBusbar>())
                    .Select(CreateModeledBusbarSnapshot)
                    .ToList();
            }
            catch (Exception busbarCaptureException)
            {
                AddCaptureWarning("柜顶伸出排快照", busbarCaptureException);
            }
        }

        internal void AttachInsulators(
            IReadOnlyList<InsulatorConnectionInfo> connectionInsulators)
        {
            try
            {
                _snapshot.Insulators =
                    (connectionInsulators ??
                        Array.Empty<InsulatorConnectionInfo>())
                    .Select(CreateInsulatorSnapshot)
                    .ToList();
            }
            catch (Exception insulatorCaptureException)
            {
                AddCaptureWarning("绝缘子配合面快照", insulatorCaptureException);
            }
        }

        internal void AttachPlan(FaceToFaceModelBusbarPlan busbarPlan)
        {
            try
            {
                _snapshot.Planning =
                    busbarPlan?.SideToSideFixedPhaseChangePlanning;
                if (!string.IsNullOrWhiteSpace(
                        busbarPlan?
                            .SideToSideFixedPhaseChangePlanningCaptureWarning))
                {
                    _snapshot.CaptureWarnings.Add(
                        "规划诊断快照：" +
                        busbarPlan
                            .SideToSideFixedPhaseChangePlanningCaptureWarning);
                }
                _snapshot.GeneratedGroups = busbarPlan?.GeneratedBusbarGroups?
                    .Select(CreateGeneratedGroupSnapshot)
                    .ToList() ??
                    new List<
                        SideToSideFixedPhaseChangeDebugGeneratedGroupSnapshot>();
            }
            catch (Exception planCaptureException)
            {
                AddCaptureWarning("规划结果快照", planCaptureException);
            }
        }

        internal void MarkSuccess()
        {
            _snapshot.CaptureStatus = "Success";
        }

        internal void MarkFailure(Exception captureTargetException)
        {
            _snapshot.CaptureStatus = "Failed";
            _snapshot.Failure = captureTargetException?.ToString();
        }

        internal void Save()
        {
#if DEBUG
            try
            {
                if (string.Equals(
                        _snapshot.CaptureStatus,
                        "Started",
                        StringComparison.Ordinal))
                {
                    _snapshot.CaptureStatus = "Incomplete";
                }

                var debugCaptureDirectory = ResolveDebugCaptureDirectory();
                Directory.CreateDirectory(debugCaptureDirectory);
                var assemblyPathKey = NormalizeAssemblyPathKey(
                    _snapshot.AssemblyPath);
                _snapshot.AssemblyPath = assemblyPathKey;
                var debugCaptureDataStorePath = Path.Combine(
                    debugCaptureDirectory,
                    "side-to-side-fixed-phase-change-by-assembly.json");
                lock (DebugCaptureWriteLock)
                {
                    var debugDataStore = File.Exists(
                            debugCaptureDataStorePath)
                        ? LoadDataStore(debugCaptureDataStorePath)
                        : new SideToSideFixedPhaseChangeDebugDataStore();
                    debugDataStore.SnapshotsByAssemblyPath[assemblyPathKey] =
                        _snapshot;
                    WriteDataStoreAtomically(
                        debugCaptureDataStorePath,
                        debugDataStore);
                }

                FaceToWallPreviewTrace.Info(
                    "侧对侧固定换相-Debug测试数据已保存",
                    "状态=" + _snapshot.CaptureStatus +
                    "，装配体Key=" + assemblyPathKey +
                    "，文件=" + debugCaptureDataStorePath);
            }
            catch (Exception debugCaptureWriteException)
            {
                FaceToWallPreviewTrace.Error(
                    "侧对侧固定换相-Debug测试数据保存失败",
                    debugCaptureWriteException);
            }
#endif
        }

        internal static SideToSideFixedPhaseChangeDebugDataStore LoadDataStore(
            string debugDataStoreFilePath)
        {
            if (string.IsNullOrWhiteSpace(debugDataStoreFilePath))
            {
                throw new ArgumentException(
                    "Debug 测试数据文件路径不能为空。",
                    nameof(debugDataStoreFilePath));
            }

            var debugDataStore = JsonConvert.DeserializeObject<
                SideToSideFixedPhaseChangeDebugDataStore>(
                File.ReadAllText(debugDataStoreFilePath, Encoding.UTF8));
            if (debugDataStore == null || debugDataStore.SchemaVersion != 1)
            {
                throw new InvalidDataException(
                    "侧对侧固定换相 Debug 测试数据版本无效。");
            }
            var normalizedSnapshotsByAssemblyPath =
                new Dictionary<string, SideToSideFixedPhaseChangeDebugSnapshot>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (var assemblySnapshotPair in
                debugDataStore.SnapshotsByAssemblyPath ??
                    new Dictionary<
                        string,
                        SideToSideFixedPhaseChangeDebugSnapshot>())
            {
                normalizedSnapshotsByAssemblyPath[assemblySnapshotPair.Key] =
                    assemblySnapshotPair.Value;
            }
            debugDataStore.SnapshotsByAssemblyPath =
                normalizedSnapshotsByAssemblyPath;
            return debugDataStore;
        }

        internal static SideToSideFixedPhaseChangeDebugSnapshot Load(
            string debugDataStoreFilePath,
            string assemblyFullPath)
        {
            var assemblyPathKey = NormalizeAssemblyPathKey(assemblyFullPath);
            var debugDataStore = LoadDataStore(debugDataStoreFilePath);
            if (!debugDataStore.SnapshotsByAssemblyPath.TryGetValue(
                    assemblyPathKey,
                    out var assemblySnapshot))
            {
                throw new KeyNotFoundException(
                    "没有找到总装配体路径对应的侧对侧固定换相测试数据：" +
                    assemblyPathKey);
            }
            return assemblySnapshot;
        }

        internal static SideToSideFixedPhaseChangeDebugCoordinate CreateCoordinate(
            Point3d point)
        {
            return point == null
                ? null
                : new SideToSideFixedPhaseChangeDebugCoordinate
                {
                    X = point.X,
                    Y = point.Y,
                    Z = point.Z
                };
        }

        internal static SideToSideFixedPhaseChangeDebugCoordinate CreateCoordinate(
            Vector3d vector)
        {
            return vector == null
                ? null
                : new SideToSideFixedPhaseChangeDebugCoordinate
                {
                    X = vector.X,
                    Y = vector.Y,
                    Z = vector.Z
                };
        }

        private static SideToSideFixedPhaseChangeDebugComponentSnapshot
            CreateComponentSnapshot(Component2 component)
        {
            Component2 parentComponent = null;
            try
            {
                parentComponent = component?.GetParent() as Component2;
            }
            catch
            {
                // 父级读取失败仍保留其余物理数据。
            }

            string modelPath = null;
            try
            {
                modelPath = component?.GetPathName();
            }
            catch
            {
                // 路径不是几何回放的必要字段。
            }

            Point3d boxMinimum = null;
            Point3d boxMaximum = null;
            if (component != null)
            {
                BusbarGeometryUtility.TryGetComponentBoundingBox(
                    component,
                    out boxMinimum,
                    out boxMaximum);
            }
            string systemPartTypeName = null;
            try
            {
                systemPartTypeName = component?.GetSystemPartType().ToString();
            }
            catch
            {
                // 零件类型缺失时仍保留组件层级和 Box。
            }
            return new SideToSideFixedPhaseChangeDebugComponentSnapshot
            {
                ComponentName = BusbarGeometryUtility.SafeName(component),
                ParentComponentName = BusbarGeometryUtility.SafeName(parentComponent),
                ModelPath = modelPath,
                SystemPartType = systemPartTypeName,
                IsTopLevel = parentComponent == null,
                BoxMinimum = CreateCoordinate(boxMinimum),
                BoxMaximum = CreateCoordinate(boxMaximum),
                BoxCenter = boxMinimum == null || boxMaximum == null
                    ? null
                    : new SideToSideFixedPhaseChangeDebugCoordinate
                    {
                        X = (boxMinimum.X + boxMaximum.X) / 2d,
                        Y = (boxMinimum.Y + boxMaximum.Y) / 2d,
                        Z = (boxMinimum.Z + boxMaximum.Z) / 2d
                    }
            };
        }

        private static SideToSideFixedPhaseChangeDebugModeledBusbarSnapshot
            CreateModeledBusbarSnapshot(ModeledBusbar modeledBusbar)
        {
            return new SideToSideFixedPhaseChangeDebugModeledBusbarSnapshot
            {
                ComponentName = BusbarGeometryUtility.SafeName(
                    modeledBusbar?.Component),
                Phase = modeledBusbar?.Phase.ToString(),
                CenterPoint = CreateCoordinate(modeledBusbar?.CenterPoint),
                TerminalStart = CreateCoordinate(modeledBusbar?.TerminalSegment?.P1),
                TerminalEnd = CreateCoordinate(modeledBusbar?.TerminalSegment?.P2),
                Centerline = CreateBusbarSettingSnapshot(
                    modeledBusbar?.CenterlineSetting)
            };
        }

        private static SideToSideFixedPhaseChangeDebugInsulatorSnapshot
            CreateInsulatorSnapshot(InsulatorConnectionInfo connectionInsulator)
        {
            return new SideToSideFixedPhaseChangeDebugInsulatorSnapshot
            {
                ComponentName = BusbarGeometryUtility.SafeName(
                    connectionInsulator?.Component),
                ComponentCenter = CreateCoordinate(
                    connectionInsulator?.ComponentCenter),
                ConnectionPlanePoint = CreateCoordinate(
                    connectionInsulator?.ConnectionPlane?.Point),
                ConnectionPlaneNormal = CreateCoordinate(
                    connectionInsulator?.ConnectionPlane?.Normal),
                ConnectionCircleCenter = CreateCoordinate(
                    connectionInsulator?.ConnectionCircleCenter),
                ConnectionCircleRadius =
                    connectionInsulator?.ConnectionCircleRadius ?? 0d
            };
        }

        private static SideToSideFixedPhaseChangeDebugGeneratedGroupSnapshot
            CreateGeneratedGroupSnapshot(
            FaceToFaceGeneratedBusbarGroup generatedBusbarGroup)
        {
            return new SideToSideFixedPhaseChangeDebugGeneratedGroupSnapshot
            {
                DisplayName = generatedBusbarGroup?.DisplayName,
                Kind = generatedBusbarGroup?.Kind.ToString(),
                RouteGroupIndex = generatedBusbarGroup?.RouteGroupIndex ?? -1,
                IsBridge = generatedBusbarGroup?.IsBridge == true,
                Busbars = generatedBusbarGroup?.Busbars
                    .Select(CreateBusbarSettingSnapshot)
                    .ToList() ??
                    new List<SideToSideFixedPhaseChangeDebugBusbarSettingSnapshot>()
            };
        }

        private static SideToSideFixedPhaseChangeDebugBusbarSettingSnapshot
            CreateBusbarSettingSnapshot(PlanarBendBusbarSetting busbarSetting)
        {
            if (busbarSetting == null)
            {
                return null;
            }

            return new SideToSideFixedPhaseChangeDebugBusbarSettingSnapshot
            {
                IsCenter = busbarSetting.IsCenter,
                GroupKey = busbarSetting.GroupKey,
                StartBusbarVector = CreateCoordinate(
                    busbarSetting.StartBusbarVector),
                WidthVector = CreateCoordinate(busbarSetting.WidthVector),
                Points = (busbarSetting.PointList ?? new List<Point3d>())
                    .Select(CreateCoordinate)
                    .ToList(),
                PreviewBendPointKinds = (busbarSetting.PreviewBendPointKinds ??
                    new List<BusbarPreviewBendPointKind_e>())
                    .Select(bendPointKind => bendPointKind.ToString())
                    .ToList(),
                BarBendingRadius = busbarSetting.BarBendingRadius,
                VerticalBendingRadius = busbarSetting.VerticalBendingRadius,
                BendAllowance = busbarSetting.BendAllowance,
                BusbarThickness = busbarSetting.BusBarThickness,
                BusbarWidth = busbarSetting.BusBarWidth,
                StartLapWidth = busbarSetting.StartLapWidth,
                EndLapWidth = busbarSetting.EndLapWidth
            };
        }

#if DEBUG
        private static void WriteDataStoreAtomically(
            string debugDataStoreFilePath,
            SideToSideFixedPhaseChangeDebugDataStore debugDataStore)
        {
            var serializedDataStore = JsonConvert.SerializeObject(
                debugDataStore,
                Formatting.Indented);
            var temporaryDataStoreFilePath = debugDataStoreFilePath + "." +
                Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(
                    temporaryDataStoreFilePath,
                    serializedDataStore,
                    DebugCaptureEncoding);
                if (File.Exists(debugDataStoreFilePath))
                {
                    File.Replace(
                        temporaryDataStoreFilePath,
                        debugDataStoreFilePath,
                        null);
                }
                else
                {
                    File.Move(
                        temporaryDataStoreFilePath,
                        debugDataStoreFilePath);
                }
            }
            finally
            {
                if (File.Exists(temporaryDataStoreFilePath))
                {
                    File.Delete(temporaryDataStoreFilePath);
                }
            }
        }

        private static string ResolveDebugCaptureDirectory()
        {
            var executingAssemblyDirectory = Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location);
            return !string.IsNullOrWhiteSpace(executingAssemblyDirectory)
                ? Path.Combine(
                    executingAssemblyDirectory,
                    "logs",
                    "debug-captures",
                    "side-to-side-fixed-phase-change")
                : Path.Combine(
                    Path.GetTempPath(),
                    "CKBusbarBridge",
                    "debug-captures",
                    "side-to-side-fixed-phase-change");
        }
#endif

        private static string NormalizeAssemblyPathKey(string assemblyFullPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyFullPath) ||
                !Path.IsPathRooted(assemblyFullPath))
            {
                throw new InvalidOperationException(
                    "Debug 测试数据必须以已保存总装配体的完整路径作为 Key。" +
                    "请先保存总装配体后再执行预览。");
            }
            return Path.GetFullPath(assemblyFullPath.Trim());
        }

        private static string TryReadAssemblyTitle(IModelDoc2 assemblyModel)
        {
            try
            {
                return assemblyModel?.GetTitle();
            }
            catch
            {
                return null;
            }
        }

        private static string TryReadAssemblyPath(IModelDoc2 assemblyModel)
        {
            try
            {
                return assemblyModel?.GetPathName();
            }
            catch
            {
                return null;
            }
        }

        private void AddCaptureWarning(
            string captureStageName,
            Exception captureException)
        {
            _snapshot.CaptureWarnings.Add(
                (captureStageName ?? "未命名采集阶段") + "：" +
                (captureException?.Message ?? "未知错误"));
        }
    }
}
