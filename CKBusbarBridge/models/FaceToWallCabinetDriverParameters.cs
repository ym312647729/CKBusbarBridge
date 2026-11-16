using System;
using CKBusbarBridge;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 面对墙场景柜体驱动窗口使用的根参数模型。
    /// </summary>
    public class FaceToWallCabinetDriverParameters : CabinetParameterNotificationObject
    {
        /// <summary>
        /// 初始化面对墙场景柜体驱动参数，并为各层级子参数对象设置默认实例和默认值。
        /// </summary>
        public FaceToWallCabinetDriverParameters()
        {
            General = new FaceToWallGeneralParameters();

            const double defaultCabinetHeight = 2240;

            Cabinet = new FaceToWallCabinetSideParameters
            {
                CabinetHeight = defaultCabinetHeight,
                CabinetDepth = 1550,
                CabinetWidth = 1000,
                FrontHole = new FaceToWallCabinetHoleParameters
                {
                    IsEnabled = true,
                    BusbarCenterToBaseOffset = FaceToWallCabinetHoleParameters.FrontHoleBusbarCenterToBaseOffset,
                    BaseToCabinetEdgeDistance = 740,
                    BusbarCenterToCabinetEdgeDistance = 936,
                    FixedHoleSpacing = 475,
                    L1 = 100,
                    L2 = 250,
                    L3 = 350,
                    InsulatorPosition = FaceToWallCabinetHoleParameters.DefaultInsulatorPosition
                },
                BackHole = new FaceToWallCabinetHoleParameters
                {
                    IsEnabled = false,
                    BusbarCenterToBaseOffset = FaceToWallCabinetHoleParameters.BackHoleBusbarCenterToBaseOffset,
                    BaseToCabinetEdgeDistance = 25,
                    BusbarCenterToCabinetEdgeDistance = 210,
                    FixedHoleSpacing = 440,
                    L1 = 100,
                    L2 = 250,
                    L3 = 350,
                    InsulatorPosition = FaceToWallCabinetHoleParameters.DefaultInsulatorPosition
                }
            };

            // 面对墙场景基座高度初始值通过计算得到：
            // 基座顶部默认对齐墙端目标顶部；四边距为零时保持原公式。
            RecalculateBaseHeights();
            SyncBaseOpeningHeights();

            // 关联参数改变时，仅重算尚未手动覆盖的高度；横向和轴向安装量不改基座高度。
            General.PropertyChanged += OnRelatedParameterChanged;
            General.WallInstallation.PropertyChanged += OnRelatedParameterChanged;
            Cabinet.PropertyChanged += OnRelatedParameterChanged;
        }

        /// <summary>
        /// 当影响基座高度的关联参数发生变化时，重新计算基座高度。
        /// </summary>
        private void OnRelatedParameterChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            var installationTopChanged = sender == General.WallInstallation && e.PropertyName == nameof(FaceToWallInstallationParameters.Top);
            var installationBottomChanged = sender == General.WallInstallation && e.PropertyName == nameof(FaceToWallInstallationParameters.Bottom);
            if (installationTopChanged || e.PropertyName == nameof(FaceToWallGeneralParameters.WallHoleEdgeGroundHeight)
                || e.PropertyName == nameof(FaceToWallGeneralParameters.WallHoleHeight)
                || e.PropertyName == nameof(FaceToWallGeneralParameters.CabinetGroundHeight)
                || e.PropertyName == nameof(FaceToWallCabinetSideParameters.CabinetHeight))
            {
                RecalculateBaseHeights();
            }

            if (installationTopChanged || installationBottomChanged || e.PropertyName == nameof(FaceToWallGeneralParameters.WallHoleHeight))
            {
                SyncBaseOpeningHeights();
            }
        }

        /// <summary>
        /// 按公式重算柜前孔/柜后孔基座高度，并同步刷新延长基座高度。
        /// 基座高度 = 墙孔边距地面 + 墙孔高度 - 上间距 - 柜高 - 离地高度。
        /// </summary>
        private void RecalculateBaseHeights()
        {
            if (General == null || Cabinet == null)
            {
                return;
            }

            var baseHeight = CalculateBaseHeight(General, Cabinet.CabinetHeight);
            if (double.IsNaN(baseHeight) || double.IsInfinity(baseHeight)) return;
            ApplyBaseHeight(Cabinet.FrontHole, baseHeight);
            ApplyBaseHeight(Cabinet.BackHole, baseHeight);
        }

        /// <summary>
        /// 将重算得到的基座高度写入指定开孔参数，并同步更新延长基座高度。
        /// </summary>
        /// <param name="holeParameters">柜顶开孔参数。</param>
        /// <param name="baseHeight">基座高度。</param>
        private static void ApplyBaseHeight(FaceToWallCabinetHoleParameters holeParameters, double baseHeight)
        {
            if (holeParameters == null)
            {
                return;
            }

            if (holeParameters.ApplyFollowedBaseHeight(baseHeight))
                holeParameters.ExtensionBaseHeight = FaceToWallCabinetHoleParameters.CalculateExtensionBaseHeight(baseHeight);
        }

        /// <summary>
        /// 将墙端目标驱动高度同步到尚未被用户手动修改的基架高度。
        /// </summary>
        private void SyncBaseOpeningHeights()
        {
            if (General == null || Cabinet == null)
            {
                return;
            }

            var height = General.WallHoleHeight - General.WallInstallation.Top - General.WallInstallation.Bottom;
            if (height <= 0 || double.IsNaN(height) || double.IsInfinity(height)) return;
            Cabinet.FrontHole?.ApplyFollowedBaseOpeningHeight(height);
            Cabinet.BackHole?.ApplyFollowedBaseOpeningHeight(height);
        }

        public void SynchronizeBaseParameters()
        {
            General.WallInstallation.Capture().Validate(General.WallHoleWidth, General.WallHoleHeight);
            var baseHeight = CalculateBaseHeight(General, Cabinet.CabinetHeight);
            if (double.IsNaN(baseHeight) || double.IsInfinity(baseHeight) || baseHeight <= 0)
                throw new InvalidOperationException("墙端目标顶标高不能形成有效的正基座高度，同步未提交。");
            if (Cabinet.FrontHole.IsEnabled) Cabinet.FrontHole.RestoreManualState(false, false);
            if (Cabinet.BackHole.IsEnabled) Cabinet.BackHole.RestoreManualState(false, false);
            RecalculateBaseHeights();
            SyncBaseOpeningHeights();
        }

        /// <summary>
        /// 计算面对墙场景基座高度：
        /// 基座高度 = 墙孔边距地面 + 墙孔高度 - 上间距 - 柜高 - 离地高度。
        /// </summary>
        /// <param name="general">面对墙场景整体参数。</param>
        /// <param name="cabinetHeight">柜高。</param>
        /// <returns>基座高度。</returns>
        private static double CalculateBaseHeight(FaceToWallGeneralParameters general, double cabinetHeight)
        {
            if (general == null)
            {
                return 0;
            }

            return general.WallHoleEdgeGroundHeight
                + general.WallHoleHeight
                - general.WallInstallation.Top
                - cabinetHeight
                - general.CabinetGroundHeight;
        }

        private string _faceToWallCabinetDriverParametersProjectPath;
        /// <summary>
        /// 当前项目路径。
        /// </summary>
        public string ProjectPath
        {
            get => _faceToWallCabinetDriverParametersProjectPath;
            set => SetParameterValue(ref _faceToWallCabinetDriverParametersProjectPath, value, nameof(ProjectPath));
        }

        /// <summary>
        /// 面对墙场景整体参数。
        /// </summary>
        public FaceToWallGeneralParameters General { get; }

        /// <summary>
        /// 柜体参数（面对墙场景仅一个柜体）。
        /// </summary>
        public FaceToWallCabinetSideParameters Cabinet { get; }
    }

    /// <summary>
    /// 面对墙场景单侧柜体参数。
    /// </summary>
    public class FaceToWallCabinetSideParameters : CabinetParameterNotificationObject
    {
        private double _cabinetHeight;

        /// <summary>
        /// 柜高。
        /// 该值参与基座高度联动计算。
        /// </summary>
        public double CabinetHeight
        {
            get => _cabinetHeight;
            set
            {
                if (Math.Abs(_cabinetHeight - value) < double.Epsilon)
                {
                    return;
                }

                _cabinetHeight = value;
                RaisePropertyChanged(nameof(CabinetHeight));
            }
        }

        private double _faceToWallCabinetSideParametersCabinetDepth;
        /// <summary>
        /// 柜深。
        /// </summary>
        public double CabinetDepth
        {
            get => _faceToWallCabinetSideParametersCabinetDepth;
            set => SetParameterValue(ref _faceToWallCabinetSideParametersCabinetDepth, value, nameof(CabinetDepth));
        }

        private double _faceToWallCabinetSideParametersCabinetWidth;
        /// <summary>
        /// 柜宽。
        /// </summary>
        public double CabinetWidth
        {
            get => _faceToWallCabinetSideParametersCabinetWidth;
            set => SetParameterValue(ref _faceToWallCabinetSideParametersCabinetWidth, value, nameof(CabinetWidth));
        }

        private FaceToWallCabinetHoleParameters _faceToWallCabinetSideParametersFrontHole;
        /// <summary>
        /// 柜前开孔参数。
        /// </summary>
        public FaceToWallCabinetHoleParameters FrontHole
        {
            get => _faceToWallCabinetSideParametersFrontHole;
            set => SetParameterValue(ref _faceToWallCabinetSideParametersFrontHole, value, nameof(FrontHole));
        }

        private FaceToWallCabinetHoleParameters _faceToWallCabinetSideParametersBackHole;
        /// <summary>
        /// 柜后开孔参数。
        /// </summary>
        public FaceToWallCabinetHoleParameters BackHole
        {
            get => _faceToWallCabinetSideParametersBackHole;
            set => SetParameterValue(ref _faceToWallCabinetSideParametersBackHole, value, nameof(BackHole));
        }
    }

    /// <summary>
    /// 面对墙场景柜顶单个开孔区域参数（柜前孔或柜后孔）。
    /// </summary>
    public class FaceToWallCabinetHoleParameters : CabinetParameterNotificationObject
    {
        /// <summary>
        /// 基础基座默认高度；启用延长基座时，延长基座高 = 总基座高 - 该值。
        /// </summary>
        public const double DefaultBasicBaseHeight = 1000;

        /// <summary>
        /// 总基座高超过该阈值时启用延长基座。
        /// </summary>
        public const double ExtensionBaseHeightThreshold = 1400;

        /// <summary>
        /// 柜前孔：初始化默认铜排中心距离时使用的基座偏移。
        /// </summary>
        public const double FrontHoleBusbarCenterToBaseOffset = 279.50;

        /// <summary>
        /// 柜后孔：初始化默认铜排中心距离时使用的基座偏移。
        /// </summary>
        public const double BackHoleBusbarCenterToBaseOffset = 263.5;

        /// <summary>
        /// 面对墙基座绝缘子定位默认值（mm）。
        /// </summary>
        public const double DefaultInsulatorPosition = 600;

        private double _baseToCabinetEdgeDistance;
        private double _busbarCenterToCabinetEdgeDistance;
        private double _baseHeight;
        private double _extensionBaseHeight;
        private double _baseOpeningHeight;
        private bool _baseOpeningHeightManuallyModified;
        private bool _baseHeightManuallyModified;
        public bool IsBaseHeightManuallyModified => _baseHeightManuallyModified;
        public bool IsBaseOpeningHeightManuallyModified => _baseOpeningHeightManuallyModified;

        internal void RestoreManualState(bool baseHeightModified, bool openingHeightModified)
        {
            _baseHeightManuallyModified = baseHeightModified;
            _baseOpeningHeightManuallyModified = openingHeightModified;
        }

        internal bool ApplyFollowedBaseHeight(double value)
        {
            if (_baseHeightManuallyModified) return false;
            if (Math.Abs(_baseHeight - value) >= 1e-9)
            {
                _baseHeight = value;
                RaisePropertyChanged(nameof(BaseHeight));
            }
            return true;
        }

        private double _faceToWallCabinetHoleParametersBusbarCenterToBaseOffset;
        /// <summary>
        /// 默认铜排中心距基座的偏移；柜前孔与柜后孔取值不同，不参与参数编辑联动。
        /// </summary>
        public double BusbarCenterToBaseOffset
        {
            get => _faceToWallCabinetHoleParametersBusbarCenterToBaseOffset;
            set => SetParameterValue(ref _faceToWallCabinetHoleParametersBusbarCenterToBaseOffset, value, nameof(BusbarCenterToBaseOffset));
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

        private bool _faceToWallCabinetHoleParametersIsEnabled;
        /// <summary>
        /// 是否启用当前开孔区域。
        /// </summary>
        public bool IsEnabled
        {
            get => _faceToWallCabinetHoleParametersIsEnabled;
            set => SetParameterValue(ref _faceToWallCabinetHoleParametersIsEnabled, value, nameof(IsEnabled));
        }

        /// <summary>
        /// 基座距柜前/后距离。
        /// </summary>
        public double BaseToCabinetEdgeDistance
        {
            get => _baseToCabinetEdgeDistance;
            set
            {
                if (Math.Abs(_baseToCabinetEdgeDistance - value) < double.Epsilon)
                {
                    return;
                }

                _baseToCabinetEdgeDistance = value;
                RaisePropertyChanged(nameof(BaseToCabinetEdgeDistance));
            }
        }

        /// <summary>
        /// 铜排中心至柜前/后距离；独立输入，不随基座距柜边距离或其他参数变化。
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

        private double _faceToWallCabinetHoleParametersFixedHoleSpacing;
        /// <summary>
        /// 接口固定孔间距。
        /// </summary>
        public double FixedHoleSpacing
        {
            get => _faceToWallCabinetHoleParametersFixedHoleSpacing;
            set => SetParameterValue(ref _faceToWallCabinetHoleParametersFixedHoleSpacing, value, nameof(FixedHoleSpacing));
        }

        private double _faceToWallCabinetHoleParametersL1;
        /// <summary>
        /// 开孔参数 L1。
        /// </summary>
        public double L1
        {
            get => _faceToWallCabinetHoleParametersL1;
            set => SetParameterValue(ref _faceToWallCabinetHoleParametersL1, value, nameof(L1));
        }

        private double _faceToWallCabinetHoleParametersL2;
        /// <summary>
        /// 开孔参数 L2。
        /// </summary>
        public double L2
        {
            get => _faceToWallCabinetHoleParametersL2;
            set => SetParameterValue(ref _faceToWallCabinetHoleParametersL2, value, nameof(L2));
        }

        private double _faceToWallCabinetHoleParametersL3;
        /// <summary>
        /// 开孔参数 L3。
        /// </summary>
        public double L3
        {
            get => _faceToWallCabinetHoleParametersL3;
            set => SetParameterValue(ref _faceToWallCabinetHoleParametersL3, value, nameof(L3));
        }

        private double _faceToWallCabinetHoleParametersInsulatorPosition = DefaultInsulatorPosition;
        /// <summary>
        /// 绝缘子定位。
        /// 对应当前开孔位置基座内绝缘子的高度位置。
        /// </summary>
        public double InsulatorPosition
        {
            get => _faceToWallCabinetHoleParametersInsulatorPosition;
            set => SetParameterValue(ref _faceToWallCabinetHoleParametersInsulatorPosition, value, nameof(InsulatorPosition));
        }

        /// <summary>
        /// 基座开孔高度。
        /// 默认跟随墙孔高度；用户在界面上手动修改后不再随动。
        /// </summary>
        public double BaseOpeningHeight
        {
            get => _baseOpeningHeight;
            set
            {
                _baseOpeningHeightManuallyModified = true;
                SetBaseOpeningHeightCore(value);
            }
        }

        /// <summary>
        /// 墙孔高度变化时写入基座开孔高度；用户已手动改过则保持原值。
        /// </summary>
        /// <param name="wallHoleHeight">当前墙孔高度。</param>
        internal void ApplyFollowedBaseOpeningHeight(double wallHoleHeight)
        {
            if (_baseOpeningHeightManuallyModified)
            {
                return;
            }

            SetBaseOpeningHeightCore(wallHoleHeight);
        }

        /// <summary>
        /// 从独立基座反读开孔高度。与墙孔高度不一致时视为用户曾改过，后续不再随动。
        /// </summary>
        /// <param name="value">反读到的基座开孔高度。</param>
        /// <param name="wallHoleHeight">当前墙孔高度。</param>
        internal void ApplyReadBaseOpeningHeight(double value, double wallHoleHeight)
        {
            SetBaseOpeningHeightCore(value);
            _baseOpeningHeightManuallyModified =
                Math.Abs(value - wallHoleHeight) >= double.Epsilon;
        }

        private void SetBaseOpeningHeightCore(double value)
        {
            if (Math.Abs(_baseOpeningHeight - value) < double.Epsilon)
            {
                return;
            }

            _baseOpeningHeight = value;
            RaisePropertyChanged(nameof(BaseOpeningHeight));
        }

        /// <summary>
        /// 基座高度。
        /// 当该值超过界面阈值时，界面会显示延长基座输入区域。
        /// </summary>
        public double BaseHeight
        {
            get => _baseHeight;
            set
            {
                _baseHeightManuallyModified = true;
                if (Math.Abs(_baseHeight - value) < double.Epsilon)
                {
                    return;
                }

                _baseHeight = value;
                RaisePropertyChanged(nameof(BaseHeight));
            }
        }

        /// <summary>
        /// 延长基座高度。
        /// 当基座高度较高时，用于补充额外的延长基座尺寸。
        /// </summary>
        public double ExtensionBaseHeight
        {
            get => _extensionBaseHeight;
            set
            {
                if (Math.Abs(_extensionBaseHeight - value) < double.Epsilon)
                {
                    return;
                }

                _extensionBaseHeight = value;
                RaisePropertyChanged(nameof(ExtensionBaseHeight));
            }
        }
    }

    /// <summary>
    /// 面对墙场景整体参数。
    /// 对应界面顶部“整体参数”区域中的输入项。
    /// </summary>
    public class FaceToWallGeneralParameters : CabinetParameterNotificationObject
    {
        public FaceToWallInstallationParameters WallInstallation { get; } = new FaceToWallInstallationParameters();
        /// <summary>
        /// 绝缘子本体高度默认值（mm），与 L 排尺寸补偿基准一致。
        /// </summary>
        public const double DefaultInsulatorBodyHeight =
            InsulatorHeightRules.DefaultInsulatorBodyHeight;

        public const double DefaultInsulatorWithBracketHeight =
            InsulatorHeightRules.DefaultInsulatorWithBracketHeight;

        private const double DefaultWallHoleWidth = 1960;

        private double _wallHoleEdgeGroundHeight = 2990;
        private double _wallHoleHeight = 830;
        private double _cabinetGroundHeight = 80;
        private string _busbarSpec = CabinetGeneralParameters.DefaultBusbarSpec;
        private string _faceToWallGeneralParametersBusbarMaterial = "直角铜母线";
        public string BusbarMaterial
        {
            get => _faceToWallGeneralParametersBusbarMaterial;
            set => SetParameterValue(ref _faceToWallGeneralParametersBusbarMaterial, value, nameof(BusbarMaterial));
        }
        private bool _isPhaseSwitched;
        public bool IsPhaseSwitched
        {
            get => _isPhaseSwitched;
            set
            {
                if (_isPhaseSwitched == value) return;
                _isPhaseSwitched = value;
                RaisePropertyChanged(nameof(IsPhaseSwitched));
            }
        }
        private double _wallHoleWidth = DefaultWallHoleWidth;

        private double _faceToWallGeneralParametersCabinetToWallDistance = 1340;
        /// <summary>
        /// 柜体距墙的距离（A），具体含义由柜体朝向决定。
        /// </summary>
        public double CabinetToWallDistance
        {
            get => _faceToWallGeneralParametersCabinetToWallDistance;
            set => SetParameterValue(ref _faceToWallGeneralParametersCabinetToWallDistance, value, nameof(CabinetToWallDistance));
        }

        /// <summary>
        /// 母线桥设计尚未迁移到通用距墙参数前的编译边界；柜体驱动不再使用该名称。
        /// </summary>
        public double CabinetBackToWallDistance
        {
            get => CabinetToWallDistance;
            set => CabinetToWallDistance = value;
        }

        private double _faceToWallGeneralParametersFloorToBeamDistance = 4000;
        /// <summary>
        /// 地面距横梁距离（B）。
        /// </summary>
        public double FloorToBeamDistance
        {
            get => _faceToWallGeneralParametersFloorToBeamDistance;
            set => SetParameterValue(ref _faceToWallGeneralParametersFloorToBeamDistance, value, nameof(FloorToBeamDistance));
        }

        /// <summary>
        /// 墙孔边距地面高度（C）。
        /// 该值参与基座高度联动计算。
        /// </summary>
        public double WallHoleEdgeGroundHeight
        {
            get => _wallHoleEdgeGroundHeight;
            set
            {
                if (Math.Abs(_wallHoleEdgeGroundHeight - value) < double.Epsilon)
                {
                    return;
                }

                _wallHoleEdgeGroundHeight = value;
                RaisePropertyChanged(nameof(WallHoleEdgeGroundHeight));
            }
        }

        private double _faceToWallGeneralParametersWallEntryBusbarCenterToHoleBottomDistance = 400;
        /// <summary>
        /// 入墙排中心距墙孔底距离（D）。
        /// </summary>
        public double WallEntryBusbarCenterToHoleBottomDistance
        {
            get => _faceToWallGeneralParametersWallEntryBusbarCenterToHoleBottomDistance;
            set => SetParameterValue(ref _faceToWallGeneralParametersWallEntryBusbarCenterToHoleBottomDistance, value, nameof(WallEntryBusbarCenterToHoleBottomDistance));
        }

        /// <summary>
        /// 墙孔高度（E）。
        /// </summary>
        public double WallHoleHeight
        {
            get => _wallHoleHeight;
            set
            {
                if (Math.Abs(_wallHoleHeight - value) < double.Epsilon)
                {
                    return;
                }

                _wallHoleHeight = value;
                RaisePropertyChanged(nameof(WallHoleHeight));
            }
        }

        private double _faceToWallGeneralParametersExposedBusbarPhaseSpacing = 210;
        /// <summary>
        /// 伸出排相间距。
        /// </summary>
        public double ExposedBusbarPhaseSpacing
        {
            get => _faceToWallGeneralParametersExposedBusbarPhaseSpacing;
            set => SetParameterValue(ref _faceToWallGeneralParametersExposedBusbarPhaseSpacing, value, nameof(ExposedBusbarPhaseSpacing));
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

        /// <summary>
        /// 铜排规格（默认值与面对面场景一致）。
        /// </summary>
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

                if (FaceToFaceBusbarCenterDistanceHelper.TryParseBusbarThickness(
                    value,
                    out var busbarThickness))
                {
                    WallBushingTerminal?.ApplyBusbarThickness(busbarThickness);
                }
            }
        }

        private int _faceToWallGeneralParametersSinglePhaseRootCount = CabinetGeneralParameters.DefaultSinglePhaseRootCount;
        /// <summary>
        /// 单相根数（默认值与面对面场景一致，界面以下拉框选择 1~4）。
        /// </summary>
        public int SinglePhaseRootCount
        {
            get => _faceToWallGeneralParametersSinglePhaseRootCount;
            set => SetParameterValue(ref _faceToWallGeneralParametersSinglePhaseRootCount, value, nameof(SinglePhaseRootCount));
        }

        private double _faceToWallGeneralParametersF1 = 300;
        /// <summary>
        /// 穿墙套管到左侧墙洞的距离（F1）。
        /// </summary>
        public double F1
        {
            get => _faceToWallGeneralParametersF1;
            set => SetParameterValue(ref _faceToWallGeneralParametersF1, value, nameof(F1));
        }

        private double _faceToWallGeneralParametersF2 = 600;
        /// <summary>
        /// 第一、第二套管中心距（F2）。
        /// </summary>
        public double F2
        {
            get => _faceToWallGeneralParametersF2;
            set => SetParameterValue(ref _faceToWallGeneralParametersF2, value, nameof(F2));
        }

        private double _faceToWallGeneralParametersF3 = 600;
        /// <summary>
        /// 第二、第三套管中心距（F3）。
        /// </summary>
        public double F3
        {
            get => _faceToWallGeneralParametersF3;
            set => SetParameterValue(ref _faceToWallGeneralParametersF3, value, nameof(F3));
        }

        private double _faceToWallGeneralParametersJ = 100;
        /// <summary>
        /// 穿墙套管安装板距内侧墙面距离（J）。
        /// </summary>
        public double J
        {
            get => _faceToWallGeneralParametersJ;
            set => SetParameterValue(ref _faceToWallGeneralParametersJ, value, nameof(J));
        }

        /// <summary>
        /// 母线桥设计迁移到独立 F2/F3 参数前的编译边界；柜体驱动不再使用旧 F。
        /// </summary>
        public double WallBushingPhaseSpacing
        {
            get => F2;
            set => F2 = value;
        }

        private double _faceToWallGeneralParametersWallBushingTerminalToWallDistance = 500;
        /// <summary>
        /// 穿墙套管桩头距墙面距离（H）。
        /// </summary>
        public double WallBushingTerminalToWallDistance
        {
            get => _faceToWallGeneralParametersWallBushingTerminalToWallDistance;
            set => SetParameterValue(ref _faceToWallGeneralParametersWallBushingTerminalToWallDistance, value, nameof(WallBushingTerminalToWallDistance));
        }

        private FaceToWallBushingTerminalParameters _faceToWallGeneralParametersWallBushingTerminal = new FaceToWallBushingTerminalParameters();
        /// <summary>
        /// 穿墙套管桩头参数（C1～C10）。
        /// </summary>
        public FaceToWallBushingTerminalParameters WallBushingTerminal
        {
            get => _faceToWallGeneralParametersWallBushingTerminal;
            set => SetParameterValue(ref _faceToWallGeneralParametersWallBushingTerminal, value, nameof(WallBushingTerminal));
        }

        /// <summary>
        /// 墙孔宽度（G）。
        /// </summary>
        public double WallHoleWidth
        {
            get => _wallHoleWidth;
            set
            {
                if (Math.Abs(_wallHoleWidth - value) < double.Epsilon)
                {
                    return;
                }

                _wallHoleWidth = value;
                RaisePropertyChanged(nameof(WallHoleWidth));
            }
        }

        /// <summary>
        /// 柜体离地高度（I）。
        /// </summary>
        public double CabinetGroundHeight
        {
            get => _cabinetGroundHeight;
            set
            {
                if (Math.Abs(_cabinetGroundHeight - value) < double.Epsilon)
                {
                    return;
                }

                _cabinetGroundHeight = value;
                RaisePropertyChanged(nameof(CabinetGroundHeight));
            }
        }

        private double _faceToWallGeneralParametersBuildingHeight = 4340;
        /// <summary>
        /// 楼高（L）。
        /// </summary>
        public double BuildingHeight
        {
            get => _faceToWallGeneralParametersBuildingHeight;
            set => SetParameterValue(ref _faceToWallGeneralParametersBuildingHeight, value, nameof(BuildingHeight));
        }

        private double _faceToWallGeneralParametersWallCabinetEccentricity = 20;
        /// <summary>
        /// 墙柜偏心值。
        /// </summary>
        public double WallCabinetEccentricity
        {
            get => _faceToWallGeneralParametersWallCabinetEccentricity;
            set => SetParameterValue(ref _faceToWallGeneralParametersWallCabinetEccentricity, value, nameof(WallCabinetEccentricity));
        }

        private double _faceToWallGeneralParametersInsulatorHeight =
            DefaultInsulatorWithBracketHeight;
        /// <summary>
        /// 绝缘子与其自带25 mm支架的组合高度。
        /// </summary>
        public double InsulatorHeight
        {
            get => _faceToWallGeneralParametersInsulatorHeight;
            set => SetParameterValue(ref _faceToWallGeneralParametersInsulatorHeight, value, nameof(InsulatorHeight));
        }
    }
}
