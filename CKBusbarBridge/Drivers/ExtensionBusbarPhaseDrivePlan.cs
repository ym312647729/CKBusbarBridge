using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CKBusbarBridge.Drivers
{
    internal enum ExtensionBusbarDriveStage
    {
        Specification,
        PhaseSequence,
        Material,
        RootCount,
        ValidateSources
    }

    internal sealed class ExtensionBusbarDriveSelection
    {
        internal ExtensionBusbarDriveSelection(
            bool driveDimensions, bool driveACabinetPhaseSequence,
            bool driveBCabinetPhaseSequence, bool driveMaterial)
        {
            DriveDimensions = driveDimensions;
            DriveACabinetPhaseSequence = driveACabinetPhaseSequence;
            DriveBCabinetPhaseSequence = driveBCabinetPhaseSequence;
            DriveMaterial = driveMaterial;
        }

        internal bool DriveDimensions { get; }
        internal bool DriveACabinetPhaseSequence { get; }
        internal bool DriveBCabinetPhaseSequence { get; }
        internal bool DriveMaterial { get; }
    }

    /// <summary>
    /// 柜内伸出排相序驱动的纯规划结果。
    /// </summary>
    internal sealed class ExtensionBusbarPhaseDrivePlan
    {
        internal static void ValidateSourcePhaseCounts(
            string assemblyName, int aCount, int bCount, int cCount)
        {
            if (aCount != 1 || bCount != 1 || cCount != 1)
                throw new InvalidOperationException(string.Format(
                    "伸出排装配体“{0}”各相必须有1个未压缩源排；实际 A相={1}、B相={2}、C相={3}。已停止正式驱动。",
                    assemblyName, aCount, bCount, cCount));
        }

        internal static void ValidatePatternQuantity(int patternQuantity)
        {
            if (patternQuantity < 1 || patternQuantity > 3)
                throw new ArgumentOutOfRangeException(nameof(patternQuantity),
                    "伸出排每相根数必须为1到3，对应单相根数2到4。");
        }

        internal static void ValidateDrivenPhaseRootCounts(
            string assemblyName, int expectedCount, int aCount, int bCount, int cCount)
        {
            ValidatePatternQuantity(expectedCount);
            if (aCount != expectedCount || bCount != expectedCount || cCount != expectedCount)
                throw new InvalidOperationException(string.Format(
                    "伸出排装配体“{0}”根数驱动校验失败：期望每相{1}根；实际 A相={2}、B相={3}、C相={4}。",
                    assemblyName, expectedCount, aCount, bCount, cCount));
        }

        private static readonly Regex SourcePartPhasePattern = new Regex(
            @"^(?:[AB]柜内伸出排|柜内伸出排)-([ABC])-(?:[AB]柜|柜)(?:前|后)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly IReadOnlyList<ExtensionBusbarDriveStage> OrderedDriveStages =
            new[]
            {
                ExtensionBusbarDriveStage.ValidateSources,
                ExtensionBusbarDriveStage.PhaseSequence,
                ExtensionBusbarDriveStage.Specification,
                ExtensionBusbarDriveStage.Material,
                ExtensionBusbarDriveStage.RootCount
            };

        private ExtensionBusbarPhaseDrivePlan(
            string cabinetCode,
            string currentSequence,
            string targetSequence,
            bool shouldSwap,
            IReadOnlyList<ExtensionBusbarPhasePartUpdate> partUpdates)
        {
            CabinetCode = cabinetCode;
            CurrentSequence = currentSequence;
            TargetSequence = targetSequence;
            ShouldSwap = shouldSwap;
            PartUpdates = partUpdates;
        }

        public string CabinetCode { get; }

        public string CurrentSequence { get; }

        public string TargetSequence { get; }

        public bool ShouldSwap { get; }

        public IReadOnlyList<ExtensionBusbarPhasePartUpdate> PartUpdates { get; }

        public static IReadOnlyList<ExtensionBusbarDriveStage> DriveStages => OrderedDriveStages;

        internal static IReadOnlyList<ExtensionBusbarDriveStage> ResolveDriveStages(
            string assemblyName, ExtensionBusbarDriveSelection selection)
        {
            if (selection == null) return DriveStages;

            var drivePhaseSequence = string.Equals(ResolveCabinetCode(assemblyName), "A", StringComparison.Ordinal)
                ? selection.DriveACabinetPhaseSequence
                : selection.DriveBCabinetPhaseSequence;
            var stages = new List<ExtensionBusbarDriveStage> { ExtensionBusbarDriveStage.ValidateSources };
            if (drivePhaseSequence) stages.Add(ExtensionBusbarDriveStage.PhaseSequence);
            if (selection.DriveDimensions) stages.Add(ExtensionBusbarDriveStage.Specification);
            if (selection.DriveMaterial) stages.Add(ExtensionBusbarDriveStage.Material);
            // 根数最终收敛必须独立于脏字段判断，旧项目的变量与实际阵列可能已经不一致。
            stages.Add(ExtensionBusbarDriveStage.RootCount);
            return stages;
        }

        public static ExtensionBusbarPhaseDrivePlan Build(
            string assemblyName,
            string storedSequence,
            string targetSequence)
        {
            var cabinetCode = ResolveCabinetCode(assemblyName);
            var currentSequence = string.IsNullOrWhiteSpace(storedSequence)
                ? ResolveTemplateDefaultSequence(cabinetCode)
                : storedSequence.Trim().ToUpperInvariant();
            if (!IsSupportedSequence(currentSequence))
            {
                throw new InvalidOperationException(
                    "伸出排装配体记录的当前相序不是 ABC 或 CBA，无法安全交换。");
            }

            var normalizedTarget = string.IsNullOrWhiteSpace(targetSequence)
                ? string.Empty
                : targetSequence.Trim().ToUpperInvariant();
            if (!IsSupportedSequence(normalizedTarget))
            {
                throw new ArgumentException(
                    "伸出排目标相序只支持 ABC 或 CBA。",
                    nameof(targetSequence));
            }

            var shouldSwap = !string.Equals(
                currentSequence,
                normalizedTarget,
                StringComparison.Ordinal);
            var partUpdates = shouldSwap
                ? BuildPartUpdates()
                : new List<ExtensionBusbarPhasePartUpdate>();

            return new ExtensionBusbarPhaseDrivePlan(
                cabinetCode,
                currentSequence,
                normalizedTarget,
                shouldSwap,
                partUpdates);
        }

        public static string ResolveTargetSequence(
            string assemblyName,
            string aCabinetTargetSequence,
            string bCabinetTargetSequence)
        {
            return string.Equals(
                ResolveCabinetCode(assemblyName),
                "A",
                StringComparison.Ordinal)
                ? aCabinetTargetSequence
                : bCabinetTargetSequence;
        }

        public static string ResolveSourcePhase(string sourcePartName)
        {
            var match = SourcePartPhasePattern.Match(sourcePartName ?? string.Empty);
            if (!match.Success)
            {
                throw new InvalidOperationException("无法根据伸出排源排子件名称识别相序。");
            }

            return match.Groups[1].Value.ToUpperInvariant();
        }

        internal static void ExecuteReferenceSwap(
            Action replaceAWithC,
            Action replaceCWithA,
            Action rollbackAToA,
            Action rollbackCToC,
            Action writeTargetSequence)
        {
            if (replaceAWithC == null) throw new ArgumentNullException(nameof(replaceAWithC));
            if (replaceCWithA == null) throw new ArgumentNullException(nameof(replaceCWithA));
            if (rollbackAToA == null) throw new ArgumentNullException(nameof(rollbackAToA));
            if (rollbackCToC == null) throw new ArgumentNullException(nameof(rollbackCToC));
            if (writeTargetSequence == null) throw new ArgumentNullException(nameof(writeTargetSequence));

            var aReferenceReplaced = false;
            var cReferenceReplaced = false;
            try
            {
                replaceAWithC();
                aReferenceReplaced = true;
                replaceCWithA();
                cReferenceReplaced = true;
                writeTargetSequence();
            }
            catch (Exception driveException)
            {
                var rollbackExceptions = new List<Exception>();
                if (cReferenceReplaced)
                {
                    TryRollbackReference(rollbackCToC, rollbackExceptions);
                }

                if (aReferenceReplaced)
                {
                    TryRollbackReference(rollbackAToA, rollbackExceptions);
                }

                if (rollbackExceptions.Count > 0)
                {
                    rollbackExceptions.Insert(0, driveException);
                    throw new AggregateException(
                        "伸出排 A/C 相引用交换失败，且恢复原引用时发生错误。",
                        rollbackExceptions);
                }

                throw;
            }
        }

        private static string ResolveCabinetCode(string assemblyName)
        {
            if (!string.IsNullOrWhiteSpace(assemblyName) &&
                assemblyName.TrimStart().StartsWith("A柜内伸出排-", StringComparison.OrdinalIgnoreCase))
            {
                return "A";
            }

            if (!string.IsNullOrWhiteSpace(assemblyName) &&
                assemblyName.TrimStart().StartsWith("B柜内伸出排-", StringComparison.OrdinalIgnoreCase))
            {
                return "B";
            }

            throw new InvalidOperationException("无法根据伸出排装配体名称识别所属柜体。");
        }

        private static bool IsSupportedSequence(string sequence)
        {
            return string.Equals(sequence, "ABC", StringComparison.Ordinal) ||
                   string.Equals(sequence, "CBA", StringComparison.Ordinal);
        }

        private static string ResolveTemplateDefaultSequence(string cabinetCode)
        {
            return string.Equals(cabinetCode, "A", StringComparison.Ordinal)
                ? "CBA"
                : "ABC";
        }

        private static IReadOnlyList<ExtensionBusbarPhasePartUpdate> BuildPartUpdates()
        {
            return new List<ExtensionBusbarPhasePartUpdate>
            {
                new ExtensionBusbarPhasePartUpdate("A", "C相"),
                new ExtensionBusbarPhasePartUpdate("C", "A相")
            };
        }

        private static void TryRollbackReference(
            Action rollbackReference,
            ICollection<Exception> rollbackExceptions)
        {
            try
            {
                rollbackReference();
            }
            catch (Exception rollbackException)
            {
                rollbackExceptions.Add(rollbackException);
            }
        }

    }

    internal sealed class ExtensionBusbarPhasePartUpdate
    {
        internal ExtensionBusbarPhasePartUpdate(
            string sourcePhase,
            string targetBusbarPhase)
        {
            SourcePhase = sourcePhase;
            TargetBusbarPhase = targetBusbarPhase;
        }

        public string SourcePhase { get; }

        public string TargetBusbarPhase { get; }
    }
}
