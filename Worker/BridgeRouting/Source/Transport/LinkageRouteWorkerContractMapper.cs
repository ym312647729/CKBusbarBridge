using GeometRi;
using System.Collections.Generic;
using System.Linq;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;

namespace RouteLinkBar.Algorithm.Transport
{
    /// <summary>
    /// 在算法对象与跨进程扁平 DTO 之间转换。传输层不序列化 GeometRi 的内部状态。
    /// </summary>
    public static class LinkageRouteWorkerContractMapper
    {
        public static LinkageRouteWorkerRequestDto ToWorkerRequest(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            if (routeRequest == null)
            {
                return null;
            }

            return new LinkageRouteWorkerRequestDto
            {
                Groups = MapList(routeRequest.Groups, ToWorkerGroupRequest),
                Obstacles = MapList(routeRequest.Obstacles, ToWorkerObstacle),
                DefaultBusbarWidth = routeRequest.DefaultBusbarWidth,
                DefaultBusbarThickness = routeRequest.DefaultBusbarThickness,
                DefaultStackCount = routeRequest.DefaultStackCount,
                MinimumSegmentLength = routeRequest.MinimumSegmentLength,
                BendAllowance = routeRequest.BendAllowance,
                AllowBendGeometricLimit = routeRequest.AllowBendGeometricLimit,
                AllowConstrainedBendAllowanceReduction = routeRequest.AllowConstrainedBendAllowanceReduction,
            BendingRadius = routeRequest.BendingRadius,
            InsulationClearance = routeRequest.InsulationClearance,
            LapInsulationClearance = routeRequest.LapInsulationClearance,
                AllowUShapeRoutes = routeRequest.AllowUShapeRoutes,
                EnableSchemeLibraryMatching = routeRequest.EnableSchemeLibraryMatching,
                EnableAlgorithmCalculation = routeRequest.EnableAlgorithmCalculation,
                SchemeLibraryMatchRequest = routeRequest.SchemeLibraryMatchRequest,
                AllowDoubleStartMiddleLap = routeRequest.AllowDoubleStartMiddleLap,
                RequireSingleStartMiddleLap = routeRequest.RequireSingleStartMiddleLap,
                EnableSingleStartConnectionBar = routeRequest.EnableSingleStartConnectionBar,
                AllowPartialStartPerpendicularLap =
                    routeRequest.AllowPartialStartPerpendicularLap,
                MinimizeSupportBlocks = routeRequest.MinimizeSupportBlocks,
                UseSpacerlessTerminalLap = routeRequest.UseSpacerlessTerminalLap,
                RotateSlotHole90Degrees = routeRequest.RotateSlotHole90Degrees,
                TerminalHoleEdgeDistance = routeRequest.TerminalHoleEdgeDistance,
                RequireFixedTerminalEndLap = routeRequest.RequireFixedTerminalEndLap,
                RequireAxisAlignedRoutes = routeRequest.RequireAxisAlignedRoutes,
                UseExplicitContainmentBox = routeRequest.UseExplicitContainmentBox,
                ContainmentMinimum = ToWorkerCoordinate(routeRequest.ContainmentMinimum),
                ContainmentMaximum = ToWorkerCoordinate(routeRequest.ContainmentMaximum),
                SearchOptions = ToWorkerSearchOptions(routeRequest.SearchOptions)
            };
        }

        public static LinkageMultiGroupRouteRequest FromWorkerRequest(
            LinkageRouteWorkerRequestDto workerRequest)
        {
            if (workerRequest == null)
            {
                return null;
            }

            return new LinkageMultiGroupRouteRequest
            {
                Groups = MapList(workerRequest.Groups, FromWorkerGroupRequest),
                Obstacles = MapList(workerRequest.Obstacles, FromWorkerObstacle),
                DefaultBusbarWidth = workerRequest.DefaultBusbarWidth,
                DefaultBusbarThickness = workerRequest.DefaultBusbarThickness,
                DefaultStackCount = workerRequest.DefaultStackCount,
                MinimumSegmentLength = workerRequest.MinimumSegmentLength,
                BendAllowance = workerRequest.BendAllowance,
                AllowBendGeometricLimit = workerRequest.AllowBendGeometricLimit,
                AllowConstrainedBendAllowanceReduction = workerRequest.AllowConstrainedBendAllowanceReduction,
            BendingRadius = workerRequest.BendingRadius,
            InsulationClearance = workerRequest.InsulationClearance,
            LapInsulationClearance =
                !double.IsNaN(workerRequest.LapInsulationClearance) &&
                !double.IsInfinity(workerRequest.LapInsulationClearance) &&
                workerRequest.LapInsulationClearance >= 0d
                    ? workerRequest.LapInsulationClearance
                    : 35d,
                AllowUShapeRoutes = workerRequest.AllowUShapeRoutes,
                EnableSchemeLibraryMatching = workerRequest.EnableSchemeLibraryMatching,
                EnableAlgorithmCalculation = workerRequest.EnableAlgorithmCalculation,
                SchemeLibraryMatchRequest = workerRequest.SchemeLibraryMatchRequest,
                AllowDoubleStartMiddleLap = workerRequest.AllowDoubleStartMiddleLap,
                RequireSingleStartMiddleLap = workerRequest.RequireSingleStartMiddleLap,
                EnableSingleStartConnectionBar = workerRequest.EnableSingleStartConnectionBar,
                AllowPartialStartPerpendicularLap =
                    workerRequest.AllowPartialStartPerpendicularLap,
                MinimizeSupportBlocks = workerRequest.MinimizeSupportBlocks,
                UseSpacerlessTerminalLap = workerRequest.UseSpacerlessTerminalLap,
                RotateSlotHole90Degrees = workerRequest.RotateSlotHole90Degrees,
                TerminalHoleEdgeDistance = workerRequest.TerminalHoleEdgeDistance,
                RequireFixedTerminalEndLap = workerRequest.RequireFixedTerminalEndLap,
                RequireAxisAlignedRoutes = workerRequest.RequireAxisAlignedRoutes,
                UseExplicitContainmentBox = workerRequest.UseExplicitContainmentBox,
                ContainmentMinimum = ToPoint(workerRequest.ContainmentMinimum),
                ContainmentMaximum = ToPoint(workerRequest.ContainmentMaximum),
                SearchOptions = FromWorkerSearchOptions(workerRequest.SearchOptions)
            };
        }

        public static LinkageRouteWorkerProgressDto ToWorkerProgress(
            LinkageRouteProgress routeProgress)
        {
            if (routeProgress == null)
            {
                return null;
            }

            return new LinkageRouteWorkerProgressDto
            {
                AlgorithmKind = (int)routeProgress.AlgorithmKind,
                ElapsedMilliseconds = routeProgress.ElapsedMilliseconds,
                ProcessedStateCount = routeProgress.ProcessedStateCount,
                Message = routeProgress.Message
            };
        }

        public static LinkageRouteProgress FromWorkerProgress(
            LinkageRouteWorkerProgressDto workerProgress)
        {
            if (workerProgress == null)
            {
                return null;
            }

            return new LinkageRouteProgress
            {
                AlgorithmKind = (LinkageRouteAlgorithmKind)workerProgress.AlgorithmKind,
                ElapsedMilliseconds = workerProgress.ElapsedMilliseconds,
                ProcessedStateCount = workerProgress.ProcessedStateCount,
                Message = workerProgress.Message
            };
        }

        public static LinkageRouteWorkerSolutionDto ToWorkerSolution(
            LinkageRouteSolution routeSolution)
        {
            if (routeSolution == null)
            {
                return null;
            }

            return new LinkageRouteWorkerSolutionDto
            {
                SchemeIndex = routeSolution.SchemeIndex,
                OptimizationSourceTopologySignature =
                    routeSolution.OptimizationSourceTopologySignature,
                AlgorithmKind = (int)routeSolution.AlgorithmKind,
                IsSuccess = routeSolution.IsSuccess,
                IsCancelled = routeSolution.IsCancelled,
                ActualCopperLength = routeSolution.ActualCopperLength,
                PerpendicularLapCount = routeSolution.PerpendicularLapCount,
                // JSON 协议不能承载 NaN/Infinity。失败候选内部允许用非有限值
                // 表示“尚未计算净距”，但跨进程必须输出有限诊断值，避免客户端
                // 在真正的几何失败原因之前报 Error parsing Infinity value。
                MinimumClearance = ToFiniteProtocolNumber(
                    routeSolution.MinimumClearance),
                RequestedInsulationClearance =
                    routeSolution.RequestedInsulationClearance,
                RequestedLapInsulationClearance =
                    routeSolution.RequestedLapInsulationClearance,
                SearchInsulationClearance = routeSolution.SearchInsulationClearance,
                IsRelaxedClearanceSolution =
                    routeSolution.IsRelaxedClearanceSolution,
                GroupSolutions = MapList(routeSolution.GroupSolutions, ToWorkerGroupSolution),
                MinimumClearanceFaceGroups = routeSolution.GroupSolutions
                    .Where(groupSolution => groupSolution != null)
                    .SelectMany(groupSolution => groupSolution.MinimumClearanceFaceGroups ??
                        new List<LinkageRouteClearanceFaceGroup>())
                    .Select(ToWorkerClearanceFaceGroup)
                    .ToList(),
                Diagnostics = MapList(routeSolution.Diagnostics, ToWorkerDiagnostic)
            };
        }

        public static LinkageRouteSolution FromWorkerSolution(
            LinkageRouteWorkerSolutionDto workerSolution)
        {
            if (workerSolution == null)
            {
                return null;
            }

            var routeSolution = new LinkageRouteSolution
            {
                SchemeIndex = workerSolution.SchemeIndex,
                OptimizationSourceTopologySignature =
                    workerSolution.OptimizationSourceTopologySignature,
                AlgorithmKind = (LinkageRouteAlgorithmKind)workerSolution.AlgorithmKind,
                IsSuccess = workerSolution.IsSuccess,
                IsCancelled = workerSolution.IsCancelled,
                ActualCopperLength = workerSolution.ActualCopperLength,
                PerpendicularLapCount = workerSolution.PerpendicularLapCount,
                MinimumClearance = workerSolution.MinimumClearance,
                RequestedInsulationClearance =
                    workerSolution.RequestedInsulationClearance,
                RequestedLapInsulationClearance =
                    workerSolution.RequestedLapInsulationClearance,
                SearchInsulationClearance = workerSolution.SearchInsulationClearance,
                IsRelaxedClearanceSolution =
                    workerSolution.IsRelaxedClearanceSolution
            };
            AddRange(routeSolution.GroupSolutions,
                workerSolution.GroupSolutions,
                FromWorkerGroupSolution);
            foreach (var workerFaceGroup in workerSolution.MinimumClearanceFaceGroups ??
                new List<LinkageRouteWorkerClearanceFaceGroupDto>())
            {
                var targetGroup = routeSolution.GroupSolutions.FirstOrDefault(
                    groupSolution => string.Equals(
                        groupSolution?.GroupKey,
                        workerFaceGroup.GeneratedGroupKey,
                        System.StringComparison.OrdinalIgnoreCase));
                if (targetGroup != null)
                {
                    targetGroup.MinimumClearanceFaceGroups.Add(
                        FromWorkerClearanceFaceGroup(workerFaceGroup));
                }
            }
            AddRange(routeSolution.Diagnostics,
                workerSolution.Diagnostics,
                FromWorkerDiagnostic);
            return routeSolution;
        }

        public static LinkageRouteWorkerComparisonResultDto ToWorkerComparisonResult(
            LinkageRouteComparisonResult comparisonResult)
        {
            if (comparisonResult == null)
            {
                return null;
            }

            return new LinkageRouteWorkerComparisonResultDto
            {
                RankedSolutions = MapList(
                    comparisonResult.RankedSolutions,
                    ToWorkerSolution),
                IsCancelled = comparisonResult.IsCancelled,
                ElapsedMilliseconds = comparisonResult.ElapsedMilliseconds,
                FirstSolutionElapsedMilliseconds =
                    comparisonResult.FirstSolutionElapsedMilliseconds
            };
        }

        public static LinkageRouteComparisonResult FromWorkerComparisonResult(
            LinkageRouteWorkerComparisonResultDto workerComparisonResult)
        {
            if (workerComparisonResult == null)
            {
                return null;
            }

            var comparisonResult = new LinkageRouteComparisonResult
            {
                IsCancelled = workerComparisonResult.IsCancelled,
                ElapsedMilliseconds = workerComparisonResult.ElapsedMilliseconds,
                FirstSolutionElapsedMilliseconds =
                    workerComparisonResult.FirstSolutionElapsedMilliseconds
            };
            AddRange(comparisonResult.RankedSolutions,
                workerComparisonResult.RankedSolutions,
                FromWorkerSolution);
            return comparisonResult;
        }

        private static LinkageRouteWorkerGroupRequestDto ToWorkerGroupRequest(
            LinkageRouteGroupRequest routeGroupRequest)
        {
            if (routeGroupRequest == null)
            {
                return null;
            }

            return new LinkageRouteWorkerGroupRequestDto
            {
                GroupKey = routeGroupRequest.GroupKey,
                Phase = routeGroupRequest.Phase,
                DisplayName = routeGroupRequest.DisplayName,
                StartTerminal = ToWorkerTerminal(routeGroupRequest.StartTerminal),
                EndTerminal = ToWorkerTerminal(routeGroupRequest.EndTerminal),
                BusbarWidth = routeGroupRequest.BusbarWidth,
                BusbarThickness = routeGroupRequest.BusbarThickness,
                StackCount = routeGroupRequest.StackCount,
                StartLapBusbarWidth = routeGroupRequest.StartLapBusbarWidth,
                StartLapBusbarThickness = routeGroupRequest.StartLapBusbarThickness,
                StartLapStackCount = routeGroupRequest.StartLapStackCount,
                CabinetTopLapBusbarWidth = routeGroupRequest.CabinetTopLapBusbarWidth,
                CabinetTopLapBusbarThickness = routeGroupRequest.CabinetTopLapBusbarThickness,
                CabinetTopLapStackCount = routeGroupRequest.CabinetTopLapStackCount,
                StackStartSpace = routeGroupRequest.StackStartSpace,
                StackMiddleSpace = routeGroupRequest.StackMiddleSpace,
                StackEndSpace = routeGroupRequest.StackEndSpace,
                StackMoveVector = ToWorkerCoordinate(routeGroupRequest.StackMoveVector),
                StackReverse = routeGroupRequest.StackReverse
            };
        }

        private static LinkageRouteGroupRequest FromWorkerGroupRequest(
            LinkageRouteWorkerGroupRequestDto workerGroupRequest)
        {
            if (workerGroupRequest == null)
            {
                return null;
            }

            return new LinkageRouteGroupRequest
            {
                GroupKey = workerGroupRequest.GroupKey,
                Phase = workerGroupRequest.Phase,
                DisplayName = workerGroupRequest.DisplayName,
                StartTerminal = FromWorkerTerminal(workerGroupRequest.StartTerminal),
                EndTerminal = FromWorkerTerminal(workerGroupRequest.EndTerminal),
                BusbarWidth = workerGroupRequest.BusbarWidth,
                BusbarThickness = workerGroupRequest.BusbarThickness,
                StackCount = workerGroupRequest.StackCount,
                StartLapBusbarWidth = workerGroupRequest.StartLapBusbarWidth,
                StartLapBusbarThickness = workerGroupRequest.StartLapBusbarThickness,
                StartLapStackCount = workerGroupRequest.StartLapStackCount,
                CabinetTopLapBusbarWidth = workerGroupRequest.CabinetTopLapBusbarWidth,
                CabinetTopLapBusbarThickness = workerGroupRequest.CabinetTopLapBusbarThickness,
                CabinetTopLapStackCount = workerGroupRequest.CabinetTopLapStackCount,
                StackStartSpace = workerGroupRequest.StackStartSpace,
                StackMiddleSpace = workerGroupRequest.StackMiddleSpace,
                StackEndSpace = workerGroupRequest.StackEndSpace,
                StackMoveVector = ToVector(workerGroupRequest.StackMoveVector),
                StackReverse = workerGroupRequest.StackReverse
            };
        }

        private static LinkageRouteWorkerTerminalAssemblyDto ToWorkerTerminal(
            LinkageTerminalAssembly terminalAssembly)
        {
            if (terminalAssembly == null)
            {
                return null;
            }

            return new LinkageRouteWorkerTerminalAssemblyDto
            {
                RouteAnchor = ToWorkerCoordinate(terminalAssembly.RouteAnchor),
                LengthDirection = ToWorkerCoordinate(terminalAssembly.LengthDirection),
                ThicknessDirection = ToWorkerCoordinate(terminalAssembly.ThicknessDirection),
                Members = MapList(terminalAssembly.Members, ToWorkerTerminalMember)
            };
        }

        private static LinkageTerminalAssembly FromWorkerTerminal(
            LinkageRouteWorkerTerminalAssemblyDto workerTerminal)
        {
            if (workerTerminal == null)
            {
                return null;
            }

            return new LinkageTerminalAssembly
            {
                RouteAnchor = ToPoint(workerTerminal.RouteAnchor),
                LengthDirection = ToVector(workerTerminal.LengthDirection),
                ThicknessDirection = ToVector(workerTerminal.ThicknessDirection),
                Members = MapList(workerTerminal.Members, FromWorkerTerminalMember)
            };
        }

        private static LinkageRouteWorkerTerminalMemberDto ToWorkerTerminalMember(
            LinkageTerminalMember terminalMember)
        {
            if (terminalMember == null)
            {
                return null;
            }

            return new LinkageRouteWorkerTerminalMemberDto
            {
                MemberKey = terminalMember.MemberKey,
                FixedBusbar = ToWorkerPlanarSetting(terminalMember.FixedBusbar),
                Holes = MapList(terminalMember.Holes?.Where(hole => hole != null), ToWorkerHoleInfo),
                ForbiddenHorizontalExitDirection = ToWorkerCoordinate(
                    terminalMember.ForbiddenHorizontalExitDirection)
            };
        }

        private static LinkageTerminalMember FromWorkerTerminalMember(
            LinkageRouteWorkerTerminalMemberDto workerTerminalMember)
        {
            if (workerTerminalMember == null)
            {
                return null;
            }

            return new LinkageTerminalMember
            {
                MemberKey = workerTerminalMember.MemberKey,
                FixedBusbar = FromWorkerPlanarSetting(workerTerminalMember.FixedBusbar),
                Holes = MapList(workerTerminalMember.Holes?.Where(hole => hole != null), FromWorkerHoleInfo),
                ForbiddenHorizontalExitDirection = ToVector(
                    workerTerminalMember.ForbiddenHorizontalExitDirection)
            };
        }

        private static LinkageRouteWorkerHoleInfoDto ToWorkerHoleInfo(LinkageTerminalHoleInfo hole)
        {
            if (hole == null) return null;
            return new LinkageRouteWorkerHoleInfoDto
            {
                PlanePoint = ToWorkerCoordinate(hole.PlanePoint),
                PlaneNormal = ToWorkerCoordinate(hole.PlaneNormal),
                Vector = ToWorkerCoordinate(hole.Vector),
                HoleVectorInPart = ToWorkerCoordinate(hole.HoleVectorInPart),
                HoleVector = ToWorkerCoordinate(hole.HoleVector),
                CenterPointInPart = ToWorkerCoordinate(hole.CenterPointInPart),
                CenterPointInPart2 = ToWorkerCoordinate(hole.CenterPointInPart2),
                CenterPoint = ToWorkerCoordinate(hole.CenterPoint),
                CenterPoint2 = ToWorkerCoordinate(hole.CenterPoint2),
                HoleTypeValue = hole.HoleTypeValue,
                DimD = hole.DimD,
                DimX = hole.DimX,
                DimY = hole.DimY,
                NotYahua = hole.NotYahua,
            };
        }

        private static LinkageTerminalHoleInfo FromWorkerHoleInfo(LinkageRouteWorkerHoleInfoDto hole)
        {
            if (hole == null) return null;
            return new LinkageTerminalHoleInfo
            {
                PlanePoint = ToPoint(hole.PlanePoint),
                PlaneNormal = ToVector(hole.PlaneNormal),
                Vector = ToVector(hole.Vector),
                HoleVectorInPart = ToVector(hole.HoleVectorInPart),
                HoleVector = ToVector(hole.HoleVector),
                CenterPointInPart = ToPoint(hole.CenterPointInPart),
                CenterPointInPart2 = ToPoint(hole.CenterPointInPart2),
                CenterPoint = ToPoint(hole.CenterPoint),
                CenterPoint2 = ToPoint(hole.CenterPoint2),
                HoleTypeValue = hole.HoleTypeValue,
                DimD = hole.DimD,
                DimX = hole.DimX,
                DimY = hole.DimY,
                NotYahua = hole.NotYahua,
            };
        }

        private static LinkageRouteWorkerObstacleDto ToWorkerObstacle(
            LinkageRouteObstacle routeObstacle)
        {
            if (routeObstacle == null)
            {
                return null;
            }

            return new LinkageRouteWorkerObstacleDto
            {
                ObstacleKey = routeObstacle.ObstacleKey,
                GroupKey = routeObstacle.GroupKey,
                Minimum = ToWorkerCoordinate(routeObstacle.Minimum),
                Maximum = ToWorkerCoordinate(routeObstacle.Maximum)
            };
        }

        private static LinkageRouteObstacle FromWorkerObstacle(
            LinkageRouteWorkerObstacleDto workerObstacle)
        {
            if (workerObstacle == null)
            {
                return null;
            }

            return new LinkageRouteObstacle
            {
                ObstacleKey = workerObstacle.ObstacleKey,
                GroupKey = workerObstacle.GroupKey,
                Minimum = ToPoint(workerObstacle.Minimum),
                Maximum = ToPoint(workerObstacle.Maximum)
            };
        }

        private static LinkageRouteWorkerSearchOptionsDto ToWorkerSearchOptions(
            LinkageRouteSearchOptions searchOptions)
        {
            if (searchOptions == null)
            {
                return null;
            }

            return new LinkageRouteWorkerSearchOptionsDto
            {
                PrioritizeAllSolutions = searchOptions.PrioritizeAllSolutions,
                FirstSolutionTargetMilliseconds = searchOptions.FirstSolutionTargetMilliseconds,
                TotalSearchLimitMilliseconds = searchOptions.TotalSearchLimitMilliseconds,
                EnableAutomaticTotalSearchLimit = searchOptions.EnableAutomaticTotalSearchLimit,
                MaximumCandidatesPerGroup = searchOptions.MaximumCandidatesPerGroup,
                MaximumClearanceRepairCandidatesPerState =
                    searchOptions.MaximumClearanceRepairCandidatesPerState,
                MaximumSolutionsPerAlgorithm = searchOptions.MaximumSolutionsPerAlgorithm,
                BeamWidth = searchOptions.BeamWidth,
                MaximumDegreeOfParallelism = searchOptions.MaximumDegreeOfParallelism,
                MinimumAdaptiveRepairStep = searchOptions.MinimumAdaptiveRepairStep,
                FastSearchStageLimitMilliseconds = searchOptions.FastSearchStageLimitMilliseconds,
                ExtendedFastSearchStageLimitMilliseconds = searchOptions.ExtendedFastSearchStageLimitMilliseconds,
                DeepTraversalTimeSliceMilliseconds = searchOptions.DeepTraversalTimeSliceMilliseconds,
                ConstraintDirectedDeepRepairLimitMilliseconds = searchOptions.ConstraintDirectedDeepRepairLimitMilliseconds,
                ConstraintDirectedRootCandidateCount = searchOptions.ConstraintDirectedRootCandidateCount,
                SolutionAllocationSwitchSolutionCount = searchOptions.SolutionAllocationSwitchSolutionCount,
                PostSwitchMainSearchWorkerRatio = searchOptions.PostSwitchMainSearchWorkerRatio,
                PostSwitchSolutionOptimizationWorkerRatio = searchOptions.PostSwitchSolutionOptimizationWorkerRatio,
                PreferredSolutionOptimizationCount = searchOptions.PreferredSolutionOptimizationCount,
                SolutionOptimizationStagnationMilliseconds = searchOptions.SolutionOptimizationStagnationMilliseconds,
                StagnationMainSearchWorkerCount = searchOptions.StagnationMainSearchWorkerCount,
                MaximumSolutionOptimizationDurationMilliseconds = searchOptions.MaximumSolutionOptimizationDurationMilliseconds,
                SchedulingAndPublishingWorkerReserve = searchOptions.SchedulingAndPublishingWorkerReserve,
                InitialSolutionOptimizationWorkerRatio = searchOptions.InitialSolutionOptimizationWorkerRatio,
                MaximumInitialSolutionOptimizationWorkerCount = searchOptions.MaximumInitialSolutionOptimizationWorkerCount,
                FastSolutionOptimizationWorkerReserve = searchOptions.FastSolutionOptimizationWorkerReserve,
                ClearanceOptimizationLimitMilliseconds = searchOptions.ClearanceOptimizationLimitMilliseconds,
                ClearanceOptimizationTraversalLimitMilliseconds = searchOptions.ClearanceOptimizationTraversalLimitMilliseconds,
                MaximumClearanceOptimizationCandidateCount = searchOptions.MaximumClearanceOptimizationCandidateCount,
                MaximumClearanceOptimizationPassCount = searchOptions.MaximumClearanceOptimizationPassCount,
                ClearanceOptimizationStep = searchOptions.ClearanceOptimizationStep,
                ClearanceOptimizationCatalogLimitMilliseconds = searchOptions.ClearanceOptimizationCatalogLimitMilliseconds,
                MinimumPublishableClearance = searchOptions.MinimumPublishableClearance,
                SupplementalClearanceStartDelayMilliseconds = searchOptions.SupplementalClearanceStartDelayMilliseconds,
                RelaxedClearancePublicationMilliseconds = searchOptions.RelaxedClearancePublicationMilliseconds,
                SupplementalClearanceAttemptLimitMilliseconds = searchOptions.SupplementalClearanceAttemptLimitMilliseconds,
                SupplementalClearanceStep = searchOptions.SupplementalClearanceStep,
                SupplementalClearanceIncreaseDelayMilliseconds = searchOptions.SupplementalClearanceIncreaseDelayMilliseconds,
                SupplementalClearanceMaximumIncrease = searchOptions.SupplementalClearanceMaximumIncrease,
                SupplementalClearanceSeed = searchOptions.SupplementalClearanceSeed,
                SupplementalClearanceMaximumDegreeOfParallelism = searchOptions.SupplementalClearanceMaximumDegreeOfParallelism,
                EnableSupplementalLegacyDoubleStartPlacementSearch = searchOptions.EnableSupplementalLegacyDoubleStartPlacementSearch,
                SupplementalLegacyPlacementMaximumDegreeOfParallelism = searchOptions.SupplementalLegacyPlacementMaximumDegreeOfParallelism,
                SupplementalLegacyPlacementMaximumSolutions = searchOptions.SupplementalLegacyPlacementMaximumSolutions,
                FastLayerMaximumEvaluatedSkeletonCount = searchOptions.FastLayerMaximumEvaluatedSkeletonCount,
                FastLayerMaximumSeedCandidateCount = searchOptions.FastLayerMaximumSeedCandidateCount,
                FastLayerMaximumSeedCandidateCountPerSegmentLayer = searchOptions.FastLayerMaximumSeedCandidateCountPerSegmentLayer,
                FastLayerMaximumVisitedSkeletonCount = searchOptions.FastLayerMaximumVisitedSkeletonCount,
                FastLayerMaximumEvaluatedSkeletonCountPerTopology = searchOptions.FastLayerMaximumEvaluatedSkeletonCountPerTopology,
                ExtendedLayerMaximumEvaluatedSkeletonCount = searchOptions.ExtendedLayerMaximumEvaluatedSkeletonCount,
                ExtendedLayerMaximumSeedCandidateCount = searchOptions.ExtendedLayerMaximumSeedCandidateCount,
                ExtendedLayerMaximumSeedCandidateCountPerSegmentLayer = searchOptions.ExtendedLayerMaximumSeedCandidateCountPerSegmentLayer,
                ExtendedLayerMaximumVisitedSkeletonCount = searchOptions.ExtendedLayerMaximumVisitedSkeletonCount,
                ExtendedLayerMaximumEvaluatedSkeletonCountPerTopology = searchOptions.ExtendedLayerMaximumEvaluatedSkeletonCountPerTopology,
                FastLayerMaximumSegmentCount = searchOptions.FastLayerMaximumSegmentCount,
                ExtendedLayerMaximumSegmentCount = searchOptions.ExtendedLayerMaximumSegmentCount,
                MaximumSeedSkeletonsPerSegmentLayer = searchOptions.MaximumSeedSkeletonsPerSegmentLayer,
                MaximumPairRepairCandidateCount = searchOptions.MaximumPairRepairCandidateCount,
                DoubleStartBridgeMinimumCandidateCount = searchOptions.DoubleStartBridgeMinimumCandidateCount,
                DoubleStartBridgeMaximumCandidateCount = searchOptions.DoubleStartBridgeMaximumCandidateCount,
                DoubleStartBridgeMaximumInternalLapCandidateCount = searchOptions.DoubleStartBridgeMaximumInternalLapCandidateCount,
                MaximumSequenceSearchNodeCount = searchOptions.MaximumSequenceSearchNodeCount,
                MaximumCartesianBoundaryRefinementCount = searchOptions.MaximumCartesianBoundaryRefinementCount,
                MaximumCartesianOptimizationMotionCount = searchOptions.MaximumCartesianOptimizationMotionCount,
                MaximumRestrictedWholePathCandidateCount = searchOptions.MaximumRestrictedWholePathCandidateCount
            };
        }

        private static LinkageRouteSearchOptions FromWorkerSearchOptions(
            LinkageRouteWorkerSearchOptionsDto workerSearchOptions)
        {
            if (workerSearchOptions == null)
            {
                return new LinkageRouteSearchOptions();
            }

            return new LinkageRouteSearchOptions
            {
                PrioritizeAllSolutions = workerSearchOptions.PrioritizeAllSolutions,
                FirstSolutionTargetMilliseconds =
                    workerSearchOptions.FirstSolutionTargetMilliseconds,
                TotalSearchLimitMilliseconds = workerSearchOptions.TotalSearchLimitMilliseconds,
                EnableAutomaticTotalSearchLimit =
                    workerSearchOptions.EnableAutomaticTotalSearchLimit,
                MaximumCandidatesPerGroup = workerSearchOptions.MaximumCandidatesPerGroup,
                MaximumClearanceRepairCandidatesPerState =
                    workerSearchOptions.MaximumClearanceRepairCandidatesPerState,
                MaximumSolutionsPerAlgorithm = workerSearchOptions.MaximumSolutionsPerAlgorithm,
                BeamWidth = workerSearchOptions.BeamWidth,
                MaximumDegreeOfParallelism = workerSearchOptions.MaximumDegreeOfParallelism,
                MinimumAdaptiveRepairStep = workerSearchOptions.MinimumAdaptiveRepairStep,
                FastSearchStageLimitMilliseconds = workerSearchOptions.FastSearchStageLimitMilliseconds,
                ExtendedFastSearchStageLimitMilliseconds = workerSearchOptions.ExtendedFastSearchStageLimitMilliseconds,
                DeepTraversalTimeSliceMilliseconds = workerSearchOptions.DeepTraversalTimeSliceMilliseconds,
                ConstraintDirectedDeepRepairLimitMilliseconds = workerSearchOptions.ConstraintDirectedDeepRepairLimitMilliseconds,
                ConstraintDirectedRootCandidateCount = workerSearchOptions.ConstraintDirectedRootCandidateCount,
                SolutionAllocationSwitchSolutionCount = workerSearchOptions.SolutionAllocationSwitchSolutionCount,
                PostSwitchMainSearchWorkerRatio = workerSearchOptions.PostSwitchMainSearchWorkerRatio,
                PostSwitchSolutionOptimizationWorkerRatio = workerSearchOptions.PostSwitchSolutionOptimizationWorkerRatio,
                PreferredSolutionOptimizationCount = workerSearchOptions.PreferredSolutionOptimizationCount,
                SolutionOptimizationStagnationMilliseconds = workerSearchOptions.SolutionOptimizationStagnationMilliseconds,
                StagnationMainSearchWorkerCount = workerSearchOptions.StagnationMainSearchWorkerCount,
                MaximumSolutionOptimizationDurationMilliseconds = workerSearchOptions.MaximumSolutionOptimizationDurationMilliseconds,
                SchedulingAndPublishingWorkerReserve = workerSearchOptions.SchedulingAndPublishingWorkerReserve,
                InitialSolutionOptimizationWorkerRatio = workerSearchOptions.InitialSolutionOptimizationWorkerRatio,
                MaximumInitialSolutionOptimizationWorkerCount = workerSearchOptions.MaximumInitialSolutionOptimizationWorkerCount,
                FastSolutionOptimizationWorkerReserve = workerSearchOptions.FastSolutionOptimizationWorkerReserve,
                ClearanceOptimizationLimitMilliseconds = workerSearchOptions.ClearanceOptimizationLimitMilliseconds,
                ClearanceOptimizationTraversalLimitMilliseconds = workerSearchOptions.ClearanceOptimizationTraversalLimitMilliseconds,
                MaximumClearanceOptimizationCandidateCount = workerSearchOptions.MaximumClearanceOptimizationCandidateCount,
                MaximumClearanceOptimizationPassCount = workerSearchOptions.MaximumClearanceOptimizationPassCount,
                ClearanceOptimizationStep = workerSearchOptions.ClearanceOptimizationStep,
                ClearanceOptimizationCatalogLimitMilliseconds = workerSearchOptions.ClearanceOptimizationCatalogLimitMilliseconds,
                MinimumPublishableClearance = workerSearchOptions.MinimumPublishableClearance,
                SupplementalClearanceStartDelayMilliseconds = workerSearchOptions.SupplementalClearanceStartDelayMilliseconds,
                RelaxedClearancePublicationMilliseconds = workerSearchOptions.RelaxedClearancePublicationMilliseconds,
                SupplementalClearanceAttemptLimitMilliseconds = workerSearchOptions.SupplementalClearanceAttemptLimitMilliseconds,
                SupplementalClearanceStep = workerSearchOptions.SupplementalClearanceStep,
                SupplementalClearanceIncreaseDelayMilliseconds = workerSearchOptions.SupplementalClearanceIncreaseDelayMilliseconds,
                SupplementalClearanceMaximumIncrease = workerSearchOptions.SupplementalClearanceMaximumIncrease,
                SupplementalClearanceSeed = workerSearchOptions.SupplementalClearanceSeed,
                SupplementalClearanceMaximumDegreeOfParallelism = workerSearchOptions.SupplementalClearanceMaximumDegreeOfParallelism,
                EnableSupplementalLegacyDoubleStartPlacementSearch = workerSearchOptions.EnableSupplementalLegacyDoubleStartPlacementSearch,
                SupplementalLegacyPlacementMaximumDegreeOfParallelism = workerSearchOptions.SupplementalLegacyPlacementMaximumDegreeOfParallelism,
                SupplementalLegacyPlacementMaximumSolutions = workerSearchOptions.SupplementalLegacyPlacementMaximumSolutions,
                FastLayerMaximumEvaluatedSkeletonCount = workerSearchOptions.FastLayerMaximumEvaluatedSkeletonCount,
                FastLayerMaximumSeedCandidateCount = workerSearchOptions.FastLayerMaximumSeedCandidateCount,
                FastLayerMaximumSeedCandidateCountPerSegmentLayer = workerSearchOptions.FastLayerMaximumSeedCandidateCountPerSegmentLayer,
                FastLayerMaximumVisitedSkeletonCount = workerSearchOptions.FastLayerMaximumVisitedSkeletonCount,
                FastLayerMaximumEvaluatedSkeletonCountPerTopology = workerSearchOptions.FastLayerMaximumEvaluatedSkeletonCountPerTopology,
                ExtendedLayerMaximumEvaluatedSkeletonCount = workerSearchOptions.ExtendedLayerMaximumEvaluatedSkeletonCount,
                ExtendedLayerMaximumSeedCandidateCount = workerSearchOptions.ExtendedLayerMaximumSeedCandidateCount,
                ExtendedLayerMaximumSeedCandidateCountPerSegmentLayer = workerSearchOptions.ExtendedLayerMaximumSeedCandidateCountPerSegmentLayer,
                ExtendedLayerMaximumVisitedSkeletonCount = workerSearchOptions.ExtendedLayerMaximumVisitedSkeletonCount,
                ExtendedLayerMaximumEvaluatedSkeletonCountPerTopology = workerSearchOptions.ExtendedLayerMaximumEvaluatedSkeletonCountPerTopology,
                FastLayerMaximumSegmentCount = workerSearchOptions.FastLayerMaximumSegmentCount,
                ExtendedLayerMaximumSegmentCount = workerSearchOptions.ExtendedLayerMaximumSegmentCount,
                MaximumSeedSkeletonsPerSegmentLayer = workerSearchOptions.MaximumSeedSkeletonsPerSegmentLayer,
                MaximumPairRepairCandidateCount = workerSearchOptions.MaximumPairRepairCandidateCount,
                DoubleStartBridgeMinimumCandidateCount = workerSearchOptions.DoubleStartBridgeMinimumCandidateCount,
                DoubleStartBridgeMaximumCandidateCount = workerSearchOptions.DoubleStartBridgeMaximumCandidateCount,
                DoubleStartBridgeMaximumInternalLapCandidateCount = workerSearchOptions.DoubleStartBridgeMaximumInternalLapCandidateCount,
                MaximumSequenceSearchNodeCount = workerSearchOptions.MaximumSequenceSearchNodeCount,
                MaximumCartesianBoundaryRefinementCount = workerSearchOptions.MaximumCartesianBoundaryRefinementCount,
                MaximumCartesianOptimizationMotionCount = workerSearchOptions.MaximumCartesianOptimizationMotionCount,
                MaximumRestrictedWholePathCandidateCount = workerSearchOptions.MaximumRestrictedWholePathCandidateCount
            };
        }

        private static LinkageRouteWorkerGroupSolutionDto ToWorkerGroupSolution(
            LinkageGroupRouteSolution groupSolution)
        {
            if (groupSolution == null)
            {
                return null;
            }

            return new LinkageRouteWorkerGroupSolutionDto
            {
                GroupKey = groupSolution.GroupKey,
                AxisSkeleton = MapList(groupSolution.AxisSkeleton, ToWorkerCoordinate),
                ManufacturableCenterline = MapList(
                    groupSolution.ManufacturableCenterline,
                    ToWorkerCoordinate),
                PlanarParts = MapList(groupSolution.PlanarParts, ToWorkerPlanarSetting),
                StackMembers = MapList(groupSolution.StackMembers, ToWorkerStackedMember),
                Accessories = MapList(groupSolution.Accessories, ToWorkerAccessory),
                ActualCopperLength = groupSolution.ActualCopperLength,
                PerpendicularLapCount = groupSolution.PerpendicularLapCount,
                IsConvertedShape = groupSolution.IsConvertedShape,
                TopologySignature = groupSolution.TopologySignature
            };
        }

        private static LinkageGroupRouteSolution FromWorkerGroupSolution(
            LinkageRouteWorkerGroupSolutionDto workerGroupSolution)
        {
            if (workerGroupSolution == null)
            {
                return null;
            }

            return new LinkageGroupRouteSolution
            {
                GroupKey = workerGroupSolution.GroupKey,
                AxisSkeleton = MapList(workerGroupSolution.AxisSkeleton, ToPoint),
                ManufacturableCenterline = MapList(
                    workerGroupSolution.ManufacturableCenterline,
                    ToPoint),
                PlanarParts = MapList(workerGroupSolution.PlanarParts, FromWorkerPlanarSetting),
                StackMembers = MapList(workerGroupSolution.StackMembers, FromWorkerStackedMember),
                Accessories = MapList(workerGroupSolution.Accessories, FromWorkerAccessory),
                ActualCopperLength = workerGroupSolution.ActualCopperLength,
                PerpendicularLapCount = workerGroupSolution.PerpendicularLapCount,
                IsConvertedShape = workerGroupSolution.IsConvertedShape,
                TopologySignature = workerGroupSolution.TopologySignature
            };
        }

        private static LinkageRouteWorkerStackedMemberDto ToWorkerStackedMember(
            LinkageStackedRouteMember stackedRouteMember)
        {
            if (stackedRouteMember == null)
            {
                return null;
            }

            return new LinkageRouteWorkerStackedMemberDto
            {
                MemberIndex = stackedRouteMember.MemberIndex,
                CenterlineOffset = stackedRouteMember.CenterlineOffset,
                PlanarParts = MapList(stackedRouteMember.PlanarParts, ToWorkerPlanarSetting)
            };
        }

        private static LinkageRouteWorkerAccessoryDto ToWorkerAccessory(
            LinkageRouteAccessory routeAccessory)
        {
            if (routeAccessory == null)
            {
                return null;
            }

            return new LinkageRouteWorkerAccessoryDto
            {
                Kind = (int)routeAccessory.Kind,
                TerminalSide = (int)routeAccessory.TerminalSide,
                RelatedGeneratedMemberIndex =
                    routeAccessory.RelatedGeneratedMemberIndex,
                Setting = ToWorkerPlanarSetting(routeAccessory.Setting)
            };
        }

        private static LinkageRouteAccessory FromWorkerAccessory(
            LinkageRouteWorkerAccessoryDto workerAccessory)
        {
            if (workerAccessory == null)
            {
                return null;
            }

            return new LinkageRouteAccessory
            {
                Kind = (LinkageRouteAccessoryKind)workerAccessory.Kind,
                TerminalSide = (LinkageRouteTerminalSide)
                    workerAccessory.TerminalSide,
                RelatedGeneratedMemberIndex =
                    workerAccessory.RelatedGeneratedMemberIndex,
                Setting = FromWorkerPlanarSetting(workerAccessory.Setting)
            };
        }

        private static LinkageStackedRouteMember FromWorkerStackedMember(
            LinkageRouteWorkerStackedMemberDto workerStackedMember)
        {
            if (workerStackedMember == null)
            {
                return null;
            }

            return new LinkageStackedRouteMember
            {
                MemberIndex = workerStackedMember.MemberIndex,
                CenterlineOffset = workerStackedMember.CenterlineOffset,
                PlanarParts = MapList(
                    workerStackedMember.PlanarParts,
                    FromWorkerPlanarSetting)
            };
        }

        private static LinkageRouteWorkerFailureDiagnosticDto ToWorkerDiagnostic(
            LinkageRouteFailureDiagnostic routeDiagnostic)
        {
            if (routeDiagnostic == null)
            {
                return null;
            }

            return new LinkageRouteWorkerFailureDiagnosticDto
            {
                GroupKey = routeDiagnostic.GroupKey,
                Stage = (int)routeDiagnostic.Stage,
                Message = routeDiagnostic.Message,
                CompletedGroupCount = routeDiagnostic.CompletedGroupCount,
                ViolationCount = routeDiagnostic.ViolationCount,
                MaximumViolationDepth = routeDiagnostic.MaximumViolationDepth,
                PreviewCenterline = MapList(
                    routeDiagnostic.PreviewCenterline,
                    ToWorkerCoordinate)
            };
        }

        private static LinkageRouteFailureDiagnostic FromWorkerDiagnostic(
            LinkageRouteWorkerFailureDiagnosticDto workerDiagnostic)
        {
            if (workerDiagnostic == null)
            {
                return null;
            }

            return new LinkageRouteFailureDiagnostic
            {
                GroupKey = workerDiagnostic.GroupKey,
                Stage = (LinkageRouteFailureStage)workerDiagnostic.Stage,
                Message = workerDiagnostic.Message,
                CompletedGroupCount = workerDiagnostic.CompletedGroupCount,
                ViolationCount = workerDiagnostic.ViolationCount,
                MaximumViolationDepth = workerDiagnostic.MaximumViolationDepth,
                PreviewCenterline = MapList(workerDiagnostic.PreviewCenterline, ToPoint)
            };
        }

        private static LinkageRouteWorkerPlanarSettingDto ToWorkerPlanarSetting(
            PlanarBendBusbarSetting planarSetting)
        {
            if (planarSetting == null)
            {
                return null;
            }

            return new LinkageRouteWorkerPlanarSettingDto
            {
                IsCenter = planarSetting.IsCenter,
                StartBusbarVector = ToWorkerCoordinate(planarSetting.StartBusbarVector),
                WidthVector = ToWorkerCoordinate(planarSetting.WidthVector),
                PointList = MapList(planarSetting.PointList, ToWorkerCoordinate),
                BarBendingRadius = planarSetting.BarBendingRadius,
                BendAllowance = planarSetting.BendAllowance,
                BusBarThickness = planarSetting.BusBarThickness,
                BusBarWidth = planarSetting.BusBarWidth,
                StartLapWidth = planarSetting.StartLapWidth,
                EndLapWidth = planarSetting.EndLapWidth,
                GroupKey = planarSetting.GroupKey
            };
        }

        private static PlanarBendBusbarSetting FromWorkerPlanarSetting(
            LinkageRouteWorkerPlanarSettingDto workerPlanarSetting)
        {
            if (workerPlanarSetting == null)
            {
                return null;
            }

            return new PlanarBendBusbarSetting
            {
                IsCenter = workerPlanarSetting.IsCenter,
                StartBusbarVector = ToVector(workerPlanarSetting.StartBusbarVector),
                WidthVector = ToVector(workerPlanarSetting.WidthVector),
                PointList = MapList(workerPlanarSetting.PointList, ToPoint).ToList(),
                BarBendingRadius = workerPlanarSetting.BarBendingRadius,
                BendAllowance = workerPlanarSetting.BendAllowance,
                BusBarThickness = workerPlanarSetting.BusBarThickness,
                BusBarWidth = workerPlanarSetting.BusBarWidth,
                StartLapWidth = workerPlanarSetting.StartLapWidth,
                EndLapWidth = workerPlanarSetting.EndLapWidth,
                GroupKey = workerPlanarSetting.GroupKey
            };
        }

        private static LinkageRouteWorkerCoordinateDto ToWorkerCoordinate(Point3d point)
        {
            return point == null
                ? null
                : new LinkageRouteWorkerCoordinateDto { X = point.X, Y = point.Y, Z = point.Z };
        }

        private static LinkageRouteWorkerCoordinateDto ToWorkerCoordinate(Vector3d vector)
        {
            return vector == null
                ? null
                : new LinkageRouteWorkerCoordinateDto { X = vector.X, Y = vector.Y, Z = vector.Z };
        }

        private static Point3d ToPoint(LinkageRouteWorkerCoordinateDto coordinate)
        {
            return coordinate == null ? null : new Point3d(coordinate.X, coordinate.Y, coordinate.Z);
        }

        private static Vector3d ToVector(LinkageRouteWorkerCoordinateDto coordinate)
        {
            return coordinate == null ? null : new Vector3d(coordinate.X, coordinate.Y, coordinate.Z);
        }

        private static double ToFiniteProtocolNumber(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0d : value;
        }

        private static LinkageRouteWorkerClearanceFaceGroupDto ToWorkerClearanceFaceGroup(
            LinkageRouteClearanceFaceGroup sourceFaceGroup)
        {
            return sourceFaceGroup == null
                ? null
                : new LinkageRouteWorkerClearanceFaceGroupDto
                {
                    GeneratedGroupKey = sourceFaceGroup.GeneratedGroupKey,
                    GeneratedSegmentIndex = sourceFaceGroup.GeneratedSegmentIndex,
                    StaticGroupKey = sourceFaceGroup.StaticGroupKey,
                    StaticSegmentIndex = sourceFaceGroup.StaticSegmentIndex,
                    Distance = sourceFaceGroup.Distance,
                    GeneratedFaceCenter = ToWorkerCoordinate(sourceFaceGroup.GeneratedFaceCenter),
                    GeneratedFaceNormal = ToWorkerCoordinate(sourceFaceGroup.GeneratedFaceNormal),
                    StaticFaceCenter = ToWorkerCoordinate(sourceFaceGroup.StaticFaceCenter),
                    StaticFaceNormal = ToWorkerCoordinate(sourceFaceGroup.StaticFaceNormal)
                };
        }

        private static LinkageRouteClearanceFaceGroup FromWorkerClearanceFaceGroup(
            LinkageRouteWorkerClearanceFaceGroupDto sourceFaceGroup)
        {
            return sourceFaceGroup == null
                ? null
                : new LinkageRouteClearanceFaceGroup
                {
                    GeneratedGroupKey = sourceFaceGroup.GeneratedGroupKey,
                    GeneratedSegmentIndex = sourceFaceGroup.GeneratedSegmentIndex,
                    StaticGroupKey = sourceFaceGroup.StaticGroupKey,
                    StaticSegmentIndex = sourceFaceGroup.StaticSegmentIndex,
                    Distance = sourceFaceGroup.Distance,
                    GeneratedFaceCenter = ToPoint(sourceFaceGroup.GeneratedFaceCenter),
                    GeneratedFaceNormal = ToVector(sourceFaceGroup.GeneratedFaceNormal),
                    StaticFaceCenter = ToPoint(sourceFaceGroup.StaticFaceCenter),
                    StaticFaceNormal = ToVector(sourceFaceGroup.StaticFaceNormal)
                };
        }

        private static IList<TTarget> MapList<TSource, TTarget>(
            IEnumerable<TSource> sourceItems,
            System.Func<TSource, TTarget> mapItem)
        {
            return (sourceItems ?? Enumerable.Empty<TSource>())
                .Select(mapItem)
                .ToList();
        }

        private static void AddRange<TSource, TTarget>(
            ICollection<TTarget> targetItems,
            IEnumerable<TSource> sourceItems,
            System.Func<TSource, TTarget> mapItem)
        {
            foreach (var sourceItem in sourceItems ?? Enumerable.Empty<TSource>())
            {
                targetItems.Add(mapItem(sourceItem));
            }
        }
    }
}
