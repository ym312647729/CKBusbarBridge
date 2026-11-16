using System;
using System.ComponentModel;
using System.Globalization;
using CKBusbarBridge;

namespace CKBusbarBridge.Models
{
    /// <summary>表格输入与视口尺寸输入共享的参数通知入口。</summary>
    public abstract class CabinetParameterNotificationObject : NotificationObject
    {
        protected void SetParameterValue<T>(ref T parameterStorage, T parameterValue, string propertyName)
        {
            // 相等判断用于检测输入变化，不是工程数值容差校验。
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(parameterStorage, parameterValue))
                return;
            parameterStorage = parameterValue;
            RaisePropertyChanged(propertyName);
        }
    }

    internal static class InsulationDistanceMetadata
    {
        internal const string PropertyName = "绝缘距离";
        internal const double DefaultMillimetres = 125d;

        private static string SettingsPath => System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "CKBusbarBridge", "clearance-mm.txt");

        internal static double ConfiguredMillimetres
        {
            get
            {
                try
                {
                    return System.IO.File.Exists(SettingsPath)
                        ? ParseOrDefault(System.IO.File.ReadAllText(SettingsPath), out _)
                        : DefaultMillimetres;
                }
                catch (System.IO.IOException) { return DefaultMillimetres; }
                catch (UnauthorizedAccessException) { return DefaultMillimetres; }
            }
        }

        internal static void SaveDefault(double millimetres)
        {
            var value = Format(millimetres);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath));
            System.IO.File.WriteAllText(SettingsPath, value, new System.Text.UTF8Encoding(false));
        }

        internal static bool TryParse(string text, out double millimetres)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out millimetres)
                && !double.IsNaN(millimetres)
                && !double.IsInfinity(millimetres)
                && millimetres > 0d;
        }

        internal static double ParseOrDefault(string text, out bool usedDefault)
        {
            if (TryParse(text, out var value))
            {
                usedDefault = false;
                return value;
            }
            usedDefault = true;
            return DefaultMillimetres;
        }

        internal static string Format(double millimetres)
        {
            if (double.IsNaN(millimetres) || double.IsInfinity(millimetres)
                || millimetres <= 0d)
                throw new ArgumentOutOfRangeException(nameof(millimetres),
                    "绝缘距离必须为有限正数。");
            return millimetres.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 柜体驱动界面使用绝缘子与其自带支架的组合高度；绝缘子模型中的
    /// “绝缘子高度”全局变量仍只表示绝缘子本体高度。
    /// 组合高度不包含上方垫块；基架内总成默认采用同一柜体输入总高。
    /// </summary>
    public static class InsulatorHeightRules
    {
        public const double InsulatorBracketHeight = 25d;
        public const double BaseSupportBracketHeight = 41.5d;
        public const double DefaultInsulatorBodyHeight = 205d;
        // 各场景界面默认总高独立于模板的205 mm本体尺寸补偿基准。
        public const double DefaultInsulatorWithBracketHeight = 165d;

        public static double ToBodyHeight(double insulatorWithBracketHeight)
        {
            ValidateFinitePositive(
                insulatorWithBracketHeight,
                nameof(insulatorWithBracketHeight));
            var bodyHeight = insulatorWithBracketHeight -
                InsulatorBracketHeight;
            if (bodyHeight <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(insulatorWithBracketHeight),
                    insulatorWithBracketHeight,
                    "绝缘子+支架高度必须大于支架高度25 mm。");
            }

            return bodyHeight;
        }

        public static double ToInsulatorWithBracketHeight(
            double insulatorBodyHeight)
        {
            ValidateFinitePositive(
                insulatorBodyHeight,
                nameof(insulatorBodyHeight));
            return insulatorBodyHeight + InsulatorBracketHeight;
        }

        private static void ValidateFinitePositive(double value, string name)
        {
            if (!(value > 0d) || double.IsNaN(value) ||
                double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    name,
                    value,
                    name + "必须为有限正数。");
            }
        }
    }

    /// <summary>
    /// 柜体驱动窗口使用的根参数模型。
    /// 统一汇总项目路径、整体参数、公共参数，以及 A/B 两侧柜体与开孔参数，
    /// 便于在 ViewModel 中集中实例化并提供给界面进行数据绑定。
    /// </summary>
    public class CabinetDriverParameters : CabinetParameterNotificationObject
    {
        /// <summary>
        /// 初始化柜体驱动参数，并为各层级子参数对象设置默认实例和默认值。
        /// </summary>
        public CabinetDriverParameters()
        {
            General = new CabinetGeneralParameters();
            ACabinet = CreateDefaultCabinetSideParameters(2240, 1500, 1000, 80);
            BCabinet = CreateDefaultCabinetSideParameters(2240, 1500, 1000, 80);

            InitializeFaceToFaceBusbarCenterDistances();
            WireFaceToFaceBaseOpeningHeightSync();
        }

        /// <summary>
        /// 创建单侧柜体默认参数，铜排中心距离在创建后单独初始化。
        /// </summary>
        private static CabinetSideParameters CreateDefaultCabinetSideParameters(double cabinetHeight, double cabinetDepth, double cabinetWidth, double groundHeight)
        {
            const double defaultBaseHeight = 1000;

            return new CabinetSideParameters
            {
                CabinetHeight = cabinetHeight,
                CabinetDepth = cabinetDepth,
                CabinetWidth = cabinetWidth,
                GroundHeight = groundHeight,
                FrontHole = CreateDefaultFrontHoleParameters(defaultBaseHeight),
                BackHole = CreateDefaultBackHoleParameters(defaultBaseHeight)
            };
        }

        private static CabinetHoleParameters CreateDefaultFrontHoleParameters(double defaultBaseHeight)
        {
            return new CabinetHoleParameters
            {
                IsEnabled = true,
                InsulatorPosition = 500,
                BaseHeight = defaultBaseHeight,
                BaseOpeningHeight = CabinetHoleParameters.DefaultBaseOpeningHeight,
                ExtensionBaseHeight = CabinetHoleParameters.CalculateExtensionBaseHeight(defaultBaseHeight),
                ToCabinetEdgeDistance = 740,
                FixedHoleSpacing = 475,
                L1 = 100,
                L2 = 250,
                L3 = 350
            };
        }

        private static CabinetHoleParameters CreateDefaultBackHoleParameters(double defaultBaseHeight)
        {
            return new CabinetHoleParameters
            {
                IsEnabled = false,
                InsulatorPosition = 500,
                BaseHeight = defaultBaseHeight,
                BaseOpeningHeight = CabinetHoleParameters.DefaultBaseOpeningHeight,
                ExtensionBaseHeight = CabinetHoleParameters.CalculateExtensionBaseHeight(defaultBaseHeight),
                ToCabinetEdgeDistance = 25,
                FixedHoleSpacing = 440,
                L1 = 100,
                L2 = 250,
                L3 = 350
            };
        }

        /// <summary>
        /// 仅在创建参数时设置固定默认铜排中心距离；后续编辑及模型反读均保留独立输入值。
        /// </summary>
        private void InitializeFaceToFaceBusbarCenterDistances()
        {
            InitializeHoleBusbarCenterDistances(ACabinet?.FrontHole, isFrontHole: true);
            InitializeHoleBusbarCenterDistances(ACabinet?.BackHole, isFrontHole: false);
            InitializeHoleBusbarCenterDistances(BCabinet?.FrontHole, isFrontHole: true);
            InitializeHoleBusbarCenterDistances(BCabinet?.BackHole, isFrontHole: false);
        }

        /// <summary>
        /// 初始化单个开孔的独立默认铜排中心距离，并计算铜排中心距基座距离。
        /// </summary>
        /// <param name="holeParameters">开孔参数。</param>
        /// <param name="isFrontHole">是否为柜前孔。</param>
        private void InitializeHoleBusbarCenterDistances(CabinetHoleParameters holeParameters, bool isFrontHole)
        {
            if (holeParameters == null || General == null)
            {
                return;
            }

            var busbarThickness = FaceToFaceBusbarCenterDistanceHelper.ResolveBusbarThickness(General.BusbarSpec);
            var baseDepth = FaceToFaceBusbarCenterDistanceHelper.GetBaseDepth(holeParameters.FixedHoleSpacing);
            holeParameters.BusbarCenterToBaseOffset = FaceToFaceBusbarCenterDistanceHelper.GetBusbarCenterToBaseDistance(
                baseDepth,
                General.InsulatorHeight,
                busbarThickness);
            holeParameters.BusbarCenterToCabinetEdgeDistance = isFrontHole ? 936 : 210;
        }

        /// <summary>
        /// 面对面场景下 A/B 柜对应开孔的「基座开孔高度」保持一致：改任意一侧，另一侧同步。
        /// </summary>
        private bool _syncingBaseOpeningHeights;

        private void WireFaceToFaceBaseOpeningHeightSync()
        {
            WirePairedBaseOpeningHeightSync(ACabinet?.FrontHole, BCabinet?.FrontHole);
            WirePairedBaseOpeningHeightSync(ACabinet?.BackHole, BCabinet?.BackHole);
        }

        private void WirePairedBaseOpeningHeightSync(CabinetHoleParameters first, CabinetHoleParameters second)
        {
            if (first == null || second == null)
            {
                return;
            }

            first.PropertyChanged += (sender, args) => SyncPairedBaseOpeningHeight(args, first, second);
            second.PropertyChanged += (sender, args) => SyncPairedBaseOpeningHeight(args, second, first);
        }

        private void SyncPairedBaseOpeningHeight(
            PropertyChangedEventArgs args,
            CabinetHoleParameters source,
            CabinetHoleParameters target)
        {
            if (_syncingBaseOpeningHeights
                || args == null
                || args.PropertyName != nameof(CabinetHoleParameters.BaseOpeningHeight)
                || source == null
                || target == null)
            {
                return;
            }

            _syncingBaseOpeningHeights = true;
            try
            {
                target.BaseOpeningHeight = source.BaseOpeningHeight;
            }
            finally
            {
                _syncingBaseOpeningHeights = false;
            }
        }

        private string _cabinetDriverParametersProjectPath;
        /// <summary>
        /// 当前项目路径。
        /// 通常由“设置项目路径”按钮选择后回写，用于后续生成或导出逻辑。
        /// </summary>
        public string ProjectPath
        {
            get => _cabinetDriverParametersProjectPath;
            set => SetParameterValue(ref _cabinetDriverParametersProjectPath, value, nameof(ProjectPath));
        }

        /// <summary>
        /// 整体参数。
        /// 包含楼高、柜间距、柜间偏心值、母排规格等与单双柜无关的公共输入。
        /// </summary>
        public CabinetGeneralParameters General { get; }

        /// <summary>
        /// A 柜参数。
        /// 包含柜高、柜深、柜宽、离地高度及前后开孔参数。
        /// </summary>
        public CabinetSideParameters ACabinet { get; }

        /// <summary>
        /// B 柜参数。
        /// 包含柜高、柜深、柜宽、离地高度及前后开孔参数。
        /// </summary>
        public CabinetSideParameters BCabinet { get; }
    }

    /// <summary>
    /// 柜体整体参数。
    /// 对应界面顶部“整体参数”区域中的输入项。
    /// </summary>
    public class CabinetGeneralParameters : CabinetParameterNotificationObject
    {
        public const string AbcPhaseSequence = "ABC";
        public const string CbaPhaseSequence = "CBA";

        /// <summary>
        /// 绝缘子本体高度默认值（mm），与 L 排尺寸补偿基准一致。
        /// </summary>
        public const double DefaultInsulatorBodyHeight =
            InsulatorHeightRules.DefaultInsulatorBodyHeight;

        /// <summary>
        /// 柜体驱动界面的绝缘子与25 mm支架组合高度默认值。
        /// </summary>
        public const double DefaultInsulatorWithBracketHeight =
            InsulatorHeightRules.DefaultInsulatorWithBracketHeight;

        /// <summary>
        /// 铜排规格默认值，与界面下拉/输入初始值一致。
        /// </summary>
        public const string DefaultBusbarSpec = "80×8";

        /// <summary>
        /// 单相根数默认值，与界面下拉框初始选中项一致。
        /// </summary>
        public const int DefaultSinglePhaseRootCount = 4;

        private double _cabinetGeneralParametersFloorHeight = 4377;
        /// <summary>
        /// 楼高 D。
        /// 用于描述安装环境的总高度条件。
        /// </summary>
        public double FloorHeight
        {
            get => _cabinetGeneralParametersFloorHeight;
            set => SetParameterValue(ref _cabinetGeneralParametersFloorHeight, value, nameof(FloorHeight));
        }

        private double _cabinetGeneralParametersCabinetSpacing = 3500;
        /// <summary>
        /// 柜间距 A。
        /// 表示 A 柜与 B 柜之间的安装间距。
        /// </summary>
        public double CabinetSpacing
        {
            get => _cabinetGeneralParametersCabinetSpacing;
            set => SetParameterValue(ref _cabinetGeneralParametersCabinetSpacing, value, nameof(CabinetSpacing));
        }

        private double _cabinetGeneralParametersCabinetEccentricity = 0;
        /// <summary>
        /// 柜间偏心值。
        /// 用于描述两柜安装时的偏移量。
        /// </summary>
        public double CabinetEccentricity
        {
            get => _cabinetGeneralParametersCabinetEccentricity;
            set => SetParameterValue(ref _cabinetGeneralParametersCabinetEccentricity, value, nameof(CabinetEccentricity));
        }

        private double _cabinetGeneralParametersPhaseSpacing = 210;
        /// <summary>
        /// 相间距。
        /// 用于描述相邻相之间的距离。
        /// </summary>
        public double PhaseSpacing
        {
            get => _cabinetGeneralParametersPhaseSpacing;
            set => SetParameterValue(ref _cabinetGeneralParametersPhaseSpacing, value, nameof(PhaseSpacing));
        }

        /// <summary>中间铜排的净绝缘距离（mm）。</summary>
        private double _insulationDistance = InsulationDistanceMetadata.ConfiguredMillimetres;
        public double InsulationDistance
        {
            get => _insulationDistance;
            set
            {
                InsulationDistanceMetadata.Format(value);
                if (Math.Abs(_insulationDistance - value) < 0.000001d) return;
                _insulationDistance = value;
                RaisePropertyChanged(nameof(InsulationDistance));
            }
        }

        private double _cabinetGeneralParametersParameterB = 4000;
        /// <summary>
        /// 参数 B。
        /// 该参数描述整套柜体方案的整体参数，因此放在整体参数对象中统一维护。
        /// </summary>
        public double ParameterB
        {
            get => _cabinetGeneralParametersParameterB;
            set => SetParameterValue(ref _cabinetGeneralParametersParameterB, value, nameof(ParameterB));
        }

        /// <summary>
        /// 母排规格。
        /// 例如“80×8”，用于描述母排截面规格。
        /// </summary>
        private string _busbarSpec = DefaultBusbarSpec;
        private string _aCabinetExposedBusbarPhaseSequence = CbaPhaseSequence;
        private string _bCabinetExposedBusbarPhaseSequence = AbcPhaseSequence;

        public string BusbarSpec
        {
            get => _busbarSpec;
            set
            {
                if (_busbarSpec == value)
                {
                    return;
                }

                _busbarSpec = value;
                RaisePropertyChanged(nameof(BusbarSpec));
            }
        }

        private int _cabinetGeneralParametersSinglePhaseRootCount = DefaultSinglePhaseRootCount;
        /// <summary>
        /// 单相根数。
        /// 通过下拉框选择 1 到 4 的候选值。
        /// </summary>
        public int SinglePhaseRootCount
        {
            get => _cabinetGeneralParametersSinglePhaseRootCount;
            set => SetParameterValue(ref _cabinetGeneralParametersSinglePhaseRootCount, value, nameof(SinglePhaseRootCount));
        }

        /// <summary>
        /// A 柜柜内伸出排相序。
        /// </summary>
        public string ACabinetExposedBusbarPhaseSequence
        {
            get => _aCabinetExposedBusbarPhaseSequence;
            set
            {
                EnsureSupportedPhaseSequence(
                    value,
                    nameof(ACabinetExposedBusbarPhaseSequence));
                if (_aCabinetExposedBusbarPhaseSequence == value)
                {
                    return;
                }

                _aCabinetExposedBusbarPhaseSequence = value;
                RaisePropertyChanged(nameof(ACabinetExposedBusbarPhaseSequence));
            }
        }

        /// <summary>
        /// B 柜柜内伸出排相序。
        /// </summary>
        public string BCabinetExposedBusbarPhaseSequence
        {
            get => _bCabinetExposedBusbarPhaseSequence;
            set
            {
                EnsureSupportedPhaseSequence(
                    value,
                    nameof(BCabinetExposedBusbarPhaseSequence));
                if (_bCabinetExposedBusbarPhaseSequence == value)
                {
                    return;
                }

                _bCabinetExposedBusbarPhaseSequence = value;
                RaisePropertyChanged(nameof(BCabinetExposedBusbarPhaseSequence));
            }
        }

        private string _cabinetGeneralParametersBusbarMaterial = "直角铜母线";
        /// <summary>
        /// 铜排材质。
        /// 通过下拉框选择：直角铜母线、全圆边铜母线。
        /// </summary>
        public string BusbarMaterial
        {
            get => _cabinetGeneralParametersBusbarMaterial;
            set => SetParameterValue(ref _cabinetGeneralParametersBusbarMaterial, value, nameof(BusbarMaterial));
        }

        /// <summary>
        /// 绝缘子与其自带25 mm支架的组合高度。
        /// 与柜顶开孔参数中的绝缘子定位相互独立。
        /// </summary>
        private double _insulatorHeight = DefaultInsulatorWithBracketHeight;

        public double InsulatorHeight
        {
            get => _insulatorHeight;
            set
            {
                if (Math.Abs(_insulatorHeight - value) < double.Epsilon)
                {
                    return;
                }

                _insulatorHeight = value;
                RaisePropertyChanged(nameof(InsulatorHeight));
            }
        }

        private static void EnsureSupportedPhaseSequence(
            string value,
            string propertyName)
        {
            if (string.Equals(value, AbcPhaseSequence, StringComparison.Ordinal)
                || string.Equals(value, CbaPhaseSequence, StringComparison.Ordinal))
            {
                return;
            }

            throw new ArgumentOutOfRangeException(
                propertyName,
                value,
                "伸出排相序仅支持 ABC 或 CBA。");
        }
    }

    /// <summary>
    /// 单侧柜体参数。
    /// 既可用于 A 柜，也可用于 B 柜，包含柜体本体参数和对应开孔参数。
    /// </summary>
    public class CabinetSideParameters : CabinetParameterNotificationObject
    {
        private double _cabinetSideParametersCabinetHeight;
        /// <summary>
        /// 柜高。
        /// 该参数属于单柜属性，因此 A 柜与 B 柜分别独立维护。
        /// </summary>
        public double CabinetHeight
        {
            get => _cabinetSideParametersCabinetHeight;
            set => SetParameterValue(ref _cabinetSideParametersCabinetHeight, value, nameof(CabinetHeight));
        }

        private double _cabinetSideParametersCabinetDepth;
        /// <summary>
        /// 柜深。
        /// 表示当前侧柜体的前后方向深度尺寸。
        /// </summary>
        public double CabinetDepth
        {
            get => _cabinetSideParametersCabinetDepth;
            set => SetParameterValue(ref _cabinetSideParametersCabinetDepth, value, nameof(CabinetDepth));
        }

        private double _cabinetSideParametersCabinetWidth;
        /// <summary>
        /// 柜宽。
        /// 表示当前侧柜体的左右方向宽度尺寸。
        /// </summary>
        public double CabinetWidth
        {
            get => _cabinetSideParametersCabinetWidth;
            set => SetParameterValue(ref _cabinetSideParametersCabinetWidth, value, nameof(CabinetWidth));
        }

        private double _cabinetSideParametersGroundHeight;
        /// <summary>
        /// 离地高度 I。
        /// 表示当前侧柜体相关结构距地面的安装高度。
        /// </summary>
        public double GroundHeight
        {
            get => _cabinetSideParametersGroundHeight;
            set => SetParameterValue(ref _cabinetSideParametersGroundHeight, value, nameof(GroundHeight));
        }

        private CabinetHoleParameters _cabinetSideParametersFrontHole;
        /// <summary>
        /// 柜前开孔参数。
        /// 用于描述当前侧柜体前侧开孔的启用状态和详细尺寸。
        /// </summary>
        public CabinetHoleParameters FrontHole
        {
            get => _cabinetSideParametersFrontHole;
            set => SetParameterValue(ref _cabinetSideParametersFrontHole, value, nameof(FrontHole));
        }

        private CabinetHoleParameters _cabinetSideParametersBackHole;
        /// <summary>
        /// 柜后开孔参数。
        /// 用于描述当前侧柜体后侧开孔的启用状态和详细尺寸。
        /// </summary>
        public CabinetHoleParameters BackHole
        {
            get => _cabinetSideParametersBackHole;
            set => SetParameterValue(ref _cabinetSideParametersBackHole, value, nameof(BackHole));
        }
    }

    /// <summary>
    /// 柜体单个开孔区域的参数模型。
    /// 适用于柜前孔或柜后孔，包含开关状态及各尺寸参数。
    /// </summary>
    public class CabinetHoleParameters : CabinetParameterNotificationObject
    {
        /// <summary>
        /// 基础基座默认高度；启用延长基座时，延长基座高 = 总基座高 - 该值。
        /// </summary>
        public const double DefaultBasicBaseHeight = 1000;

        /// <summary>
        /// 面对面场景基座开孔高度默认值（mm）。
        /// </summary>
        public const double DefaultBaseOpeningHeight = 500;

        /// <summary>
        /// 独立普通基座全局变量名。
        /// </summary>
        public const string BaseOpeningHeightVariableName = "基座开孔高度";

        /// <summary>界面、校验和异常使用的名称；模型读写仍使用原变量名。</summary>
        public const string BaseOpeningHeightDisplayName = "基架高度";

        internal static string GetParameterDisplayName(string parameterName) =>
            parameterName?.Replace(BaseOpeningHeightVariableName, BaseOpeningHeightDisplayName);

        /// <summary>
        /// 总基座高超过该阈值时启用延长基座。
        /// </summary>
        public const double ExtensionBaseHeightThreshold = 1400;

        /// <summary>
        /// 柜前孔：面对墙场景使用的固定偏移（面对面场景改由 <see cref="FaceToFaceBusbarCenterDistanceHelper"/> 动态计算）。
        /// </summary>
        public const double FrontHoleBusbarCenterToBaseOffset = 279.50;

        /// <summary>
        /// 柜后孔：面对墙场景使用的固定偏移（面对面场景改由 <see cref="FaceToFaceBusbarCenterDistanceHelper"/> 动态计算）。
        /// </summary>
        public const double BackHoleBusbarCenterToBaseOffset = 263.5;

        private double _toCabinetEdgeDistance;
        private double _busbarCenterToCabinetEdgeDistance;
        private double _fixedHoleSpacing = 458.50;
        private double _baseOpeningHeight = DefaultBaseOpeningHeight;

        private double _cabinetHoleParametersBusbarCenterToBaseOffset;
        /// <summary>
        /// 铜排中心距基座前缘距离；面对面场景下由公式动态写入，面对墙场景仍可作为固定偏移使用。
        /// </summary>
        public double BusbarCenterToBaseOffset
        {
            get => _cabinetHoleParametersBusbarCenterToBaseOffset;
            set => SetParameterValue(ref _cabinetHoleParametersBusbarCenterToBaseOffset, value, nameof(BusbarCenterToBaseOffset));
        }

        /// <summary>
        /// 根据总基座高计算延长基座高度；未超过阈值时返回 0。
        /// </summary>
        /// <param name="baseHeight">总基座高度。</param>
        /// <returns>延长基座高度。</returns>
        public static double CalculateExtensionBaseHeight(double baseHeight)
        {
            return baseHeight > ExtensionBaseHeightThreshold
                ? baseHeight - DefaultBasicBaseHeight
                : 0;
        }

        private bool _cabinetHoleParametersIsEnabled;
        /// <summary>
        /// 是否启用当前开孔区域。
        /// 界面中的开关按钮绑定到该属性。
        /// </summary>
        public bool IsEnabled
        {
            get => _cabinetHoleParametersIsEnabled;
            set => SetParameterValue(ref _cabinetHoleParametersIsEnabled, value, nameof(IsEnabled));
        }

        private double _cabinetHoleParametersInsulatorPosition;
        /// <summary>
        /// 绝缘子定位。
        /// 对应当前开孔位置基座内绝缘子的高度位置。
        /// </summary>
        public double InsulatorPosition
        {
            get => _cabinetHoleParametersInsulatorPosition;
            set => SetParameterValue(ref _cabinetHoleParametersInsulatorPosition, value, nameof(InsulatorPosition));
        }

        private double _cabinetHoleParametersBaseHeight;
        /// <summary>
        /// 基座高度。
        /// 对应当前开孔位置基座的高度。
        /// 当该值超过界面阈值时，界面会显示延长基座输入区域。
        /// </summary>
        public double BaseHeight
        {
            get => _cabinetHoleParametersBaseHeight;
            set => SetParameterValue(ref _cabinetHoleParametersBaseHeight, value, nameof(BaseHeight));
        }

        /// <summary>
        /// 基座开孔高度。
        /// 面对面场景默认 500，写入独立普通基座全局变量「基座开孔高度」。
        /// A/B 柜同一开孔位置保持一致，修改任意一侧会同步到另一侧。
        /// </summary>
        public double BaseOpeningHeight
        {
            get => _baseOpeningHeight;
            set
            {
                if (Math.Abs(_baseOpeningHeight - value) < double.Epsilon)
                {
                    return;
                }

                _baseOpeningHeight = value;
                RaisePropertyChanged(nameof(BaseOpeningHeight));
            }
        }

        private double _cabinetHoleParametersExtensionBaseHeight;
        /// <summary>
        /// 延长基座高度。
        /// 当当前开孔位置的基座高度较高时，用于补充额外的延长基座尺寸。
        /// </summary>
        public double ExtensionBaseHeight
        {
            get => _cabinetHoleParametersExtensionBaseHeight;
            set => SetParameterValue(ref _cabinetHoleParametersExtensionBaseHeight, value, nameof(ExtensionBaseHeight));
        }

        /// <summary>
        /// 距柜边距离。
        /// 对于前孔表示距柜前距离，对于后孔表示距柜后距离。
        /// </summary>
        public double ToCabinetEdgeDistance
        {
            get => _toCabinetEdgeDistance;
            set
            {
                if (Math.Abs(_toCabinetEdgeDistance - value) < double.Epsilon)
                {
                    return;
                }

                _toCabinetEdgeDistance = value;
                RaisePropertyChanged(nameof(ToCabinetEdgeDistance));
            }
        }

        /// <summary>
        /// 铜排中心至柜边距离。
        /// 对于前孔表示铜排中心至柜前距离，对于后孔表示铜排中心至柜后距离。
        /// 独立输入，不随距柜边距离、接口固定孔间距、绝缘子高度或铜排规格变化。
        /// </summary>
        public double BusbarCenterToCabinetEdgeDistance
        {
            get => _busbarCenterToCabinetEdgeDistance;
            set
            {
                if (Math.Abs(_busbarCenterToCabinetEdgeDistance - value) < double.Epsilon)
                {
                    return;
                }

                _busbarCenterToCabinetEdgeDistance = value;
                RaisePropertyChanged(nameof(BusbarCenterToCabinetEdgeDistance));
            }
        }

        /// <summary>
        /// 接口固定孔间距。
        /// 用于定义接口安装孔之间的距离，并参与基座深推导。
        /// </summary>
        public double FixedHoleSpacing
        {
            get => _fixedHoleSpacing;
            set
            {
                if (Math.Abs(_fixedHoleSpacing - value) < double.Epsilon)
                {
                    return;
                }

                _fixedHoleSpacing = value;
                RaisePropertyChanged(nameof(FixedHoleSpacing));
            }
        }

        private double _cabinetHoleParametersL1;
        /// <summary>
        /// 开孔参数 L1。
        /// </summary>
        public double L1
        {
            get => _cabinetHoleParametersL1;
            set => SetParameterValue(ref _cabinetHoleParametersL1, value, nameof(L1));
        }

        private double _cabinetHoleParametersL2;
        /// <summary>
        /// 开孔参数 L2。
        /// </summary>
        public double L2
        {
            get => _cabinetHoleParametersL2;
            set => SetParameterValue(ref _cabinetHoleParametersL2, value, nameof(L2));
        }

        private double _cabinetHoleParametersL3;
        /// <summary>
        /// 开孔参数 L3。
        /// </summary>
        public double L3
        {
            get => _cabinetHoleParametersL3;
            set => SetParameterValue(ref _cabinetHoleParametersL3, value, nameof(L3));
        }
    }
}
