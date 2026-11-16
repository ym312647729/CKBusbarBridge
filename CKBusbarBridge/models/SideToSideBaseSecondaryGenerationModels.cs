using CKBusbarBridge.Drivers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Models
{
    internal sealed class SideToSideBasePhysicalModelSnapshot
    {
        internal SideToSideBasePhysicalModelSnapshot(
            string positionName,
            SideToSideBaseModelKind kind,
            string componentFullName,
            string modelPath,
            IEnumerable<string> insulatorPaths,
            BaseModelIdentity identity,
            BaseModelNameState nameState,
            IEnumerable<string> obsoleteFiles)
        {
            PositionName = RequireText(positionName, nameof(positionName));
            Kind = kind;
            ComponentFullName = RequireText(
                componentFullName,
                nameof(componentFullName));
            ModelPath = NormalizePath(modelPath, nameof(modelPath));
            InsulatorPaths = Array.AsReadOnly((insulatorPaths
                ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            NameState = nameState ?? throw new ArgumentNullException(nameof(nameState));
            ObsoleteFiles = Array.AsReadOnly((obsoleteFiles
                ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }

        internal string PositionName { get; }
        internal SideToSideBaseModelKind Kind { get; }
        internal string ComponentFullName { get; }
        internal string ModelPath { get; }
        internal IReadOnlyList<string> InsulatorPaths { get; }
        internal BaseModelIdentity Identity { get; }
        internal BaseModelNameState NameState { get; }
        internal IReadOnlyList<string> ObsoleteFiles { get; }

        internal BasePhysicalModel ToPhysicalModel()
        {
            return new BasePhysicalModel(
                PositionName,
                Kind.ToString(),
                ModelPath,
                Identity,
                NameState,
                this,
                ObsoleteFiles);
        }

        private static string NormalizePath(string value, string parameterName)
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
                throw new ArgumentException("文本不能为空。", parameterName);
            }

            return value;
        }
    }

    internal sealed class SideToSideBaseSecondarySnapshot
    {
        internal SideToSideBaseSecondarySnapshot(
            SideToSideCabinetParameterSnapshot parameters,
            IEnumerable<SideToSideBasePhysicalModelSnapshot> models)
        {
            Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            if (models == null)
            {
                throw new ArgumentNullException(nameof(models));
            }

            var copied = models.ToArray();
            var duplicate = copied
                .GroupBy(model => model.PositionName + "|" + model.Kind)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                throw new ArgumentException(
                    "基座物理快照存在重复位置和类型：" + duplicate.Key,
                    nameof(models));
            }

            Models = Array.AsReadOnly(copied);
        }

        internal SideToSideCabinetParameterSnapshot Parameters { get; }
        internal IReadOnlyList<SideToSideBasePhysicalModelSnapshot> Models { get; }
    }

    internal enum SideToSideBasePositionActionKind
    {
        Keep,
        Insert,
        Replace,
        Remove
    }

    internal sealed class SideToSideBasePositionAction
    {
        internal SideToSideBasePositionAction(
            string positionName,
            SideToSideBasePositionActionKind kind,
            SideToSideBasePositionPlan targetPlan,
            IEnumerable<SideToSideBasePhysicalModelSnapshot> oldModels,
            IDictionary<SideToSideBaseModelKind, string> targetModelKeys)
        {
            PositionName = string.IsNullOrWhiteSpace(positionName)
                ? throw new ArgumentException("位置名称不能为空。", nameof(positionName))
                : positionName;
            Kind = kind;
            TargetPlan = targetPlan;
            OldModels = Array.AsReadOnly((oldModels
                ?? Enumerable.Empty<SideToSideBasePhysicalModelSnapshot>()).ToArray());
            TargetModelKeys = new ReadOnlyDictionary<SideToSideBaseModelKind, string>(
                new Dictionary<SideToSideBaseModelKind, string>(
                    targetModelKeys
                    ?? new Dictionary<SideToSideBaseModelKind, string>()));
        }

        internal string PositionName { get; }
        internal SideToSideBasePositionActionKind Kind { get; }
        internal SideToSideBasePositionPlan TargetPlan { get; }
        internal IReadOnlyList<SideToSideBasePhysicalModelSnapshot> OldModels { get; }
        internal IReadOnlyDictionary<SideToSideBaseModelKind, string> TargetModelKeys { get; }
    }

    internal sealed class SideToSideBaseSecondaryDrivePlan
    {
        internal SideToSideBaseSecondaryDrivePlan(
            BaseModelReusePlan modelReusePlan,
            IEnumerable<SideToSideBasePositionAction> positionActions)
        {
            ModelReusePlan = modelReusePlan
                ?? throw new ArgumentNullException(nameof(modelReusePlan));
            PositionActions = Array.AsReadOnly((positionActions
                ?? throw new ArgumentNullException(nameof(positionActions))).ToArray());
        }

        internal BaseModelReusePlan ModelReusePlan { get; }
        internal IReadOnlyList<SideToSideBasePositionAction> PositionActions { get; }

        internal bool IsEmpty => ModelReusePlan.ModelActions.All(action =>
                action.SourceKind == BaseModelSourceKind.Existing
                && !action.NeedsDrive
                && action.NameAction == BaseModelNameActionKind.Keep)
            && ModelReusePlan.Retirements.Count == 0
            && PositionActions.All(action =>
                action.Kind == SideToSideBasePositionActionKind.Keep);
    }
}
