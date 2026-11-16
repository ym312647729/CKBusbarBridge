using AutoOpeningHoleBase;
using AutoOpeningHoleDataModel;
using GeometRi;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 铜排大师 BusbarVerticalBendPreview 的项目内移植版本，保留
    /// BendPreview、GetFreeDrawBodies、BendCreator 以及钣金立弯创建方法。
    /// </summary>
    public sealed class BusbarVerticalBendPreview
    {
        private readonly ISldWorks solidWorksApplication;
        private readonly ModelDoc2 assemblyModel;
        private readonly AssemblyDoc assemblyDocument;

        public BusbarVerticalBendPreview(ISldWorks solidWorksApplication)
        {
            this.solidWorksApplication = solidWorksApplication ??
                throw new ArgumentNullException(
                    nameof(solidWorksApplication));
            assemblyModel = solidWorksApplication.IActiveDoc2;
            assemblyDocument = assemblyModel as AssemblyDoc;
        }

        public List<FreeDrawBody> PreviewBodies { get; private set; } =
            new List<FreeDrawBody>();

        public List<FreeDrawBody> fdBodys
        {
            get => PreviewBodies;
            set => PreviewBodies = value ?? new List<FreeDrawBody>();
        }

        public void HideFdBody()
        {
            foreach (var previewBody in PreviewBodies)
            {
                previewBody?.Hide();
            }
        }

        public void ShowFdBody()
        {
            foreach (var previewBody in PreviewBodies)
            {
                previewBody?.Show();
            }
        }

        public List<FreeDrawBody> GetFreeDrawBodies(
            ISldWorks application,
            VerticalBendSetting verticalBendSetting,
            Component2 previewComponent,
            Color previewColor,
            bool setSegmentation = true)
        {
            if (setSegmentation)
            {
                verticalBendSetting.GetWidthVector();
                verticalBendSetting.SetSegmentPoints(false);
            }
            PreviewBodies = verticalBendSetting.GetVerticalBendPreviewBodies(
                application,
                previewColor,
                previewComponent);
            return PreviewBodies;
        }

        public void BendPreview(
            ISldWorks application,
            VerticalBendSetting verticalBendSetting,
            Component2 previewComponent,
            Color previewColor,
            bool setSegmentation = true)
        {
            GetFreeDrawBodies(
                application,
                verticalBendSetting,
                previewComponent,
                previewColor,
                setSegmentation);
        }

        /// <summary>
        /// 按铜排大师 BendCreator 的分段钣金方案生成立弯零件。
        /// </summary>
        public Component2 BendCreator(
            bool useSpecifiedBusbarName,
            string selectedName,
            VerticalBendSetting verticalBendSetting,
            int busbarSectionType = 1)
        {
            EnsureAssemblyAvailable();
            if (verticalBendSetting == null)
            {
                throw new ArgumentNullException(
                    nameof(verticalBendSetting));
            }

            var assemblyPath = assemblyModel.GetPathName();
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                throw new InvalidOperationException(
                    "生成立弯前必须先保存当前装配体。");
            }
            var outputDirectoryPath = Path.GetDirectoryName(assemblyPath);
            var outputBaseName = useSpecifiedBusbarName &&
                                 !string.IsNullOrWhiteSpace(selectedName)
                ? selectedName.Trim()
                : ResolveAvailablePartBaseName(outputDirectoryPath);
            var targetPartPath = Path.Combine(
                outputDirectoryPath,
                outputBaseName + ".SLDPRT");
            var sourceTemplatePath = ResolveFreeVerticalBendTemplatePath();
            return BendCreator(
                sourceTemplatePath,
                targetPartPath,
                verticalBendSetting,
                busbarSectionType);
        }

        /// <summary>
        /// 可供当前母排批量生成流程调用的确定路径重载。
        /// </summary>
        public Component2 BendCreator(
            string sourceTemplatePath,
            string targetPartPath,
            VerticalBendSetting verticalBendSetting,
            int busbarSectionType = 1)
        {
            EnsureAssemblyAvailable();
            ValidateGenerationPaths(sourceTemplatePath, targetPartPath);
            verticalBendSetting.GetWidthVector();
            verticalBendSetting.SetSegmentPoints(false);
            File.Copy(sourceTemplatePath, targetPartPath, false);

            Component2 insertedComponent = null;
            ModelDoc2 generatedPartModel = null;
            var generatedPartDocumentClosed = false;
            var originalInputDimensionPreference =
                solidWorksApplication.GetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            var originalSketchInferencePreference =
                solidWorksApplication.GetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swSketchInference);
            try
            {
                var openErrors = 0;
                var openWarnings = 0;
                generatedPartModel = solidWorksApplication.OpenDoc6(
                    targetPartPath,
                    (int)swDocumentTypes_e.swDocPART,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    string.Empty,
                    ref openErrors,
                    ref openWarnings) as ModelDoc2;
                if (generatedPartModel == null)
                {
                    throw new InvalidOperationException(
                        "无法打开立弯模板副本，错误码=" + openErrors +
                        "，警告码=" + openWarnings + "。");
                }

                solidWorksApplication.ActivateDoc(assemblyModel.GetPathName());
                insertedComponent = assemblyDocument.AddComponent5(
                    targetPartPath,
                    0,
                    string.Empty,
                    false,
                    string.Empty,
                    0d,
                    0d,
                    0d);
                if (insertedComponent == null)
                {
                    throw new InvalidOperationException(
                        "立弯零件插入当前装配体失败。");
                }

                SetComponentTransform(
                    insertedComponent,
                    verticalBendSetting);
                assemblyModel.ClearSelection2(true);
                insertedComponent.Select(false);
                assemblyDocument.FixComponent();
                CreateVerticalBendFeatures(
                    insertedComponent,
                    verticalBendSetting);
                generatedPartModel.EditRebuild3();
                var saveErrors = 0;
                var saveWarnings = 0;
                if (!generatedPartModel.Save3(
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                        ref saveErrors,
                        ref saveWarnings))
                {
                    throw new InvalidOperationException(
                        "立弯零件保存失败，错误码=" + saveErrors +
                        "，警告码=" + saveWarnings + "。");
                }
                solidWorksApplication.CloseDoc(
                    generatedPartModel.GetPathName());
                generatedPartDocumentClosed = true;
                solidWorksApplication.ActivateDoc(
                    assemblyModel.GetPathName());
                assemblyDocument.EditAssembly();

                return insertedComponent;
            }
            catch
            {
                if (insertedComponent == null && File.Exists(targetPartPath))
                {
                    File.Delete(targetPartPath);
                }
                throw;
            }
            finally
            {
                if (generatedPartModel != null &&
                    !generatedPartDocumentClosed)
                {
                    try
                    {
                        solidWorksApplication.CloseDoc(
                            generatedPartModel.GetPathName());
                        generatedPartDocumentClosed = true;
                    }
                    catch (Exception closeGeneratedPartException)
                    {
                        Debug.Print(
                            "关闭生成的立弯零件失败：" +
                            closeGeneratedPartException.Message);
                    }
                }
                try
                {
                    solidWorksApplication.ActivateDoc(
                        assemblyModel.GetPathName());
                    assemblyDocument.EditAssembly();
                }
                catch (Exception restoreAssemblyException)
                {
                    Debug.Print(
                        "恢复立弯所属装配体失败：" +
                        restoreAssemblyException.Message);
                }
                solidWorksApplication.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swInputDimValOnCreate,
                    originalInputDimensionPreference);
                solidWorksApplication.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swSketchInference,
                    originalSketchInferencePreference);
                assemblyModel.SketchManager.AddToDB = false;
            }
        }

        private void CreateVerticalBendFeatures(
            Component2 insertedComponent,
            VerticalBendSetting verticalBendSetting)
        {
            assemblyModel.ClearSelection2(true);
            insertedComponent.Select(false);
            var editPartError = 0;
            if (assemblyDocument.EditPart2(
                    true,
                    true,
                    ref editPartError) < 0)
            {
                throw new InvalidOperationException(
                    "无法进入立弯零件编辑状态，错误码=" +
                    editPartError + "。");
            }
            assemblyModel.SketchManager.AddToDB = true;
            solidWorksApplication.SetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swInputDimValOnCreate,
                false);
            solidWorksApplication.SetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swSketchInference,
                false);

            CreateOriginalPathSketch(insertedComponent, verticalBendSetting);
            var partSegments = ConvertSegmentsToPartCoordinates(
                insertedComponent,
                verticalBendSetting);
            var existingSheetMetalFeatureNames = new List<string>();
            var segmentStartPoints = new List<Point3d>();
            var expandFeatures =
                new List<VerticalBendExpandFeatureData>();
            var lastBarVector = verticalBendSetting.WidthVector
                .ConvertVectorAssemblyToPart(
                    solidWorksApplication,
                    insertedComponent);
            for (var segmentIndex = 0;
                 segmentIndex < partSegments.Count;
                 segmentIndex++)
            {
                var partSegment = partSegments[segmentIndex];
                var segmentPoints = partSegment.Item2;
                segmentStartPoints.Add(segmentPoints[0]);
                var segmentWidthDirection = lastBarVector.Normalized;
                var segmentBarDirection = segmentWidthDirection.Cross(
                    new Vector3d(segmentPoints[0], segmentPoints[1]))
                    .Normalized;
                if (segmentIndex == 0 && !partSegment.Item1)
                {
                    segmentBarDirection = lastBarVector.Negate().Normalized;
                }

                var referencePlane = SetRefPlane3Point(
                    insertedComponent,
                    segmentPoints[0],
                    segmentPoints[1],
                    segmentBarDirection);
                CreateSegmentSketch(
                    insertedComponent,
                    referencePlane,
                    segmentPoints,
                    partSegment.Item1,
                    segmentIndex + 1);
                existingSheetMetalFeatureNames = SetBusBarBendBody(
                    verticalBendSetting,
                    insertedComponent,
                    partSegment,
                    existingSheetMetalFeatureNames,
                    segmentIndex + 1,
                    out var bendFeatureNames);
                expandFeatures.Add(new VerticalBendExpandFeatureData
                {
                    IsPlanarBendSegment = partSegment.Item1,
                    BendFeatureNames = bendFeatureNames
                });
                lastBarVector = partSegment.Item3
                    .ConvertVectorAssemblyToPart(
                        solidWorksApplication,
                        insertedComponent);
            }
            CreateUnfoldFeatures(
                insertedComponent,
                segmentStartPoints,
                expandFeatures);
        }

        private void CreateOriginalPathSketch(
            Component2 insertedComponent,
            VerticalBendSetting verticalBendSetting)
        {
            var partPoints = verticalBendSetting.PointList
                .Select(point => point.ConvertPointInAssemblyToPart1000(
                    solidWorksApplication,
                    insertedComponent).UnitX0001Copy())
                .ToList();
            assemblyModel.Insert3DSketch();
            var activeSketch = assemblyModel.SketchManager.ActiveSketch;
            for (var pointIndex = 0;
                 pointIndex < partPoints.Count - 1;
                 pointIndex++)
            {
                assemblyModel.SketchManager.CreateLine(
                    partPoints[pointIndex].X,
                    partPoints[pointIndex].Y,
                    partPoints[pointIndex].Z,
                    partPoints[pointIndex + 1].X,
                    partPoints[pointIndex + 1].Y,
                    partPoints[pointIndex + 1].Z);
            }
            assemblyModel.SketchManager.InsertSketch(true);
            if (activeSketch is Feature pathFeature)
            {
                pathFeature.Name = "原始草图";
            }
        }

        private List<Tuple<bool, List<Point3d>, Vector3d>>
            ConvertSegmentsToPartCoordinates(
            Component2 insertedComponent,
            VerticalBendSetting verticalBendSetting)
        {
            var convertedSegments =
                new List<Tuple<bool, List<Point3d>, Vector3d>>();
            var lastBarVector = verticalBendSetting.WidthVector
                .ConvertVectorAssemblyToPart(
                    solidWorksApplication,
                    insertedComponent);
            foreach (var sourceSegment in
                     verticalBendSetting.SegmentationPoints)
            {
                var isPlanarBendSegment = sourceSegment.Item1;
                var partPoints = sourceSegment.Item2.Select(point =>
                    point.ConvertPointInAssemblyToPart1000(
                        solidWorksApplication,
                        insertedComponent)).ToList();
                var offsetDirection = lastBarVector.Normalized;
                var barDirection = offsetDirection.Cross(new Vector3d(
                    partPoints[0],
                    partPoints[1])).Negate().Normalized;
                if (!isPlanarBendSegment)
                {
                    offsetDirection = barDirection;
                    partPoints = OffsetPolyline(
                        partPoints,
                        offsetDirection,
                        verticalBendSetting.BusBarWidth / 2d);
                }
                else
                {
                    partPoints = partPoints.Select(point => point.Move(
                        offsetDirection,
                        verticalBendSetting.BusBarWidth / 2d)).ToList();
                }

                convertedSegments.Add(Tuple.Create(
                    isPlanarBendSegment,
                    partPoints,
                    sourceSegment.Item3.Copy()));
                lastBarVector = sourceSegment.Item3
                    .ConvertVectorAssemblyToPart(
                        solidWorksApplication,
                        insertedComponent);
            }
            return convertedSegments;
        }

        private void CreateUnfoldFeatures(
            Component2 insertedComponent,
            IReadOnlyList<Point3d> segmentStartPoints,
            List<VerticalBendExpandFeatureData> expandFeatures)
        {
            if (expandFeatures.Count == 0)
            {
                return;
            }
            var partModel = insertedComponent.IGetModelDoc();
            if (partModel == null)
            {
                throw new InvalidOperationException(
                    "无法取得立弯零件模型以生成展开特征。");
            }
            solidWorksApplication.ActivateDoc(partModel.GetPathName());
            IdentifySegmentVertices(
                partModel,
                segmentStartPoints,
                expandFeatures);
            ResolveExpandFaces(expandFeatures);
            foreach (var expandFeature in expandFeatures)
            {
                expandFeature.Model = partModel;
                expandFeature.CapturePersistentReferences();
                InsertSheetMetalUnfold(partModel, expandFeature);
            }
            MoveUnfoldBodies(partModel, expandFeatures);
            var unfoldFeatures = TraverseUnfold(partModel);
            foreach (var unfoldFeature in unfoldFeatures)
            {
                unfoldFeature.SetSuppression2(
                    (int)swFeatureSuppressionAction_e.swSuppressFeature,
                    (int)swInConfigurationOpts_e.swThisConfiguration,
                    null);
            }
        }

        private static void IdentifySegmentVertices(
            ModelDoc2 partModel,
            IReadOnlyList<Point3d> segmentStartPoints,
            IList<VerticalBendExpandFeatureData> expandFeatures)
        {
            var partDocument = partModel as PartDoc;
            var solidBodies = (partDocument?.GetBodies(
                    (int)swBodyType_e.swSolidBody) as object[] ??
                Array.Empty<object>()).OfType<Body2>().ToList();
            var bodyVertices = solidBodies.Select(body =>
                (body.GetVertices() as object[] ?? Array.Empty<object>())
                    .OfType<Vertex>().ToList()).ToList();
            if (bodyVertices.Count < expandFeatures.Count)
            {
                throw new InvalidOperationException(
                    "立弯钣金实体数量少于分段数量，无法生成展开特征。");
            }
            var orderedBodyVertices = new List<List<Vertex>>();
            for (var pointIndex = 0;
                 pointIndex < segmentStartPoints.Count - 1;
                 pointIndex++)
            {
                var startPoint = segmentStartPoints[pointIndex];
                var nextPoint = segmentStartPoints[pointIndex + 1];
                var matchingVertices = bodyVertices.FirstOrDefault(vertices =>
                    vertices.Any(vertex => GetVertexPoint(vertex)
                        .DistanceTo(startPoint) < 0.01d) &&
                    vertices.Any(vertex => GetVertexPoint(vertex)
                        .DistanceTo(nextPoint) < 0.01d));
                if (matchingVertices == null)
                {
                    throw new InvalidOperationException(
                        "无法按立弯分段端点匹配钣金实体。");
                }
                orderedBodyVertices.Add(matchingVertices);
            }
            var finalBodyVertices = bodyVertices.FirstOrDefault(vertices =>
                !orderedBodyVertices.Any(orderedVertices =>
                    orderedVertices.SequenceEqual(vertices)));
            if (finalBodyVertices != null)
            {
                orderedBodyVertices.Add(finalBodyVertices);
            }
            if (orderedBodyVertices.Count != expandFeatures.Count)
            {
                throw new InvalidOperationException(
                    "立弯分段与钣金实体未能一一对应。");
            }

            for (var segmentIndex = 0;
                 segmentIndex < expandFeatures.Count;
                 segmentIndex++)
            {
                var startPoint = segmentStartPoints[segmentIndex];
                var matchingStartVertex = orderedBodyVertices[segmentIndex]
                    .FirstOrDefault(vertex => GetVertexPoint(vertex)
                        .DistanceTo(startPoint) < 0.1d);
                if (matchingStartVertex == null)
                {
                    throw new InvalidOperationException(
                        "无法识别立弯第" + (segmentIndex + 1) +
                        "段的起始顶点。");
                }
                expandFeatures[segmentIndex].StartVertex = matchingStartVertex;
                expandFeatures[segmentIndex].EndVertex =
                    segmentIndex < segmentStartPoints.Count - 1
                        ? orderedBodyVertices[segmentIndex]
                            .FirstOrDefault(vertex => GetVertexPoint(vertex)
                                .DistanceTo(segmentStartPoints[
                                    segmentIndex + 1]) < 0.1d)
                        : orderedBodyVertices[segmentIndex]
                            .FirstOrDefault(vertex =>
                                !ReferenceEquals(
                                    vertex,
                                    matchingStartVertex));
            }
        }

        private static void ResolveExpandFaces(
            IList<VerticalBendExpandFeatureData> expandFeatures)
        {
            var originalGeometryTolerance = GeometRi3D.Tolerance;
            try
            {
                GeometRi3D.Tolerance = 0.1d;
                for (var segmentIndex = 0;
                     segmentIndex < expandFeatures.Count;
                     segmentIndex++)
                {
                    var expandFeature = expandFeatures[segmentIndex];
                    expandFeature.StartFaces = GetAdjacentFaces(
                        expandFeature.StartVertex);
                    if (segmentIndex != expandFeatures.Count - 1)
                    {
                        expandFeature.EndFaces = GetAdjacentFaces(
                            expandFeature.EndVertex);
                    }
                    if (segmentIndex == 0)
                    {
                        expandFeature.ExpandFace =
                            expandFeature.StartFaces.FirstOrDefault(face =>
                                face.ToPlane3d().Normal.IsParallelToTol(
                                    new Vector3d(0d, 0d, 1d),
                                    0.1d));
                        continue;
                    }

                    var previousExpandFeature =
                        expandFeatures[segmentIndex - 1];
                    foreach (var previousFace in
                             previousExpandFeature.EndFaces)
                    {
                        expandFeature.StartCoincidenceExpandFace =
                            expandFeature.StartFaces.FirstOrDefault(
                                currentFace => previousFace.ToPlane3d()
                                    .Normal.Normalized.EqualsToTol(
                                        currentFace.ToPlane3d().Normal
                                            .Negate(),
                                        0.1d));
                        if (expandFeature.StartCoincidenceExpandFace == null)
                        {
                            continue;
                        }
                        previousExpandFeature.EndCoincidenceExpandFace =
                            previousFace;
                        break;
                    }
                    if (previousExpandFeature.EndCoincidenceExpandFace == null ||
                        expandFeature.StartCoincidenceExpandFace == null)
                    {
                        throw new InvalidOperationException(
                            "无法识别相邻立弯分段的展开重合面。");
                    }
                    if (expandFeature.IsPlanarBendSegment)
                    {
                        expandFeature.ExpandFace =
                            expandFeature.StartCoincidenceExpandFace;
                    }
                    var previousRemainingFaces = previousExpandFeature.EndFaces
                        .Where(face => !ReferenceEquals(
                            face,
                            previousExpandFeature
                                .EndCoincidenceExpandFace))
                        .OrderBy(face => face.GetArea()).ToList();
                    var currentRemainingFaces = expandFeature.StartFaces
                        .Where(face => !ReferenceEquals(
                            face,
                            expandFeature.StartCoincidenceExpandFace))
                        .OrderBy(face => face.GetArea()).ToList();
                    if (previousRemainingFaces.Count < 2 ||
                        currentRemainingFaces.Count < 2)
                    {
                        throw new InvalidOperationException(
                            "立弯分段连接面数量不足，无法拼接展开实体。");
                    }
                    previousExpandFeature.EndOtherFace =
                        previousRemainingFaces[0];
                    expandFeature.StartOtherFace = currentRemainingFaces[0];
                    previousExpandFeature.EndCoincidenceFace =
                        previousRemainingFaces[1];
                    expandFeature.StartCoincidenceFace =
                        currentRemainingFaces[1];
                    if (!expandFeature.IsPlanarBendSegment)
                    {
                        expandFeature.ExpandFace =
                            expandFeature.StartOtherFace;
                    }
                }
            }
            finally
            {
                GeometRi3D.Tolerance = originalGeometryTolerance;
            }
        }

        private static List<Face2> GetAdjacentFaces(Vertex vertex)
        {
            if (vertex == null)
            {
                return new List<Face2>();
            }
            return (vertex.GetEdges() as object[] ?? Array.Empty<object>())
                .OfType<Edge>()
                .SelectMany(edge =>
                    (edge.GetTwoAdjacentFaces2() as object[] ??
                     Array.Empty<object>()).OfType<Face2>())
                .Distinct()
                .ToList();
        }

        public static void InsertSheetMetalUnfold(
            ModelDoc2 partModel,
            VerticalBendExpandFeatureData expandFeature)
        {
            if (expandFeature.ExpandFace == null)
            {
                throw new InvalidOperationException(
                    "立弯分段缺少展开基准面。");
            }
            partModel.ClearSelection2(true);
            ((Entity)expandFeature.ExpandFace).Select2(false, 1);
            foreach (var bendFeatureName in
                     expandFeature.BendFeatureNames)
            {
                partModel.Extension.SelectByID2(
                    bendFeatureName,
                    "BODYFEATURE",
                    0,
                    0d,
                    0d,
                    true,
                    4,
                    null,
                    0);
            }
            partModel.InsertSheetMetalUnfold();
        }

        private static void MoveUnfoldBodies(
            ModelDoc2 partModel,
            IReadOnlyList<VerticalBendExpandFeatureData> expandFeatures)
        {
            for (var segmentIndex = 1;
                 segmentIndex < expandFeatures.Count;
                 segmentIndex++)
            {
                partModel.ClearSelection2(true);
                var currentExpandFeature = expandFeatures[segmentIndex];
                var previousExpandFeature = expandFeatures[segmentIndex - 1];
                var currentBody = currentExpandFeature.ResolveFace(
                    currentExpandFeature.ExpandFaceId)?.IGetBody();
                if (currentBody == null || !partModel.Extension.SelectByID2(
                        currentBody.Name,
                        "SOLIDBODY",
                        0d,
                        0d,
                        0d,
                        false,
                        1,
                        null,
                        0))
                {
                    throw new InvalidOperationException(
                        "无法选择立弯展开实体进行拼接。");
                }
                var moveBodyFeature = partModel.FeatureManager
                    .InsertMoveCopyBody2(
                        0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d,
                        false,
                        1);
                var moveBodyDefinition = moveBodyFeature?.IGetDefinition()
                    as IMoveCopyBodyFeatureData;
                if (moveBodyDefinition == null)
                {
                    throw new InvalidOperationException(
                        "立弯展开实体移动特征创建失败。");
                }
                AddMoveBodyMate(
                    partModel,
                    moveBodyDefinition,
                    previousExpandFeature.ResolveFace(
                        previousExpandFeature.EndCoincidenceExpandFaceId),
                    currentExpandFeature.ResolveFace(
                        currentExpandFeature
                            .StartCoincidenceExpandFaceId),
                    1);
                AddMoveBodyMate(
                    partModel,
                    moveBodyDefinition,
                    previousExpandFeature.ResolveFace(
                        previousExpandFeature.EndCoincidenceFaceId),
                    currentExpandFeature.ResolveFace(
                        currentExpandFeature.StartCoincidenceFaceId),
                    0);
                AddMoveBodyMate(
                    partModel,
                    moveBodyDefinition,
                    previousExpandFeature.ResolveFace(
                        previousExpandFeature.EndOtherFaceId),
                    currentExpandFeature.ResolveFace(
                        currentExpandFeature.StartOtherFaceId),
                    0);
                moveBodyFeature.ModifyDefinition(
                    moveBodyDefinition,
                    partModel,
                    null);
            }
        }

        private static void AddMoveBodyMate(
            ModelDoc2 partModel,
            IMoveCopyBodyFeatureData moveBodyDefinition,
            Face2 firstFace,
            Face2 secondFace,
            int mateAlignment)
        {
            if (firstFace == null || secondFace == null)
            {
                throw new InvalidOperationException(
                    "立弯展开实体拼接面无效。");
            }
            partModel.ClearSelection2(true);
            ((Entity)firstFace).Select2(false, 1);
            ((Entity)secondFace).Select2(true, 1);
            moveBodyDefinition.AddMate(
                null,
                0,
                mateAlignment,
                0d,
                0d,
                out _);
        }

        private static List<Feature> TraverseUnfold(ModelDoc2 partModel)
        {
            var unfoldFeatures = new List<Feature>();
            for (var feature = partModel.IFirstFeature();
                 feature != null;
                 feature = feature.IGetNextFeature())
            {
                if (Regex.IsMatch(feature.Name, @"^展开\d+$") ||
                    Regex.IsMatch(feature.Name, @"^实体-移动/复制\d+$"))
                {
                    unfoldFeatures.Add(feature);
                }
            }
            return unfoldFeatures;
        }

        public List<string> SetBusBarBendBody(
            VerticalBendSetting verticalBendSetting,
            Component2 insertedComponent,
            Tuple<bool, List<Point3d>, Vector3d> segment,
            List<string> existingSheetMetalFeatureNames,
            int segmentIndex,
            out List<string> bendFeatureNames)
        {
            var customBendAllowance = assemblyModel.FeatureManager
                .CreateCustomBendAllowance();
            customBendAllowance.KFactor = verticalBendSetting.K;
            var isPlanarBendSegment = segment.Item1;
            var flangeWidth = isPlanarBendSegment
                ? verticalBendSetting.BusBarWidth
                : verticalBendSetting.BusBarThickness;
            var createdFeature = assemblyModel.FeatureManager
                .InsertSheetMetalBaseFlange2(
                    verticalBendSetting.BusBarThickness / 1000d,
                    true,
                    verticalBendSetting.BarBendingRadius / 1000d,
                    flangeWidth / 1000d,
                    0.01d,
                    isPlanarBendSegment,
                    0,
                    0,
                    1,
                    customBendAllowance,
                    false,
                    2,
                    0.0001d,
                    0.0001d,
                    0.5d,
                    true,
                    false,
                    true,
                    true);
            if (createdFeature == null)
            {
                throw new InvalidOperationException(
                    "立弯第" + segmentIndex +
                    "段钣金基体创建失败。");
            }
            createdFeature.Name =
                (isPlanarBendSegment ? string.Empty : "立弯") +
                "铜排基体" + segmentIndex;

            var sheetMetalFeatureNames = GetAllFeatureNamesByType(
                insertedComponent.IGetModelDoc(),
                "SheetMetal");
            var newSheetMetalFeatureName = sheetMetalFeatureNames
                .FirstOrDefault(featureName =>
                    !existingSheetMetalFeatureNames.Contains(featureName));
            if (!isPlanarBendSegment &&
                !string.IsNullOrWhiteSpace(newSheetMetalFeatureName))
            {
                var partDocument = insertedComponent.IGetModelDoc() as PartDoc;
                var sheetMetalFeature = partDocument?.FeatureByName(
                    newSheetMetalFeatureName) as Feature;
                if (sheetMetalFeature != null)
                {
                    sheetMetalFeature.Name = "立弯钣金" + segmentIndex;
                    ResetThicknessOverride(
                        insertedComponent,
                        sheetMetalFeature.Name,
                        true,
                        verticalBendSetting);
                }
            }

            var bendFeatures = new List<Feature>();
            var childFeature = createdFeature.IGetFirstSubFeature()
                ?.GetSpecificFeature2() as Feature;
            while (childFeature != null)
            {
                if (childFeature.Name.Contains("折弯"))
                {
                    bendFeatures.Add(childFeature);
                }
                childFeature = childFeature.IGetNextSubFeature();
            }
            bendFeatureNames = bendFeatures.Select(feature => feature.Name)
                .ToList();
            return sheetMetalFeatureNames;
        }

        public bool ResetThicknessOverride(
            Component2 insertedComponent,
            string featureName,
            bool overrideDefaults,
            VerticalBendSetting verticalBendSetting)
        {
            var partModel = insertedComponent.IGetModelDoc();
            var partDocument = partModel as PartDoc;
            var sheetMetalFeature = partDocument?.FeatureByName(featureName)
                as Feature;
            if (sheetMetalFeature == null ||
                sheetMetalFeature.GetTypeName() != "SheetMetal")
            {
                return false;
            }

            var sheetMetalDefinition = sheetMetalFeature.GetDefinition()
                as ISheetMetalFeatureData;
            if (sheetMetalDefinition == null)
            {
                return false;
            }
            sheetMetalDefinition.IAccessSelections2(
                partModel,
                insertedComponent);
            try
            {
                sheetMetalDefinition.SetOverrideDefaultParameter(
                    overrideDefaults);
                sheetMetalDefinition.BendRadius =
                    verticalBendSetting.BusBarVerticalRadius / 1000d;
                sheetMetalDefinition.Thickness =
                    verticalBendSetting.BusBarWidth / 1000d;
                return sheetMetalFeature.ModifyDefinition(
                    sheetMetalDefinition,
                    assemblyModel,
                    insertedComponent);
            }
            finally
            {
                sheetMetalDefinition.ReleaseSelectionAccess();
            }
        }

        public Feature SetRefPlane3Point(
            Component2 insertedComponent,
            Point3d firstPoint,
            Point3d secondPoint,
            Vector3d planeDirection)
        {
            assemblyModel.ClearSelection2(true);
            assemblyModel.Insert3DSketch();
            var sketchFeature = assemblyModel.IGetActiveSketch2() as Feature;
            var sketchName = sketchFeature?.Name;
            if (string.IsNullOrWhiteSpace(sketchName))
            {
                throw new InvalidOperationException(
                    "创建立弯参考面辅助草图失败。");
            }
            var thirdPoint = firstPoint.Move(planeDirection, 100d);
            var sketchPointIds = DrawPoints(
                assemblyModel,
                new List<Point3d>
                {
                    firstPoint,
                    secondPoint,
                    thirdPoint
                });
            assemblyModel.SketchManager.InsertSketch(true);
            assemblyModel.BlankSketch();
            assemblyModel.ClearSelection2(true);
            for (var pointIndex = 0;
                 pointIndex < sketchPointIds.Count;
                 pointIndex++)
            {
                assemblyModel.Extension.SelectByID2(
                    "Point" + sketchPointIds[pointIndex] + "@" +
                    sketchName + "@" +
                    insertedComponent.GetSelectByIDString(),
                    "EXTSKETCHPOINT",
                    0d,
                    0d,
                    0d,
                    true,
                    pointIndex,
                    null,
                    0);
            }
            var referencePlane = assemblyModel.FeatureManager.InsertRefPlane(
                4,
                0d,
                4,
                0d,
                4,
                0d) as IRefPlane;
            if (referencePlane == null)
            {
                throw new InvalidOperationException(
                    "立弯三点参考面创建失败。");
            }
            return (Feature)referencePlane;
        }

        public static List<int> DrawPoints(
            ModelDoc2 model,
            IEnumerable<Point3d> points)
        {
            return points.Select(point => point.UnitX0001Copy())
                .Select(point => model.SketchManager.CreatePoint(
                    point.X,
                    point.Y,
                    point.Z))
                .Where(sketchPoint => sketchPoint != null)
                .Select(GetSketchPointSelectId)
                .ToList();
        }

        private static Point3d GetVertexPoint(Vertex vertex)
        {
            var vertexCoordinates = vertex?.GetPoint() as double[];
            if (vertexCoordinates == null || vertexCoordinates.Length < 3)
            {
                throw new InvalidOperationException(
                    "无法读取立弯钣金实体顶点坐标。");
            }
            // SolidWorks Vertex.GetPoint() 返回米；立弯分段及模板几何统一使用
            // 毫米。铜排大师原 ToPoint3d(Vertex) 扩展同样在此处乘以 1000。
            return vertexCoordinates.ToPoint3d().UnitX1000Copy();
        }

        private static int GetSketchPointSelectId(SketchPoint sketchPoint)
        {
            var sketchPointIds = sketchPoint?.GetID() as int[];
            if (sketchPointIds == null || sketchPointIds.Length < 2)
            {
                throw new InvalidOperationException(
                    "无法读取立弯参考面草图点选择编号。");
            }
            return sketchPointIds[1];
        }

        private void CreateSegmentSketch(
            Component2 insertedComponent,
            Feature referencePlane,
            IReadOnlyList<Point3d> partPoints,
            bool isPlanarBendSegment,
            int segmentIndex)
        {
            assemblyModel.ClearSelection2(true);
            assemblyModel.Extension.SelectByID2(
                referencePlane.Name + "@" +
                insertedComponent.GetSelectByIDString(),
                "PLANE",
                0d,
                0d,
                0d,
                false,
                0,
                null,
                0);
            assemblyModel.BlankRefGeom();
            assemblyModel.Extension.SelectByID2(
                referencePlane.Name + "@" +
                insertedComponent.GetSelectByIDString(),
                "PLANE",
                0d,
                0d,
                0d,
                false,
                0,
                null,
                0);
            assemblyModel.SketchManager.InsertSketch(true);
            assemblyModel.ClearSelection2(true);
            var activeSketch = assemblyModel.SketchManager.ActiveSketch;
            var sketchPoints = partPoints.Select(point =>
                point.ConvertModelPointToSketch(
                    solidWorksApplication,
                    activeSketch).UnitX0001Copy()).ToList();
            for (var pointIndex = 0;
                 pointIndex < sketchPoints.Count - 1;
                 pointIndex++)
            {
                assemblyModel.SketchManager.CreateLine(
                    sketchPoints[pointIndex].X,
                    sketchPoints[pointIndex].Y,
                    0d,
                    sketchPoints[pointIndex + 1].X,
                    sketchPoints[pointIndex + 1].Y,
                    0d);
            }
            assemblyModel.SketchManager.InsertSketch(true);
            if (activeSketch is Feature sketchFeature)
            {
                sketchFeature.Name =
                    (isPlanarBendSegment ? "平弯" : "立弯") +
                    "草图" + segmentIndex;
            }
        }

        private void SetComponentTransform(
            Component2 insertedComponent,
            VerticalBendSetting verticalBendSetting)
        {
            var originPoint = verticalBendSetting.PointList[0];
            var xAxisPoint = verticalBendSetting.PointList[1];
            var xyPlanePoint = originPoint.Move(
                verticalBendSetting.WidthVector.Negate().Normalized,
                verticalBendSetting.BusBarWidth / 2d);
            var coordinate = new Coord3d(
                originPoint,
                xAxisPoint,
                xyPlanePoint);
            var mathUtility = solidWorksApplication.IGetMathUtility();
            var originVector = (MathVector)mathUtility.CreateVector(new[]
            {
                originPoint.X / 1000d,
                originPoint.Y / 1000d,
                originPoint.Z / 1000d
            });
            var xAxisVector = (MathVector)mathUtility.CreateVector(
                coordinate.Xaxis.ToArrayData());
            var yAxisVector = (MathVector)mathUtility.CreateVector(
                coordinate.Yaxis.ToArrayData());
            var zAxisVector = (MathVector)mathUtility.CreateVector(
                coordinate.Zaxis.ToArrayData());
            insertedComponent.Transform2 = mathUtility.ComposeTransform(
                xAxisVector,
                yAxisVector,
                zAxisVector,
                originVector,
                1d);
        }

        private static List<Point3d> OffsetPolyline(
            IReadOnlyList<Point3d> points,
            Vector3d firstOffsetDirection,
            double offsetDistance)
        {
            if (points == null || points.Count < 2)
            {
                throw new InvalidOperationException(
                    "立弯段偏移路径至少需要两个点。");
            }
            var sourceSegments = new List<MySegment>();
            for (var pointIndex = 0;
                 pointIndex < points.Count - 1;
                 pointIndex++)
            {
                sourceSegments.Add(new MySegment(
                    points[pointIndex],
                    points[pointIndex + 1]));
            }
            var offsetSegments = sourceSegments.Select(segment =>
                new MySegment(
                    segment.StartPoint.Copy(),
                    segment.EndPoint.Copy())).ToList();
            offsetSegments[0].StartPoint = offsetSegments[0].StartPoint.Move(
                firstOffsetDirection,
                offsetDistance);
            offsetSegments[0].EndPoint = offsetSegments[0].EndPoint.Move(
                firstOffsetDirection,
                offsetDistance);
            if (offsetSegments.Count > 1)
            {
                var offsetPlane = new Plane3d(
                    sourceSegments[0].StartPoint,
                    sourceSegments[0].EndPoint,
                    offsetSegments[0].StartPoint);
                for (var segmentIndex = 1;
                     segmentIndex < offsetSegments.Count;
                     segmentIndex++)
                {
                    var segmentDirection = new Vector3d(
                        sourceSegments[segmentIndex].StartPoint,
                        sourceSegments[segmentIndex].EndPoint).Normalized;
                    var segmentOffsetDirection = segmentDirection.Cross(
                        offsetPlane.Normal.Negate()).Normalized;
                    offsetSegments[segmentIndex].StartPoint =
                        offsetSegments[segmentIndex].StartPoint.Move(
                            segmentOffsetDirection,
                            offsetDistance);
                    offsetSegments[segmentIndex].EndPoint =
                        offsetSegments[segmentIndex].EndPoint.Move(
                            segmentOffsetDirection,
                            offsetDistance);
                }
                for (var segmentIndex = 1;
                     segmentIndex < offsetSegments.Count;
                     segmentIndex++)
                {
                    var intersection = new Line3d(
                            offsetSegments[segmentIndex - 1].StartPoint,
                            offsetSegments[segmentIndex - 1].EndPoint)
                        .IntersectionWith(new Line3d(
                            offsetSegments[segmentIndex].StartPoint,
                            offsetSegments[segmentIndex].EndPoint)) as Point3d;
                    if (intersection == null)
                    {
                        throw new InvalidOperationException(
                            "立弯段相邻偏移线无法相交。");
                    }
                    offsetSegments[segmentIndex - 1].EndPoint = intersection;
                    offsetSegments[segmentIndex].StartPoint = intersection;
                }
            }
            var result = new List<Point3d>
            {
                offsetSegments[0].StartPoint
            };
            result.AddRange(offsetSegments.Select(segment =>
                segment.EndPoint));
            return result;
        }

        private static List<string> GetAllFeatureNamesByType(
            ModelDoc2 model,
            string featureType)
        {
            var names = new List<string>();
            for (var feature = model?.IFirstFeature();
                 feature != null;
                 feature = feature.IGetNextFeature())
            {
                if (string.Equals(
                        feature.GetTypeName2(),
                        featureType,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        feature.GetTypeName(),
                        featureType,
                        StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(feature.Name);
                }
            }
            return names;
        }

        private static string ResolveAvailablePartBaseName(
            string outputDirectoryPath)
        {
            var temporaryIndex = 1;
            while (File.Exists(Path.Combine(
                       outputDirectoryPath,
                       temporaryIndex + ".SLDPRT")))
            {
                temporaryIndex++;
            }
            return temporaryIndex.ToString();
        }

        private static string ResolveFreeVerticalBendTemplatePath()
        {
            var assemblyDirectoryPath = Path.GetDirectoryName(
                typeof(BusbarVerticalBendPreview).Assembly.Location);
            var templatePath = Path.Combine(
                assemblyDirectoryPath,
                "BusbarTemplates",
                "FreeBendBar",
                "自由立弯.SLDPRT");
            if (!File.Exists(templatePath))
            {
                throw new FileNotFoundException(
                    "未找到铜排大师自由立弯模板。",
                    templatePath);
            }
            return templatePath;
        }

        private static void ValidateGenerationPaths(
            string sourceTemplatePath,
            string targetPartPath)
        {
            if (string.IsNullOrWhiteSpace(sourceTemplatePath) ||
                !File.Exists(sourceTemplatePath))
            {
                throw new FileNotFoundException(
                    "立弯源模板不存在。",
                    sourceTemplatePath);
            }
            if (string.IsNullOrWhiteSpace(targetPartPath))
            {
                throw new ArgumentException(
                    "立弯目标零件路径不能为空。",
                    nameof(targetPartPath));
            }
            if (File.Exists(targetPartPath))
            {
                throw new IOException(
                    "立弯目标零件已存在：" + targetPartPath);
            }
            var targetDirectoryPath = Path.GetDirectoryName(targetPartPath);
            if (!Directory.Exists(targetDirectoryPath))
            {
                Directory.CreateDirectory(targetDirectoryPath);
            }
        }

        private void EnsureAssemblyAvailable()
        {
            if (assemblyModel == null || assemblyDocument == null ||
                assemblyModel.GetType() !=
                    (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException(
                    "立弯生成必须在有效装配体中执行。");
            }
        }
    }
}
