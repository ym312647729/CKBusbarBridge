using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 侧对侧柜体选配界面的根参数模型。
    /// 整棵业务参数类型均为侧对侧专属类型，不复用面对面场景的参数规则。
    /// </summary>
    public sealed class SideToSideCabinetDriverParameters : CabinetParameterNotificationObject
    {
        private const double DefaultBusbarCenterToBaseOffset = 200.5;
        private const float DefaultFrontBusbarCenterToCabinetEdgeDistance = 936f;
        private const float DefaultBackBusbarCenterToCabinetEdgeDistance = 210f;

        public SideToSideCabinetDriverParameters()
        {
            General = new SideToSideGeneralParameters();
            ACabinet = CreateDefaultCabinet();
            BCabinet = CreateDefaultCabinet();
            // 侧对侧基架要求 A/B 同位置、同基座类型共用开孔高度。
            // 分成四条独立链，避免柜前/柜后或基座1/基座2之间相互覆盖。
            LinkBase1OpeningHeight(
                ACabinet.FrontHole.Base1,
                BCabinet.FrontHole.Base1);
            LinkBase1OpeningHeight(
                ACabinet.BackHole.Base1,
                BCabinet.BackHole.Base1);
            LinkBase2OpeningHeight(
                ACabinet.FrontHole.Base2,
                BCabinet.FrontHole.Base2);
            LinkBase2OpeningHeight(
                ACabinet.BackHole.Base2,
                BCabinet.BackHole.Base2);
            ApplyBaseKind();
            RefreshBase2WidthConstraints();
            General.PropertyChanged += (sender, args) =>
            {
                if (args?.PropertyName == nameof(SideToSideGeneralParameters.BaseKind))
                {
                    ApplyBaseKind();
                }

                if (args?.PropertyName == nameof(SideToSideGeneralParameters.BusbarSpec)
                    || args?.PropertyName == nameof(SideToSideGeneralParameters.ExposedBusbarPhaseSpacing)
                    || args?.PropertyName == nameof(SideToSideGeneralParameters.InsulationDistance))
                {
                    RefreshBase2WidthConstraints();
                }
            };
        }

        private string _sideToSideCabinetDriverParametersProjectPath;
        public string ProjectPath
        {
            get => _sideToSideCabinetDriverParametersProjectPath;
            set => SetParameterValue(ref _sideToSideCabinetDriverParametersProjectPath, value, nameof(ProjectPath));
        }

        public SideToSideGeneralParameters General { get; }

        public SideToSideCabinetParameters ACabinet { get; }

        public SideToSideCabinetParameters BCabinet { get; }

        private static void LinkBase1OpeningHeight(
            SideToSideBase1Parameters first,
            SideToSideBase1Parameters second)
        {
            first.PropertyChanged += (sender, args) =>
            {
                if (args?.PropertyName == nameof(SideToSideBase1Parameters.BaseOpeningHeight))
                {
                    second.BaseOpeningHeight = first.BaseOpeningHeight;
                }
            };
            second.PropertyChanged += (sender, args) =>
            {
                if (args?.PropertyName == nameof(SideToSideBase1Parameters.BaseOpeningHeight))
                {
                    first.BaseOpeningHeight = second.BaseOpeningHeight;
                }
            };
        }

        private static void LinkBase2OpeningHeight(
            SideToSideBase2Parameters first,
            SideToSideBase2Parameters second)
        {
            first.PropertyChanged += (sender, args) =>
            {
                if (args?.PropertyName == nameof(SideToSideBase2Parameters.BaseOpeningHeight))
                {
                    second.BaseOpeningHeight = first.BaseOpeningHeight;
                }
            };
            second.PropertyChanged += (sender, args) =>
            {
                if (args?.PropertyName == nameof(SideToSideBase2Parameters.BaseOpeningHeight))
                {
                    first.BaseOpeningHeight = second.BaseOpeningHeight;
                }
            };
        }

        private static SideToSideCabinetParameters CreateDefaultCabinet()
        {
            return new SideToSideCabinetParameters
            {
                CabinetHeight = 2240,
                CabinetDepth = 1500,
                CabinetWidth = 1000,
                GroundHeight = 80,
                FrontHole = CreateDefaultHole(isFrontHole: true),
                BackHole = CreateDefaultHole(isFrontHole: false)
            };
        }

        private static SideToSideCabinetHoleParameters CreateDefaultHole(bool isFrontHole)
        {
            var hole = new SideToSideCabinetHoleParameters
            {
                IsEnabled = isFrontHole,
                // 所有侧对侧子场景与面对面、面对墙采用相同的柜顶孔默认输入。
                ToCabinetEdgeDistance = isFrontHole ? 740f : 25f,
                FixedHoleSpacing = isFrontHole ? 475f : 440f,
                BusbarCenterToBaseOffset = DefaultBusbarCenterToBaseOffset,
                L1 = 100,
                L2 = 250,
                L3 = 350f
            };
            hole.BusbarCenterToCabinetEdgeDistance = isFrontHole
                ? DefaultFrontBusbarCenterToCabinetEdgeDistance
                : DefaultBackBusbarCenterToCabinetEdgeDistance;

            return hole;
        }

        private void ApplyBaseKind()
        {
            var kind = General.BaseKind;
            ACabinet.FrontHole.SetBaseKind(kind);
            ACabinet.BackHole.SetBaseKind(kind);
            BCabinet.FrontHole.SetBaseKind(kind);
            BCabinet.BackHole.SetBaseKind(kind);
        }

        private void RefreshBase2WidthConstraints()
        {
            if (SideToSideBase2Rules.TryCalculateMinimumWidth(
                    General.ExposedBusbarPhaseSpacing,
                    General.BusbarSpec,
                    General.InsulationDistance,
                    out var minimumWidth))
            {
                UpdateBase2WidthConstraints(minimumWidth, null);
                return;
            }

            UpdateBase2WidthConstraints(
                double.NaN,
                "无法从铜排规格解析排宽，不能校验基座2宽。");
        }

        private void UpdateBase2WidthConstraints(double minimumWidth, string errorMessage)
        {
            ACabinet.FrontHole.Base2.UpdateWidthConstraint(minimumWidth, errorMessage);
            ACabinet.BackHole.Base2.UpdateWidthConstraint(minimumWidth, errorMessage);
            BCabinet.FrontHole.Base2.UpdateWidthConstraint(minimumWidth, errorMessage);
            BCabinet.BackHole.Base2.UpdateWidthConstraint(minimumWidth, errorMessage);
        }
    }

    /// <summary>
    /// 侧对侧场景整体参数及柜顶开孔方式判断规则。
    /// </summary>
    public sealed class SideToSideGeneralParameters : CabinetParameterNotificationObject
    {
        public const string AbcPhaseSequence = "ABC";
        public const string CbaPhaseSequence = "CBA";

        private double _cabinetEccentricity;
        private string _aCabinetExposedBusbarPhaseSequence = AbcPhaseSequence;
        private string _bCabinetExposedBusbarPhaseSequence = AbcPhaseSequence;
        private string _busbarSpec = "80×8";
        private double _insulatorHeight =
            InsulatorHeightRules.DefaultInsulatorWithBracketHeight;
        private double _exposedBusbarPhaseSpacing = 210;

        private double _sideToSideGeneralParametersFloorHeight = 4377;
        public double FloorHeight
        {
            get => _sideToSideGeneralParametersFloorHeight;
            set => SetParameterValue(ref _sideToSideGeneralParametersFloorHeight, value, nameof(FloorHeight));
        }

        private double _sideToSideGeneralParametersParameterB = 4000;
        public double ParameterB
        {
            get => _sideToSideGeneralParametersParameterB;
            set => SetParameterValue(ref _sideToSideGeneralParametersParameterB, value, nameof(ParameterB));
        }

        private double _sideToSideGeneralParametersCabinetSpacing = 1000;
        public double CabinetSpacing
        {
            get => _sideToSideGeneralParametersCabinetSpacing;
            set => SetParameterValue(ref _sideToSideGeneralParametersCabinetSpacing, value, nameof(CabinetSpacing));
        }

        public double CabinetEccentricity
        {
            get => _cabinetEccentricity;
            set
            {
                if (Math.Abs(_cabinetEccentricity - value) < double.Epsilon)
                {
                    return;
                }

                var previousKind = BaseKind;
                _cabinetEccentricity = value;
                RaisePropertyChanged(nameof(CabinetEccentricity));
                RaiseBaseKindChangedIfNeeded(previousKind);
            }
        }

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

        private int _sideToSideGeneralParametersSinglePhaseRootCount = 4;
        public int SinglePhaseRootCount
        {
            get => _sideToSideGeneralParametersSinglePhaseRootCount;
            set => SetParameterValue(ref _sideToSideGeneralParametersSinglePhaseRootCount, value, nameof(SinglePhaseRootCount));
        }

        public double ExposedBusbarPhaseSpacing
        {
            get => _exposedBusbarPhaseSpacing;
            set
            {
                if (_exposedBusbarPhaseSpacing.Equals(value))
                {
                    return;
                }

                _exposedBusbarPhaseSpacing = value;
                RaisePropertyChanged(nameof(ExposedBusbarPhaseSpacing));
            }
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

        private string _sideToSideGeneralParametersBusbarMaterial = "直角铜母线";
        public string BusbarMaterial
        {
            get => _sideToSideGeneralParametersBusbarMaterial;
            set => SetParameterValue(ref _sideToSideGeneralParametersBusbarMaterial, value, nameof(BusbarMaterial));
        }

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

                var previousKind = BaseKind;
                _aCabinetExposedBusbarPhaseSequence = value;
                RaisePropertyChanged(nameof(ACabinetExposedBusbarPhaseSequence));
                RaiseBaseKindChangedIfNeeded(previousKind);
            }
        }

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

                var previousKind = BaseKind;
                _bCabinetExposedBusbarPhaseSequence = value;
                RaisePropertyChanged(nameof(BCabinetExposedBusbarPhaseSequence));
                RaiseBaseKindChangedIfNeeded(previousKind);
            }
        }

        // 开孔几何固定；相序和偏移只决定普通基座类型。
        public SideToSideCabinetTopOpeningMode CabinetTopOpeningMode => SideToSideCabinetTopOpeningMode.FrontRear;

        public SideToSideCabinetBaseKind BaseKind => SideToSideSchemeRules.ResolveBaseKind(
            ACabinetExposedBusbarPhaseSequence, BCabinetExposedBusbarPhaseSequence, CabinetEccentricity);

        public string CabinetTopOpeningModeDisplayName => "前后开孔";
        public string BackHoleToCabinetEdgeDistanceDisplayName => "距柜后距离：";

        private void RaiseBaseKindChangedIfNeeded(SideToSideCabinetBaseKind previousKind)
        {
            if (previousKind != BaseKind) RaisePropertyChanged(nameof(BaseKind));
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

    public enum SideToSideCabinetBaseKind
    {
        Base1 = 0,
        Base2 = 1
    }

    // 保留历史枚举编号；新模板只允许 FrontRear。
    public enum SideToSideCabinetTopOpeningMode
    {
        FrontRear = 0,
        LeftRight = 1
    }

    /// <summary>
    /// 侧对侧场景单柜参数。
    /// </summary>
    public sealed class SideToSideCabinetParameters : CabinetParameterNotificationObject
    {
        private double _cabinetWidth;
        private SideToSideCabinetHoleParameters _frontHole;
        private SideToSideCabinetHoleParameters _backHole;

        private double _sideToSideCabinetParametersCabinetHeight;
        public double CabinetHeight
        {
            get => _sideToSideCabinetParametersCabinetHeight;
            set => SetParameterValue(ref _sideToSideCabinetParametersCabinetHeight, value, nameof(CabinetHeight));
        }

        private double _sideToSideCabinetParametersCabinetDepth;
        public double CabinetDepth
        {
            get => _sideToSideCabinetParametersCabinetDepth;
            set => SetParameterValue(ref _sideToSideCabinetParametersCabinetDepth, value, nameof(CabinetDepth));
        }

        public double CabinetWidth
        {
            get => _cabinetWidth;
            set => SetParameterValue(ref _cabinetWidth, value, nameof(CabinetWidth));
        }

        private double _sideToSideCabinetParametersGroundHeight;
        public double GroundHeight
        {
            get => _sideToSideCabinetParametersGroundHeight;
            set => SetParameterValue(ref _sideToSideCabinetParametersGroundHeight, value, nameof(GroundHeight));
        }

        public SideToSideCabinetHoleParameters FrontHole
        {
            get => _frontHole;
            set => SetParameterValue(ref _frontHole, value, nameof(FrontHole));
        }

        public SideToSideCabinetHoleParameters BackHole
        {
            get => _backHole;
            set => SetParameterValue(ref _backHole, value, nameof(BackHole));
        }

    }

    /// <summary>
    /// 侧对侧场景单个柜顶开孔及其基架参数。
    /// </summary>
    public sealed class SideToSideCabinetHoleParameters : CabinetParameterNotificationObject
    {
        private SideToSideCabinetBaseKind _baseKind = SideToSideCabinetBaseKind.Base2;
        internal SideToSideCabinetBaseKind BaseKind => _baseKind;
        private bool _isEnabled;

        public SideToSideCabinetHoleParameters()
        {
            FrontRearOpeningParameters = new SideToSideCabinetTopOpeningParameters();
            LeftRightOpeningParameters = new SideToSideCabinetTopOpeningParameters();
            Base1 = new SideToSideBase1Parameters();
            Base2 = new SideToSideBase2Parameters();
        }

        public SideToSideCabinetTopOpeningParameters FrontRearOpeningParameters { get; }

        // 仅保留旧快照字段兼容；不再参与新模板的读写与绑定。
        public SideToSideCabinetTopOpeningParameters LeftRightOpeningParameters { get; }

        /// <summary>
        /// 换相且无偏移使用的侧对侧基座1参数。切换基座类型时保留本对象中的输入值。
        /// </summary>
        public SideToSideBase1Parameters Base1 { get; }

        /// <summary>
        /// 其余方案使用的侧对侧基座2及增高基座参数。切换基座类型时保留本对象中的输入值。
        /// </summary>
        public SideToSideBase2Parameters Base2 { get; }

        public Visibility ActiveBase1Visibility =>
            _baseKind == SideToSideCabinetBaseKind.Base1 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ActiveBase2Visibility =>
            _baseKind == SideToSideCabinetBaseKind.Base2 ? Visibility.Visible : Visibility.Collapsed;

        private SideToSideCabinetTopOpeningParameters ActiveOpeningParameters => FrontRearOpeningParameters;

        internal void SetBaseKind(SideToSideCabinetBaseKind baseKind)
        {
            if (_baseKind == baseKind) return;
            _baseKind = baseKind;
            RaisePropertyChanged(nameof(ActiveBase1Visibility));
            RaisePropertyChanged(nameof(ActiveBase2Visibility));
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value)
                {
                    return;
                }

                _isEnabled = value;
                RaisePropertyChanged(nameof(IsEnabled));
            }
        }

        public double ToCabinetEdgeDistance
        {
            get => ActiveOpeningParameters.ToCabinetEdgeDistance;
            set
            {
                var openingParameters = ActiveOpeningParameters;
                if (Math.Abs(value - openingParameters.ToCabinetEdgeDistance) < double.Epsilon)
                {
                    return;
                }

                openingParameters.ToCabinetEdgeDistance = value;
                RaisePropertyChanged(nameof(ToCabinetEdgeDistance));
            }
        }

        /// <summary>
        /// 铜排中心至柜前/后距离；独立输入，切换基座类型时保留输入值。
        /// </summary>
        public double BusbarCenterToCabinetEdgeDistance
        {
            get => ActiveOpeningParameters.BusbarCenterToCabinetEdgeDistance;
            set
            {
                if (Math.Abs(ActiveOpeningParameters.BusbarCenterToCabinetEdgeDistance - value) < double.Epsilon)
                {
                    return;
                }

                ActiveOpeningParameters.BusbarCenterToCabinetEdgeDistance = value;
                RaisePropertyChanged(nameof(BusbarCenterToCabinetEdgeDistance));
            }
        }

        public double FixedHoleSpacing
        {
            get => ActiveOpeningParameters.FixedHoleSpacing;
            set
            {
                var previous = ActiveOpeningParameters.FixedHoleSpacing;
                // 显式输入或模型反读即使等于默认值，也必须保存，避免以后改柜宽覆盖实际孔距。
                ActiveOpeningParameters.FixedHoleSpacing = value;
                if (!previous.Equals(value)) RaisePropertyChanged(nameof(FixedHoleSpacing));
            }
        }

        private double _sideToSideCabinetHoleParametersBusbarCenterToBaseOffset;
        public double BusbarCenterToBaseOffset
        {
            get => _sideToSideCabinetHoleParametersBusbarCenterToBaseOffset;
            set => SetParameterValue(ref _sideToSideCabinetHoleParametersBusbarCenterToBaseOffset, value, nameof(BusbarCenterToBaseOffset));
        }

        public double L1
        {
            get => ActiveOpeningParameters.L1;
            set
            {
                if (Math.Abs(ActiveOpeningParameters.L1 - value) < double.Epsilon)
                {
                    return;
                }

                ActiveOpeningParameters.L1 = value;
                RaisePropertyChanged(nameof(L1));
            }
        }

        public double L2
        {
            get => ActiveOpeningParameters.L2;
            set
            {
                if (Math.Abs(ActiveOpeningParameters.L2 - value) < double.Epsilon)
                {
                    return;
                }

                ActiveOpeningParameters.L2 = value;
                RaisePropertyChanged(nameof(L2));
            }
        }

        public double L3
        {
            get => ActiveOpeningParameters.L3;
            set
            {
                if (Math.Abs(ActiveOpeningParameters.L3 - value) < double.Epsilon)
                {
                    return;
                }

                ActiveOpeningParameters.L3 = value;
                RaisePropertyChanged(nameof(L3));
            }
        }
    }

    /// <summary>
    /// 换相且无偏移使用的侧对侧基座1参数。基座1直接安装三个绝缘子。
    /// </summary>
    public sealed class SideToSideBase1Parameters : CabinetParameterNotificationObject
    {
        private double _baseOpeningHeight = 800;

        private double _sideToSideBase1ParametersBaseHeight = 1300;
        public double BaseHeight
        {
            get => _sideToSideBase1ParametersBaseHeight;
            set => SetParameterValue(ref _sideToSideBase1ParametersBaseHeight, value, nameof(BaseHeight));
        }

        /// <summary>
        /// 真实基座1本体宽深仅供模型反读和基架规划使用，不作为界面输入项。
        /// </summary>
        internal double BaseWidth { get; set; }

        internal double BaseDepth { get; set; }

        public double BaseOpeningHeight
        {
            get => _baseOpeningHeight;
            set
            {
                if (_baseOpeningHeight.Equals(value))
                {
                    return;
                }

                _baseOpeningHeight = value;
                RaisePropertyChanged(nameof(BaseOpeningHeight));
            }
        }

        private double _sideToSideBase1ParametersInsulatorPosition1 = 400;
        public double InsulatorPosition1
        {
            get => _sideToSideBase1ParametersInsulatorPosition1;
            set => SetParameterValue(ref _sideToSideBase1ParametersInsulatorPosition1, value, nameof(InsulatorPosition1));
        }

        private double _sideToSideBase1ParametersInsulatorPosition2 = 600;
        public double InsulatorPosition2
        {
            get => _sideToSideBase1ParametersInsulatorPosition2;
            set => SetParameterValue(ref _sideToSideBase1ParametersInsulatorPosition2, value, nameof(InsulatorPosition2));
        }

        private double _sideToSideBase1ParametersInsulatorPosition3 = 800;
        public double InsulatorPosition3
        {
            get => _sideToSideBase1ParametersInsulatorPosition3;
            set => SetParameterValue(ref _sideToSideBase1ParametersInsulatorPosition3, value, nameof(InsulatorPosition3));
        }
    }

    /// <summary>
    /// 其余方案使用的侧对侧基座2参数。界面基座高是基座2和增高基座的组合总高。
    /// </summary>
    public sealed class SideToSideBase2Parameters : CabinetParameterNotificationObject, INotifyDataErrorInfo
    {
        private double _baseHeight = 1500;
        private double _baseWidth = 800;
        private double _baseOpeningHeight = 500;
        private double _elevatedBaseHeight = 500;
        private double _insulatorPosition = 400;
        private double _minimumBaseWidth = 799;
        private string _widthConstraintError;

        public double BaseHeight
        {
            get => _baseHeight;
            set
            {
                if (_baseHeight.Equals(value))
                {
                    return;
                }

                _baseHeight = value;
                RaisePropertyChanged(nameof(BaseHeight));
                RaisePropertyChanged(nameof(Base2Height));
            }
        }

        public double Base2Height => BaseHeight - ElevatedBaseHeight;

        public double BaseWidth
        {
            get => _baseWidth;
            set
            {
                if (_baseWidth.Equals(value))
                {
                    return;
                }

                _baseWidth = value;
                RaisePropertyChanged(nameof(BaseWidth));
                RaiseErrorsChanged(nameof(BaseWidth));
            }
        }

        /// <summary>
        /// 真实基座2本体深度只参与基架长度计算，不能用增高基座尺寸替代。
        /// </summary>
        internal double BaseDepth { get; set; }

        public double BaseOpeningHeight
        {
            get => _baseOpeningHeight;
            set
            {
                if (_baseOpeningHeight.Equals(value))
                {
                    return;
                }

                _baseOpeningHeight = value;
                RaisePropertyChanged(nameof(BaseOpeningHeight));
            }
        }

        public double ElevatedBaseHeight
        {
            get => _elevatedBaseHeight;
            set
            {
                if (_elevatedBaseHeight.Equals(value))
                {
                    return;
                }

                _elevatedBaseHeight = value;
                RaisePropertyChanged(nameof(ElevatedBaseHeight));
                RaisePropertyChanged(nameof(Base2Height));
                RaiseErrorsChanged(nameof(InsulatorPosition));
            }
        }

        public double InsulatorPosition
        {
            get => _insulatorPosition;
            set
            {
                if (_insulatorPosition.Equals(value))
                {
                    return;
                }

                _insulatorPosition = value;
                RaisePropertyChanged(nameof(InsulatorPosition));
                RaiseErrorsChanged(nameof(InsulatorPosition));
            }
        }

        public double MinimumBaseWidth => _minimumBaseWidth;

        public bool HasErrors => GetBaseWidthError() != null || GetInsulatorPositionError() != null;

        public event EventHandler<DataErrorsChangedEventArgs> ErrorsChanged;

        public IEnumerable GetErrors(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                var allErrors = new List<string>();
                AddIfPresent(allErrors, GetBaseWidthError());
                AddIfPresent(allErrors, GetInsulatorPositionError());
                return allErrors;
            }

            var error = propertyName == nameof(BaseWidth)
                ? GetBaseWidthError()
                : propertyName == nameof(InsulatorPosition)
                    ? GetInsulatorPositionError()
                    : null;
            return error == null ? new string[0] : new[] { error };
        }

        internal void UpdateWidthConstraint(double minimumWidth, string errorMessage)
        {
            _minimumBaseWidth = minimumWidth;
            _widthConstraintError = errorMessage;
            RaisePropertyChanged(nameof(MinimumBaseWidth));
            RaiseErrorsChanged(nameof(BaseWidth));
        }

        private string GetBaseWidthError()
        {
            if (!string.IsNullOrWhiteSpace(_widthConstraintError))
            {
                return _widthConstraintError;
            }

            if (!(BaseWidth > 0d) || double.IsNaN(BaseWidth) || double.IsInfinity(BaseWidth))
            {
                return "基座2宽必须为有限正数。";
            }

            return BaseWidth < MinimumBaseWidth
                ? "基座2宽不能小于"
                    + MinimumBaseWidth.ToString("0.###", CultureInfo.InvariantCulture)
                    + "（相间距×2+排宽+2×(19.5+绝缘距离+5)）。"
                : null;
        }

        private string GetInsulatorPositionError()
        {
            if (InsulatorPosition < 0d
                || double.IsNaN(InsulatorPosition)
                || double.IsInfinity(InsulatorPosition))
            {
                return "绝缘子定位必须为有限非负数。";
            }

            return InsulatorPosition > ElevatedBaseHeight
                ? "绝缘子定位不能大于增高基座高"
                    + ElevatedBaseHeight.ToString("0.###", CultureInfo.InvariantCulture)
                    + "。"
                : null;
        }

        private void RaiseErrorsChanged(string propertyName)
        {
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        }

        private static void AddIfPresent(ICollection<string> errors, string error)
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                errors.Add(error);
            }
        }
    }

    /// <summary>
    /// 侧对侧基架2的共用尺寸公式，供界面即时校验和生成规划共用。
    /// </summary>
    internal static class SideToSideBase2Rules
    {
        internal const double L3Allowance = 100d;
        internal const double ElevatedDepthAllowance = 41.5d;

        internal static bool TryCalculateMinimumWidth(
            double phaseSpacing,
            string busbarSpec,
            double insulationDistance,
            out double minimumWidth)
        {
            minimumWidth = 0d;
            if (!(phaseSpacing > 0d)
                || double.IsNaN(phaseSpacing)
                || double.IsInfinity(phaseSpacing)
                || !(insulationDistance > 0d)
                || double.IsNaN(insulationDistance)
                || double.IsInfinity(insulationDistance)
                || !TryGetBusbarWidth(busbarSpec, out var busbarWidth))
            {
                return false;
            }

            minimumWidth = CKBusbarBridgePreview.FaceToFaceUnifiedPlanningRules
                .CalculateMinimumBaseWidth(phaseSpacing, busbarWidth, insulationDistance);
            return true;
        }

        internal static bool TryGetBusbarWidth(string busbarSpec, out double busbarWidth)
        {
            busbarWidth = 0d;
            if (string.IsNullOrWhiteSpace(busbarSpec))
            {
                return false;
            }

            var matches = Regex.Matches(busbarSpec, @"\d+(?:\.\d+)?");
            var widthIndex = matches.Count >= 3 ? 1 : 0;
            return matches.Count >= 2
                && double.TryParse(
                    matches[widthIndex].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out busbarWidth)
                && busbarWidth > 0d
                && !double.IsNaN(busbarWidth)
                && !double.IsInfinity(busbarWidth);
        }
    }

    /// <summary>
    /// 侧对侧场景单种开孔方式下的柜顶孔几何参数。
    /// </summary>
    public sealed class SideToSideCabinetTopOpeningParameters : CabinetParameterNotificationObject
    {
        private double? _fixedHoleSpacing;
        private double _defaultFixedHoleSpacing;

        private double _sideToSideCabinetTopOpeningParametersToCabinetEdgeDistance;
        public double ToCabinetEdgeDistance
        {
            get => _sideToSideCabinetTopOpeningParametersToCabinetEdgeDistance;
            set => SetParameterValue(ref _sideToSideCabinetTopOpeningParametersToCabinetEdgeDistance, value, nameof(ToCabinetEdgeDistance));
        }

        private double _sideToSideCabinetTopOpeningParametersBusbarCenterToCabinetEdgeDistance;
        public double BusbarCenterToCabinetEdgeDistance
        {
            get => _sideToSideCabinetTopOpeningParametersBusbarCenterToCabinetEdgeDistance;
            set => SetParameterValue(ref _sideToSideCabinetTopOpeningParametersBusbarCenterToCabinetEdgeDistance, value, nameof(BusbarCenterToCabinetEdgeDistance));
        }

        public double FixedHoleSpacing
        {
            get => _fixedHoleSpacing ?? _defaultFixedHoleSpacing;
            set
            {
                var previous = FixedHoleSpacing;
                _fixedHoleSpacing = value;
                if (!previous.Equals(value)) RaisePropertyChanged(nameof(FixedHoleSpacing));
            }
        }

        internal void UpdateFixedHoleSpacingDefault(double value)
        {
            _defaultFixedHoleSpacing = value;
        }

        private double _sideToSideCabinetTopOpeningParametersL1;
        public double L1
        {
            get => _sideToSideCabinetTopOpeningParametersL1;
            set => SetParameterValue(ref _sideToSideCabinetTopOpeningParametersL1, value, nameof(L1));
        }

        private double _sideToSideCabinetTopOpeningParametersL2;
        public double L2
        {
            get => _sideToSideCabinetTopOpeningParametersL2;
            set => SetParameterValue(ref _sideToSideCabinetTopOpeningParametersL2, value, nameof(L2));
        }

        private double _sideToSideCabinetTopOpeningParametersL3;
        public double L3
        {
            get => _sideToSideCabinetTopOpeningParametersL3;
            set => SetParameterValue(ref _sideToSideCabinetTopOpeningParametersL3, value, nameof(L3));
        }
    }
}
