using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 描述一次坐标系配合所使用的父级坐标系和新模型坐标系。
    /// </summary>
    internal sealed class FaceToFaceBaseMateRoute
    {
        internal FaceToFaceBaseMateRoute(
            string parentCoordinateName,
            string childCoordinateName)
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
    /// 将独立普通基座和增高基座插入面对面总装配体，并通过确认的坐标系名称完成定位。
    /// </summary>
    internal sealed class FaceToFaceBaseAssembler : IFaceToFaceBaseAssembler
    {
        /// <summary>
        /// 普通基座与总装根或增高基座定位时使用的坐标系；二次 remate 只删此端点相关配合。
        /// </summary>
        internal const string OrdinaryBaseInstallationCoordinateName = "安装坐标系";

        /// <summary>
        /// 增高基座与总装根或普通基座定位时使用的坐标系。
        /// </summary>
        internal const string ElevatedBaseBottomCoordinateName = "下";

        internal const string ElevatedBaseTopCoordinateName = "上";

        private readonly ISldWorks _swApp;
        private readonly IModelDoc2 _totalAssemblyModel;
        private readonly AssemblyDoc _totalAssembly;

        internal FaceToFaceBaseAssembler(
            ISldWorks swApp,
            IModelDoc2 totalAssemblyModel)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _totalAssemblyModel = totalAssemblyModel
                ?? throw new ArgumentNullException(nameof(totalAssemblyModel));
            _totalAssembly = totalAssemblyModel as AssemblyDoc;
            if (_totalAssembly == null
                || totalAssemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException(
                    "独立基座只能装配到有效的 SolidWorks 总装配体文档中。");
            }
        }

        public FaceToFaceBaseAssemblyToken InsertAtRoot(
            string positionName,
            FaceToFaceBaseModelKind kind,
            string modelPath,
            string rootCoordinateName)
        {
            var route = ResolveRootMateRoute(rootCoordinateName, kind);
            return InsertAndMate(
                positionName,
                kind,
                modelPath,
                parentComponent: null,
                route);
        }

        public FaceToFaceBaseAssemblyToken InsertBaseOnElevated(
            string positionName,
            string modelPath,
            FaceToFaceBaseAssemblyToken elevatedBase)
        {
            if (elevatedBase == null)
            {
                throw new ArgumentNullException(nameof(elevatedBase));
            }

            if (elevatedBase.Kind != FaceToFaceBaseModelKind.ElevatedBase
                || !(elevatedBase.NativeComponent is Component2 elevatedComponent))
            {
                throw new InvalidOperationException(
                    $"{positionName}普通基座缺少有效的增高基座装配实例。");
            }

            if (!string.Equals(
                positionName,
                elevatedBase.PositionName,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"普通基座位置“{positionName}”与增高基座位置“{elevatedBase.PositionName}”不一致。");
            }

            return InsertAndMate(
                positionName,
                FaceToFaceBaseModelKind.Base,
                modelPath,
                elevatedComponent,
                ResolveStackedMateRoute());
        }

        /// <summary>
        /// 二次替换前只删除目标基座安装坐标系参与的定位配合。
        /// </summary>
        internal void DeletePositioningMates(
            string componentFullName,
            FaceToFaceBaseModelKind kind)
        {
            ActivateTotalAssembly();
            var component = FindTopLevelComponent(componentFullName);
            if (component == null)
            {
                throw new InvalidOperationException(
                    "删除定位配合失败，未找到总装根级组件：" + componentFullName);
            }

            DeleteComponentMatesMatching(
                component,
                kind == FaceToFaceBaseModelKind.ElevatedBase
                    ? (Func<string, string, string, bool>)ShouldDeleteElevatedPositioningMateEndpoint
                    : ShouldRemateDeleteMateEndpoint);
        }

        /// <summary>
        /// 删除根级基座组件。调用方必须先执行 <see cref="DeletePositioningMates"/>，
        /// 因为带活动配合直接移除组件会留下悬空引用或触发 SolidWorks 删除失败。
        /// </summary>
        internal void DeleteRootComponent(string componentFullName)
        {
            ActivateTotalAssembly();
            var component = FindTopLevelComponent(componentFullName);
            if (component == null)
            {
                throw new InvalidOperationException(
                    "删除基座组件失败，未找到总装根级组件：" + componentFullName);
            }

            DeleteInsertedComponent(component);
        }

        /// <summary>
        /// 将位置与零部件全名对照表写入总装自定义属性「模型对照映射」。
        /// </summary>
        public void WriteModelMapping(string mappingJson)
        {
            if (string.IsNullOrWhiteSpace(mappingJson))
            {
                throw new ArgumentException("模型对照映射 JSON 不能为空。", nameof(mappingJson));
            }

            ActivateTotalAssembly();
            new SolidWorksDriver(_swApp).SetCustomProperty(
                _totalAssemblyModel,
                FaceToFaceBaseModelMappingHelper.PropertyName,
                mappingJson);
            SaveAndRebuild();
        }

        public void SaveAndRebuild()
        {
            FaceToFaceBaseOperationGuard.EnsureRebuildSucceeded(
                _totalAssemblyModel.EditRebuild3(),
                "基座装配结果",
                GetTotalAssemblyIdentity());
            SaveTotalAssembly();
        }

        public void Rollback(
            IReadOnlyList<FaceToFaceBaseAssemblyToken> instancesInReverseOrder)
        {
            if (instancesInReverseOrder == null)
            {
                throw new ArgumentNullException(nameof(instancesInReverseOrder));
            }

            var errors = new List<string>();
            foreach (var token in instancesInReverseOrder)
            {
                if (!(token?.NativeComponent is Component2 component))
                {
                    errors.Add("回滚记录中包含无效的 SolidWorks 组件句柄。");
                    continue;
                }

                try
                {
                    DeleteInsertedComponent(component);
                }
                catch (Exception exception)
                {
                    errors.Add(
                        $"删除{token.PositionName}{GetKindDisplayName(token.Kind)}失败：{exception.Message}");
                }
            }

            try
            {
                FaceToFaceBaseOperationGuard.EnsureRebuildSucceeded(
                    _totalAssemblyModel.EditRebuild3(),
                    "基座回滚结果",
                    GetTotalAssemblyIdentity());
                SaveTotalAssembly();
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

        /// <summary>
        /// 解析直接装配到总装配体根级时的坐标系配合规则。
        /// 父级坐标系是总装根坐标系，不再默认等于位置名。
        /// </summary>
        internal static FaceToFaceBaseMateRoute ResolveRootMateRoute(
            string rootCoordinateName,
            FaceToFaceBaseModelKind kind)
        {
            if (string.IsNullOrWhiteSpace(rootCoordinateName))
            {
                throw new ArgumentException("总装根坐标系名称不能为空。", nameof(rootCoordinateName));
            }

            return new FaceToFaceBaseMateRoute(
                rootCoordinateName,
                kind == FaceToFaceBaseModelKind.ElevatedBase
                    ? ElevatedBaseBottomCoordinateName
                    : OrdinaryBaseInstallationCoordinateName);
        }

        /// <summary>
        /// 解析普通基座叠装在增高基座上方时的坐标系配合规则。
        /// </summary>
        internal static FaceToFaceBaseMateRoute ResolveStackedMateRoute()
        {
            return new FaceToFaceBaseMateRoute(
                ElevatedBaseTopCoordinateName,
                OrdinaryBaseInstallationCoordinateName);
        }

        /// <summary>
        /// 删除增高基座前，仅删除该件上「上/下」定位端点参与的配合。
        /// </summary>
        internal static bool ShouldDeleteElevatedPositioningMateEndpoint(
            string targetComponentInstanceName,
            string mateComponentInstanceName,
            string mateCoordinateName)
        {
            if (string.IsNullOrWhiteSpace(targetComponentInstanceName)
                || string.IsNullOrWhiteSpace(mateComponentInstanceName)
                || string.IsNullOrWhiteSpace(mateCoordinateName))
            {
                return false;
            }

            if (!FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(
                    mateComponentInstanceName,
                    targetComponentInstanceName))
            {
                return false;
            }

            var normalizedCoordinateName = mateCoordinateName.Trim();
            return string.Equals(
                    normalizedCoordinateName,
                    ElevatedBaseTopCoordinateName,
                    StringComparison.Ordinal)
                || string.Equals(
                    normalizedCoordinateName,
                    ElevatedBaseBottomCoordinateName,
                    StringComparison.Ordinal);
        }

        /// <summary>
        /// 二次 remate 普通基座时，仅删除该件上「安装坐标系」端点参与的配合，保留「基座安装坐标系」上的首基架配合。
        /// </summary>
        internal static bool ShouldRemateDeleteMateEndpoint(
            string targetComponentInstanceName,
            string mateComponentInstanceName,
            string mateCoordinateName)
        {
            if (string.IsNullOrWhiteSpace(targetComponentInstanceName)
                || string.IsNullOrWhiteSpace(mateComponentInstanceName)
                || string.IsNullOrWhiteSpace(mateCoordinateName))
            {
                return false;
            }

            if (!FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(
                    mateComponentInstanceName,
                    targetComponentInstanceName))
            {
                return false;
            }

            return string.Equals(
                mateCoordinateName.Trim(),
                OrdinaryBaseInstallationCoordinateName,
                StringComparison.Ordinal);
        }

        internal static string BuildMateFailureMessage(
            string positionName,
            FaceToFaceBaseModelKind kind,
            string modelPath,
            FaceToFaceBaseMateRoute route)
        {
            if (route == null)
            {
                throw new ArgumentNullException(nameof(route));
            }

            return $"{positionName}{GetKindDisplayName(kind)}装配配合失败："
                + $"模型={modelPath}，"
                + $"父级坐标系={route.ParentCoordinateName}，"
                + $"子模型坐标系={route.ChildCoordinateName}。";
        }

        /// <summary>
        /// 同时检查 SolidWorks 删除返回值和删除后的组件树，防止误判回滚成功。
        /// </summary>
        internal static void ValidateComponentDeletion(
            bool deleteSucceeded,
            bool componentStillExists,
            string componentName)
        {
            if (!deleteSucceeded)
            {
                throw new InvalidOperationException(
                    $"删除基座组件失败，DeleteSelections 返回失败：{componentName}");
            }

            if (componentStillExists)
            {
                throw new InvalidOperationException(
                    $"删除操作返回成功，但总装配体中仍存在基座组件：{componentName}");
            }
        }

        private FaceToFaceBaseAssemblyToken InsertAndMate(
            string positionName,
            FaceToFaceBaseModelKind kind,
            string modelPath,
            Component2 parentComponent,
            FaceToFaceBaseMateRoute route)
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                throw new FileNotFoundException(
                    $"待装配{GetKindDisplayName(kind)}模型不存在：{modelPath}",
                    modelPath);
            }

            var fullModelPath = Path.GetFullPath(modelPath);
            IModelDoc2 openedModel = null;
            var openedByAssembler = false;
            Component2 insertedComponent = null;
            try
            {
                openedModel = _swApp.GetOpenDocumentByName(fullModelPath) as IModelDoc2;
                if (openedModel == null)
                {
                    openedModel = new SolidWorksDriver(_swApp)
                        .OpenOrActivateDocument(fullModelPath);
                    openedByAssembler = true;
                }

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
                        $"{positionName}{GetKindDisplayName(kind)}插入总装配体失败：{fullModelPath}");
                }

                try
                {
                    _totalAssembly.ResolveAllLightWeightComponents(false);
                }
                catch
                {
                    // 解析失败时仍尝试按当前组件状态创建坐标系配合，失败信息会包含完整上下文。
                }

                if (!PublicMethod.AddMateUseCoord(
                    _swApp,
                    _totalAssembly,
                    parentComponent,
                    insertedComponent,
                    route.ParentCoordinateName,
                    route.ChildCoordinateName))
                {
                    throw new InvalidOperationException(
                        BuildMateFailureMessage(positionName, kind, fullModelPath, route));
                }

                return new FaceToFaceBaseAssemblyToken(
                    positionName,
                    kind,
                    fullModelPath,
                    insertedComponent,
                    // 零部件属性全名（含实例号），供「模型对照映射」精确定位。
                    insertedComponent.Name2);
            }
            catch (Exception operationException)
            {
                if (insertedComponent != null)
                {
                    try
                    {
                        // 配合未成功的组件没有返回给编排层，因此必须在本方法内立即删除。
                        DeleteInsertedComponent(insertedComponent);
                    }
                    catch (Exception cleanupException)
                    {
                        throw new FaceToFaceBaseAssemblyCleanupException(
                            operationException.Message
                                + System.Environment.NewLine
                                + "已插入组件清理失败："
                                + cleanupException.Message,
                            new AggregateException(operationException, cleanupException));
                    }
                }

                throw;
            }
            finally
            {
                if (openedByAssembler && openedModel != null)
                {
                    try
                    {
                        _swApp.CloseDoc(openedModel.GetTitle());
                    }
                    catch
                    {
                        // 文档可能已由 SolidWorks 关闭；继续恢复总装配体为活动文档。
                    }
                }

                try
                {
                    ActivateTotalAssembly();
                }
                catch
                {
                    // 主异常由插入或配合步骤提供，激活失败不覆盖原始诊断信息。
                }
            }
        }

        private void DeleteInsertedComponent(Component2 component)
        {
            ActivateTotalAssembly();
            _totalAssemblyModel.ClearSelection2(true);
            try
            {
                var componentName = component.Name2;
                var selectName = component.GetSelectByIDString();
                var selected = !string.IsNullOrWhiteSpace(selectName)
                    && _totalAssemblyModel.Extension.SelectByID2(
                        selectName,
                        "COMPONENT",
                        0,
                        0,
                        0,
                        false,
                        0,
                        null,
                        0);
                if (!selected)
                {
                    selected = component.Select(false);
                }

                if (!selected)
                {
                    throw new InvalidOperationException("无法选中待删除的基座组件。");
                }

                var deleteSucceeded = _totalAssembly.DeleteSelections(
                    (int)swDeleteSelectionOptions_e.swDelete_Children);
                var componentStillExists = IsTopLevelComponentPresent(componentName);
                ValidateComponentDeletion(
                    deleteSucceeded,
                    componentStillExists,
                    componentName);
            }
            finally
            {
                _totalAssemblyModel.ClearSelection2(true);
            }
        }

        private Component2 FindTopLevelComponent(string componentFullName)
        {
            if (string.IsNullOrWhiteSpace(componentFullName))
            {
                return null;
            }

            var components = _totalAssembly.GetComponents(false) as object[];
            if (components == null)
            {
                return null;
            }

            foreach (var item in components)
            {
                var component = item as Component2;
                if (component != null
                    && FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(
                        component.Name2,
                        componentFullName))
                {
                    return component;
                }
            }

            return null;
        }

        private void DeleteComponentMatesMatching(
            Component2 component,
            Func<string, string, string, bool> shouldDeleteMateEndpoint)
        {
            var componentName = component?.Name2;
            var mates = component?.GetMates() as object[];
            if (string.IsNullOrWhiteSpace(componentName)
                || mates == null
                || mates.Length == 0)
            {
                return;
            }

            var mateNames = new List<string>();
            foreach (var mateObject in mates)
            {
                var mate = mateObject as IMate2;
                if (mate == null
                    || !MateInvolvesComponentCoordinate(
                        mate,
                        componentName,
                        shouldDeleteMateEndpoint))
                {
                    continue;
                }

                var feature = mateObject as Feature;
                if (feature != null && !string.IsNullOrWhiteSpace(feature.Name))
                {
                    mateNames.Add(feature.Name);
                }
            }

            SWHelper.TryDeleteAssemblyMatesByName(_totalAssemblyModel, mateNames);
        }

        private static bool MateInvolvesComponentCoordinate(
            IMate2 mate,
            string targetComponentInstanceName,
            Func<string, string, string, bool> shouldDeleteMateEndpoint)
        {
            var entityCount = mate.GetMateEntityCount();
            for (var index = 0; index < entityCount; index++)
            {
                var entity = mate.MateEntity(index) as MateEntity2;
                if (entity == null)
                {
                    continue;
                }

                string componentName = null;
                string coordinateName = null;
                try
                {
                    componentName = entity.ReferenceComponent?.Name2;
                    coordinateName = (entity.Reference as IFeature)?.Name;
                }
                catch
                {
                    // 无法读取的配合端点不属于可安全删除的安装坐标系端点。
                }

                if (shouldDeleteMateEndpoint(
                    targetComponentInstanceName,
                    componentName,
                    coordinateName))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsTopLevelComponentPresent(string componentName)
        {
            var components = _totalAssembly.GetComponents(false) as object[];
            if (components == null)
            {
                return false;
            }

            foreach (var item in components)
            {
                if (item is Component2 candidate
                    && string.Equals(
                        candidate.Name2,
                        componentName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void SaveTotalAssembly()
        {
            new SolidWorksDocumentPersistence(_swApp).SaveModel(_totalAssemblyModel);
        }

        private string GetTotalAssemblyIdentity()
        {
            var path = _totalAssemblyModel.GetPathName();
            return string.IsNullOrWhiteSpace(path)
                ? _totalAssemblyModel.GetTitle()
                : path;
        }

        private void ActivateTotalAssembly()
        {
            var errors = 0;
            var activated = _swApp.ActivateDoc3(
                _totalAssemblyModel.GetTitle(),
                true,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                ref errors) as IModelDoc2;

            // errors=2 表示文档已激活但需要重建，并不属于激活失败；
            // 此时立即重建总装配体，再继续插入和配合独立基座。
            EnsureActivatedDocumentReady(
                activated != null,
                errors,
                () => _totalAssemblyModel.EditRebuild3(),
                GetTotalAssemblyIdentity());
        }

        /// <summary>
        /// 校验 SolidWorks 文档激活结果，并在返回“需要重建”警告时完成重建。
        /// 仅放行明确的重建警告，避免忽略真正的文档激活错误。
        /// </summary>
        internal static void EnsureActivatedDocumentReady(
            bool documentActivated,
            int activationErrors,
            Func<bool> rebuildAction,
            string modelIdentity)
        {
            var rebuildWarning = (int)swActivateDocError_e.swDocNeedsRebuildWarning;
            if (!documentActivated
                || (activationErrors != 0 && activationErrors != rebuildWarning))
            {
                throw new InvalidOperationException(
                    $"无法激活基座目标总装配体：{modelIdentity}，errors={activationErrors}。");
            }

            if (activationErrors == rebuildWarning)
            {
                if (rebuildAction == null)
                {
                    throw new ArgumentNullException(nameof(rebuildAction));
                }

                FaceToFaceBaseOperationGuard.EnsureRebuildSucceeded(
                    rebuildAction(),
                    "激活基座目标总装配体后的重建",
                    modelIdentity);
            }
        }

        private static string GetKindDisplayName(FaceToFaceBaseModelKind kind)
        {
            return kind == FaceToFaceBaseModelKind.ElevatedBase
                ? "增高基座"
                : "普通基座";
        }
    }
}
