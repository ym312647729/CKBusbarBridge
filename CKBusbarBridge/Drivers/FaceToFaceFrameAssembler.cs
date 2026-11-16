using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal interface IFaceToFaceFrameAssembler
    {
        string CaptureModelMapping();

        FaceToFaceFrameAssemblyToken InsertFirstFrame(
            FaceToFaceFramePositionPlan plan,
            string modelPath,
            string bCabinetBasePositionName);

        FaceToFaceFrameAssemblyToken InsertChainedFrame(
            FaceToFaceFramePositionPlan plan,
            string modelPath,
            FaceToFaceFrameAssemblyToken previousFrame);

        void Complete(
            IReadOnlyList<FaceToFaceFrameAssemblyToken> tokens,
            string originalMapping);

        void Rollback(
            IReadOnlyList<FaceToFaceFrameAssemblyToken> instancesInReverseOrder,
            string originalMapping);

        FaceToFaceFrameCleanupConfirmation ConfirmDocumentsClosedUnderDirectory(
            string directoryPath);
    }

    internal interface IFaceToFaceFrameLifecycleOperations
    {
        void RebuildTotalAssembly();

        bool SaveFrame();

        FaceToFaceFrameCleanupConfirmation CloseFrame();

        void ActivateTotalAssembly();
    }

    /// <summary>
    /// 完成和回滚总装配体的可注入边界，确保属性写入、重建、保存和删除顺序可验证。
    /// </summary>
    internal interface IFaceToFaceFrameCompletionOperations
    {
        bool TryWriteModelMapping(string mapping);

        bool TryDeleteFrame(FaceToFaceFrameAssemblyToken token);

        void RebuildTotalAssembly();

        void SaveTotalAssembly();
    }

    internal sealed class FaceToFaceFrameMateRoute
    {
        internal FaceToFaceFrameMateRoute(string parentCoordinateName, string childCoordinateName)
        {
            ParentCoordinateName = string.IsNullOrWhiteSpace(parentCoordinateName)
                ? throw new ArgumentException("父级坐标系名称不能为空。", nameof(parentCoordinateName))
                : parentCoordinateName;
            ChildCoordinateName = string.IsNullOrWhiteSpace(childCoordinateName)
                ? throw new ArgumentException("子模型坐标系名称不能为空。", nameof(childCoordinateName))
                : childCoordinateName;
        }

        internal string ParentCoordinateName { get; }

        internal string ChildCoordinateName { get; }
    }

    /// <summary>
    /// 将已驱动的面对面基架逐段插入总装配体，并以计划中的坐标系连接。
    /// </summary>
    internal sealed class FaceToFaceFrameAssembler : IFaceToFaceFrameAssembler
    {
        private const string BaseInstallationCoordinateName = "基座安装坐标系";

        private readonly ISldWorks _swApp;
        private readonly IModelDoc2 _totalAssemblyModel;
        private readonly AssemblyDoc _totalAssembly;
        private readonly FaceToFaceFrameScopedDocumentCleaner _documentCleaner;
        private readonly IFrameBaseComponentResolver _baseComponentResolver;

        internal FaceToFaceFrameAssembler(ISldWorks swApp, IModelDoc2 totalAssemblyModel)
            : this(
                swApp,
                totalAssemblyModel,
                new FaceToFaceFrameBaseComponentResolver())
        {
        }

        /// <summary>
        /// 注入首段基座解析策略，使面对面和侧对侧共用同一条 B→A 链式装配事务。
        /// </summary>
        internal FaceToFaceFrameAssembler(
            ISldWorks swApp,
            IModelDoc2 totalAssemblyModel,
            IFrameBaseComponentResolver baseComponentResolver)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _totalAssemblyModel = totalAssemblyModel
                ?? throw new ArgumentNullException(nameof(totalAssemblyModel));
            _totalAssembly = totalAssemblyModel as AssemblyDoc;
            if (_totalAssembly == null
                || totalAssemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException("基架只能装配到有效的 SolidWorks 总装配体文档中。");
            }

            _baseComponentResolver = baseComponentResolver
                ?? throw new ArgumentNullException(nameof(baseComponentResolver));
            _documentCleaner = new FaceToFaceFrameScopedDocumentCleaner(
                new SolidWorksFaceToFaceFrameDocumentSession(_swApp));
        }

        public string CaptureModelMapping()
        {
            var mapping = new SolidWorksDriver(_swApp).GetCustomProperty(
                _totalAssemblyModel,
                FaceToFaceBaseModelMappingHelper.PropertyName);
            return mapping;
        }

        public FaceToFaceFrameAssemblyToken InsertFirstFrame(
            FaceToFaceFramePositionPlan plan,
            string modelPath,
            string bCabinetBasePositionName)
        {
            if (string.IsNullOrWhiteSpace(bCabinetBasePositionName))
            {
                throw new ArgumentException("B柜普通基座位置名称不能为空。", nameof(bCabinetBasePositionName));
            }

            var baseFullName = ResolveBaseComponentFullName(
                CaptureModelMapping(),
                bCabinetBasePositionName,
                _baseComponentResolver);

            return InsertAndMate(
                plan,
                modelPath,
                FindTopLevelComponent(baseFullName),
                ResolveFirstMateRoute(plan));
        }

        public FaceToFaceFrameAssemblyToken InsertChainedFrame(
            FaceToFaceFramePositionPlan plan,
            string modelPath,
            FaceToFaceFrameAssemblyToken previousFrame)
        {
            if (!(previousFrame?.NativeComponent is Component2 previousComponent))
            {
                throw new InvalidOperationException("后续基架缺少有效的前一段装配组件。");
            }

            return InsertAndMate(
                plan,
                modelPath,
                previousComponent,
                ResolveChainedMateRoute(previousFrame, plan));
        }

        public void Complete(
            IReadOnlyList<FaceToFaceFrameAssemblyToken> tokens,
            string originalMapping)
        {
            if (tokens == null)
            {
                throw new ArgumentNullException(nameof(tokens));
            }

            ActivateTotalAssembly();
            CompleteAssembly(tokens, originalMapping, new SolidWorksCompletionOperations(this));
        }

        public void Rollback(
            IReadOnlyList<FaceToFaceFrameAssemblyToken> instancesInReverseOrder,
            string originalMapping)
        {
            if (instancesInReverseOrder == null)
            {
                throw new ArgumentNullException(nameof(instancesInReverseOrder));
            }

            ActivateTotalAssembly();
            RollbackAssembly(
                instancesInReverseOrder,
                originalMapping,
                new SolidWorksCompletionOperations(this));
        }

        public FaceToFaceFrameCleanupConfirmation ConfirmDocumentsClosedUnderDirectory(
            string directoryPath)
        {
            return _documentCleaner.CloseDocumentsUnderDirectory(directoryPath);
        }

        internal static FaceToFaceFrameMateRoute ResolveFirstMateRoute(
            FaceToFaceFramePositionPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            return new FaceToFaceFrameMateRoute(
                BaseInstallationCoordinateName,
                plan.InletCoordinateName);
        }

        /// <summary>
        /// 通过场景策略解析首段基座；失败信息使用策略提供的真实基座描述。
        /// </summary>
        internal static string ResolveBaseComponentFullName(
            string mappingJson,
            string positionName,
            IFrameBaseComponentResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (!resolver.TryResolve(
                mappingJson,
                positionName,
                out var componentFullName))
            {
                throw new InvalidOperationException(
                    $"模型对照映射中未找到{resolver.Describe(positionName)}的零部件全名。");
            }

            return componentFullName;
        }

        internal static FaceToFaceFrameMateRoute ResolveChainedMateRoute(
            FaceToFaceFrameAssemblyToken previousFrame,
            FaceToFaceFramePositionPlan currentPlan)
        {
            if (previousFrame?.PositionPlan == null)
            {
                throw new ArgumentNullException(nameof(previousFrame));
            }

            if (currentPlan == null) throw new ArgumentNullException(nameof(currentPlan));
            return new FaceToFaceFrameMateRoute(
                previousFrame.PositionPlan.OutletCoordinateName,
                currentPlan.InletCoordinateName);
        }

        internal static void RunPostMateFrameLifecycle(
            IFaceToFaceFrameLifecycleOperations operations)
        {
            if (operations == null) throw new ArgumentNullException(nameof(operations));

            operations.RebuildTotalAssembly();
            if (!operations.SaveFrame())
            {
                throw new InvalidOperationException("保存当前基架模型失败，已禁止关闭该模型。");
            }

            var closeResult = operations.CloseFrame();
            if (closeResult == null || !closeResult.IsConfirmed)
            {
                var exception = new InvalidOperationException(
                    "关闭当前基架模型失败或未得到确认。");
                FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(
                    exception,
                    closeResult,
                    null,
                    "当前基架文档关闭");
                throw exception;
            }

            operations.ActivateTotalAssembly();
        }

        internal static Exception AttachImmediateCleanupDiagnostics(
            Exception primaryException,
            Action deleteInsertedComponent,
            Func<FaceToFaceFrameCleanupConfirmation> closeFrameDocuments,
            string retainedPath)
        {
            if (primaryException == null)
            {
                throw new ArgumentNullException(nameof(primaryException));
            }

            var diagnostics = new List<string>();
            var componentCleanupConfirmed = deleteInsertedComponent == null;
            if (deleteInsertedComponent != null)
            {
                try
                {
                    deleteInsertedComponent();
                    componentCleanupConfirmed = true;
                }
                catch (Exception cleanupException)
                {
                    diagnostics.Add("已插入基架组件清理失败：" + cleanupException.Message);
                }
            }

            FaceToFaceFrameCleanupConfirmation documentCleanup;
            try
            {
                documentCleanup = closeFrameDocuments == null
                    ? FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                        "未提供基架文档关闭确认操作。")
                    : closeFrameDocuments();
            }
            catch (Exception cleanupException)
            {
                documentCleanup = FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                    "关闭基架文档异常：" + cleanupException.Message);
            }

            if (documentCleanup == null || !documentCleanup.IsConfirmed)
            {
                diagnostics.Add(
                    "基架文档关闭未得到确认："
                    + (documentCleanup?.Diagnostic ?? "未返回确认结果"));
            }

            var confirmation = componentCleanupConfirmed
                && documentCleanup != null
                && documentCleanup.IsConfirmed
                    ? FaceToFaceFrameCleanupConfirmation.Confirmed()
                    : FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                        string.Join("；", diagnostics));
            return FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(
                primaryException,
                confirmation,
                retainedPath,
                "插入失败后的组件/文档清理");
        }

        /// <summary>
        /// 将配合和随后保存/关闭契约置于同一可注入顺序中，供 COM 边界和契约测试共同使用。
        /// </summary>
        internal static void RunFrameAssemblyLifecycle(
            Action mateFrame,
            IFaceToFaceFrameLifecycleOperations operations)
        {
            if (mateFrame == null) throw new ArgumentNullException(nameof(mateFrame));
            mateFrame();
            RunPostMateFrameLifecycle(operations);
        }

        internal static void CompleteAssembly(
            IReadOnlyList<FaceToFaceFrameAssemblyToken> tokens,
            string originalMapping,
            IFaceToFaceFrameCompletionOperations operations)
        {
            if (tokens == null) throw new ArgumentNullException(nameof(tokens));
            if (operations == null) throw new ArgumentNullException(nameof(operations));

            var mergedMapping = FaceToFaceFrameModelMappingHelper.MergeFrames(originalMapping, tokens);
            if (!operations.TryWriteModelMapping(mergedMapping))
            {
                throw new InvalidOperationException("写入基架模型对照映射失败或未得到确认。");
            }

            operations.RebuildTotalAssembly();
            operations.SaveTotalAssembly();
        }

        internal static void RollbackAssembly(
            IReadOnlyList<FaceToFaceFrameAssemblyToken> instancesInReverseOrder,
            string originalMapping,
            IFaceToFaceFrameCompletionOperations operations)
        {
            if (instancesInReverseOrder == null)
            {
                throw new ArgumentNullException(nameof(instancesInReverseOrder));
            }

            if (operations == null) throw new ArgumentNullException(nameof(operations));

            var errors = new List<string>();
            foreach (var token in instancesInReverseOrder)
            {
                try
                {
                    if (token == null || !operations.TryDeleteFrame(token))
                    {
                        var position = token?.PositionPlan?.PositionKey ?? "未知位置";
                        errors.Add($"删除{position}基架失败或未得到确认。");
                    }
                }
                catch (Exception exception)
                {
                    var position = token?.PositionPlan?.PositionKey ?? "未知位置";
                    errors.Add($"删除{position}基架异常：{exception.Message}");
                }
            }

            var mappingRestored = false;
            try
            {
                mappingRestored = operations.TryWriteModelMapping(originalMapping);
                if (!mappingRestored)
                {
                    errors.Add("恢复基架模型对照映射失败或未得到确认。");
                }
            }
            catch (Exception exception)
            {
                errors.Add("恢复基架模型对照映射异常：" + exception.Message);
            }

            // 映射无法恢复时，不得把已删除但映射仍失真的总装保存为成功事务。
            if (!mappingRestored)
            {
                throw new InvalidOperationException(string.Join("；", errors));
            }

            try
            {
                operations.RebuildTotalAssembly();
                operations.SaveTotalAssembly();
            }
            catch (Exception exception)
            {
                errors.Add("保存回滚后的总装配体失败：" + exception.Message);
            }

            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", errors));
            }
        }

        internal static bool IsTopLevelComponentMatch(
            bool isTopLevel,
            string candidateComponentFullName,
            string requestedComponentFullName)
        {
            return isTopLevel && FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(
                candidateComponentFullName,
                requestedComponentFullName);
        }

        internal static void ValidateComponentDeletion(
            bool deleteSucceeded,
            bool componentStillExists,
            string componentName)
        {
            if (!deleteSucceeded)
            {
                throw new InvalidOperationException(
                    $"删除基架组件失败，DeleteSelections 返回失败：{componentName}");
            }

            if (componentStillExists)
            {
                throw new InvalidOperationException(
                    $"删除操作返回成功，但总装配体中仍存在基架组件：{componentName}");
            }
        }

        internal FaceToFaceFrameAssemblyToken InsertAtPosition(
            FaceToFaceFramePositionPlan plan,
            string modelPath)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                throw new FileNotFoundException("待插入的基架模型不存在：" + modelPath, modelPath);
            }

            var fullModelPath = Path.GetFullPath(modelPath);
            var frameModel = _swApp.GetOpenDocumentByName(fullModelPath) as IModelDoc2
                ?? new SolidWorksDriver(_swApp).OpenOrActivateDocument(fullModelPath);
            Component2 insertedComponent = null;
            try
            {
                ActivateTotalAssembly();
                insertedComponent = _totalAssembly.AddComponent5(
                    fullModelPath,
                    (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                    string.Empty,
                    false,
                    string.Empty,
                    0,
                    0,
                    0);
                if (insertedComponent == null)
                {
                    throw new InvalidOperationException(
                        $"{plan.PositionKey}基架插入总装配体失败：{fullModelPath}");
                }

                // 二次生成会在所有位置替换完成后统一重建链式配合，
                // 这里只插入并保存/关闭基架文档，不依赖已失效的旧相邻关系。
                RunPostMateFrameLifecycle(new SolidWorksFrameLifecycleOperations(
                    _swApp,
                    frameModel,
                    ActivateTotalAssembly,
                    () => EnsureTotalRebuildSucceeded("基架插入结果")));
                return new FaceToFaceFrameAssemblyToken(
                    plan,
                    fullModelPath,
                    insertedComponent,
                    insertedComponent.Name2);
            }
            catch (Exception operationException)
            {
                AttachImmediateCleanupDiagnostics(
                    operationException,
                    insertedComponent == null
                        ? (Action)null
                        : () => DeleteInsertedComponent(insertedComponent),
                    () => _documentCleaner.CloseDocumentsUnderDirectory(
                        Path.GetDirectoryName(fullModelPath)),
                    Path.GetDirectoryName(fullModelPath));
                throw;
            }
        }

        internal void DeleteFrameChainMates(string componentInstanceId)
        {
            var component = FindTopLevelComponent(componentInstanceId);
            var mateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mateObject in (component.GetMates() as object[]) ?? Array.Empty<object>())
            {
                var mate = mateObject as IMate2;
                if (mate == null || !MateContainsFrameChainEndpoint(mate, componentInstanceId))
                {
                    continue;
                }

                var mateName = TryGetMateFeatureName(mateObject);
                if (string.IsNullOrWhiteSpace(mateName))
                {
                    throw new InvalidOperationException(
                        "已识别基架链式配合，但无法取得其特征名称，已停止重新配合："
                        + componentInstanceId);
                }

                mateNames.Add(mateName);
            }

            SWHelper.TryDeleteAssemblyMatesByName(_totalAssemblyModel, mateNames);
            var undeleted = mateNames.Where(name => SWHelper.FindFeature(
                _totalAssemblyModel,
                name,
                null) != null).ToArray();
            if (undeleted.Length > 0)
            {
                throw new InvalidOperationException(
                    "删除基架链式配合失败：" + string.Join("，", undeleted));
            }
        }

        private static string TryGetMateFeatureName(object mateObject)
        {
            if (mateObject == null)
            {
                return null;
            }

            // GetMates 返回 IMate2，但底层 COM 对象同时可查询为 Feature；
            // 两种接口/类转换都尝试，兼容当前项目的旧互操作程序集。
            try
            {
                var concreteFeature = mateObject as Feature;
                if (!string.IsNullOrWhiteSpace(concreteFeature?.Name))
                {
                    return concreteFeature.Name;
                }
            }
            catch
            {
                // 继续尝试 IFeature。
            }

            try
            {
                var feature = mateObject as IFeature;
                return string.IsNullOrWhiteSpace(feature?.Name) ? null : feature.Name;
            }
            catch
            {
                return null;
            }
        }

        internal void DeleteComponentByInstanceId(string componentInstanceId)
        {
            // 组件仍在总装中时才能取得其前、后链式配合；必须先删配合再移除组件，
            // 否则 SolidWorks 会因残留配合继续引用该组件而拒绝删除。
            DeleteFrameChainMates(componentInstanceId);
            DeleteInsertedComponent(FindTopLevelComponent(componentInstanceId));
        }

        internal void RepairAdjacent(FaceToFaceFrameAssemblyToken previous, FaceToFaceFrameAssemblyToken current,
            string ordinaryBasePositionName)
        {
            var child = ResolveTokenComponent(current);
            var parent = previous == null ? FindTopLevelComponent(ResolveBaseComponentFullName(CaptureModelMapping(),
                ordinaryBasePositionName, _baseComponentResolver)) : ResolveTokenComponent(previous);
            var route = previous == null ? ResolveFirstMateRoute(current.PositionPlan) : ResolveChainedMateRoute(previous, current.PositionPlan);
            var suppression = BridgeReferencedModelParameters.MateSuppression(_totalAssemblyModel,
                _totalAssemblyModel.ConfigurationManager.ActiveConfiguration.Name);
            var incoming = ((child.GetMates() as object[]) ?? Array.Empty<object>()).OfType<IMate2>()
                .Where(m => (!suppression.TryGetValue(m, out var suppressed) || !suppressed) &&
                    HasCoordinateEndpoint(m, child.Name2, route.ChildCoordinateName)).ToArray();
            if (incoming.Length > 1) throw new InvalidOperationException("修复基架入口存在多个有效配合：" + child.Name2);
            if (incoming.Length == 1 && HasCoordinateEndpoint(incoming[0], parent.Name2, route.ParentCoordinateName) &&
                CoordinateOriginsCoincide(parent, route.ParentCoordinateName, child, route.ChildCoordinateName)) return;
            // 仅替换当前子件的入口边；保留其出口边、其他有效链式边以及辅助功能配合。
            var names = incoming.Select(m => TryGetMateFeatureName(m)).ToArray();
            if (names.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("修复无法唯一识别原入口配合：" + child.Name2);
            SWHelper.TryDeleteAssemblyMatesByName(_totalAssemblyModel, names);
            if (names.Any(name => SWHelper.FindFeature(_totalAssemblyModel, name, null) != null))
                throw new InvalidOperationException("修复未能移除原入口配合：" + child.Name2);
            MateAdjacent(previous, current, ordinaryBasePositionName);
        }

        private bool HasCoordinateEndpoint(IMate2 mate, string componentName, string coordinate)
        {
            for (var i = 0; i < mate.GetMateEntityCount(); i++)
            {
                var entity = mate.MateEntity(i) as IMateEntity2;
                if (FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(entity?.ReferenceComponent?.Name2, componentName) &&
                    (entity.Reference as IFeature)?.Name == coordinate) return true;
            }
            return false;
        }

        private bool CoordinateOriginsCoincide(Component2 parent, string parentCoordinate, Component2 child, string childCoordinate)
        {
            Func<Component2, string, double[]> origin = (component, name) =>
            {
                var definition = component.FeatureByName(name)?.GetDefinition() as CoordinateSystemFeatureData;
                var transform = definition?.Transform?.Multiply(component.GetTotalTransform(false)) as MathTransform;
                var data = transform?.ArrayData as double[];
                if (data == null || data.Length != 16) throw new InvalidOperationException("修复连接坐标系不可读：" + component.Name2 + "/" + name);
                return new[] { data[9] * 1000, data[10] * 1000, data[11] * 1000 };
            };
            var a = origin(parent, parentCoordinate); var b = origin(child, childCoordinate);
            return Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum()) < .15;
        }

        internal void MateAdjacent(
            FaceToFaceFrameAssemblyToken previous,
            FaceToFaceFrameAssemblyToken current,
            string ordinaryBasePositionName)
        {
            if (current?.PositionPlan == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            Component2 parentComponent;
            FaceToFaceFrameMateRoute route;
            if (previous == null)
            {
                if (string.IsNullOrWhiteSpace(ordinaryBasePositionName))
                {
                    throw new ArgumentException("B柜普通基座位置名称不能为空。", nameof(ordinaryBasePositionName));
                }

                var baseFullName = ResolveBaseComponentFullName(
                    CaptureModelMapping(),
                    ordinaryBasePositionName,
                    _baseComponentResolver);

                parentComponent = FindTopLevelComponent(baseFullName);
                route = ResolveFirstMateRoute(current.PositionPlan);
            }
            else
            {
                parentComponent = ResolveTokenComponent(previous);
                route = ResolveChainedMateRoute(previous, current.PositionPlan);
            }

            var currentComponent = ResolveTokenComponent(current);
            ActivateTotalAssembly();
            if (!PublicMethod.AddMateUseCoord(
                _swApp,
                _totalAssembly,
                parentComponent,
                currentComponent,
                route.ParentCoordinateName,
                route.ChildCoordinateName))
            {
                throw new InvalidOperationException(
                    $"{current.PositionPlan.PositionKey}基架链式配合失败："
                    + $"父级坐标系={route.ParentCoordinateName}，子级坐标系={route.ChildCoordinateName}。");
            }

            EnsureTotalRebuildSucceeded(current.PositionPlan.PositionKey + "链式配合");
        }

        internal static bool IsFrameChainMateEndpoint(
            string targetComponentInstanceId,
            string endpointComponentInstanceId,
            string endpointCoordinateName)
        {
            if (!FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(
                    targetComponentInstanceId,
                    endpointComponentInstanceId)
                || string.IsNullOrWhiteSpace(endpointCoordinateName))
            {
                return false;
            }

            return string.Equals(endpointCoordinateName, "坐标系-进1", StringComparison.Ordinal)
                || string.Equals(endpointCoordinateName, "坐标系-进2", StringComparison.Ordinal)
                || string.Equals(endpointCoordinateName, "坐标系-出1", StringComparison.Ordinal)
                || string.Equals(endpointCoordinateName, "坐标系-出2", StringComparison.Ordinal);
        }

        private static bool MateContainsFrameChainEndpoint(
            IMate2 mate,
            string componentInstanceId)
        {
            for (var index = 0; index < mate.GetMateEntityCount(); index++)
            {
                var entity = mate.MateEntity(index) as MateEntity2;
                string endpointComponent = null;
                string endpointCoordinate = null;
                try { endpointComponent = entity?.ReferenceComponent?.Name2; }
                catch { }
                try { endpointCoordinate = (entity?.Reference as IFeature)?.Name; }
                catch { }
                if (IsFrameChainMateEndpoint(
                    componentInstanceId,
                    endpointComponent,
                    endpointCoordinate))
                {
                    return true;
                }
            }

            return false;
        }

        private Component2 ResolveTokenComponent(FaceToFaceFrameAssemblyToken token)
        {
            if (token?.NativeComponent is Component2 nativeComponent
                && IsTopLevelComponentPresent(nativeComponent.Name2))
            {
                return nativeComponent;
            }

            return FindTopLevelComponent(token?.ComponentFullName);
        }

        private FaceToFaceFrameAssemblyToken InsertAndMate(
            FaceToFaceFramePositionPlan plan,
            string modelPath,
            Component2 parentComponent,
            FaceToFaceFrameMateRoute route)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (parentComponent == null) throw new ArgumentNullException(nameof(parentComponent));
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                throw new FileNotFoundException($"待装配基架模型不存在：{modelPath}", modelPath);
            }

            var fullModelPath = Path.GetFullPath(modelPath);
            var frameModel = _swApp.GetOpenDocumentByName(fullModelPath) as IModelDoc2
                ?? new SolidWorksDriver(_swApp).OpenOrActivateDocument(fullModelPath);
            Component2 insertedComponent = null;
            try
            {
                ActivateTotalAssembly();
                insertedComponent = _totalAssembly.AddComponent5(
                    fullModelPath,
                    (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                    string.Empty,
                    false,
                    string.Empty,
                    0,
                    0,
                    0);
                if (insertedComponent == null)
                {
                    throw new InvalidOperationException($"{plan.PositionKey}基架插入总装配体失败：{fullModelPath}");
                }

                try { _totalAssembly.ResolveAllLightWeightComponents(false); }
                catch { /* 配合失败信息会包含完整的坐标系上下文。 */ }

                RunFrameAssemblyLifecycle(
                    () =>
                    {
                        if (!PublicMethod.AddMateUseCoord(
                            _swApp,
                            _totalAssembly,
                            parentComponent,
                            insertedComponent,
                            route.ParentCoordinateName,
                            route.ChildCoordinateName))
                        {
                            throw new InvalidOperationException(
                                $"{plan.PositionKey}基架装配配合失败：模型={fullModelPath}，父级坐标系={route.ParentCoordinateName}，子模型坐标系={route.ChildCoordinateName}。");
                        }
                    },
                    new SolidWorksFrameLifecycleOperations(
                        _swApp,
                        frameModel,
                        ActivateTotalAssembly,
                        () => EnsureTotalRebuildSucceeded("基架装配结果")));

                return new FaceToFaceFrameAssemblyToken(
                    plan,
                    fullModelPath,
                    insertedComponent,
                    insertedComponent.Name2);
            }
            catch (Exception operationException)
            {
                // 保持原始异常对象、类型和堆栈；清理结果仅附加为诊断和删除门槛。
                AttachImmediateCleanupDiagnostics(
                    operationException,
                    insertedComponent == null
                        ? (Action)null
                        : () => DeleteInsertedComponent(insertedComponent),
                    () => _documentCleaner.CloseDocumentsUnderDirectory(
                        Path.GetDirectoryName(fullModelPath)),
                    Path.GetDirectoryName(fullModelPath));
                throw;
            }
        }

        private Component2 FindTopLevelComponent(string componentFullName)
        {
            ActivateTotalAssembly();
            var components = _totalAssembly.GetComponents(true) as object[];
            if (components != null)
            {
                foreach (var item in components)
                {
                    if (item is Component2 component
                        && IsTopLevelComponentMatch(
                            true,
                            component.Name2,
                            componentFullName))
                    {
                        return component;
                    }
                }
            }

            throw new InvalidOperationException($"总装配体中未找到 B柜普通基座组件：{componentFullName}");
        }

        private void DeleteInsertedComponent(Component2 component)
        {
            ActivateTotalAssembly();
            _totalAssemblyModel.ClearSelection2(true);
            try
            {
                var componentName = component.Name2;
                var selected = false;
                var selectName = component.GetSelectByIDString();
                if (!string.IsNullOrWhiteSpace(selectName))
                {
                    selected = _totalAssemblyModel.Extension.SelectByID2(
                        selectName, "COMPONENT", 0, 0, 0, false, 0, null, 0);
                }

                if (!selected) selected = component.Select(false);
                if (!selected) throw new InvalidOperationException("无法选中待删除的基架组件。");

                var deleted = _totalAssembly.DeleteSelections(
                    (int)swDeleteSelectionOptions_e.swDelete_Children);
                ValidateComponentDeletion(deleted, IsTopLevelComponentPresent(componentName), componentName);
            }
            finally
            {
                _totalAssemblyModel.ClearSelection2(true);
            }
        }

        private bool IsTopLevelComponentPresent(string componentName)
        {
            var components = _totalAssembly.GetComponents(true) as object[];
            if (components == null) return false;
            foreach (var item in components)
            {
                if (item is Component2 component
                    && string.Equals(component.Name2, componentName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void EnsureTotalRebuildSucceeded(string operationName)
        {
            SolidWorksFaceToFaceFrameInitialGenerationSession.RebuildTopLevelAssembly(
                _totalAssemblyModel.ForceRebuild3,
                operationName + "：" + GetTotalAssemblyIdentity());
        }

        private void SaveTotalAssembly()
        {
            var errors = 0;
            var warnings = 0;
            var saved = _totalAssemblyModel.Save3(
                (int)(swSaveAsOptions_e.swSaveAsOptions_Silent
                    | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                ref errors,
                ref warnings);
            if (!saved || errors != 0)
            {
                throw new InvalidOperationException($"保存总装配体失败：errors={errors}，warnings={warnings}。");
            }
        }

        private void ActivateTotalAssembly()
        {
            var errors = 0;
            var activated = _swApp.ActivateDoc3(
                _totalAssemblyModel.GetTitle(),
                true,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                ref errors) as IModelDoc2;
            var rebuildWarning = (int)swActivateDocError_e.swDocNeedsRebuildWarning;
            if (activated == null || (errors != 0 && errors != rebuildWarning))
            {
                throw new InvalidOperationException($"无法激活基架目标总装配体：{GetTotalAssemblyIdentity()}，errors={errors}。");
            }

            if (errors == rebuildWarning)
            {
                EnsureTotalRebuildSucceeded("激活基架目标总装配体后的重建");
            }
        }

        private string GetTotalAssemblyIdentity()
        {
            var path = _totalAssemblyModel.GetPathName();
            return string.IsNullOrWhiteSpace(path) ? _totalAssemblyModel.GetTitle() : path;
        }

        private sealed class SolidWorksFrameLifecycleOperations : IFaceToFaceFrameLifecycleOperations
        {
            private readonly ISldWorks _swApp;
            private readonly IModelDoc2 _frameModel;
            private readonly Action _activateTotalAssembly;
            private readonly Action _rebuildTotalAssembly;

            internal SolidWorksFrameLifecycleOperations(
                ISldWorks swApp,
                IModelDoc2 frameModel,
                Action activateTotalAssembly,
                Action rebuildTotalAssembly)
            {
                _swApp = swApp;
                _frameModel = frameModel;
                _activateTotalAssembly = activateTotalAssembly;
                _rebuildTotalAssembly = rebuildTotalAssembly;
            }

            public void RebuildTotalAssembly() => _rebuildTotalAssembly();

            public bool SaveFrame()
            {
                var errors = 0;
                var warnings = 0;
                var saved = _frameModel.Save3(
                    (int)(swSaveAsOptions_e.swSaveAsOptions_Silent
                        | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                    ref errors,
                    ref warnings);
                return saved && errors == 0;
            }

            public FaceToFaceFrameCleanupConfirmation CloseFrame()
            {
                var modelPath = _frameModel.GetPathName();
                return new FaceToFaceFrameScopedDocumentCleaner(
                    new SolidWorksFaceToFaceFrameDocumentSession(_swApp))
                    .RequestCloseDocumentsUnderDirectory(Path.GetDirectoryName(modelPath));
            }

            public void ActivateTotalAssembly() => _activateTotalAssembly();
        }

        private sealed class SolidWorksCompletionOperations : IFaceToFaceFrameCompletionOperations
        {
            private readonly FaceToFaceFrameAssembler _owner;

            internal SolidWorksCompletionOperations(FaceToFaceFrameAssembler owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            public bool TryWriteModelMapping(string mapping)
            {
                new SolidWorksDriver(_owner._swApp).SetCustomProperty(
                    _owner._totalAssemblyModel,
                    FaceToFaceBaseModelMappingHelper.PropertyName,
                    mapping);
                var written = new SolidWorksDriver(_owner._swApp).GetCustomProperty(
                    _owner._totalAssemblyModel,
                    FaceToFaceBaseModelMappingHelper.PropertyName);
                return string.Equals(written, mapping, StringComparison.Ordinal);
            }

            public bool TryDeleteFrame(FaceToFaceFrameAssemblyToken token)
            {
                if (!(token?.NativeComponent is Component2 component))
                {
                    return false;
                }

                _owner.DeleteInsertedComponent(component);
                return true;
            }

            public void RebuildTotalAssembly()
            {
                _owner.EnsureTotalRebuildSucceeded("基架装配结果");
            }

            public void SaveTotalAssembly()
            {
                _owner.SaveTotalAssembly();
            }
        }
    }
}
