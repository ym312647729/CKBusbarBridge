using GeometRi;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CKBusbarBridgePreview
{
    /// <summary>从当前启用的柜前/柜后柜顶伸出排零件读取中心线和初始排厚方向。</summary>
    public static class CabinetTopExtensionBusbarReader
    {
        private const string BusbarSpecificationPropertyName = "铜排规格";
        private const string BusbarPhasePropertyName = "铜排相序";
        private static readonly Regex ReferencedPartPhasePattern = new Regex(
            @"^(?:[AB]柜内伸出排|柜内伸出排|柜顶伸出排)-([ABC])-(?:[AB]柜|柜)?(?:前|后)(?:-\d+)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static IReadOnlyList<ModeledBusbar> Read(
            AssemblyPartCatalog partCatalog,
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting)
            => ReadCore(partCatalog, swApp, axisSetting, true);

        /// <summary>面对墙按实际连接端位建立逻辑组，读取几何时不访问铜排相序属性。</summary>
        public static IReadOnlyList<ModeledBusbar> ReadGeometry(
            AssemblyPartCatalog partCatalog, ISldWorks swApp, BusbarBridgeAxisSetting axisSetting)
            => ReadCore(partCatalog, swApp, axisSetting, false);

        private static IReadOnlyList<ModeledBusbar> ReadCore(
            AssemblyPartCatalog partCatalog, ISldWorks swApp, BusbarBridgeAxisSetting axisSetting,
            bool readPhaseAttributes)
        {
            if (partCatalog == null) throw new ArgumentNullException(nameof(partCatalog));
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));
            if (axisSetting == null) throw new ArgumentNullException(nameof(axisSetting));

            var extensionBusbars = new List<ModeledBusbar>();
            var readFailures = new List<string>();
            var extensionComponents = partCatalog.GetComponents(
                SystemPartType_e.CabinetTopExtensionBusbar);
            FaceToWallPreviewTrace.Info(
                "柜顶伸出排-候选组件",
                "识别数量=" + extensionComponents.Count +
                "，组件=" + string.Join(",", extensionComponents.Select(
                    BusbarGeometryUtility.SafeName)));
            foreach (var extensionComponent in extensionComponents)
            {
                var extensionComponentName = BusbarGeometryUtility.SafeName(extensionComponent);
                if (!HasReadablePartDocument(extensionComponent))
                {
                    FaceToWallPreviewTrace.Warning(
                        "柜顶伸出排-跳过非零件候选",
                        "组件=" + extensionComponentName);
                    continue;
                }

                try
                {
                    FaceToWallPreviewTrace.Info(
                        "柜顶伸出排-开始解析",
                        "组件=" + extensionComponentName);
                    var extensionBusbar = ReadSingle(extensionComponent, swApp, axisSetting, readPhaseAttributes);
                    extensionBusbars.Add(extensionBusbar);
                    FaceToWallPreviewTrace.Info(
                        "柜顶伸出排-解析完成",
                        "组件=" + extensionComponentName +
                        "，相序=" + extensionBusbar.Phase +
                        "，中心线点数=" + extensionBusbar.CenterlineSetting.PointList.Count +
                        "，规格=" + extensionBusbar.CenterlineSetting.BusBarWidth.ToString("0.###") +
                        "x" + extensionBusbar.CenterlineSetting.BusBarThickness.ToString("0.###"));
                }
                catch (Exception readException)
                {
                    FaceToWallPreviewTrace.Error(
                        "柜顶伸出排-解析失败",
                        readException,
                        "组件=" + extensionComponentName);
                    readFailures.Add(
                        extensionComponentName + "：" + readException.Message);
                }
            }

            if (readFailures.Count > 0)
            {
                throw new InvalidOperationException(
                    "柜顶伸出排读取不完整：" + string.Join("；", readFailures));
            }

            if (extensionBusbars.Count == 0)
            {
                throw new InvalidOperationException(
                    "柜顶伸出排读取失败：没有识别到未隐藏、未压缩的柜顶伸出排。");
            }

            return extensionBusbars.AsReadOnly();
        }

        private static bool HasReadablePartDocument(Component2 extensionComponent)
        {
            if (extensionComponent == null)
            {
                return false;
            }

            try
            {
                return extensionComponent.IGetModelDoc() is PartDoc;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>组件必须来自总装上下文，不能传子装文档中重新枚举的局部实例。</summary>
        public static ModeledBusbar ReadComponent(
            Component2 component, ISldWorks swApp, BusbarBridgeAxisSetting axisSetting)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));
            if (axisSetting == null) throw new ArgumentNullException(nameof(axisSetting));
            if (component.IsSuppressed() || !HasReadablePartDocument(component))
                throw new InvalidOperationException("伸出排组件被压缩或不是已解析的零件：" + component.Name2);
            return ReadSingle(component, swApp, axisSetting);
        }

        /// <summary>与正式生成共用的相组中心线起点；单根排的叠排展开仍由正式生成处理。</summary>
        public static Point3d CalculateConnectionStart(
            Point3d upperCenter, Vector3d thicknessDirection, Vector3d heightDirection,
            int generatedCount, int sourceCount, double width, double thickness, double sourceThickness)
        {
            if (upperCenter == null || thicknessDirection == null || heightDirection == null)
                throw new ArgumentNullException(nameof(upperCenter));
            if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0d ||
                thicknessDirection.Norm < 1e-9d || heightDirection.Norm < 1e-9d)
                throw new ArgumentException("铜排排宽及起点方向必须有效。");
            var offset = FaceToWallModelBusbarPlanner.CalculateCabinetTopLapReferenceOffset(
                generatedCount, sourceCount, thickness, sourceThickness);
            var lapReference = PlanarBendBusbarSetting.Translate(
                upperCenter, thicknessDirection.Normalized, offset);
            return PlanarBendBusbarSetting.Translate(lapReference, heightDirection.Normalized, -width);
        }

        /// <summary>预览复用正式生成的五点端排及叠排展开；不读取任何中间基架。</summary>
        public static IReadOnlyList<PlanarBendBusbarSetting> CreateBaseConnection(
            IReadOnlyList<ModeledBusbar> phaseBars, InsulatorConnectionInfo baseInsulator,
            bool isACabinet, double interfaceX, double supportHeight, int phaseIndex,
            out Point3d ordinaryReference)
        {
            // 兼容既有等根数调用；新预览明确传入N，不能以源排数量E代替。
            return CreateBaseConnection(phaseBars, baseInsulator, isACabinet, interfaceX,
                supportHeight, phaseIndex, phaseBars.Count, out ordinaryReference);
        }

        public static IReadOnlyList<PlanarBendBusbarSetting> CreateBaseConnection(
            IReadOnlyList<ModeledBusbar> phaseBars, InsulatorConnectionInfo baseInsulator,
            bool isACabinet, double interfaceX, double supportHeight, int phaseIndex,
            int generatedCount, out Point3d ordinaryReference)
        {
            // 保留既有调用签名与端排层位；新预览显式选择入口桥接。
            return CreateBaseConnection(phaseBars, baseInsulator, isACabinet, interfaceX,
                supportHeight, phaseIndex, generatedCount, out ordinaryReference, false);
        }

        public static IReadOnlyList<PlanarBendBusbarSetting> CreateBaseConnection(
            IReadOnlyList<ModeledBusbar> phaseBars, InsulatorConnectionInfo baseInsulator,
            bool isACabinet, double interfaceX, double supportHeight, int phaseIndex,
            int generatedCount, out Point3d ordinaryReference, bool frontBridgeAtB)
        {
            if (phaseBars == null || phaseBars.Count == 0 || generatedCount < 1 || generatedCount > 4)
                throw new ArgumentException("端排规划必须提供源伸出排及1到4根生成排。");
            var axis = new BusbarBridgeAxisSetting(
                isACabinet ? BusbarBridgeAxisDirection_e.NegativeX : BusbarBridgeAxisDirection_e.PositiveX,
                BusbarBridgeAxisDirection_e.PositiveY, BusbarBridgeAxisDirection_e.PositiveZ);
            var sign = isACabinet ? -1d : 1d;
            // 新中间排入口桥头位于普通层上方一个排厚；B端L排尾部改为普通层。
            // 在生成叠排前调整P4/P5高度，保留各成员平行折弯关系，不平移整个L排。
            var bTailAdjustment = frontBridgeAtB && !isACabinet
                ? phaseBars[0].CenterlineSetting.BusBarThickness : 0d;
            // 输入是绝缘子顶面的支承高度；叠排规划入口使用中面高度。
            // N根排及N-1层等厚垫块的总厚度为(2N-1)T，不能把半厚从绝缘子高度中扣除。
            var stackHalfThickness = (2d * generatedCount - 1d) * phaseBars[0].CenterlineSetting.BusBarThickness / 2d;
            var groups = FaceToWallModelBusbarPlanner.BuildPhaseStack(
                phaseBars, baseInsulator, null, axis, sign * interfaceX, new[] { 0d, 1d },
                0d, 1d, phaseIndex, generatedCount,
                Math.Max(10d, phaseBars[0].CenterlineSetting.BusBarThickness), 20d,
                false, 0, null, null, null, false, null, null, out var context,
                true, false, supportHeight + stackHalfThickness - bTailAdjustment);
            ordinaryReference = context.OrdinaryRouteReferencePoint.Copy();
            ordinaryReference.Y += bTailAdjustment;
            return groups.Single();
        }

        /// <summary>从柜端连接排继续生成普通段；仅使用接口快照，不依赖中间基架。</summary>
        public static IReadOnlyList<IReadOnlyList<PlanarBendBusbarSetting>> CreateOrdinaryPrefix(
            IReadOnlyList<ModeledBusbar> phaseBars, InsulatorConnectionInfo baseInsulator,
            double interfaceX, double supportHeight, int phaseIndex, int generatedCount,
            IReadOnlyList<double> breakpointPositions, double specialNearX, double specialFarX)
        {
            var axis = BusbarBridgeAxisSetting.CreateDefault();
            // 输入是绝缘子顶面的支承高度，BuildPhaseStack使用的是叠排中面高度。
            // N根排与N-1层等厚垫块共(2N-1)T，补半厚后最下层实际表面才落在支承面上。
            var stackHalfThickness = (2d * generatedCount - 1d) * phaseBars[0].CenterlineSetting.BusBarThickness / 2d;
            var sourceGroups = FaceToWallModelBusbarPlanner.BuildPhaseStack(
                phaseBars, baseInsulator, null, axis, interfaceX, breakpointPositions,
                specialNearX, specialFarX, phaseIndex, generatedCount,
                Math.Max(10d, phaseBars[0].CenterlineSetting.BusBarThickness), 20d,
                true, 0, null, null, null, false, null, null, out var context,
                true, false, supportHeight + stackHalfThickness);
            var groups = new List<IReadOnlyList<PlanarBendBusbarSetting>>();
            var fixedMembers = sourceGroups[0].Select(b => b.Copy()).ToArray();
            foreach (var member in fixedMembers)
                for (var p = member.PointList.Count - 2; p < member.PointList.Count; p++)
                    member.PointList[p].Y -= member.BusBarThickness;
            groups.Add(Array.AsReadOnly(fixedMembers));
            for (var g = 1; g < sourceGroups.Count; g++)
            {
                var first = sourceGroups[g][0].PointList.First(); var last = sourceGroups[g][0].PointList.Last();
                var height = supportHeight + stackHalfThickness;
                groups.Add(CreateBridgeHeadStraightTailMembers(sourceGroups[g],
                    new Point3d(first.X, height, first.Z), new Point3d(last.X, height, last.Z), phaseIndex));
            }
            return groups.AsReadOnly();
        }

        public static IReadOnlyList<PlanarBendBusbarSetting> CreateBridgeHeadStraightTailMembers(
            IReadOnlyList<PlanarBendBusbarSetting> sourceMembers, Point3d start, Point3d end, int phaseIndex)
        {
            var shape = sourceMembers[0].Copy();
            if (end.X - start.X <= MinimumBridgeHeadStraightTailLength(shape.BusBarWidth, shape.BarBendingRadius))
                throw new InvalidOperationException("桥头直尾铜排长度不足，请调整相邻断点或特殊段起点。");
            shape.StartBusbarVector = new Vector3d(0, 1, 0);
            shape.PointList = FaceToWallModelBusbarPlanner.CreateBridgeReferenceCenterlinePoints(start, end,
                new Vector3d(1, 0, 0), new Vector3d(0, 1, 0), shape.BusBarWidth, shape.BusBarThickness,
                shape.BusBarThickness, 0d).ToList();
            var offsets = FaceToWallModelBusbarPlanner.CalculateStackMemberOffsets(sourceMembers.Count, shape.BusBarThickness);
            var members = FaceToWallModelBusbarPlanner.CreateBridgeStackMemberSettings(shape, offsets, new Vector3d(0, 1, 0), phaseIndex);
            var orderedSource = sourceMembers.OrderBy(b => b.PointList.Last().Y).ToArray();
            for (var i = 0; i < members.Count; i++)
            {
                members[i].GroupKey = orderedSource[i].GroupKey;
                members[i].RemoveInvalidPoints();
            }
            return members;
        }

        public static double MinimumBridgeHeadStraightTailLength(double width, double radius)
            => 2d * (width + FaceToWallModelBusbarPlanner.BridgeLowestEdgeStraightAdditionalLength +
                FaceToWallModelBusbarPlanner.BridgeInnermostTransitionProjectionLength) + radius;

        private static ModeledBusbar ReadSingle(
            Component2 extensionComponent,
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting, bool readPhaseAttributes = true)
        {
            var eccentricBaselineSegments = extensionComponent.GetSegmentsInAss(swApp);
            FaceToWallPreviewTrace.Info(
                "柜顶伸出排-中心线",
                "组件=" + BusbarGeometryUtility.SafeName(extensionComponent) +
                "，偏心基线段数=" + (eccentricBaselineSegments?.Count ?? 0));
            if (eccentricBaselineSegments == null || eccentricBaselineSegments.Count == 0)
            {
                throw new InvalidOperationException("形状草图没有可用偏心基线。");
            }

            if (!TryReadBusbarSection(
                    extensionComponent,
                    out var busbarWidth,
                    out var busbarThickness))
            {
                throw new InvalidOperationException("引用配置中缺少有效的“铜排规格”。");
            }
            FaceToWallPreviewTrace.Info(
                "柜顶伸出排-规格",
                "组件=" + BusbarGeometryUtility.SafeName(extensionComponent) +
                "，排宽=" + busbarWidth.ToString("0.###") +
                "，排厚=" + busbarThickness.ToString("0.###"));

            var extensionBusbarPhase = readPhaseAttributes
                ? ReadBusbarPhase(extensionComponent) : BusbarPhase_e.Unknow;
            FaceToWallPreviewTrace.Info(
                "柜顶伸出排-相序",
                "组件=" + BusbarGeometryUtility.SafeName(extensionComponent) +
                "，识别相序=" + extensionBusbarPhase);

            var virtualComponent = extensionComponent.GetComponentInto(swApp);
            FaceToWallPreviewTrace.Info(
                "柜顶伸出排-虚拟组件",
                "组件=" + BusbarGeometryUtility.SafeName(extensionComponent) +
                "，实体数=" + (virtualComponent?.BodyIntos?.Count ?? 0));
            var firstEccentricBaselineSegment = eccentricBaselineSegments[0];
            var sourceThicknessDirection = FindInitialThicknessDirection(
                virtualComponent,
                firstEccentricBaselineSegment);
            if (sourceThicknessDirection == null)
            {
                throw new InvalidOperationException("无法从首段偏心基线所在平面确定初始排厚方向。");
            }

            var normalizedSourceThicknessDirection = sourceThicknessDirection.Normalized;
            var firstEccentricBaselineDirection = new Vector3d(
                firstEccentricBaselineSegment.P1,
                firstEccentricBaselineSegment.P2);
            if (firstEccentricBaselineDirection.Norm < 0.001d)
            {
                throw new InvalidOperationException("形状草图首段长度无效。");
            }

            var normalizedSourceWidthDirection = normalizedSourceThicknessDirection
                .Cross(firstEccentricBaselineDirection.Normalized)
                .Negate()
                .Normalized;
            var eccentricBaselinePoints = new List<Point3d>
            {
                eccentricBaselineSegments[0].P1.Copy()
            };
            eccentricBaselinePoints.AddRange(
                eccentricBaselineSegments.Select(
                    eccentricBaselineSegment => eccentricBaselineSegment.P2.Copy()));
            var centerlineOffsetDistance = busbarThickness / 2d;
            var eccentricBaselineSetting = new PlanarBendBusbarSetting
            {
                IsCenter = false,
                StartBusbarVector = normalizedSourceThicknessDirection,
                WidthVector = normalizedSourceWidthDirection,
                PointList = eccentricBaselinePoints,
                BusBarWidth = busbarWidth,
                BusBarThickness = busbarThickness
            };
            var orderedCenterlinePoints = eccentricBaselineSetting
                .GetOffsetCenterlinePointList(centerlineOffsetDistance);
            if (orderedCenterlinePoints.Count != eccentricBaselinePoints.Count)
            {
                throw new InvalidOperationException("偏心基线转换中心线后点数不一致。");
            }

            var actualCenterlineSegments = Enumerable
                .Range(0, orderedCenterlinePoints.Count - 1)
                .Select(centerlinePointIndex => new Segment3d(
                    orderedCenterlinePoints[centerlinePointIndex].Copy(),
                    orderedCenterlinePoints[centerlinePointIndex + 1].Copy()))
                .ToList();
            FaceToWallPreviewTrace.Info(
                "柜顶伸出排-偏心基线转中心线",
                "组件=" + BusbarGeometryUtility.SafeName(extensionComponent) +
                "，沿初始排厚方向内移=" + centerlineOffsetDistance.ToString("0.###") +
                "，初始排厚方向=" + normalizedSourceThicknessDirection);

            var centerlineSetting = new PlanarBendBusbarSetting
            {
                IsCenter = true,
                StartBusbarVector = normalizedSourceThicknessDirection,
                WidthVector = normalizedSourceWidthDirection,
                PointList = orderedCenterlinePoints,
                BusBarWidth = busbarWidth,
                BusBarThickness = busbarThickness,
                BarBendingRadius = Math.Max(10d, busbarThickness),
                StartLapWidth = busbarWidth,
                EndLapWidth = busbarWidth,
                GroupKey = extensionBusbarPhase.ToString()
            };
            centerlineSetting.RemoveInvalidPoints();

            return new ModeledBusbar
            {
                Component = extensionComponent,
                Phase = extensionBusbarPhase,
                CenterlineSetting = centerlineSetting,
                TerminalSegment = FindUpperTerminalSegment(
                    actualCenterlineSegments,
                    axisSetting.HeightVector),
                CenterPoint = BusbarGeometryUtility.Average(orderedCenterlinePoints)
            };
        }

        private static BusbarPhase_e ReadBusbarPhase(Component2 extensionComponent)
        {
            ModelDoc2 extensionPartModel;
            try
            {
                extensionPartModel = extensionComponent?.IGetModelDoc();
            }
            catch (Exception extensionPartModelException)
            {
                FaceToWallPreviewTrace.Error(
                    "柜顶伸出排-获取相序模型",
                    extensionPartModelException,
                    "组件=" + BusbarGeometryUtility.SafeName(extensionComponent));
                extensionPartModel = null;
            }

            var configuredPhaseText = extensionPartModel == null
                ? null
                : ReadCustomProperty(
                    extensionPartModel,
                    SafeReferencedConfiguration(extensionComponent),
                    BusbarPhasePropertyName);
            if (string.IsNullOrWhiteSpace(configuredPhaseText) &&
                extensionPartModel != null)
            {
                configuredPhaseText = ReadCustomProperty(
                    extensionPartModel,
                    string.Empty,
                    BusbarPhasePropertyName);
            }

            string referencedPartPath;
            try
            {
                referencedPartPath = extensionComponent?.GetPathName();
            }
            catch (Exception referencedPartPathException)
            {
                FaceToWallPreviewTrace.Error(
                    "柜顶伸出排-获取引用路径",
                    referencedPartPathException,
                    "组件=" + BusbarGeometryUtility.SafeName(extensionComponent));
                referencedPartPath = null;
            }

            var configuredBusbarPhase = ParseBusbarPhaseText(configuredPhaseText);
            var referencedPartBusbarPhase = ParseReferencedPartPhase(
                referencedPartPath);
            if (configuredBusbarPhase != BusbarPhase_e.Unknow &&
                referencedPartBusbarPhase != BusbarPhase_e.Unknow &&
                configuredBusbarPhase != referencedPartBusbarPhase)
            {
                throw new InvalidOperationException(
                    "柜顶伸出排“" +
                    BusbarGeometryUtility.SafeName(extensionComponent) +
                    "”的配置属性“" + BusbarPhasePropertyName +
                    "”为 " + configuredBusbarPhase +
                    " 相，但实际引用零件识别为 " + referencedPartBusbarPhase +
                    " 相，请修正模型相序信息。");
            }

            var resolvedBusbarPhase = configuredBusbarPhase != BusbarPhase_e.Unknow
                ? configuredBusbarPhase
                : referencedPartBusbarPhase;
            if (resolvedBusbarPhase == BusbarPhase_e.Unknow)
            {
                throw new InvalidOperationException(
                    "无法从引用配置属性“" + BusbarPhasePropertyName +
                    "”或实际引用零件文件名识别柜顶伸出排“" +
                    BusbarGeometryUtility.SafeName(extensionComponent) +
                    "”的 A/B/C 相序。");
            }

            return resolvedBusbarPhase;
        }

        internal static BusbarPhase_e ParseBusbarPhaseText(
            string busbarPhaseText)
        {
            var normalizedBusbarPhaseText = (busbarPhaseText ?? string.Empty)
                .Trim()
                .ToUpperInvariant();
            if (normalizedBusbarPhaseText.EndsWith("相", StringComparison.Ordinal))
            {
                normalizedBusbarPhaseText = normalizedBusbarPhaseText.Substring(
                    0,
                    normalizedBusbarPhaseText.Length - 1);
            }

            return normalizedBusbarPhaseText == "A"
                ? BusbarPhase_e.A
                : normalizedBusbarPhaseText == "B"
                    ? BusbarPhase_e.B
                    : normalizedBusbarPhaseText == "C"
                        ? BusbarPhase_e.C
                        : BusbarPhase_e.Unknow;
        }

        internal static BusbarPhase_e ParseReferencedPartPhase(
            string referencedPartPath)
        {
            if (string.IsNullOrWhiteSpace(referencedPartPath))
            {
                return BusbarPhase_e.Unknow;
            }

            string referencedPartName;
            try
            {
                referencedPartName = System.IO.Path.GetFileNameWithoutExtension(
                    referencedPartPath.Trim());
            }
            catch
            {
                return BusbarPhase_e.Unknow;
            }

            var referencedPartPhaseMatch = ReferencedPartPhasePattern.Match(
                referencedPartName ?? string.Empty);
            return referencedPartPhaseMatch.Success
                ? ParseBusbarPhaseText(referencedPartPhaseMatch.Groups[1].Value)
                : BusbarPhase_e.Unknow;
        }

        private static Segment3d FindUpperTerminalSegment(
            IEnumerable<Segment3d> sourceSegments,
            Vector3d heightDirection)
        {
            var candidates = sourceSegments
                .Where(segment => segment != null && segment.Length > 0.001d)
                .Select(segment => new
                {
                    Segment = segment,
                    HeightAlignment = Math.Abs(segment.ToVector.Normalized.Dot(heightDirection)),
                    MaximumHeight = Math.Max(
                        BusbarGeometryUtility.Project(segment.P1, heightDirection),
                        BusbarGeometryUtility.Project(segment.P2, heightDirection))
                })
                .ToList();
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException("中心线没有有效线段。");
            }

            var verticalCandidates = candidates
                .Where(candidate => candidate.HeightAlignment >= 0.95d)
                .ToList();
            var selectedSegment = (verticalCandidates.Count > 0 ? verticalCandidates : candidates)
                .OrderByDescending(candidate => candidate.MaximumHeight)
                .ThenByDescending(candidate => candidate.HeightAlignment)
                .First()
                .Segment;

            return BusbarGeometryUtility.Project(selectedSegment.P1, heightDirection) <=
                   BusbarGeometryUtility.Project(selectedSegment.P2, heightDirection)
                ? new Segment3d(selectedSegment.P1.Copy(), selectedSegment.P2.Copy())
                : new Segment3d(selectedSegment.P2.Copy(), selectedSegment.P1.Copy());
        }

        private static Vector3d FindInitialThicknessDirection(
            ComponentInto virtualComponent,
            Segment3d firstEccentricBaselineSegment)
        {
            if (virtualComponent?.BodyIntos == null)
            {
                return null;
            }

            return virtualComponent.BodyIntos
                .Where(bodyInto => bodyInto?.FaceIntos != null)
                .SelectMany(bodyInto => bodyInto.FaceIntos)
                .Where(faceInto => faceInto?.IsPlane == true && faceInto.FacePlane != null)
                .Where(faceInto =>
                    firstEccentricBaselineSegment.P1.DistanceTo(faceInto.FacePlane) <= 0.5d &&
                    firstEccentricBaselineSegment.P2.DistanceTo(faceInto.FacePlane) <= 0.5d)
                .OrderByDescending(faceInto => faceInto.EdgeIntos?.Count(edgeInto =>
                    edgeInto?.CurveType == SwCurveType.Line && edgeInto.Segment != null) ?? 0)
                .Select(faceInto => faceInto.FacePlane.Normal.Negate())
                .FirstOrDefault(normal => normal != null && normal.Norm > 0.001d);
        }

        private static bool TryReadBusbarSection(
            Component2 component,
            out double busbarWidth,
            out double busbarThickness)
        {
            busbarWidth = 0d;
            busbarThickness = 0d;
            ModelDoc2 componentModel;
            try
            {
                componentModel = component?.IGetModelDoc();
            }
            catch (Exception componentModelException)
            {
                FaceToWallPreviewTrace.Error(
                    "柜顶伸出排-获取规格模型",
                    componentModelException,
                    "组件=" + BusbarGeometryUtility.SafeName(component));
                componentModel = null;
            }

            if (componentModel == null)
            {
                return false;
            }

            var propertyText = ReadCustomProperty(
                componentModel,
                SafeReferencedConfiguration(component),
                BusbarSpecificationPropertyName);
            if (string.IsNullOrWhiteSpace(propertyText))
            {
                propertyText = ReadCustomProperty(
                    componentModel,
                    string.Empty,
                    BusbarSpecificationPropertyName);
            }

            var numberMatches = Regex.Matches(propertyText ?? string.Empty, @"\d+(?:\.\d+)?");
            if (numberMatches.Count < 2)
            {
                return false;
            }

            var firstSectionIndex = numberMatches.Count >= 3 ? numberMatches.Count - 2 : 0;
            if (!TryParseDouble(numberMatches[firstSectionIndex].Value, out var firstSectionValue) ||
                !TryParseDouble(numberMatches[firstSectionIndex + 1].Value, out var secondSectionValue))
            {
                return false;
            }

            busbarWidth = Math.Max(firstSectionValue, secondSectionValue);
            busbarThickness = Math.Min(firstSectionValue, secondSectionValue);
            return busbarWidth > 0d && busbarThickness > 0d;
        }

        private static string ReadCustomProperty(
            ModelDoc2 componentModel,
            string configurationName,
            string propertyName)
        {
            try
            {
                var propertyManager = componentModel.Extension.CustomPropertyManager[
                    configurationName ?? string.Empty];
                if (propertyManager == null)
                {
                    return null;
                }

                propertyManager.Get4(
                    propertyName,
                    false,
                    out var rawPropertyValue,
                    out var resolvedPropertyValue);
                return string.IsNullOrWhiteSpace(resolvedPropertyValue)
                    ? rawPropertyValue
                    : resolvedPropertyValue;
            }
            catch (Exception propertyReadException)
            {
                FaceToWallPreviewTrace.Error(
                    "柜顶伸出排-读取自定义属性",
                    propertyReadException,
                    "配置=" + (configurationName ?? string.Empty) +
                    "，属性=" + propertyName +
                    "，模型运行时类型=" +
                    (componentModel == null
                        ? "null"
                        : ((object)componentModel).GetType().FullName));
                return null;
            }
        }

        private static string SafeReferencedConfiguration(Component2 component)
        {
            try
            {
                return component?.ReferencedConfiguration ?? string.Empty;
            }
            catch (Exception referencedConfigurationException)
            {
                FaceToWallPreviewTrace.Error(
                    "柜顶伸出排-读取引用配置",
                    referencedConfigurationException,
                    "组件=" + BusbarGeometryUtility.SafeName(component));
                return string.Empty;
            }
        }

        private static bool TryParseDouble(string value, out double parsedValue)
        {
            return double.TryParse(
                       value,
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out parsedValue) ||
                   double.TryParse(value, out parsedValue);
        }
    }
}
