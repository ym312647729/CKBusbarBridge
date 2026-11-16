using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 一棵普通或增高基座完整模型树的规范身份。
    /// </summary>
    internal sealed class FaceToFaceBaseModelIdentity : IEquatable<FaceToFaceBaseModelIdentity>
    {
        private FaceToFaceBaseModelIdentity(
            string value,
            string baseValue,
            bool isTemplateDefault)
        {
            Value = value;
            BaseValue = baseValue;
            IsTemplateDefault = isTemplateDefault;
            SharedIdentity = BaseModelIdentity.FromCanonical(
                "FaceToFaceCompatible",
                value.Split('|')[0],
                value,
                baseValue,
                isTemplateDefault);
        }

        internal string Value { get; }

        /// <summary>
        /// 仅包含基座几何和结构参数，不含宿主绝缘子；用于局部驱动时避免重复驱动未修改基座。
        /// </summary>
        internal string BaseValue { get; }

        internal bool IsTemplateDefault { get; }

        internal BaseModelIdentity SharedIdentity { get; }

        internal static FaceToFaceBaseModelIdentity FromPlan(
            FaceToFaceBaseModelPlan model,
            FaceToFaceBaseInsulatorPlan hostedInsulator)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            if (hostedInsulator != null && hostedInsulator.TargetBaseKind != model.Kind)
            {
                throw new ArgumentException("绝缘子宿主类型与基座模型类型不一致。", nameof(hostedInsulator));
            }

            var baseParts = new List<string>
            {
                model.Kind.ToString(),
                Format(model.Height),
                Format(model.Width),
                Format(model.Depth),
                Format(model.L1),
                Format(model.L2),
                Format(model.L3),
                ResolveOpeningHeight(model),
                "HasElevatedBase=" + (model.HasElevatedBase.HasValue
                    ? model.HasElevatedBase.Value.ToString()
                    : "NA")
            };
            var parts = new List<string>(baseParts);

            if (hostedInsulator == null)
            {
                parts.AddRange(Enumerable.Repeat("NA", 9));
            }
            else
            {
                // 绝缘子会改变宿主基座的尺寸与装配引用树；即使基座板尺寸相同，
                // 宿主绝缘子不同的两个基座也绝不能复用同一棵装配树。
                parts.Add(hostedInsulator.TargetBaseKind.ToString());
                parts.Add(hostedInsulator.PlacementDimensionName);
                parts.Add(Format(hostedInsulator.PlacementHeight));
                parts.Add(Format(GetGlobalDouble(hostedInsulator, "排厚")));
                parts.Add(Format(GetGlobalDouble(hostedInsulator, "相间距")));
                parts.Add(Format(GetGlobalDouble(hostedInsulator, "绝缘子高度")));
                parts.Add(Format(GetGlobalDouble(hostedInsulator, "支架长度")));
                parts.Add(hostedInsulator.SinglePhaseRootCount.ToString(CultureInfo.InvariantCulture));
                parts.Add(hostedInsulator.BusbarSpec);
            }

            return new FaceToFaceBaseModelIdentity(
                string.Join("|", parts),
                string.Join("|", baseParts),
                model.IsTemplateDefault);
        }

        public bool Equals(FaceToFaceBaseModelIdentity other)
        {
            return other != null
                && string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as FaceToFaceBaseModelIdentity);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(Value);
        }

        public override string ToString()
        {
            return Value;
        }

        private static string ResolveOpeningHeight(FaceToFaceBaseModelPlan model)
        {
            object value;
            return model.Kind == FaceToFaceBaseModelKind.Base
                && model.GlobalVariables.TryGetValue(
                    CabinetHoleParameters.BaseOpeningHeightVariableName,
                    out value)
                ? Format(Convert.ToDouble(value, CultureInfo.InvariantCulture))
                : "NA";
        }

        private static double GetGlobalDouble(
            FaceToFaceBaseInsulatorPlan plan,
            string name)
        {
            object value;
            if (!plan.GlobalVariables.TryGetValue(name, out value))
            {
                throw new ArgumentException($"绝缘子计划缺少全局变量：{name}", nameof(plan));
            }

            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static string Format(double value)
        {
            return Math.Round(value, 3, MidpointRounding.AwayFromZero)
                .ToString("0.000", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 物理模型名称相对模板名称的当前状态。
    /// </summary>
    internal sealed class FaceToFaceBaseModelNameState
    {
        internal FaceToFaceBaseModelNameState(
            string templateAssemblyBaseName,
            string currentAssemblyBaseName,
            string currentDrawingNumberSuffix)
        {
            TemplateAssemblyBaseName = RequireText(
                templateAssemblyBaseName,
                nameof(templateAssemblyBaseName));
            CurrentAssemblyBaseName = RequireText(
                currentAssemblyBaseName,
                nameof(currentAssemblyBaseName));
            CurrentDrawingNumberSuffix = currentDrawingNumberSuffix ?? string.Empty;
        }

        internal string TemplateAssemblyBaseName { get; }

        internal string CurrentAssemblyBaseName { get; }

        internal string CurrentDrawingNumberSuffix { get; }

        internal bool HasDrawingNumberSuffix =>
            !string.IsNullOrWhiteSpace(CurrentDrawingNumberSuffix);

        private static string RequireText(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("模型名称不能为空。", parameterName);
            }

            return value;
        }
    }

    /// <summary>
    /// 总装中一个位置、一个基座类型所对应的实际组件与文件引用。
    /// </summary>
    internal sealed class FaceToFaceBasePhysicalModelSnapshot
    {
        internal FaceToFaceBasePhysicalModelSnapshot(
            string positionName,
            FaceToFaceBaseModelKind kind,
            string componentFullName,
            string modelPath,
            string insulatorPath,
            FaceToFaceBaseModelIdentity identity,
            FaceToFaceBaseModelNameState nameState,
            FaceToFaceBaseConnectionKind connectionKind)
        {
            PositionName = RequireText(positionName, nameof(positionName));
            Kind = kind;
            ComponentFullName = RequireText(componentFullName, nameof(componentFullName));
            ModelPath = NormalizeRequiredPath(modelPath, nameof(modelPath));
            InsulatorPath = string.IsNullOrWhiteSpace(insulatorPath)
                ? null
                : Path.GetFullPath(insulatorPath);
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            NameState = nameState ?? throw new ArgumentNullException(nameof(nameState));
            ConnectionKind = connectionKind;
        }

        internal string PositionName { get; }

        internal FaceToFaceBaseModelKind Kind { get; }

        internal string ComponentFullName { get; }

        internal string ModelPath { get; }

        internal string InsulatorPath { get; }

        internal FaceToFaceBaseModelIdentity Identity { get; }

        internal FaceToFaceBaseModelNameState NameState { get; }

        /// <summary>
        /// 快照时已从实际 Mate 端点确认的安装方式。
        /// </summary>
        internal FaceToFaceBaseConnectionKind ConnectionKind { get; }

        private static string NormalizeRequiredPath(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("模型路径不能为空。", parameterName);
            }

            return Path.GetFullPath(value);
        }

        private static string RequireText(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("文本值不能为空。", parameterName);
            }

            return value;
        }
    }

    internal enum FaceToFaceBaseConnectionKind
    {
        Root,
        StackedOnElevatedBase
    }

    /// <summary>
    /// 参数旧值与总装实际物理引用必须作为一个整体快照推进。
    /// </summary>
    internal sealed class FaceToFaceBaseSecondarySnapshot
    {
        internal FaceToFaceBaseSecondarySnapshot(
            CabinetDriverParameters parameters,
            IEnumerable<FaceToFaceBasePhysicalModelSnapshot> models)
            : this(CabinetDriverParameterSnapshot.Capture(parameters), models)
        {
        }

        internal FaceToFaceBaseSecondarySnapshot(
            CabinetDriverParameterSnapshot parameters,
            IEnumerable<FaceToFaceBasePhysicalModelSnapshot> models)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            if (models == null)
            {
                throw new ArgumentNullException(nameof(models));
            }

            var copiedModels = models.ToArray();
            var duplicate = copiedModels
                .GroupBy(x => new { x.PositionName, x.Kind })
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                throw new ArgumentException(
                    $"物理快照存在重复位置：{duplicate.Key.PositionName}/{duplicate.Key.Kind}",
                    nameof(models));
            }

            Parameters = CopyParameters(parameters);
            Models = Array.AsReadOnly(copiedModels);
        }

        internal CabinetDriverParameterSnapshot Parameters { get; }

        internal IReadOnlyList<FaceToFaceBasePhysicalModelSnapshot> Models { get; }

        private static CabinetDriverParameterSnapshot CopyParameters(
            CabinetDriverParameterSnapshot source)
        {
            return new CabinetDriverParameterSnapshot(
                new CabinetGeneralParameterSnapshot
                {
                    FloorHeight = source.General.FloorHeight,
                    CabinetSpacing = source.General.CabinetSpacing,
                    CabinetEccentricity = source.General.CabinetEccentricity,
                    ParameterB = source.General.ParameterB,
                    PhaseSpacing = source.General.PhaseSpacing,
                    InsulationDistance = source.General.InsulationDistance,
                    BusbarSpec = source.General.BusbarSpec,
                    SinglePhaseRootCount = source.General.SinglePhaseRootCount,
                    ACabinetExposedBusbarPhaseSequence =
                        source.General.ACabinetExposedBusbarPhaseSequence,
                    BCabinetExposedBusbarPhaseSequence =
                        source.General.BCabinetExposedBusbarPhaseSequence,
                    BusbarMaterial = source.General.BusbarMaterial,
                    InsulatorHeight = source.General.InsulatorHeight
                },
                CopySide(source.ACabinet),
                CopySide(source.BCabinet));
        }

        private static CabinetSideParameterSnapshot CopySide(
            CabinetSideParameterSnapshot source)
        {
            return new CabinetSideParameterSnapshot
            {
                CabinetHeight = source.CabinetHeight,
                CabinetDepth = source.CabinetDepth,
                CabinetWidth = source.CabinetWidth,
                GroundHeight = source.GroundHeight,
                FrontHole = CopyHole(source.FrontHole),
                BackHole = CopyHole(source.BackHole)
            };
        }

        private static CabinetHoleParameterSnapshot CopyHole(
            CabinetHoleParameterSnapshot source)
        {
            return new CabinetHoleParameterSnapshot
            {
                IsEnabled = source.IsEnabled,
                ToCabinetEdgeDistance = source.ToCabinetEdgeDistance,
                FixedHoleSpacing = source.FixedHoleSpacing,
                L1 = source.L1,
                L2 = source.L2,
                L3 = source.L3,
                BusbarCenterToCabinetEdgeDistance = source.BusbarCenterToCabinetEdgeDistance,
                BaseHeight = source.BaseHeight,
                ExtensionBaseHeight = source.ExtensionBaseHeight,
                BaseOpeningHeight = source.BaseOpeningHeight,
                InsulatorPosition = source.InsulatorPosition
            };
        }
    }

    internal static class FaceToFaceBaseSecondarySnapshotComparer
    {
        internal static void EnsureSamePhysicalState(
            FaceToFaceBaseSecondarySnapshot expected,
            FaceToFaceBaseSecondarySnapshot actual)
        {
            if (expected == null)
            {
                throw new ArgumentNullException(nameof(expected));
            }

            if (actual == null)
            {
                throw new ArgumentNullException(nameof(actual));
            }

            if (expected.Models.Count != actual.Models.Count)
            {
                throw new InvalidOperationException("基座物理引用数量已变化，请重新打开界面后再驱动。");
            }

            foreach (var expectedModel in expected.Models)
            {
                var actualModel = actual.Models.SingleOrDefault(x =>
                    x.Kind == expectedModel.Kind
                    && string.Equals(
                        x.PositionName,
                        expectedModel.PositionName,
                        StringComparison.Ordinal));
                if (actualModel == null
                    || !SameText(expectedModel.ComponentFullName, actualModel.ComponentFullName)
                    || !SamePath(expectedModel.ModelPath, actualModel.ModelPath)
                    || !SameOptionalPath(expectedModel.InsulatorPath, actualModel.InsulatorPath)
                    || !expectedModel.Identity.Equals(actualModel.Identity)
                    || expectedModel.ConnectionKind != actualModel.ConnectionKind
                    || !SameNameState(expectedModel.NameState, actualModel.NameState))
                {
                    throw new InvalidOperationException(
                        $"{expectedModel.PositionName}/{expectedModel.Kind} 的基座物理引用已变化，请重新打开界面后再驱动。");
                }
            }
        }

        private static bool SameNameState(
            FaceToFaceBaseModelNameState left,
            FaceToFaceBaseModelNameState right)
        {
            return SameText(left.TemplateAssemblyBaseName, right.TemplateAssemblyBaseName)
                && SameText(left.CurrentAssemblyBaseName, right.CurrentAssemblyBaseName)
                && string.Equals(
                    left.CurrentDrawingNumberSuffix,
                    right.CurrentDrawingNumberSuffix,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameOptionalPath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
            }

            return SamePath(left, right);
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameText(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal enum FaceToFaceBaseModelSourceKind
    {
        Existing,
        Template
    }

    internal enum FaceToFaceBaseNameActionKind
    {
        Keep,
        AddDrawingNumber,
        RemoveDrawingNumber
    }

    internal enum FaceToFaceBasePositionActionKind
    {
        Keep,
        Replace,
        Insert,
        Remove,
        Remate
    }

    /// <summary>
    /// 一棵最终物理基座模型的准备动作；多个位置可共同引用同一个动作结果。
    /// </summary>
    internal sealed class FaceToFaceBaseTargetModelAction
    {
        internal FaceToFaceBaseTargetModelAction(
            string targetKey,
            FaceToFaceBaseModelIdentity targetIdentity,
            FaceToFaceBaseModelSourceKind sourceKind,
            FaceToFaceBasePhysicalModelSnapshot existingModel,
            FaceToFaceBasePositionPlan targetPositionPlan,
            FaceToFaceBaseModelKind kind,
            bool needsDrive,
            FaceToFaceBaseNameActionKind nameAction,
            IEnumerable<string> positionNames)
        {
            TargetKey = string.IsNullOrWhiteSpace(targetKey)
                ? throw new ArgumentException("目标模型键不能为空。", nameof(targetKey))
                : targetKey;
            TargetIdentity = targetIdentity
                ?? throw new ArgumentNullException(nameof(targetIdentity));
            SourceKind = sourceKind;
            ExistingModel = existingModel;
            TargetPositionPlan = targetPositionPlan
                ?? throw new ArgumentNullException(nameof(targetPositionPlan));
            Kind = kind;
            NeedsDrive = needsDrive;
            NameAction = nameAction;
            PositionNames = Array.AsReadOnly((positionNames
                ?? throw new ArgumentNullException(nameof(positionNames))).ToArray());

            if (PositionNames.Count == 0)
            {
                throw new ArgumentException("目标模型至少需要一个引用位置。", nameof(positionNames));
            }

            if (sourceKind == FaceToFaceBaseModelSourceKind.Existing && existingModel == null)
            {
                throw new ArgumentException("复用已有模型时必须提供物理模型快照。", nameof(existingModel));
            }

            if (sourceKind == FaceToFaceBaseModelSourceKind.Template && existingModel != null)
            {
                throw new ArgumentException("模板生成动作不能携带已有模型。", nameof(existingModel));
            }
        }

        internal string TargetKey { get; }

        internal FaceToFaceBaseModelIdentity TargetIdentity { get; }

        internal FaceToFaceBaseModelSourceKind SourceKind { get; }

        internal FaceToFaceBasePhysicalModelSnapshot ExistingModel { get; }

        internal FaceToFaceBasePositionPlan TargetPositionPlan { get; }

        internal FaceToFaceBaseModelKind Kind { get; }

        internal bool NeedsDrive { get; }

        internal FaceToFaceBaseNameActionKind NameAction { get; }

        internal IReadOnlyList<string> PositionNames { get; }
    }

    internal sealed class FaceToFaceBasePositionAction
    {
        internal FaceToFaceBasePositionAction(
            string positionName,
            FaceToFaceBasePositionActionKind kind,
            FaceToFaceBasePositionPlan targetPlan,
            FaceToFaceBasePhysicalModelSnapshot oldOrdinaryBase,
            FaceToFaceBasePhysicalModelSnapshot oldElevatedBase,
            string targetOrdinaryModelKey,
            string targetElevatedModelKey)
        {
            PositionName = string.IsNullOrWhiteSpace(positionName)
                ? throw new ArgumentException("位置名称不能为空。", nameof(positionName))
                : positionName;
            Kind = kind;
            TargetPlan = targetPlan;
            OldOrdinaryBase = oldOrdinaryBase;
            OldElevatedBase = oldElevatedBase;
            TargetOrdinaryModelKey = targetOrdinaryModelKey;
            TargetElevatedModelKey = targetElevatedModelKey;
        }

        internal string PositionName { get; }

        internal FaceToFaceBasePositionActionKind Kind { get; }

        internal FaceToFaceBasePositionPlan TargetPlan { get; }

        internal FaceToFaceBasePhysicalModelSnapshot OldOrdinaryBase { get; }

        internal FaceToFaceBasePhysicalModelSnapshot OldElevatedBase { get; }

        internal string TargetOrdinaryModelKey { get; }

        internal string TargetElevatedModelKey { get; }
    }

    internal sealed class FaceToFaceBaseRetirementCandidate
    {
        internal FaceToFaceBaseRetirementCandidate(
            string modelPath,
            IEnumerable<string> obsoleteFiles)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                throw new ArgumentException("待退役模型路径不能为空。", nameof(modelPath));
            }

            ModelPath = Path.GetFullPath(modelPath);
            ModelDirectory = Path.GetDirectoryName(ModelPath);
            ObsoleteFiles = Array.AsReadOnly((obsoleteFiles ?? Enumerable.Empty<string>())
                .Select(Path.GetFullPath)
                .ToArray());
        }

        internal string ModelPath { get; }

        internal string ModelDirectory { get; }

        internal IReadOnlyList<string> ObsoleteFiles { get; }
    }

    internal sealed class FaceToFaceBaseSecondaryDrivePlan
    {
        internal FaceToFaceBaseSecondaryDrivePlan(
            IEnumerable<FaceToFaceBaseTargetModelAction> modelActions,
            IEnumerable<FaceToFaceBasePositionAction> positionActions,
            IEnumerable<FaceToFaceBaseRetirementCandidate> retirementCandidates)
        {
            ModelActions = Array.AsReadOnly((modelActions
                ?? throw new ArgumentNullException(nameof(modelActions))).ToArray());
            PositionActions = Array.AsReadOnly((positionActions
                ?? throw new ArgumentNullException(nameof(positionActions))).ToArray());
            RetirementCandidates = Array.AsReadOnly((retirementCandidates
                ?? throw new ArgumentNullException(nameof(retirementCandidates))).ToArray());
        }

        internal IReadOnlyList<FaceToFaceBaseTargetModelAction> ModelActions { get; }

        internal IReadOnlyList<FaceToFaceBasePositionAction> PositionActions { get; }

        internal IReadOnlyList<FaceToFaceBaseRetirementCandidate> RetirementCandidates { get; }

        internal bool IsEmpty =>
            ModelActions.All(x =>
                x.SourceKind == FaceToFaceBaseModelSourceKind.Existing
                && !x.NeedsDrive
                && x.NameAction == FaceToFaceBaseNameActionKind.Keep)
            && PositionActions.All(x => x.Kind == FaceToFaceBasePositionActionKind.Keep)
            && RetirementCandidates.Count == 0;
    }

    /// <summary>
    /// 一次面对面基座二次生成完成后的统计与新快照。
    /// 只有总装验证、重建和保存均成功时，CommittedSnapshot 才代表可继续使用的新基线。
    /// </summary>
    internal sealed class FaceToFaceBaseSecondaryDriveResult
    {
        internal FaceToFaceBaseSecondaryDriveResult(
            bool hasChanges,
            int drivenModelCount,
            int createdModelCount,
            int replacedPositionCount,
            int deletedArtifactCount,
            IEnumerable<string> warnings,
            FaceToFaceBaseSecondarySnapshot committedSnapshot,
            IReadOnlyList<BaseInstallationChange> installationChanges = null)
        {
            HasChanges = hasChanges;
            DrivenModelCount = drivenModelCount;
            CreatedModelCount = createdModelCount;
            ReplacedPositionCount = replacedPositionCount;
            DeletedArtifactCount = deletedArtifactCount;
            Warnings = Array.AsReadOnly((warnings ?? Enumerable.Empty<string>()).ToArray());
            CommittedSnapshot = committedSnapshot
                ?? throw new ArgumentNullException(nameof(committedSnapshot));
            InstallationChanges = installationChanges ?? Array.Empty<BaseInstallationChange>();
        }

        internal bool HasChanges { get; }

        internal int DrivenModelCount { get; }

        internal int CreatedModelCount { get; }

        internal int ReplacedPositionCount { get; }

        internal int DeletedArtifactCount { get; }

        internal IReadOnlyList<string> Warnings { get; }

        internal FaceToFaceBaseSecondarySnapshot CommittedSnapshot { get; }

        internal IReadOnlyList<BaseInstallationChange> InstallationChanges { get; }

        internal static FaceToFaceBaseSecondaryDriveResult NoChanges(
            FaceToFaceBaseSecondarySnapshot snapshot)
        {
            return new FaceToFaceBaseSecondaryDriveResult(
                false,
                0,
                0,
                0,
                0,
                Enumerable.Empty<string>(),
                snapshot);
        }
    }
}
