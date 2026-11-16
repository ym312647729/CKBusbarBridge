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
    /// 一次侧对侧基座坐标系配合的父、子坐标系名称。
    /// </summary>


    /// <summary>
    /// 表示插入失败后，SolidWorks 未确认即时删除新组件；服务必须保留磁盘模型引用。
    /// </summary>
    internal sealed class SideToSideBaseAssemblyCleanupException : InvalidOperationException
    {
        internal SideToSideBaseAssemblyCleanupException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// 侧对侧基座总装装配器。所有位置和A/B坐标选择均来自纯规划结果。
    /// </summary>
    internal sealed class SideToSideBaseAssembler : ISideToSideBaseAssembler
    {
        private readonly ISldWorks _swApp;
        private readonly IModelDoc2 _totalAssemblyModel;
        private readonly AssemblyDoc _totalAssembly;
        private readonly HashSet<string> _pendingComponentDocumentPaths
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal SideToSideBaseAssembler(
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
                throw new InvalidOperationException("侧对侧基座只能装到有效总装配体中。");
            }
        }

        public SideToSideBaseAssemblyToken InsertBase1AtRoot(
            SideToSideBasePositionPlan position,
            string modelPath)
        {
            if (position == null) throw new ArgumentNullException(nameof(position));
            return InsertAndMate(
                position,
                SideToSideBaseModelKind.Base1,
                modelPath,
                null,
                ResolveBase1Route(position.PositionName));
        }

        public SideToSideBaseAssemblyToken InsertElevatedAtRoot(
            SideToSideBasePositionPlan position,
            string modelPath)
        {
            if (position == null) throw new ArgumentNullException(nameof(position));
            return InsertAndMate(
                position,
                SideToSideBaseModelKind.ElevatedBase,
                modelPath,
                null,
                ResolveElevatedRoute(position.PositionName));
        }

        public SideToSideBaseAssemblyToken InsertBase2OnElevated(
            SideToSideBasePositionPlan position,
            string modelPath,
            SideToSideBaseAssemblyToken elevatedBase)
        {
            if (position == null) throw new ArgumentNullException(nameof(position));
            if (elevatedBase == null
                || elevatedBase.Kind != SideToSideBaseModelKind.ElevatedBase
                || !(elevatedBase.NativeComponent is Component2 parentComponent))
            {
                throw new InvalidOperationException(
                    position.PositionName + "缺少有效增高基座实例，无法安装基座2。");
            }

            if (!string.Equals(
                position.PositionName,
                elevatedBase.PositionName,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException("基座2与增高基座的位置不一致。");
            }

            return InsertAndMate(
                position,
                SideToSideBaseModelKind.Base2,
                modelPath,
                parentComponent,
                ResolveBase2Route(position.IsACabinet));
        }

        public void SaveAndRebuild()
        {
            try
            {
                SWHelper.EnsureAssemblyEditingContext(_swApp, _totalAssemblyModel);
                // 侧对侧总装即使重建返回 false 也可能正常保存；调用异常仍会向上抛出。
                _totalAssemblyModel.EditRebuild3();
                SaveTotalAssembly();
            }
            finally
            {
                // 基座驱动阶段移交的模型必须保持打开直至总装保存完成，
                // 保存成功或失败后统一关闭，避免留下磁盘占用。
                ClosePendingComponentDocuments();
            }
        }

        public void WriteModelMapping(string mappingJson)
        {
            if (string.IsNullOrWhiteSpace(mappingJson))
            {
                throw new ArgumentException("模型对照映射不能为空。", nameof(mappingJson));
            }

            SWHelper.EnsureAssemblyEditingContext(_swApp, _totalAssemblyModel);
            var driver = new SolidWorksDriver(_swApp);
            var previous = driver.GetCustomProperty(
                _totalAssemblyModel,
                SideToSideBaseModelMappingHelper.PropertyName);
            try
            {
                driver.SetCustomProperty(
                    _totalAssemblyModel,
                    SideToSideBaseModelMappingHelper.PropertyName,
                    mappingJson);
                SaveAndRebuild();
            }
            catch
            {
                // 映射保存失败时先恢复内存中的旧值；随后服务回滚并保存总装，
                // 防止失败映射跟随回滚结果落盘。
                if (string.IsNullOrWhiteSpace(previous))
                {
                    _totalAssemblyModel.Extension
                        .CustomPropertyManager[string.Empty]
                        .Delete2(SideToSideBaseModelMappingHelper.PropertyName);
                }
                else
                {
                    driver.SetCustomProperty(
                        _totalAssemblyModel,
                        SideToSideBaseModelMappingHelper.PropertyName,
                        previous);
                }

                throw;
            }
        }

        public void Rollback(
            IReadOnlyList<SideToSideBaseAssemblyToken> instancesInReverseOrder)
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
                    errors.Add("回滚记录包含无效组件句柄。");
                    continue;
                }

                try
                {
                    DeleteComponent(component);
                }
                catch (Exception exception)
                {
                    errors.Add(token.PositionName + "回滚失败：" + exception.Message);
                }
            }

            try
            {
                SaveAndRebuild();
            }
            catch (Exception exception)
            {
                errors.Add("保存回滚结果失败：" + exception.Message);
            }

            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", errors));
            }
        }

        internal static SideToSideBaseMateRoute ResolveBase1Route(string positionName)
        {
            // 实现迁入界面运行时；正式生成与反读共用相同规则。
            return BridgeModelReadOperations.ResolveBase1Route(positionName);
        }

        internal static SideToSideBaseMateRoute ResolveElevatedRoute(string positionName)
        {
            // 实现迁入界面运行时；正式生成与反读共用相同规则。
            return BridgeModelReadOperations.ResolveElevatedRoute(positionName);
        }

        internal static SideToSideBaseMateRoute ResolveBase2Route(bool isCabinetA)
        {
            // 实现迁入界面运行时；正式生成与反读共用相同规则。
            return BridgeModelReadOperations.ResolveBase2Route(isCabinetA);
        }

        private static string ResolveCabinetCoordinateName(
            string positionName,
            string openingModeName)
        {
            // 实现迁入界面运行时；正式生成与反读共用相同规则。
            return BridgeModelReadOperations.ResolveCabinetCoordinateName(positionName, openingModeName);
        }

        private SideToSideBaseAssemblyToken InsertAndMate(
            SideToSideBasePositionPlan position,
            SideToSideBaseModelKind kind,
            string modelPath,
            Component2 parentComponent,
            SideToSideBaseMateRoute route)
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                throw new FileNotFoundException("待装配侧对侧基座不存在：" + modelPath, modelPath);
            }

            var fullPath = Path.GetFullPath(modelPath);
            var driver = new SolidWorksDriver(_swApp);
            IModelDoc2 openedModel = null;
            Component2 inserted = null;
            var insertionSucceeded = false;
            try
            {
                openedModel = _swApp.GetOpenDocumentByName(fullPath) as IModelDoc2;
                if (openedModel == null)
                {
                    openedModel = driver.OpenOrActivateDocument(fullPath);
                }

                SWHelper.EnsureAssemblyEditingContext(_swApp, _totalAssemblyModel);
                inserted = _totalAssembly.AddComponent5(
                    fullPath,
                    (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                    string.Empty,
                    false,
                    string.Empty,
                    0,
                    0,
                    0);
                if (inserted == null)
                {
                    throw new InvalidOperationException(
                        position.PositionName + "插入" + kind + "失败：" + fullPath);
                }

                try
                {
                    _totalAssembly.ResolveAllLightWeightComponents(false);
                }
                catch
                {
                    // 与面对面装配保持一致：解析失败时仍按当前组件状态尝试创建坐标系配合。
                }

                // 直接复用面对面场景的坐标系重合配合逻辑；根级安装时 parent 为 null。
                if (!PublicMethod.AddMateUseCoord(
                    _swApp,
                    _totalAssembly,
                    parentComponent,
                    inserted,
                    route.ParentCoordinateName,
                    route.ChildCoordinateName))
                {
                    throw new InvalidOperationException(
                        position.PositionName
                        + kind
                        + "坐标系配合失败：父="
                        + route.ParentCoordinateName
                        + "，子="
                        + route.ChildCoordinateName
                        + "。");
                }

                // 模型由前序驱动阶段打开并移交；总装完成重建与保存后再统一关闭。
                _pendingComponentDocumentPaths.Add(fullPath);
                insertionSucceeded = true;
                return new SideToSideBaseAssemblyToken(
                    position.PositionName,
                    kind,
                    fullPath,
                    inserted,
                    inserted.Name2);
            }
            catch (Exception originalException)
            {
                if (inserted != null)
                {
                    try
                    {
                        DeleteComponent(inserted);
                    }
                    catch (Exception cleanupException)
                    {
                        throw new SideToSideBaseAssemblyCleanupException(
                            originalException.Message
                                + System.Environment.NewLine
                                + "失败组件即时清理未确认："
                                + cleanupException.Message,
                            new AggregateException(originalException, cleanupException));
                    }
                }

                throw;
            }
            finally
            {
                if (openedModel != null)
                {
                    try
                    {
                        // 插入或配合失败时不会进入总装保存阶段，必须在此立即关闭。
                        if (!insertionSucceeded)
                        {
                            driver.TryCloseDocumentByPath(fullPath);
                        }
                    }
                    finally
                    {
                        SolidWorksFileHelper.ReleaseComReference(openedModel);
                        SWHelper.EnsureAssemblyEditingContext(_swApp, _totalAssemblyModel);
                    }
                }
            }
        }

        private void ClosePendingComponentDocuments()
        {
            if (_pendingComponentDocumentPaths.Count == 0)
            {
                return;
            }

            var paths = new List<string>(_pendingComponentDocumentPaths);
            _pendingComponentDocumentPaths.Clear();
            var driver = new SolidWorksDriver(_swApp);
            foreach (var path in paths)
            {
                driver.TryCloseDocumentByPath(path);
            }

            SWHelper.EnsureAssemblyEditingContext(_swApp, _totalAssemblyModel);
        }

        private void DeleteComponent(Component2 component)
        {
            SWHelper.EnsureAssemblyEditingContext(_swApp, _totalAssemblyModel);
            _totalAssemblyModel.ClearSelection2(true);
            try
            {
                if (!component.Select(false)
                    || !_totalAssembly.DeleteSelections(
                        (int)swDeleteSelectionOptions_e.swDelete_Children))
                {
                    throw new InvalidOperationException(
                        "SolidWorks 未确认组件删除：" + component.Name2);
                }
            }
            finally
            {
                _totalAssemblyModel.ClearSelection2(true);
            }
        }

        private void SaveTotalAssembly()
        {
            new SolidWorksDocumentPersistence(_swApp).SaveModel(_totalAssemblyModel);
        }
    }
}
