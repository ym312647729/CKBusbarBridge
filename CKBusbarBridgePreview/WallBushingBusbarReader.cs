using GeometRi;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 将穿墙套管实体中的平面直线几何识别为 I 排中心线。
    /// 厚度取重复出现的最短直线，单根排由同一实体内的一对长×宽外侧平面确定。
    /// </summary>
    public static class WallBushingBusbarReader
    {
        private const double DimensionTolerance = 0.25d;

        public static IReadOnlyList<ModeledBusbar> Read(
            AssemblyPartCatalog partCatalog,
            ISldWorks swApp)
        {
            return Read(
                partCatalog,
                swApp,
                BusbarBridgeAxisSetting.CreateDefault());
        }

        public static IReadOnlyList<ModeledBusbar> Read(
            AssemblyPartCatalog partCatalog,
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting)
        {
            if (partCatalog == null) throw new ArgumentNullException(nameof(partCatalog));
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));
            axisSetting = axisSetting ?? BusbarBridgeAxisSetting.CreateDefault();

            var wallBushingComponents = partCatalog.GetComponents(SystemPartType_e.WallBushing);
            if (wallBushingComponents.Count == 0)
            {
                throw new InvalidOperationException("没有通过“RD类型/名称/Description”识别到穿墙套管。");
            }

            var recognizedBusbars = new List<ModeledBusbar>();
            var failedBushingNames = new List<string>();
            foreach (var wallBushingComponent in wallBushingComponents)
            {
                var bushingCandidates = new List<Component2> { wallBushingComponent };
                bushingCandidates.AddRange(partCatalog.GetAllDescendants(wallBushingComponent));

                var bushingBusbars = new List<ModeledBusbar>();
                foreach (var bushingCandidate in bushingCandidates)
                {
                    var virtualComponent = bushingCandidate.GetComponentInto(swApp);
                    if (virtualComponent == null)
                    {
                        continue;
                    }

                    var virtualComponentBusbars = AnalyzeVirtualComponent(
                        virtualComponent,
                        axisSetting);
                    if (virtualComponentBusbars.Count > 0)
                    {
                        FaceToWallPreviewTrace.Info(
                            "穿墙套管识别-虚拟组件",
                            "组件=" + BusbarGeometryUtility.SafeName(bushingCandidate) +
                            "，候选=" + virtualComponentBusbars.Count +
                            "，" + FormatBusbars(virtualComponentBusbars));
                    }

                    bushingBusbars.AddRange(virtualComponentBusbars);
                }

                if (bushingBusbars.Count == 0)
                {
                    failedBushingNames.Add(BusbarGeometryUtility.SafeName(wallBushingComponent));
                    continue;
                }

                var deduplicatedBushingBusbars = DeduplicateBusbars(bushingBusbars);
                FaceToWallPreviewTrace.Info(
                    "穿墙套管识别-组件完成",
                    "组件=" + BusbarGeometryUtility.SafeName(wallBushingComponent) +
                    "，铜排候选=" + deduplicatedBushingBusbars.Count +
                    "，" + FormatBusbars(deduplicatedBushingBusbars));
                recognizedBusbars.AddRange(deduplicatedBushingBusbars);
            }

            if (recognizedBusbars.Count == 0)
            {
                throw new InvalidOperationException(
                    "穿墙套管中没有识别出长×宽×厚的 I 排几何。未识别组件：" +
                    string.Join("、", failedBushingNames));
            }

            if (failedBushingNames.Count > 0)
            {
                throw new InvalidOperationException(
                    "部分穿墙套管没有识别出可搭接的 I 排几何：" +
                    string.Join("、", failedBushingNames));
            }

            return DeduplicateBusbars(recognizedBusbars).AsReadOnly();
        }

        /// <summary>识别已冻结的平面/直线快照；此入口拒绝任何SolidWorks对象引用。</summary>
        public static IReadOnlyList<ModeledBusbar> AnalyzeSnapshot(ComponentInto snapshot, BusbarBridgeAxisSetting axisSetting)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.swApp != null || snapshot.Component != null || snapshot.BodyIntos.Any(body => body.Body != null ||
                body.Face2s.Any(face => face != null) || body.FaceIntos.Any(face => face.Face != null || face.Edges.Any(edge => edge != null) ||
                    face.EdgeIntos.Any(edge => edge.swApp != null || edge.Comp != null || edge.Edge != null || edge.Curve != null))))
                throw new InvalidOperationException("套管冻结快照不能携带SolidWorks对象。");
            var recognized = AnalyzeVirtualComponent(snapshot, axisSetting);
            for (var index = 0; index < recognized.Count; index++) recognized[index].SourceIdentity = snapshot.Name + ":" + index;
            return recognized;
        }

        internal static IReadOnlyList<ModeledBusbar> AnalyzeVirtualComponent(
            ComponentInto virtualComponent,
            BusbarBridgeAxisSetting axisSetting)
        {
            if (virtualComponent == null)
            {
                throw new ArgumentNullException(nameof(virtualComponent));
            }

            axisSetting = axisSetting ?? BusbarBridgeAxisSetting.CreateDefault();
            var planarFaces = virtualComponent.BodyIntos
                .Where(bodyInto => bodyInto?.FaceIntos != null)
                .SelectMany(bodyInto => bodyInto.FaceIntos)
                .Where(faceInto => faceInto?.IsPlane == true &&
                                   faceInto.FacePlane != null &&
                                   faceInto.EdgeIntos != null &&
                                   faceInto.EdgeIntos.Any(edgeInto =>
                                       edgeInto?.CurveType == SwCurveType.Line &&
                                       edgeInto.Segment != null))
                .ToList();
            if (planarFaces.Count == 0)
            {
                return new List<ModeledBusbar>().AsReadOnly();
            }

            var allStraightSegments = BusbarGeometryUtility.DistinctSegments(
                planarFaces.SelectMany(faceInto => faceInto.EdgeIntos)
                    .Where(edgeInto => edgeInto?.CurveType == SwCurveType.Line)
                    .Select(edgeInto => edgeInto.Segment));
            var thicknessHypotheses = BuildThicknessHypotheses(allStraightSegments)
                .Where(thicknessHypothesis =>
                    BusbarGeometryUtility.AreParallel(
                        thicknessHypothesis.Direction,
                        axisSetting.HeightVector))
                .ToList();
            var analyzedHypotheses = new List<HypothesisAnalysis>();
            foreach (var thicknessHypothesis in thicknessHypotheses)
            {
                var hypothesisBusbars = new List<ModeledBusbar>();
                foreach (var virtualBody in virtualComponent.BodyIntos.Where(bodyInto => bodyInto != null))
                {
                    hypothesisBusbars.AddRange(CreateBodyBusbars(
                        virtualComponent,
                        virtualBody,
                        thicknessHypothesis,
                        axisSetting));
                }

                if (hypothesisBusbars.Count > 0)
                {
                    analyzedHypotheses.Add(new HypothesisAnalysis
                    {
                        Thickness = thicknessHypothesis.Thickness,
                        Busbars = DeduplicateBusbars(hypothesisBusbars)
                    });
                }
            }

            var orderedHypotheses = analyzedHypotheses
                .OrderByDescending(hypothesisAnalysis =>
                    hypothesisAnalysis.Busbars.Count)
                .ThenBy(hypothesisAnalysis => hypothesisAnalysis.Thickness)
                .ToList();
            var selectedHypothesis = orderedHypotheses.FirstOrDefault();
            if (orderedHypotheses.Count > 1 &&
                orderedHypotheses[0].Busbars.Count ==
                orderedHypotheses[1].Busbars.Count &&
                Math.Abs(
                    orderedHypotheses[0].Thickness -
                    orderedHypotheses[1].Thickness) > DimensionTolerance)
            {
                throw new InvalidOperationException(
                    "穿墙套管存在多个数量相同的排厚候选（" +
                    orderedHypotheses[0].Thickness.ToString("0.###") + " mm / " +
                    orderedHypotheses[1].Thickness.ToString("0.###") +
                    " mm），无法唯一确定铜排实体。");
            }

            return selectedHypothesis?.Busbars.AsReadOnly() ??
                new List<ModeledBusbar>().AsReadOnly();
        }

        private static List<ThicknessHypothesis> BuildThicknessHypotheses(
            IEnumerable<Segment3d> straightSegments)
        {
            var lengthGroups = straightSegments
                .Where(segment => segment != null && segment.Length > 0.5d)
                .GroupBy(segment => Math.Round(segment.Length, 1))
                .Where(group => group.Count() >= 4)
                .OrderBy(group => group.Key)
                .ToList();
            var result = new List<ThicknessHypothesis>();
            foreach (var lengthGroup in lengthGroups)
            {
                var directionGroups = new List<List<Segment3d>>();
                foreach (var segment in lengthGroup)
                {
                    var normalizedDirection = BusbarGeometryUtility.Canonicalize(segment.ToVector);
                    var matchingGroup = directionGroups.FirstOrDefault(group =>
                        BusbarGeometryUtility.AreParallel(group[0].ToVector, normalizedDirection));
                    if (matchingGroup == null)
                    {
                        matchingGroup = new List<Segment3d>();
                        directionGroups.Add(matchingGroup);
                    }

                    matchingGroup.Add(segment);
                }

                foreach (var directionGroup in directionGroups
                             .Where(group => group.Count >= 4)
                             .OrderByDescending(group => group.Count))
                {
                    result.Add(new ThicknessHypothesis
                    {
                        Thickness = directionGroup.Average(segment => segment.Length),
                        Direction = BusbarGeometryUtility.Canonicalize(
                            directionGroup[0].ToVector)
                    });
                }
            }

            return result;
        }

        private static IEnumerable<ModeledBusbar> CreateBodyBusbars(
            ComponentInto virtualComponent,
            BodyInto virtualBody,
            ThicknessHypothesis thicknessHypothesis,
            BusbarBridgeAxisSetting axisSetting)
        {
            var rectangularBroadFaces = virtualBody.FaceIntos
                .Where(faceInto => faceInto?.IsPlane == true &&
                                       faceInto.FacePlane != null &&
                                       BusbarGeometryUtility.AreParallel(
                                           faceInto.FacePlane.Normal,
                                           thicknessHypothesis.Direction))
                .Select(faceInto => TryCreateRectangleFace(
                    faceInto,
                    axisSetting))
                .Where(rectangleFace => rectangleFace != null &&
                                        rectangleFace.Length > thicknessHypothesis.Thickness + DimensionTolerance &&
                                        rectangleFace.Width > thicknessHypothesis.Thickness + DimensionTolerance &&
                                        rectangleFace.Length > DimensionTolerance &&
                                        rectangleFace.Width > DimensionTolerance)
                .ToList();
            if (rectangularBroadFaces.Count < 2)
            {
                return Enumerable.Empty<ModeledBusbar>();
            }

            var result = new List<ModeledBusbar>();
            var usedFaceIndexes = new HashSet<int>();
            for (var firstFaceIndex = 0;
                 firstFaceIndex < rectangularBroadFaces.Count - 1;
                 firstFaceIndex++)
            {
                if (usedFaceIndexes.Contains(firstFaceIndex))
                {
                    continue;
                }

                var firstFace = rectangularBroadFaces[firstFaceIndex];
                var matchingFaceIndex = -1;
                var bestSeparationError = double.MaxValue;
                for (var secondFaceIndex = firstFaceIndex + 1;
                     secondFaceIndex < rectangularBroadFaces.Count;
                     secondFaceIndex++)
                {
                    if (usedFaceIndexes.Contains(secondFaceIndex))
                    {
                        continue;
                    }

                    var secondFace = rectangularBroadFaces[secondFaceIndex];
                    if (Math.Abs(firstFace.Length - secondFace.Length) > DimensionTolerance ||
                        Math.Abs(firstFace.Width - secondFace.Width) > DimensionTolerance ||
                        !BusbarGeometryUtility.AreParallel(
                            firstFace.LengthDirection,
                            secondFace.LengthDirection) ||
                        !BusbarGeometryUtility.AreParallel(
                            firstFace.WidthDirection,
                            secondFace.WidthDirection))
                    {
                        continue;
                    }

                    var faceSeparationVector = new Vector3d(firstFace.Center, secondFace.Center);
                    var faceSeparation = faceSeparationVector.Norm;
                    if (faceSeparation < 0.001d ||
                        !BusbarGeometryUtility.AreParallel(
                            faceSeparationVector,
                            thicknessHypothesis.Direction))
                    {
                        continue;
                    }

                    var separationError = Math.Abs(faceSeparation - thicknessHypothesis.Thickness);
                    if (separationError <= DimensionTolerance && separationError < bestSeparationError)
                    {
                        matchingFaceIndex = secondFaceIndex;
                        bestSeparationError = separationError;
                    }
                }

                if (matchingFaceIndex < 0)
                {
                    continue;
                }

                usedFaceIndexes.Add(firstFaceIndex);
                usedFaceIndexes.Add(matchingFaceIndex);
                var secondMatchedFace = rectangularBroadFaces[matchingFaceIndex];
                var busbarCenter = BusbarGeometryUtility.Average(
                    new[] { firstFace.Center, secondMatchedFace.Center });
                var lengthDirection = axisSetting.LengthVector.Normalized;
                var thicknessDirection = axisSetting.LengthVector
                    .Cross(axisSetting.WidthVector)
                    .Normalized;
                var startPoint = PlanarBendBusbarSetting.Translate(
                    busbarCenter,
                    lengthDirection,
                    -firstFace.Length / 2d);
                var endPoint = PlanarBendBusbarSetting.Translate(
                    busbarCenter,
                    lengthDirection,
                    firstFace.Length / 2d);

                result.Add(new ModeledBusbar
                {
                    Component = virtualComponent.Component,
                    CenterPoint = busbarCenter,
                    TerminalSegment = new Segment3d(startPoint.Copy(), endPoint.Copy()),
                    CenterlineSetting = new PlanarBendBusbarSetting
                    {
                        IsCenter = true,
                        GroupKey = BusbarGeometryUtility.SafeName(virtualComponent.Component),
                        StartBusbarVector = thicknessDirection,
                        WidthVector = axisSetting.WidthVector.Normalized,
                        PointList = new List<Point3d> { startPoint, endPoint },
                        BusBarThickness = thicknessHypothesis.Thickness,
                        BusBarWidth = firstFace.Width,
                        StartLapWidth = firstFace.Width,
                        EndLapWidth = firstFace.Width,
                        BarBendingRadius = Math.Max(10d, thicknessHypothesis.Thickness)
                    }
                });
            }

            return result;
        }

        private static RectangleFace TryCreateRectangleFace(
            FaceInto faceInto,
            BusbarBridgeAxisSetting axisSetting)
        {
            var straightSegments = BusbarGeometryUtility.DistinctSegments(
                faceInto.EdgeIntos
                    .Where(edgeInto => edgeInto?.CurveType == SwCurveType.Line)
                    .Select(edgeInto => edgeInto.Segment));
            var lengthSegments = straightSegments
                .Where(segment => BusbarGeometryUtility.AreParallel(
                    segment.ToVector,
                    axisSetting.LengthVector))
                .ToList();
            var widthSegments = straightSegments
                .Where(segment => BusbarGeometryUtility.AreParallel(
                    segment.ToVector,
                    axisSetting.WidthVector))
                .ToList();
            if (lengthSegments.Count < 2 || widthSegments.Count < 2)
            {
                return null;
            }

            var boundaryPoints = BusbarGeometryUtility.DistinctPoints(
                lengthSegments.Concat(widthSegments)
                    .SelectMany(segment => new[] { segment.P1, segment.P2 }));
            if (boundaryPoints.Count < 4)
            {
                return null;
            }

            var lengthCoordinates = boundaryPoints.Select(point =>
                BusbarGeometryUtility.Project(
                    point,
                    axisSetting.LengthVector)).ToList();
            var widthCoordinates = boundaryPoints.Select(point =>
                BusbarGeometryUtility.Project(
                    point,
                    axisSetting.WidthVector)).ToList();
            var length = lengthCoordinates.Max() - lengthCoordinates.Min();
            var width = widthCoordinates.Max() - widthCoordinates.Min();
            if (length <= DimensionTolerance || width <= DimensionTolerance)
            {
                return null;
            }

            var lengthCenterCoordinate =
                (lengthCoordinates.Min() + lengthCoordinates.Max()) / 2d;
            var widthCenterCoordinate =
                (widthCoordinates.Min() + widthCoordinates.Max()) / 2d;
            var heightCoordinate = BusbarGeometryUtility.Project(
                faceInto.FacePlane.Point,
                axisSetting.HeightVector);

            return new RectangleFace
            {
                Center = CreatePointFromAxisCoordinates(
                    axisSetting,
                    lengthCenterCoordinate,
                    heightCoordinate,
                    widthCenterCoordinate),
                Width = width,
                Length = length,
                WidthDirection = axisSetting.WidthVector.Normalized,
                LengthDirection = axisSetting.LengthVector.Normalized
            };
        }

        private static Point3d CreatePointFromAxisCoordinates(
            BusbarBridgeAxisSetting axisSetting,
            double lengthCoordinate,
            double heightCoordinate,
            double widthCoordinate)
        {
            return new Point3d(
                axisSetting.LengthVector.X * lengthCoordinate +
                axisSetting.HeightVector.X * heightCoordinate +
                axisSetting.WidthVector.X * widthCoordinate,
                axisSetting.LengthVector.Y * lengthCoordinate +
                axisSetting.HeightVector.Y * heightCoordinate +
                axisSetting.WidthVector.Y * widthCoordinate,
                axisSetting.LengthVector.Z * lengthCoordinate +
                axisSetting.HeightVector.Z * heightCoordinate +
                axisSetting.WidthVector.Z * widthCoordinate);
        }

        private static string FormatBusbars(IEnumerable<ModeledBusbar> busbars)
        {
            return string.Join(
                "；",
                (busbars ?? Enumerable.Empty<ModeledBusbar>())
                .Where(busbar =>
                    busbar?.CenterPoint != null &&
                    busbar.CenterlineSetting != null)
                .Select(busbar =>
                    "中心=(" + busbar.CenterPoint.X.ToString("0.###") +
                    "," + busbar.CenterPoint.Y.ToString("0.###") +
                    "," + busbar.CenterPoint.Z.ToString("0.###") +
                    ")，规格=" +
                    busbar.CenterlineSetting.BusBarWidth.ToString("0.###") +
                    "x" +
                    busbar.CenterlineSetting.BusBarThickness.ToString("0.###")));
        }

        private static List<ModeledBusbar> DeduplicateBusbars(
            IEnumerable<ModeledBusbar> sourceBusbars)
        {
            var result = new List<ModeledBusbar>();
            foreach (var sourceBusbar in sourceBusbars.Where(busbar =>
                         busbar?.CenterlineSetting != null && busbar.CenterPoint != null))
            {
                if (result.Any(existingBusbar =>
                        existingBusbar.CenterPoint.DistanceTo(sourceBusbar.CenterPoint) <= DimensionTolerance &&
                        Math.Abs(existingBusbar.CenterlineSetting.BusBarWidth -
                                 sourceBusbar.CenterlineSetting.BusBarWidth) <= DimensionTolerance &&
                        Math.Abs(existingBusbar.CenterlineSetting.BusBarThickness -
                                 sourceBusbar.CenterlineSetting.BusBarThickness) <= DimensionTolerance))
                {
                    continue;
                }

                result.Add(sourceBusbar);
            }

            return result;
        }

        private sealed class ThicknessHypothesis
        {
            public double Thickness { get; set; }
            public Vector3d Direction { get; set; }
        }

        private sealed class HypothesisAnalysis
        {
            public double Thickness { get; set; }
            public List<ModeledBusbar> Busbars { get; set; }
        }

        private sealed class RectangleFace
        {
            public Point3d Center { get; set; }
            public double Width { get; set; }
            public double Length { get; set; }
            public Vector3d WidthDirection { get; set; }
            public Vector3d LengthDirection { get; set; }
        }
    }
}
