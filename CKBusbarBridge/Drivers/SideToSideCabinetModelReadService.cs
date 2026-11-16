using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridge.models;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 侧对侧柜体模型物理读取边界。实现负责访问 SolidWorks，服务只负责恢复参数。
    /// </summary>
    internal interface ISideToSideCabinetModelReadSource
    {
        double? ReadRootGlobalVariable(string variableName);

        SideToSideExtensionBusbarReadResult ReadExtensionBusbars();

        SideToSideCabinetReadResult ReadCabinet(
            string cabinetName,
            SideToSideCabinetTopOpeningMode openingMode);

        SideToSideBaseReadResult ReadBase(
            string positionName,
            SideToSideCabinetBaseKind baseKind,
            bool isACabinet);

        double? ReadInsulatorHeight(IReadOnlyList<string> enabledPositionNames);
    }

    internal sealed class SideToSideExtensionBusbarReadResult
    {
        internal string BusbarSpec { get; set; }

        internal int? SinglePhaseRootCount { get; set; }

        internal double? PhaseSpacing { get; set; }

        internal string BusbarMaterial { get; set; }

        internal string ACabinetPhaseSequence { get; set; }

        internal string BCabinetPhaseSequence { get; set; }
    }

    internal sealed class SideToSideCabinetReadResult
    {
        internal double? CabinetHeight { get; set; }

        internal double? CabinetDepth { get; set; }

        internal double? CabinetWidth { get; set; }

        internal double? GroundHeight { get; set; }

        internal SideToSideCabinetHoleReadResult FrontHole { get; set; }

        internal SideToSideCabinetHoleReadResult BackHole { get; set; }
    }

    internal sealed class SideToSideCabinetHoleReadResult
    {
        internal bool? IsEnabled { get; set; }

        internal double? ToCabinetEdgeDistance { get; set; }

        internal double? BusbarCenterToCabinetEdgeDistance { get; set; }

        internal double? FixedHoleSpacing { get; set; }

        internal double? L1 { get; set; }

        internal double? L2 { get; set; }

        internal double? L3 { get; set; }
    }

    internal sealed class SideToSideBaseReadResult
    {
        internal double? BaseHeight { get; set; }

        internal double? BaseWidth { get; set; }

        internal double? BaseDepth { get; set; }

        internal double? BaseOpeningHeight { get; set; }

        internal double? ElevatedBaseHeight { get; set; }

        internal double? ElevatedBaseWidth { get; set; }

        internal double? InsulatorPosition { get; set; }

        internal double? InsulatorPosition1 { get; set; }

        internal double? InsulatorPosition2 { get; set; }

        internal double? InsulatorPosition3 { get; set; }
    }

    /// <summary>
    /// 将侧对侧总装、柜体、伸出排和独立基座的物理读数恢复到新参数对象。
    /// 当前物理结构所需参数必须全部读取成功，再恢复界面参数。
    /// </summary>
    internal sealed class SideToSideCabinetModelReadService
    {
        private readonly ISideToSideCabinetModelReadSource _source;

        internal SideToSideCabinetModelReadService(
            ISideToSideCabinetModelReadSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        internal void ReadInto(SideToSideCabinetDriverParameters parameters) => ReadStrictInto(parameters);

        private void ApplyReadings(SideToSideCabinetDriverParameters parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            var general = parameters.General;
            ApplyIfPresent(
                _source.ReadRootGlobalVariable("柜间距A"),
                value => general.CabinetSpacing = value);
            ApplyIfPresent(
                _source.ReadRootGlobalVariable("楼高D"),
                value => general.FloorHeight = value);
            ApplyIfPresent(
                _source.ReadRootGlobalVariable("B"),
                value => general.ParameterB = value);

            var eccentricity = _source.ReadRootGlobalVariable("柜间偏心值");
            if (eccentricity.HasValue)
            {
                var direction = _source.ReadRootGlobalVariable("偏移方向") ?? 0d;
                general.CabinetEccentricity = direction >= 0.5d
                    ? Math.Abs(eccentricity.Value)
                    : -Math.Abs(eccentricity.Value);
            }

            ApplyExtensionBusbars(general, _source.ReadExtensionBusbars());

            var openingMode = general.CabinetTopOpeningMode;
            ApplyCabinet(
                parameters.ACabinet,
                _source.ReadCabinet("A柜", openingMode));
            ApplyCabinet(
                parameters.BCabinet,
                _source.ReadCabinet("B柜", openingMode));

            var enabledPositions = new List<string>();
            ReadBaseIfEnabled(
                parameters.ACabinet?.FrontHole,
                "A柜前",
                general.BaseKind,
                isACabinet: true,
                enabledPositions);
            ReadBaseIfEnabled(
                parameters.ACabinet?.BackHole,
                "A柜后",
                general.BaseKind,
                isACabinet: true,
                enabledPositions);
            ReadBaseIfEnabled(
                parameters.BCabinet?.FrontHole,
                "B柜前",
                general.BaseKind,
                isACabinet: false,
                enabledPositions);
            ReadBaseIfEnabled(
                parameters.BCabinet?.BackHole,
                "B柜后",
                general.BaseKind,
                isACabinet: false,
                enabledPositions);

            ApplyIfPresent(
                _source.ReadInsulatorHeight(enabledPositions),
                value => general.InsulatorHeight =
                    InsulatorHeightRules.ToInsulatorWithBracketHeight(value));
        }

        /// <summary>
        /// 快照专用严格反读。任何当前物理结构所需的值缺失时整体失败，
        /// 防止用参数默认值冒充模型实际值推进二次驱动基线。
        /// </summary>
        internal void ReadStrictInto(
            SideToSideCabinetDriverParameters parameters,
            Action<string> showMaterialReadWarning = null)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            var cachedSource = new CachedReadSource(_source);
            var cachedReader = new SideToSideCabinetModelReadService(cachedSource);
            var missing = cachedReader.ValidateStrictReadback();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "侧对侧模型参数反读不完整："
                    + string.Join("；", missing.Distinct(StringComparer.Ordinal)));
            }

            var extensionBusbars = cachedSource.ReadExtensionBusbars();
            BusbarMaterialReadFailurePolicy.NotifyIfFailed(
                extensionBusbars?.BusbarMaterial,
                showMaterialReadWarning);
            cachedReader.ApplyReadings(parameters);
        }

        /// <summary>
        /// 严格反读先缓存一次物理读数，再对同一批读数完成校验和参数恢复。
        /// </summary>
        private sealed class CachedReadSource : ISideToSideCabinetModelReadSource
        {
            private readonly ISideToSideCabinetModelReadSource _inner;
            private readonly Dictionary<string, double?> _rootValues =
                new Dictionary<string, double?>(StringComparer.Ordinal);
            private readonly Dictionary<string, SideToSideCabinetReadResult> _cabinets =
                new Dictionary<string, SideToSideCabinetReadResult>(StringComparer.Ordinal);
            private readonly Dictionary<string, SideToSideBaseReadResult> _bases =
                new Dictionary<string, SideToSideBaseReadResult>(StringComparer.Ordinal);
            private SideToSideExtensionBusbarReadResult _extension;
            private bool _extensionRead;
            private double? _insulatorHeight;
            private bool _insulatorHeightRead;

            internal CachedReadSource(ISideToSideCabinetModelReadSource inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public double? ReadRootGlobalVariable(string variableName)
            {
                if (!_rootValues.TryGetValue(variableName, out var value))
                {
                    value = _inner.ReadRootGlobalVariable(variableName);
                    _rootValues.Add(variableName, value);
                }
                return value;
            }

            public SideToSideExtensionBusbarReadResult ReadExtensionBusbars()
            {
                if (!_extensionRead)
                {
                    _extension = _inner.ReadExtensionBusbars();
                    _extensionRead = true;
                }
                return _extension;
            }

            public SideToSideCabinetReadResult ReadCabinet(
                string cabinetName,
                SideToSideCabinetTopOpeningMode openingMode)
            {
                var key = cabinetName + "|" + openingMode;
                if (!_cabinets.TryGetValue(key, out var value))
                {
                    value = _inner.ReadCabinet(cabinetName, openingMode);
                    _cabinets.Add(key, value);
                }
                return value;
            }

            public SideToSideBaseReadResult ReadBase(
                string positionName,
                SideToSideCabinetBaseKind baseKind,
                bool isACabinet)
            {
                var key = positionName + "|" + baseKind + "|" + isACabinet;
                if (!_bases.TryGetValue(key, out var value))
                {
                    value = _inner.ReadBase(positionName, baseKind, isACabinet);
                    _bases.Add(key, value);
                }
                return value;
            }

            public double? ReadInsulatorHeight(
                IReadOnlyList<string> enabledPositionNames)
            {
                if (!_insulatorHeightRead)
                {
                    _insulatorHeight = _inner.ReadInsulatorHeight(
                        enabledPositionNames);
                    _insulatorHeightRead = true;
                }
                return _insulatorHeight;
            }
        }

        private List<string> ValidateStrictReadback()
        {
            var missing = new List<string>();
            Require(_source.ReadRootGlobalVariable("柜间距A"), "总装/柜间距A", missing);
            Require(_source.ReadRootGlobalVariable("楼高D"), "总装/楼高D", missing);
            Require(_source.ReadRootGlobalVariable("B"), "总装/B", missing);
            var eccentricity = _source.ReadRootGlobalVariable("柜间偏心值");
            var direction = _source.ReadRootGlobalVariable("偏移方向");
            Require(eccentricity, "总装/柜间偏心值", missing);
            Require(direction, "总装/偏移方向", missing);

            var extension = _source.ReadExtensionBusbars();
            if (extension == null)
            {
                missing.Add("伸出排");
                return missing;
            }
            Require(extension.BusbarSpec, "伸出排/母排规格", missing);
            Require(extension.BusbarMaterial, "伸出排/铜排材质", missing);
            Require(extension.SinglePhaseRootCount, "伸出排/单相根数", missing);
            Require(extension.PhaseSpacing, "伸出排/相间距", missing);
            RequirePhaseSequence(
                extension.ACabinetPhaseSequence,
                "A柜伸出排/相序",
                missing);
            RequirePhaseSequence(
                extension.BCabinetPhaseSequence,
                "B柜伸出排/相序",
                missing);

            var modeProbe = new SideToSideGeneralParameters();
            if (IsPhaseSequence(extension.ACabinetPhaseSequence))
            {
                modeProbe.ACabinetExposedBusbarPhaseSequence =
                    extension.ACabinetPhaseSequence;
            }
            if (IsPhaseSequence(extension.BCabinetPhaseSequence))
            {
                modeProbe.BCabinetExposedBusbarPhaseSequence =
                    extension.BCabinetPhaseSequence;
            }
            if (eccentricity.HasValue && direction.HasValue)
            {
                modeProbe.CabinetEccentricity = direction.Value >= 0.5d
                    ? Math.Abs(eccentricity.Value)
                    : -Math.Abs(eccentricity.Value);
            }

            var mode = modeProbe.CabinetTopOpeningMode;
            var baseKind = modeProbe.BaseKind;
            var enabled = new List<Tuple<string, bool>>();
            ValidateCabinet("A柜", mode, isACabinet: true, enabled, missing);
            ValidateCabinet("B柜", mode, isACabinet: false, enabled, missing);
            foreach (var position in enabled)
            {
                ValidateBase(
                    position.Item1,
                    baseKind,
                    position.Item2,
                    missing);
            }
            ValidateMatchingBaseOpeningHeights(baseKind, enabled, missing);

            if (enabled.Count > 0
                && !_source.ReadInsulatorHeight(
                        enabled.Select(item => item.Item1).ToArray())
                    .HasValue)
            {
                missing.Add("绝缘子/高度");
            }

            return missing;
        }

        private void ValidateCabinet(
            string cabinetName,
            SideToSideCabinetTopOpeningMode mode,
            bool isACabinet,
            ICollection<Tuple<string, bool>> enabled,
            ICollection<string> missing)
        {
            var cabinet = _source.ReadCabinet(cabinetName, mode);
            if (cabinet == null)
            {
                missing.Add(cabinetName + "/柜体");
                return;
            }

            Require(cabinet.CabinetHeight, cabinetName + "/柜高", missing);
            Require(cabinet.CabinetDepth, cabinetName + "/柜深", missing);
            Require(cabinet.CabinetWidth, cabinetName + "/柜宽", missing);
            Require(cabinet.GroundHeight, cabinetName + "/离地高度", missing);
            ValidateHole(
                cabinetName + "前",
                cabinet.FrontHole,
                isACabinet,
                enabled,
                missing);
            ValidateHole(
                cabinetName + "后",
                cabinet.BackHole,
                isACabinet,
                enabled,
                missing);
        }

        private static void ValidateHole(
            string positionName,
            SideToSideCabinetHoleReadResult hole,
            bool isACabinet,
            ICollection<Tuple<string, bool>> enabled,
            ICollection<string> missing)
        {
            if (hole?.IsEnabled == null)
            {
                missing.Add(positionName + "/启用状态");
                return;
            }
            if (!hole.IsEnabled.Value)
            {
                return;
            }

            Require(hole.ToCabinetEdgeDistance, positionName + "/距柜边", missing);
            Require(
                hole.BusbarCenterToCabinetEdgeDistance,
                positionName + "/母排中心距柜边",
                missing);
            Require(hole.FixedHoleSpacing, positionName + "/接口固定孔间距", missing);
            Require(hole.L1, positionName + "/L1", missing);
            Require(hole.L2, positionName + "/L2", missing);
            Require(hole.L3, positionName + "/L3", missing);
            enabled.Add(Tuple.Create(positionName, isACabinet));
        }

        private void ValidateBase(
            string positionName,
            SideToSideCabinetBaseKind mode,
            bool isACabinet,
            ICollection<string> missing)
        {
            SideToSideBaseReadResult model;
            try
            {
                model = _source.ReadBase(positionName, mode, isACabinet);
            }
            catch (Exception exception)
            {
                missing.Add(positionName + "/基座（" + exception.Message + "）");
                return;
            }
            if (model == null)
            {
                missing.Add(positionName + "/基座");
                return;
            }

            Require(model.BaseHeight, positionName + "/基座高", missing);
            Require(model.BaseOpeningHeight, positionName + "/基架高度", missing);
            var cabinetWidth = _source.ReadCabinet(isACabinet ? "A柜" : "B柜", SideToSideCabinetTopOpeningMode.FrontRear)?.CabinetWidth;
            var depthMismatch = CabinetBaseDimensionRules.GetMismatch(positionName, "基座深", cabinetWidth, model.BaseDepth);
            if (depthMismatch != null) missing.Add(depthMismatch);
            if (mode == SideToSideCabinetBaseKind.Base1)
            {
                var cabinet = _source.ReadCabinet(isACabinet ? "A柜" : "B柜", SideToSideCabinetTopOpeningMode.FrontRear);
                var hole = positionName.EndsWith("前", StringComparison.Ordinal) ? cabinet?.FrontHole : cabinet?.BackHole;
                var expectedWidth = hole?.FixedHoleSpacing.HasValue == true ? hole.FixedHoleSpacing.Value + 43d : (double?)null;
                var widthError = CabinetBaseDimensionRules.GetMismatch(positionName, "基座1宽", expectedWidth, model.BaseWidth,
                    "接口固定孔间距+43", "期望宽度");
                if (widthError != null) missing.Add(widthError);
                Require(model.BaseWidth, positionName + "/基座宽", missing);
                Require(model.BaseDepth, positionName + "/基座深", missing);
                Require(model.InsulatorPosition1, positionName + "/绝缘子定位1", missing);
                Require(model.InsulatorPosition2, positionName + "/绝缘子定位2", missing);
                Require(model.InsulatorPosition3, positionName + "/绝缘子定位3", missing);
                return;
            }

            Require(model.BaseWidth, positionName + "/基座宽", missing);
            Require(model.BaseDepth, positionName + "/基座深", missing);
            Require(model.ElevatedBaseHeight, positionName + "/增高基座高", missing);
            var widthMismatch = CabinetBaseDimensionRules.GetMismatch(positionName, "增高基座宽", cabinetWidth, model.ElevatedBaseWidth);
            if (widthMismatch != null) missing.Add(widthMismatch);
            Require(model.InsulatorPosition, positionName + "/绝缘子定位", missing);
        }

        private void ValidateMatchingBaseOpeningHeights(
            SideToSideCabinetBaseKind mode,
            IReadOnlyCollection<Tuple<string, bool>> enabled,
            ICollection<string> missing)
        {
            ValidateMatchingBaseOpeningHeight(
                "A柜前",
                "B柜前",
                mode,
                enabled,
                missing);
            ValidateMatchingBaseOpeningHeight(
                "A柜后",
                "B柜后",
                mode,
                enabled,
                missing);
        }

        private void ValidateMatchingBaseOpeningHeight(
            string aPositionName,
            string bPositionName,
            SideToSideCabinetBaseKind mode,
            IEnumerable<Tuple<string, bool>> enabled,
            ICollection<string> missing)
        {
            if (!enabled.Any(item => item.Item1 == aPositionName)
                || !enabled.Any(item => item.Item1 == bPositionName))
            {
                return;
            }

            SideToSideBaseReadResult aBase;
            SideToSideBaseReadResult bBase;
            try
            {
                // 严格读取使用缓存源；这里比较的是写入联动参数前的原始物理值。
                aBase = _source.ReadBase(aPositionName, mode, isACabinet: true);
                bBase = _source.ReadBase(bPositionName, mode, isACabinet: false);
            }
            catch
            {
                // 对应的缺失或读取异常已由 ValidateBase 记录，避免重复错误。
                return;
            }

            if (!aBase?.BaseOpeningHeight.HasValue == true
                || !bBase?.BaseOpeningHeight.HasValue == true
                || Math.Abs(
                    aBase.BaseOpeningHeight.Value
                    - bBase.BaseOpeningHeight.Value) <= 1.0)
            {
                return;
            }

            var baseKind = mode == SideToSideCabinetBaseKind.Base1
                ? "基座1"
                : "基座2";
            missing.Add(
                aPositionName + "/" + bPositionName + "/" + baseKind
                + "的基架高度不一致");
        }

        private static void Require(double? value, string name, ICollection<string> missing)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            {
                missing.Add(name);
            }
        }

        private static void Require(int? value, string name, ICollection<string> missing)
        {
            if (!value.HasValue || value.Value <= 0)
            {
                missing.Add(name);
            }
        }

        private static void Require(string value, string name, ICollection<string> missing)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(name);
            }
        }

        private static void RequirePhaseSequence(
            string value,
            string name,
            ICollection<string> missing)
        {
            if (!IsPhaseSequence(value))
            {
                missing.Add(name);
            }
        }

        private static bool IsPhaseSequence(string value)
        {
            return string.Equals(
                    value,
                    SideToSideGeneralParameters.AbcPhaseSequence,
                    StringComparison.Ordinal)
                || string.Equals(
                    value,
                    SideToSideGeneralParameters.CbaPhaseSequence,
                    StringComparison.Ordinal);
        }

        private static void ApplyExtensionBusbars(
            SideToSideGeneralParameters general,
            SideToSideExtensionBusbarReadResult result)
        {
            if (general == null || result == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.BusbarSpec))
            {
                general.BusbarSpec = result.BusbarSpec;
            }
            if (result.SinglePhaseRootCount.HasValue
                && result.SinglePhaseRootCount.Value > 0)
            {
                general.SinglePhaseRootCount = result.SinglePhaseRootCount.Value;
            }
            ApplyIfPresent(
                result.PhaseSpacing,
                value => general.ExposedBusbarPhaseSpacing = value);
            if (!string.IsNullOrWhiteSpace(result.BusbarMaterial))
            {
                general.BusbarMaterial =
                    SolidWorksMaterialHelper.NormalizeBusbarMaterial(
                        result.BusbarMaterial);
            }

            ApplyPhaseSequence(
                result.ACabinetPhaseSequence,
                value => general.ACabinetExposedBusbarPhaseSequence = value);
            ApplyPhaseSequence(
                result.BCabinetPhaseSequence,
                value => general.BCabinetExposedBusbarPhaseSequence = value);
        }

        private static void ApplyPhaseSequence(string value, Action<string> apply)
        {
            if (string.Equals(
                    value,
                    SideToSideGeneralParameters.AbcPhaseSequence,
                    StringComparison.Ordinal)
                || string.Equals(
                    value,
                    SideToSideGeneralParameters.CbaPhaseSequence,
                    StringComparison.Ordinal))
            {
                apply(value);
            }
        }

        private static void ApplyCabinet(
            SideToSideCabinetParameters cabinet,
            SideToSideCabinetReadResult result)
        {
            if (cabinet == null || result == null)
            {
                return;
            }

            ApplyIfPresent(result.CabinetHeight, value => cabinet.CabinetHeight = value);
            ApplyIfPresent(result.CabinetDepth, value => cabinet.CabinetDepth = value);
            ApplyIfPresent(result.CabinetWidth, value => cabinet.CabinetWidth = value);
            ApplyIfPresent(result.GroundHeight, value => cabinet.GroundHeight = value);
            ApplyHole(cabinet.FrontHole, result.FrontHole);
            ApplyHole(cabinet.BackHole, result.BackHole);
        }

        private static void ApplyHole(
            SideToSideCabinetHoleParameters hole,
            SideToSideCabinetHoleReadResult result)
        {
            if (hole == null || result == null)
            {
                return;
            }

            if (result.IsEnabled.HasValue)
            {
                hole.IsEnabled = result.IsEnabled.Value;
            }
            ApplyIfPresent(
                result.ToCabinetEdgeDistance,
                value => hole.ToCabinetEdgeDistance = value);
            ApplyIfPresent(
                result.BusbarCenterToCabinetEdgeDistance,
                value => hole.BusbarCenterToCabinetEdgeDistance = value);
            ApplyIfPresent(
                result.FixedHoleSpacing,
                value => hole.FixedHoleSpacing = value);
            ApplyIfPresent(result.L1, value => hole.L1 = value);
            ApplyIfPresent(result.L2, value => hole.L2 = value);
            ApplyIfPresent(result.L3, value => hole.L3 = value);
        }

        private void ReadBaseIfEnabled(
            SideToSideCabinetHoleParameters hole,
            string positionName,
            SideToSideCabinetBaseKind openingMode,
            bool isACabinet,
            ICollection<string> enabledPositions)
        {
            if (hole == null || !hole.IsEnabled)
            {
                return;
            }

            enabledPositions.Add(positionName);
            SideToSideBaseReadResult result;
            try
            {
                result = _source.ReadBase(positionName, openingMode, isACabinet);
            }
            catch
            {
                // 单个位置缺映射、缺组件或尺寸不可读时仅保留该位置默认值。
                // 其他柜体及基座仍继续反读，与面对面、面对墙的独立基座容错一致。
                return;
            }
            if (result == null)
            {
                return;
            }

            if (openingMode == SideToSideCabinetBaseKind.Base1)
            {
                ApplyBase1(hole.Base1, result);
                return;
            }

            ApplyBase2(hole.Base2, result);
        }

        private static void ApplyBase1(
            SideToSideBase1Parameters target,
            SideToSideBaseReadResult result)
        {
            if (target == null)
            {
                return;
            }

            ApplyIfPresent(result.BaseHeight, value => target.BaseHeight = value);
            ApplyIfPresent(result.BaseWidth, value => target.BaseWidth = value);
            ApplyIfPresent(result.BaseDepth, value => target.BaseDepth = value);
            ApplyIfPresent(
                result.BaseOpeningHeight,
                value => target.BaseOpeningHeight = value);
            ApplyIfPresent(
                result.InsulatorPosition1,
                value => target.InsulatorPosition1 = value);
            ApplyIfPresent(
                result.InsulatorPosition2,
                value => target.InsulatorPosition2 = value);
            ApplyIfPresent(
                result.InsulatorPosition3,
                value => target.InsulatorPosition3 = value);
        }

        private static void ApplyBase2(
            SideToSideBase2Parameters target,
            SideToSideBaseReadResult result)
        {
            if (target == null)
            {
                return;
            }

            ApplyIfPresent(result.BaseHeight, value => target.BaseHeight = value);
            ApplyIfPresent(result.BaseWidth, value => target.BaseWidth = value);
            ApplyIfPresent(result.BaseDepth, value => target.BaseDepth = value);
            ApplyIfPresent(
                result.BaseOpeningHeight,
                value => target.BaseOpeningHeight = value);
            ApplyIfPresent(
                result.ElevatedBaseHeight,
                value => target.ElevatedBaseHeight = value);
            ApplyIfPresent(
                result.InsulatorPosition,
                value => target.InsulatorPosition = value);
        }

        private static void ApplyIfPresent(double? value, Action<double> apply)
        {
            if (value.HasValue)
            {
                apply(value.Value);
            }
        }
    }

    /// <summary>
    /// 按侧对侧生成时的模型、全局变量、尺寸和配置属性路径读取实际值。
    /// </summary>
    internal sealed class SolidWorksSideToSideCabinetModelReadSource
        : ISideToSideCabinetModelReadSource
    {
        private const string ExtensionBusbarNameFragment = "柜内伸出排";
        private readonly IModelDoc2 _rootModel;
        private SideToSideCabinetBaseKind? _baseKind;

        internal SolidWorksSideToSideCabinetModelReadSource(IModelDoc2 rootModel)
        {
            BridgeModelReadOperations.EnsureAssemblyFullyResolved(rootModel);
            _rootModel = rootModel;
        }

        public double? ReadRootGlobalVariable(string variableName)
        {
            return ReadGlobal(_rootModel, variableName);
        }

        public SideToSideExtensionBusbarReadResult ReadExtensionBusbars()
        {
            var values = ExtensionBusbarModelReadHelper.ReadRequired(_rootModel, ExtensionBusbarNameFragment);
            return new SideToSideExtensionBusbarReadResult {
                PhaseSpacing = values.PhaseSpacing, SinglePhaseRootCount = values.RootCount,
                BusbarSpec = values.Specification, BusbarMaterial = values.Material,
                ACabinetPhaseSequence = ExtensionBusbarModelReadHelper.ReadPhaseSequence(_rootModel, "A柜内伸出排"),
                BCabinetPhaseSequence = ExtensionBusbarModelReadHelper.ReadPhaseSequence(_rootModel, "B柜内伸出排")
            };
        }

        public SideToSideCabinetReadResult ReadCabinet(
            string cabinetName,
            SideToSideCabinetTopOpeningMode openingMode)
        {
            if (openingMode != SideToSideCabinetTopOpeningMode.FrontRear)
                throw new InvalidOperationException("侧对侧新模板仅支持前后开孔，请先迁移旧左右开孔模型。");
            if (string.IsNullOrWhiteSpace(cabinetName))
            {
                return null;
            }

            var subAssemblyName = cabinetName + "-子装配体";
            var subAssemblyModel = ReferencedModelValueReader.FromPath(
                _rootModel,
                new[] { subAssemblyName });
            var bodyModel = ReferencedModelValueReader.FromPath(
                _rootModel,
                new[] { subAssemblyName, cabinetName + "体" });
            if (bodyModel == null)
            {
                return null;
            }

            foreach (var position in new[] { "前", "后" })
            {
                var legacyName = cabinetName + "顶开孔（柜" + position + "左右开孔）";
                var legacy = ((PartDoc)bodyModel.Model).FeatureByName(legacyName) as Feature;
                if (legacy != null && !bodyModel.Suppressed(legacyName))
                    throw new InvalidOperationException(cabinetName + "仍启用旧左右开孔特征，请先迁移为前后开孔模型。");
            }

            return new SideToSideCabinetReadResult
            {
                CabinetHeight = ReadGlobal(bodyModel, "柜高"),
                CabinetDepth = ReadGlobal(bodyModel, cabinetName + "深"),
                CabinetWidth = ReadGlobal(bodyModel, "柜宽"),
                GroundHeight = ReadDimension(
                    _rootModel,
                    SideToSideCabinetGenerationPlan.GetCabinetGroundHeightDimensionName(cabinetName)),
                FrontHole = ReadHole(
                    bodyModel,
                    subAssemblyModel,
                    cabinetName,
                    isFront: true,
                    openingMode),
                BackHole = ReadHole(
                    bodyModel,
                    subAssemblyModel,
                    cabinetName,
                    isFront: false,
                    openingMode)
            };
        }

        public SideToSideBaseReadResult ReadBase(
            string positionName,
            SideToSideCabinetBaseKind baseKind,
            bool isACabinet)
        {
            _baseKind = baseKind;
            var modelKind = baseKind == SideToSideCabinetBaseKind.Base1
                ? SideToSideBaseModelKind.Base1
                : SideToSideBaseModelKind.Base2;
            var baseComponent = CabinetAssemblyDimensionReader.FindStandaloneBaseComponent(
                _rootModel,
                positionName,
                modelKind);
            var baseModel = ReferencedModelValueReader.FromComponent(baseComponent);
            if (baseModel == null)
            {
                return null;
            }

            var result = new SideToSideBaseReadResult
            {
                BaseOpeningHeight = ReadGlobal(baseModel, "基座开孔高度")
            };

            if (baseKind == SideToSideCabinetBaseKind.Base1)
            {
                result.BaseHeight = ReadGlobal(baseModel, "基座高");
                result.BaseWidth = ReadGlobal(baseModel, "基座宽");
                result.BaseDepth = ReadGlobal(baseModel, "基座深");
                var cabinetSuffix = isACabinet ? "A" : "B";
                result.InsulatorPosition1 = ReadDimension(
                    baseModel,
                    "D1@绝缘子安装坐标系草图" + cabinetSuffix);
                result.InsulatorPosition2 = ReadDimension(
                    baseModel,
                    "D2@绝缘子安装坐标系草图" + cabinetSuffix);
                result.InsulatorPosition3 = ReadDimension(
                    baseModel,
                    "D3@绝缘子安装坐标系草图" + cabinetSuffix);
                return result;
            }

            result.BaseWidth = ReadGlobal(baseModel, "基座宽");
            // 基架公式必须使用基座2本体深度，不能读取增高基座宽深代替。
            result.BaseDepth = ReadGlobal(baseModel, "基座深");
            var elevatedComponent = CabinetAssemblyDimensionReader.FindStandaloneBaseComponent(
                _rootModel,
                positionName,
                SideToSideBaseModelKind.ElevatedBase);
            var elevatedModel = ReferencedModelValueReader.FromComponent(elevatedComponent);
            var basicHeight = ReadGlobal(baseModel, "基座高");
            result.ElevatedBaseHeight = ReadGlobal(elevatedModel, "高度");
            result.ElevatedBaseWidth = ReadGlobal(elevatedModel, "宽度");
            if (basicHeight.HasValue && result.ElevatedBaseHeight.HasValue)
            {
                result.BaseHeight = CabinetAssemblyDimensionReader.ComposeTotalBaseHeight(
                    basicHeight.Value,
                    result.ElevatedBaseHeight.Value);
            }
            result.InsulatorPosition = ReadDimension(elevatedModel, "D1@草图2");
            return result;
        }

        public double? ReadInsulatorHeight(IReadOnlyList<string> enabledPositionNames)
        {
            if (enabledPositionNames == null || enabledPositionNames.Count == 0) return null;
            double? actual = null;
            foreach (var position in enabledPositionNames)
            {
                if (!_baseKind.HasValue) throw new InvalidOperationException("绝缘子反读失败：尚未读取基座类型。");
                var kind = _baseKind == SideToSideCabinetBaseKind.Base1
                    ? SideToSideBaseModelKind.Base1 : SideToSideBaseModelKind.Base2;
                if (!CabinetAssemblyDimensionReader.TryReadInsulatorHeightFromStandaloneBase(
                        _rootModel, position, kind, out var height))
                    throw new InvalidOperationException("侧对侧绝缘子高度反读失败：" + position);
                if (actual.HasValue && Math.Abs(actual.Value - height) > .01d)
                    throw new InvalidOperationException("侧对侧各已启用基座的绝缘子实际高度不一致。");
                actual = height;
            }
            return actual;
        }

        private static SideToSideCabinetHoleReadResult ReadHole(
            ReferencedModelValueReader bodyModel,
            ReferencedModelValueReader subAssemblyModel,
            string cabinetName,
            bool isFront,
            SideToSideCabinetTopOpeningMode openingMode)
        {
            var plan = SideToSideCabinetGenerationPlan.BuildCabinetHoleDrivePlan(
                cabinetName,
                new SideToSideCabinetHoleParameters(),
                isFront,
                openingMode);
            var enabled = ReadFeatureEnabled(bodyModel, plan.FeatureName);
            if (!enabled.HasValue) throw new InvalidOperationException("柜体开孔启用状态无法反读：" + plan.FeatureName);
            var values = CabinetHoleModelContract.ForSideToSide(isFront).Read(enabled.Value,
                    bodyModel.Dimension, subAssemblyModel.Dimension);
            return new SideToSideCabinetHoleReadResult
            {
                IsEnabled = values.IsEnabled,
                ToCabinetEdgeDistance = values.EdgeDistance,
                BusbarCenterToCabinetEdgeDistance = values.CenterDistance,
                FixedHoleSpacing = values.FixedSpacing,
                L1 = values.L1,
                L2 = values.L2,
                L3 = values.L3
            };
        }

        internal static string ResolveComponentConfigurationName(
            string referencedConfiguration,
            string activeConfiguration)
        {
            return ExtensionBusbarModelReadHelper.ResolveComponentConfigurationName(
                referencedConfiguration,
                activeConfiguration);
        }



        private static double? ReadGlobal(IModelDoc2 model, string name) =>
            new ReferencedModelValueReader(model).Global(name);

        private static double? ReadGlobal(ReferencedModelValueReader model, string name) =>
            model.Global(name);

        private static double? ReadDimension(IModelDoc2 model, string name) =>
            new ReferencedModelValueReader(model).Dimension(name);

        private static double? ReadDimension(ReferencedModelValueReader model, string name) =>
            model.Dimension(name);

        private static bool? ReadFeatureEnabled(ReferencedModelValueReader model, string featureName) =>
            !model.Suppressed(featureName);

        private static Feature FindFeatureByName(IModelDoc2 model, string featureName)
        {
            var featureObject = model?.FirstFeature();
            while (featureObject != null)
            {
                var feature = featureObject as Feature;
                var matchedFeature = FindFeatureByName(feature, featureName);
                if (matchedFeature != null)
                {
                    return matchedFeature;
                }

                featureObject = feature?.GetNextFeature();
            }

            return null;
        }

        private static Feature FindFeatureByName(Feature feature, string featureName)
        {
            if (feature == null)
            {
                return null;
            }
            if (string.Equals(feature.Name, featureName, StringComparison.OrdinalIgnoreCase))
            {
                return feature;
            }

            var childObject = feature.GetFirstSubFeature();
            while (childObject != null)
            {
                var childFeature = childObject as Feature;
                var matchedFeature = FindFeatureByName(childFeature, featureName);
                if (matchedFeature != null)
                {
                    return matchedFeature;
                }

                childObject = childFeature?.GetNextSubFeature();
            }

            return null;
        }
    }
}
