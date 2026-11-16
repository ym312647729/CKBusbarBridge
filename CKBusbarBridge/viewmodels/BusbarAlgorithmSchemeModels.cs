using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.Services;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.viewmodels
{
    /// <summary>
    /// 母排方案生成界面中的可编辑参数。字符串字段保留用户原始输入，
    /// 在点击“生成方案”时统一校验并转换为 Worker v7 请求参数。
    /// </summary>
    public sealed class BusbarAlgorithmParameterInput : NotificationObject
    {
        private string _minimumSegmentLengthText = "40";
        private string _bendingRadiusText = "10";
        private string _bendAllowanceText = "20";
        private string _insulationClearanceText = "125";
        private bool _useExplicitContainmentBox = true;
        private bool _swapPhaseAAndC;

        public string MinimumSegmentLengthText
        {
            get => _minimumSegmentLengthText;
            set
            {
                if (string.Equals(
                    _minimumSegmentLengthText,
                    value,
                    StringComparison.Ordinal))
                {
                    return;
                }

                _minimumSegmentLengthText = value;
                RaisePropertyChanged(nameof(MinimumSegmentLengthText));
            }
        }

        public string BendingRadiusText
        {
            get => _bendingRadiusText;
            set
            {
                if (string.Equals(
                    _bendingRadiusText,
                    value,
                    StringComparison.Ordinal))
                {
                    return;
                }

                _bendingRadiusText = value;
                RaisePropertyChanged(nameof(BendingRadiusText));
            }
        }

        public string BendAllowanceText
        {
            get => _bendAllowanceText;
            set
            {
                if (string.Equals(
                    _bendAllowanceText,
                    value,
                    StringComparison.Ordinal))
                {
                    return;
                }

                _bendAllowanceText = value;
                RaisePropertyChanged(nameof(BendAllowanceText));
            }
        }

        public string InsulationClearanceText
        {
            get => _insulationClearanceText;
            set
            {
                if (string.Equals(
                    _insulationClearanceText,
                    value,
                    StringComparison.Ordinal))
                {
                    return;
                }

                _insulationClearanceText = value;
                RaisePropertyChanged(nameof(InsulationClearanceText));
            }
        }

        public bool UseExplicitContainmentBox
        {
            get => _useExplicitContainmentBox;
            set
            {
                if (_useExplicitContainmentBox == value)
                {
                    return;
                }

                _useExplicitContainmentBox = value;
                RaisePropertyChanged(nameof(UseExplicitContainmentBox));
            }
        }

        public bool SwapPhaseAAndC
        {
            get => _swapPhaseAAndC;
            set
            {
                if (_swapPhaseAAndC == value)
                {
                    return;
                }

                _swapPhaseAAndC = value;
                RaisePropertyChanged(nameof(SwapPhaseAAndC));
            }
        }

        internal bool TryCreateOrdinaryPreviewBendParameters(
            out double previewBendingRadius,
            out double previewBendAllowance,
            out double previewInsulationClearance,
            out string validationMessage)
        {
            previewBendingRadius = 0d;
            previewBendAllowance = 0d;
            previewInsulationClearance = 0d;
            if (!TryParseDoubleParameter(
                    BendingRadiusText,
                    "折弯半径",
                    out previewBendingRadius,
                    out validationMessage) ||
                !TryParseDoubleParameter(
                    BendAllowanceText,
                    "折弯余量",
                    out previewBendAllowance,
                    out validationMessage) ||
                !TryParseDoubleParameter(
                    InsulationClearanceText,
                    "绝缘距离",
                    out previewInsulationClearance,
                    out validationMessage))
            {
                return false;
            }

            if (previewBendingRadius <= 0d)
            {
                validationMessage = "折弯半径必须是大于 0 的有效数值。";
                return false;
            }

            if (previewBendAllowance < 0d)
            {
                validationMessage = "折弯余量必须是大于或等于 0 的有效数值。";
                return false;
            }

            if (previewInsulationClearance < 0d)
            {
                validationMessage = "绝缘距离必须是大于或等于 0 的有效数值。";
                return false;
            }

            if (previewInsulationClearance <= 0.001d)
            {
                previewInsulationClearance = FaceToFaceModelBusbarPlanner
                    .DefaultPhaseChangeInsulationClearance;
                InsulationClearanceText =
                    FaceToFaceModelBusbarPlanner
                        .DefaultPhaseChangeInsulationClearance
                        .ToString("0.###", CultureInfo.InvariantCulture);
            }

            validationMessage = null;
            return true;
        }

        internal bool TryCreateWorkerRequestOptions(
            out BusbarAlgorithmWorkerRequestOptions workerRequestOptions,
            out string validationMessage)
        {
            workerRequestOptions = null;
            validationMessage = null;

            if (!TryParseDoubleParameter(
                    MinimumSegmentLengthText,
                    "最小段长",
                    out var parsedMinimumSegmentLength,
                    out validationMessage) ||
                !TryParseDoubleParameter(
                    BendingRadiusText,
                    "折弯半径",
                    out var parsedBendingRadius,
                    out validationMessage) ||
                !TryParseDoubleParameter(
                    BendAllowanceText,
                    "折弯余量",
                    out var parsedBendAllowance,
                    out validationMessage) ||
                !TryParseDoubleParameter(
                    InsulationClearanceText,
                    "绝缘距离",
                    out var parsedInsulationClearance,
                    out validationMessage))
            {
                return false;
            }

            var normalizedWorkerRequestOptions =
                BusbarAlgorithmWorkerRequestOptions.CreateDefault();
            if (parsedInsulationClearance <= 0.001d)
            {
                parsedInsulationClearance = FaceToFaceModelBusbarPlanner
                    .DefaultPhaseChangeInsulationClearance;
                InsulationClearanceText =
                    FaceToFaceModelBusbarPlanner
                        .DefaultPhaseChangeInsulationClearance
                        .ToString("0.###", CultureInfo.InvariantCulture);
            }
            normalizedWorkerRequestOptions.MinimumSegmentLength =
                parsedMinimumSegmentLength;
            normalizedWorkerRequestOptions.BendingRadius = parsedBendingRadius;
            normalizedWorkerRequestOptions.BendAllowance = parsedBendAllowance;
            normalizedWorkerRequestOptions.InsulationClearance =
                parsedInsulationClearance;
            normalizedWorkerRequestOptions.UseExplicitContainmentBox =
                UseExplicitContainmentBox;
            normalizedWorkerRequestOptions.SwapPhaseAAndCAtEnd =
                SwapPhaseAAndC;
            if (!normalizedWorkerRequestOptions.TryValidate(out validationMessage))
            {
                return false;
            }

            workerRequestOptions = normalizedWorkerRequestOptions;
            return true;
        }

        private static bool TryParseDoubleParameter(
            string parameterText,
            string parameterDisplayName,
            out double parsedParameterValue,
            out string validationMessage)
        {
            var normalizedParameterText = parameterText?.Trim();
            if ((!double.TryParse(
                     normalizedParameterText,
                     NumberStyles.Float,
                     CultureInfo.CurrentCulture,
                     out parsedParameterValue) &&
                 !double.TryParse(
                     normalizedParameterText,
                     NumberStyles.Float,
                     CultureInfo.InvariantCulture,
                     out parsedParameterValue)) ||
                double.IsNaN(parsedParameterValue) ||
                double.IsInfinity(parsedParameterValue))
            {
                validationMessage = parameterDisplayName + "必须填写有效数值。";
                parsedParameterValue = 0;
                return false;
            }

            validationMessage = null;
            return true;
        }

    }

    public sealed class BusbarSettingsBasicInformationItem
    {
        public BusbarSettingsBasicInformationItem(
            string categoryName,
            string parameterName,
            string displayValue)
        {
            CategoryName = categoryName ?? string.Empty;
            ParameterName = parameterName ?? string.Empty;
            DisplayValue = displayValue ?? string.Empty;
        }

        public string CategoryName { get; }
        public string ParameterName { get; }
        public string DisplayValue { get; }
    }

    /// <summary>Worker 方案表的一行，保留完整方案 DTO 供实体与 Web 预览使用。</summary>
    public sealed class BusbarAlgorithmSchemeItem : NotificationObject
    {
        private int _schemeSequence;

        private BusbarAlgorithmSchemeItem()
        {
        }

        public int SchemeSequence
        {
            get => _schemeSequence;
            set
            {
                if (_schemeSequence == value)
                {
                    return;
                }

                _schemeSequence = value;
                RaisePropertyChanged(nameof(SchemeSequence));
            }
        }

        public int WorkerSchemeIndex { get; private set; }
        public string AlgorithmName { get; private set; }
        public double ActualCopperLength { get; private set; }
        public double MinimumClearance { get; private set; }
        public int PerpendicularLapCount { get; private set; }
        public string ClearanceStatusText { get; private set; }
        public string CenterlineSummary { get; private set; }
        public string SolutionKey { get; private set; }
        public LinkageRouteWorkerSolutionDto WorkerSolution { get; private set; }
        public bool IsCurrentScheme { get; internal set; }
        public string SchemeStatusText { get; internal set; }

        public static BusbarAlgorithmSchemeItem Create(
            LinkageRouteWorkerSolutionDto workerSolution)
        {
            if (workerSolution == null)
            {
                throw new ArgumentNullException(nameof(workerSolution));
            }

            var centerlinePaths =
                BusbarWorkerSolutionCenterlineExtractor.Extract(workerSolution);
            var centerlinePointCount = centerlinePaths.Sum(
                centerlinePath => centerlinePath.Points?.Count ?? 0);
            return new BusbarAlgorithmSchemeItem
            {
                WorkerSchemeIndex = workerSolution.SchemeIndex,
                AlgorithmName = ResolveAlgorithmName(workerSolution.AlgorithmKind),
                ActualCopperLength = Math.Round(
                    workerSolution.ActualCopperLength,
                    2),
                MinimumClearance = Math.Round(
                    workerSolution.MinimumClearance,
                    2),
                PerpendicularLapCount = workerSolution.PerpendicularLapCount,
                ClearanceStatusText = workerSolution.IsRelaxedClearanceSolution
                    ? string.Format(
                        "降级绝缘（请求 {0:0.##} mm / 搜索 {1:0.##} mm）",
                        workerSolution.RequestedInsulationClearance,
                        workerSolution.SearchInsulationClearance)
                    : "严格绝缘",
                CenterlineSummary = string.Format(
                    "中心线 {0} 条 / {1} 个点",
                    centerlinePaths.Count,
                    centerlinePointCount),
                SolutionKey = BuildSolutionKey(workerSolution),
                WorkerSolution = workerSolution
            };
        }

        private static string ResolveAlgorithmName(int algorithmKind)
        {
            switch (algorithmKind)
            {
                case 0:
                    return "AxisGraph";
                case 1:
                    return "TopologyBeam";
                case 2:
                    return "SweptRepair";
                default:
                    return "算法 " + algorithmKind;
            }
        }

        private static string BuildSolutionKey(
            LinkageRouteWorkerSolutionDto workerSolution)
        {
            var topologySignatureText = string.Join(
                "|",
                (workerSolution.GroupSolutions ??
                    Array.Empty<LinkageRouteWorkerGroupSolutionDto>())
                .Where(groupSolution => groupSolution != null)
                .OrderBy(groupSolution => groupSolution.GroupKey)
                .Select(groupSolution =>
                    (groupSolution.GroupKey ?? string.Empty) + ":" +
                    (groupSolution.TopologySignature ?? string.Empty)));
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0}:{1}:{2:0.####}:{3}",
                workerSolution.SchemeIndex,
                workerSolution.AlgorithmKind,
                workerSolution.ActualCopperLength,
                topologySignatureText);
        }
    }
}
