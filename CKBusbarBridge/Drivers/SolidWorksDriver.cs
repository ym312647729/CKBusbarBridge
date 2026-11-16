using CKBusbarBridge.Helpers;
using CKBusbarBridge.Drivers;
using CKBusbarBridge.models;
using CKBusbarBridge.Models;
using Newtonsoft.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Formatting = Newtonsoft.Json.Formatting;

namespace CKBusbarBridge
{
    public class SolidWorksDriver
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private const string HoleBaseNoExtensionPlateDrawingNumber = "8GX.017.218";
        private const string FaceToFaceHoleBaseNoExtensionPlateDrawingNumber = "8GX.017.216";
        private const string HoleBaseExtensionPlateDrawingNumber = "8GX.017.219";
        private const string FaceToFaceHoleBaseExtensionPlateDrawingNumber = "8GX.017.217";
        private const string HoleBasePlateLengthVariableName = "板长";
        private const string HoleBasePlateWidthVariableName = "板宽";
        private const double HoleBasePlateEdgeOffset = 2;

        private ISldWorks _swApp;

        // 构造函数传入 SolidWorks 应用程序实例
        public SolidWorksDriver(ISldWorks swApp)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
        }

        internal void PrepareForGeneration()
        {
            new SolidWorksDocumentPersistence(_swApp).PrepareForGeneration();
        }

        internal static void HideAssemblyCoordinateSystemsAndSketches(IModelDoc2 assemblyModel)
        {
            if (assemblyModel == null) throw new ArgumentNullException(nameof(assemblyModel));

            var assemblyPath = string.Empty;
            try
            {
                assemblyPath = assemblyModel.GetPathName();
                if (assemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                    throw new ArgumentException("关闭坐标系和草图显示的目标必须为装配体：" + assemblyPath, nameof(assemblyModel));

                const int option = (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified;
                var extension = assemblyModel.Extension;
                foreach (var toggle in new[] { swUserPreferenceToggle_e.swDisplayCoordSystems, swUserPreferenceToggle_e.swDisplaySketches })
                {
                    if (!extension.GetUserPreferenceToggle((int)toggle, option)) continue;
                    if (!extension.SetUserPreferenceToggle((int)toggle, option, false)
                        || extension.GetUserPreferenceToggle((int)toggle, option))
                    {
                        Logger.Warn("驱动收尾关闭总装配体显示失败：{0}；显示项：{1}", assemblyPath, toggle);
                    }
                }
            }
            catch (Exception error) when (error is COMException || error is InvalidComObjectException)
            {
                Logger.Warn(error, "驱动收尾关闭总装配体坐标系和草图显示失败：{0}", assemblyPath);
            }
        }

        /// <summary>
        /// 在当前 SolidWorks 会话中打开或激活指定路径的模型文档，并对装配体执行去轻量化。
        /// </summary>
        /// <param name="filePath">要打开的 SolidWorks 文件完整路径（.sldasm/.sldprt/.slddrw）。</param>
        /// <returns>打开（或已激活）的 <see cref="IModelDoc2"/> 文档对象。</returns>
        /// <exception cref="ArgumentException">当 <paramref name="filePath"/> 为空时抛出。</exception>
        /// <exception cref="InvalidOperationException">当 SolidWorks 无法打开该文件时抛出。</exception>
        public IModelDoc2 OpenOrActivateDocument(string filePath, bool rebuildAfterResolve = true)
        {
            var doc = OpenOrActivateDocumentWithoutResolve(filePath);
            ResolveLightweightIfNeeded(doc, rebuildAfterResolve);
            return doc;
        }

        /// <summary>基线准备仅打开或激活文档，不隐式解析或重建；调用方按需解析并核验保存状态。</summary>
        internal IModelDoc2 OpenOrActivateDocumentWithoutResolve(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException(nameof(filePath));

            var doc = TryOpenOrActivateDocumentWithoutResolve(filePath, out int errs, out int warns);
            if (doc == null)
            {
                throw new InvalidOperationException($"OpenDoc6 failed. errors={errs}, warnings={warns}, file='{filePath}'");
            }

            return doc;
        }

        /// <summary>
        /// 尝试打开或激活指定路径的模型文档；失败时返回 null 而不抛出异常。
        /// </summary>
        public IModelDoc2 TryOpenOrActivateDocument(string filePath, bool rebuildAfterResolve = true)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            var doc = TryOpenOrActivateDocumentWithoutResolve(filePath, out _, out _);
            if (doc == null) return null;

            ResolveLightweightIfNeeded(doc, rebuildAfterResolve);
            return doc;
        }

        private IModelDoc2 TryOpenOrActivateDocumentWithoutResolve(string filePath, out int errs, out int warns)
        {
            errs = 0;
            warns = 0;

            string requestedFullPath;
            try { requestedFullPath = Path.GetFullPath(filePath); }
            catch { requestedFullPath = filePath; }

            new SolidWorksDocumentConflictGuard(_swApp).PrepareForOpen(requestedFullPath);

            // =====================
            // 1) 优先复用已打开的文档
            // =====================
            // SolidWorks API 中“打开文档”是比较重的操作：
            // - 可能触发加载、解析特征树、重建等
            // - 速度慢且容易受用户当前 SolidWorks 状态影响
            // 因此这里采用：
            // - 若当前活动文档就是目标文件：直接返回
            // - 若目标文件已在已打开文档列表中：激活并返回
            // - 否则：再走 OpenDoc6 静默打开

            var active = _swApp.ActiveDoc as IModelDoc2;
            if (active != null)
            {
                var activePath = TryGetPathName(active);
                if (!string.IsNullOrEmpty(activePath)
                    && string.Equals(
                        NormalizeDocumentPath(activePath),
                        requestedFullPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return active;
                }
            }

            var existing = FindOpenDocumentByPath(requestedFullPath);
            if (existing != null)
            {
                // ActivateDoc3 的最后一个参数是 ref int errors
                // - 传入 0 即可
                // - 返回后可读取是否有错误，但这里不强制中断
                var activateErrors = 0;
                // 激活阶段不重建；是否重建由随后 ResolveLightweightIfNeeded 的
                // rebuildAfterResolve 参数统一控制，避免“激活重建 + 去轻量化后重建”重复执行。
                _swApp.ActivateDoc3(
                    existing.GetTitle(),
                    true,
                    (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                    ref activateErrors);
                return existing;
            }

            // =====================
            // 2) 文档未打开：静默打开
            // =====================
            // OpenDoc6 需要明确指定文档类型。
            // 模板在不同阶段可能是：装配体(.sldasm) / 零件(.sldprt) / 工程图(.slddrw)
            // 为了更稳妥，这里按常见优先级依次尝试打开。

            var doc = _swApp.OpenDoc6(requestedFullPath, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns) as IModelDoc2;
            if (doc != null)
            {
                ValidateOpenedDocumentPath(requestedFullPath, doc, errs, warns);
                return doc;
            }

            doc = _swApp.OpenDoc6(requestedFullPath, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns) as IModelDoc2;
            if (doc != null)
            {
                ValidateOpenedDocumentPath(requestedFullPath, doc, errs, warns);
                return doc;
            }

            doc = _swApp.OpenDoc6(requestedFullPath, (int)swDocumentTypes_e.swDocDRAWING, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns) as IModelDoc2;
            if (doc != null)
            {
                ValidateOpenedDocumentPath(requestedFullPath, doc, errs, warns);
            }

            return doc;
        }


        private static void ValidateOpenedDocumentPath(
            string requestedFullPath,
            IModelDoc2 document,
            int errors,
            int warnings)
        {
            SolidWorksFileHelper.ValidateOpenedDocumentPath(
                requestedFullPath,
                TryGetPathName(document),
                errors,
                warnings);
        }

        private static string NormalizeDocumentPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }


        /// <summary>
        /// 对当前活动文档执行去轻量化处理。
        /// </summary>
        public void ResolveActiveDocumentLightweightIfNeeded()
        {
            ResolveLightweightIfNeeded(_swApp.ActiveDoc as IModelDoc2);
        }

        /// <summary>反读总装前确认活动配置的组件均已解析；只在发现轻量化组件时调用 SolidWorks 解析。</summary>
        public static void EnsureAssemblyFullyResolved(IModelDoc2 model)
        {
            // 实现迁入未加密的界面运行时，保留原调用入口。
            CKBusbarBridge.Drivers.BridgeModelReadOperations.EnsureAssemblyFullyResolved(model);
        }



        private static void ResolveLightweightIfNeeded(IModelDoc2 doc, bool rebuildAfterResolve = true)
        {
            if (doc == null) return;

            try
            {
                if (doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return;

                var assemblyDoc = doc as IAssemblyDoc;
                if (assemblyDoc == null) return;

                // 去轻量化：将装配体中的轻量化零部件全部解析为已解析状态
                assemblyDoc.ResolveAllLightWeightComponents(false);

                if (rebuildAfterResolve)
                {
                    doc.EditRebuild3();
                }
            }
            catch
            {
                // 去轻量化失败不阻断流程（可能是版本差异/权限/文档状态等原因）
            }
        }

        /// <summary>
        /// 将一组“中文全局变量名 -> 值”的映射写入到模型方程式中。
        /// </summary>
        /// <param name="model">需要写入方程式的模型文档（装配体/零件）。</param>
        /// <param name="nameToValue">中文全局变量名到值的映射；模型中找不到的变量会被跳过。</param>
        /// <exception cref="ArgumentNullException">当任一参数为 null 时抛出。</exception>
        public void ApplyGlobalVariables(IModelDoc2 model, IDictionary<string, object> nameToValue)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (nameToValue == null) throw new ArgumentNullException(nameof(nameToValue));

            // =====================
            // SolidWorks 参数化方式说明
            // =====================
            // 本项目模板模型通过“方程式(Equations)”实现参数化建模。
            // 模型中的“全局变量(Global Variables)”也会以方程式的形式存在。
            // 例如全局变量“柜间距”，常见的方程式字符串类似：
            // 我们要做的事情就是：
            // - 枚举方程式
            // - 找到左侧等于目标中文变量名的方程式
            // - 将该方程式整体替换为新的值
            // 注意：模板里可能不存在某些全局变量（不同版本/不同模板），此时按需求“跳过不处理”。

            ApplyGlobalVariablesToSingleModel(model, nameToValue, requireAllVariables: false, rebuildAfterChange: true);
        }

        /// <summary>
        /// 将一组“中文全局变量名 -> 值”的映射写入到模型方程式中，可选是否立即重建。
        /// </summary>
        /// <param name="model">需要写入方程式的模型文档（装配体/零件）。</param>
        /// <param name="nameToValue">中文全局变量名到值的映射；模型中找不到的变量会被跳过。</param>
        /// <param name="rebuildAfterChange">写入后是否立即重建。</param>
        public void ApplyGlobalVariables(IModelDoc2 model, IDictionary<string, object> nameToValue, bool rebuildAfterChange)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (nameToValue == null) throw new ArgumentNullException(nameof(nameToValue));

            ApplyGlobalVariablesToSingleModel(model, nameToValue, requireAllVariables: false, rebuildAfterChange: rebuildAfterChange);
        }

        /// <summary>
        /// 将一组“中文全局变量名 -> 值”的映射写入到模型方程式中。
        /// 映射中任一变量在模型中不存在时抛出异常。
        /// </summary>
        /// <param name="model">需要写入方程式的模型文档（装配体/零件）。</param>
        /// <param name="nameToValue">中文全局变量名到值的映射。</param>
        /// <exception cref="ArgumentNullException">当任一参数为 null 时抛出。</exception>
        /// <exception cref="InvalidOperationException">当模型不支持方程式或存在未找到的全局变量时抛出。</exception>
        public void ApplyGlobalVariablesRequired(IModelDoc2 model, IDictionary<string, object> nameToValue)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (nameToValue == null) throw new ArgumentNullException(nameof(nameToValue));

            ApplyGlobalVariablesToSingleModel(model, nameToValue, requireAllVariables: true, rebuildAfterChange: true);
        }

        /// <summary>
        /// 将一组“中文全局变量名 -> 值”的映射写入到模型方程式中，可选是否立即重建。
        /// 映射中任一变量在模型中不存在时抛出异常。
        /// </summary>
        /// <param name="model">需要写入方程式的模型文档（装配体/零件）。</param>
        /// <param name="nameToValue">中文全局变量名到值的映射。</param>
        /// <param name="rebuildAfterChange">写入后是否立即重建。</param>
        public void ApplyGlobalVariablesRequired(IModelDoc2 model, IDictionary<string, object> nameToValue, bool rebuildAfterChange)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (nameToValue == null) throw new ArgumentNullException(nameof(nameToValue));

            ApplyGlobalVariablesToSingleModel(model, nameToValue, requireAllVariables: true, rebuildAfterChange: rebuildAfterChange);
        }

        internal void ApplyGlobalVariablesRequired(IModelDoc2 model, IDictionary<string, object> nameToValue,
            bool rebuildAfterChange, Action<IEquationMgr, int, string, string> equationWriteObserver)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (nameToValue == null) throw new ArgumentNullException(nameof(nameToValue));
            ApplyGlobalVariablesToSingleModel(model, nameToValue, requireAllVariables: true,
                rebuildAfterChange: rebuildAfterChange, equationWriteObserver: equationWriteObserver);
        }

        /// <summary>
        /// 打开子装配体组件对应的磁盘文件，写入全局变量并在回调中完成附加配置（如自定义属性）。
        /// 5GX.355.003 基础基座与 5GX.355.002 延长基座共用此流程。
        /// </summary>
        /// <param name="rootModel">当前总装配体模型。</param>
        /// <param name="component">目标子装配体组件。</param>
        /// <param name="variableMap">全局变量名到值的映射。</param>
        /// <param name="configureAssembly">全局变量写入后、重建前对子装配体执行的附加配置。</param>
        /// <param name="parameterGroupName">参数分组名称，用于异常提示。</param>
        /// <exception cref="InvalidOperationException">组件或模型不可用时抛出。</exception>
        public void DriveComponentAssemblyGlobalVariables(
            IModelDoc2 rootModel,
            IComponent2 component,
            IDictionary<string, object> variableMap,
            Action<IModelDoc2> configureAssembly,
            string parameterGroupName)
        {
            if (rootModel == null || component == null)
            {
                return;
            }
            if ((variableMap == null || variableMap.Count == 0)
                && configureAssembly == null)
            {
                return;
            }

            IModelDoc2 assemblyModel = null;
            var openedAssembly = false;
            var driveSucceeded = false;
            string assemblyPath = null;
            try
            {
                assemblyPath = component.GetPathName();
            }
            catch
            {
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath))
                {
                    assemblyModel = OpenOrActivateDocument(assemblyPath);
                    openedAssembly = assemblyModel != null;
                }

                if (assemblyModel == null)
                {
                    assemblyModel = component.GetModelDoc2() as IModelDoc2;
                }

                if (assemblyModel == null)
                {
                    throw new InvalidOperationException($"{parameterGroupName}驱动失败，未找到基座模型。");
                }

                if (variableMap != null && variableMap.Count > 0)
                {
                    ApplyGlobalVariablesToSingleModel(assemblyModel, variableMap, requireAllVariables: true,
                        writableHost: component.IsVirtual ? rootModel : null);
                }
                configureAssembly?.Invoke(assemblyModel);

                assemblyModel.EditRebuild3();
                driveSucceeded = true;
            }
            finally
            {
                Exception saveFailure = null;
                try
                {
                    if (driveSucceeded && openedAssembly && assemblyModel != null)
                    {
                        var closePath = assemblyModel.GetPathName();
                        SolidWorksFileHelper.ReleaseComReference(assemblyModel);
                        assemblyModel = null;
                        TryCloseDocumentByPath(closePath);
                    }
                }
                catch (Exception exception)
                {
                    saveFailure = exception;
                    throw;
                }
                finally
                {
                    SolidWorksFileHelper.ReleaseComReference(assemblyModel);
                    try { SWHelper.EnsureAssemblyEditingContext(_swApp, rootModel); }
                    catch (Exception exception) when (!driveSucceeded || saveFailure != null)
                    {
                        Logger.Warn(exception, "驱动失败后恢复装配编辑上下文未完成：{0}", assemblyPath);
                    }
                }
            }

            rootModel.EditRebuild3();
        }

        /// <summary>
        /// 驱动 5GX.355.003 基础基座下的 8GX.017.218/004 挡板。
        /// 面对面场景替换为 8GX.017.216/217，面对墙保持 8GX.017.218/004。
        /// 启用延长基座时压缩 002、解压 004；未启用时相反，并仅驱动当前解压挡板的板长/板宽。
        /// 面对面与面对墙场景共用本流程，场景差异仅由调用方传入的基座开孔高度决定。
        /// </summary>
        /// <param name="rootModel">柜体总装配体。</param>
        /// <param name="baseComponent">5GX.355.003 基础基座组件。</param>
        /// <param name="suffixValue">挡板组件后缀，如 -A柜前、-柜前。</param>
        /// <param name="isExtensionBaseEnabled">是否启用延长基座。</param>
        /// <param name="baseWidth">基础基座宽度。</param>
        /// <param name="baseHeight">基础基座高度。</param>
        /// <param name="openingHeight">基座开孔高度；面对面为固定值，面对墙取墙孔高度。</param>
        /// <param name="parameterGroupName">参数分组名称，用于异常提示。</param>
        public void DriveHoleBasePlateSubComponents(
            IModelDoc2 rootModel,
            IComponent2 baseComponent,
            string suffixValue,
            bool isExtensionBaseEnabled,
            double baseWidth,
            double baseHeight,
            double openingHeight,
            bool isFaceToFace,
            string parameterGroupName)
        {
            if (rootModel == null || string.IsNullOrWhiteSpace(suffixValue))
            {
                return;
            }

            var baseModel = baseComponent?.GetModelDoc2() as IModelDoc2;
            if (baseComponent == null || baseModel == null)
            {
                throw new InvalidOperationException($"{parameterGroupName}驱动失败，未找到基础基座组件。");
            }

            var noExtensionPlateDrawingNumber = isFaceToFace
                ? FaceToFaceHoleBaseNoExtensionPlateDrawingNumber
                : HoleBaseNoExtensionPlateDrawingNumber;
            var noExtensionPlateName = noExtensionPlateDrawingNumber + suffixValue;
            var extensionPlateDrawingNumber = isFaceToFace
                ? FaceToFaceHoleBaseExtensionPlateDrawingNumber
                : HoleBaseExtensionPlateDrawingNumber;
            var extensionPlateName = extensionPlateDrawingNumber + suffixValue;
            var noExtensionPlate = FindDirectChildComponentByName(baseComponent, noExtensionPlateName);
            if (noExtensionPlate == null)
            {
                throw new InvalidOperationException($"{parameterGroupName}驱动失败，未找到组件：{noExtensionPlateName}");
            }

            var extensionPlate = FindDirectChildComponentByName(baseComponent, extensionPlateName);
            if (extensionPlate == null)
            {
                throw new InvalidOperationException($"{parameterGroupName}驱动失败，未找到组件：{extensionPlateName}");
            }

            SetComponentSuppression(noExtensionPlate, isExtensionBaseEnabled);
            SetComponentSuppression(extensionPlate, !isExtensionBaseEnabled);
            baseModel.EditRebuild3();

            var activePlate = isExtensionBaseEnabled ? extensionPlate : noExtensionPlate;
            var plateVariableMap = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                [HoleBasePlateLengthVariableName] = baseWidth - HoleBasePlateEdgeOffset,
                [HoleBasePlateWidthVariableName] = baseHeight - openingHeight - HoleBasePlateEdgeOffset
            };

            DriveComponentAssemblyGlobalVariables(
                rootModel,
                activePlate,
                plateVariableMap,
                null,
                parameterGroupName);
        }

        private static IComponent2 FindDirectChildComponentByName(IComponent2 parentComponent, string expectedName)
        {
            if (parentComponent == null || string.IsNullOrWhiteSpace(expectedName))
            {
                return null;
            }

            var children = parentComponent.GetChildren() as object[];
            if (children == null || children.Length == 0)
            {
                return null;
            }

            return children
                .OfType<IComponent2>()
                .FirstOrDefault(component => ComponentNameMatches(component, expectedName));
        }

        private static bool ComponentNameMatches(IComponent2 component, string expectedName)
        {
            if (component == null || string.IsNullOrWhiteSpace(expectedName))
            {
                return false;
            }

            var normalizedExpectedName = NormalizeHoleBaseComponentName(expectedName);
            var candidateNames = new[]
            {
                NormalizeHoleBaseComponentName(component.Name2),
                NormalizeHoleBaseComponentName(Path.GetFileNameWithoutExtension(component.GetPathName()))
            };

            return candidateNames.Any(
                name => string.Equals(name, normalizedExpectedName, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeHoleBaseComponentName(string componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName))
            {
                return string.Empty;
            }

            var normalizedName = componentName.Trim();
            var atIndex = normalizedName.IndexOf('@');
            if (atIndex >= 0)
            {
                normalizedName = normalizedName.Substring(0, atIndex);
            }

            var slashIndex = normalizedName.LastIndexOf('/');
            if (slashIndex >= 0)
            {
                normalizedName = normalizedName.Substring(slashIndex + 1);
            }

            normalizedName = Regex.Replace(normalizedName, @"<\d+>$", string.Empty);
            normalizedName = Regex.Replace(normalizedName, @"-\d+$", string.Empty);
            return normalizedName.Trim();
        }

        private static void SetComponentSuppression(IComponent2 component, bool isSuppressed)
        {
            if (component == null)
            {
                return;
            }

            var suppressionState = isSuppressed
                ? (int)swComponentSuppressionState_e.swComponentSuppressed
                : (int)swComponentSuppressionState_e.swComponentResolved;

            component.SetSuppression2(suppressionState);
        }

        /// <summary>
        /// 将指定文本写入模型的自定义属性。
        /// </summary>
        /// <param name="model">目标模型文档。</param>
        /// <param name="propertyName">自定义属性名称。</param>
        /// <param name="propertyValue">自定义属性值。</param>
        /// <exception cref="ArgumentNullException">当模型为空时抛出。</exception>
        /// <exception cref="ArgumentException">当属性名为空时抛出。</exception>
        /// <exception cref="InvalidOperationException">当模型不支持自定义属性写入时抛出。</exception>
        public void SetCustomProperty(IModelDoc2 model, string propertyName, string propertyValue)
            => SetCustomProperty(model, propertyName, propertyValue, rebuildAfterChange: true);

        // 事务标识仅为持久化元数据，不能在实体已驱动并验收后隐式重新求解总装。
        internal void SetCustomProperty(IModelDoc2 model, string propertyName, string propertyValue, bool rebuildAfterChange)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (string.IsNullOrWhiteSpace(propertyName)) throw new ArgumentException(nameof(propertyName));

            var extension = model.Extension;
            var customPropertyManager = extension?.CustomPropertyManager[string.Empty];
            if (customPropertyManager == null)
            {
                throw new InvalidOperationException("目标模型不支持自定义属性写入。");
            }

            customPropertyManager.Add3(
                propertyName,
                (int)swCustomInfoType_e.swCustomInfoText,
                propertyValue ?? string.Empty,
                (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);

            if (rebuildAfterChange) model.EditRebuild3();
            else
            {
                // 哈希是原始文本，使用缓存读取即可核验；不为元数据触发求值或配置更新。
                string raw, resolved;
                bool wasResolved, linked;
                customPropertyManager.Get6(propertyName, true, out raw, out resolved, out wasResolved, out linked);
                if (!string.Equals(raw, propertyValue ?? string.Empty, StringComparison.Ordinal))
                    throw new InvalidOperationException("元数据属性写入核验失败：" + propertyName);
            }
        }

        /// <summary>
        /// 读取模型的自定义属性值。
        /// JSON 等文本属性优先返回表达式（原始值），避免 SolidWorks 求值后的 resolved 值破坏内容。
        /// </summary>
        /// <param name="model">目标模型文档。</param>
        /// <param name="propertyName">自定义属性名称。</param>
        /// <returns>解析后的属性值；不存在时返回 null。</returns>
        public string GetCustomProperty(IModelDoc2 model, string propertyName)
        {
            // 实现迁入未加密的界面运行时，保留原调用入口。
            return CKBusbarBridge.Drivers.BridgeModelReadOperations.GetCustomProperty(model, propertyName);
        }

        /// <summary>
        /// 将文本写入模型指定配置的自定义属性，并校验实际写入结果。
        /// </summary>
        public void SetConfigurationCustomProperty(
            IModelDoc2 model,
            string configurationName,
            string propertyName,
            string propertyValue)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (string.IsNullOrWhiteSpace(configurationName))
                throw new ArgumentException(nameof(configurationName));
            if (string.IsNullOrWhiteSpace(propertyName))
                throw new ArgumentException(nameof(propertyName));

            var customPropertyManager = model.Extension
                ?.CustomPropertyManager[configurationName];
            if (customPropertyManager == null)
            {
                throw new InvalidOperationException(
                    $"目标模型配置“{configurationName}”不支持自定义属性写入。");
            }

            var targetValue = propertyValue ?? string.Empty;
            customPropertyManager.Add3(
                propertyName,
                (int)swCustomInfoType_e.swCustomInfoText,
                targetValue,
                (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);

            var writtenValue = ReadCustomPropertyFromManager(
                customPropertyManager,
                propertyName);
            if (!string.Equals(writtenValue, targetValue, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"配置“{configurationName}”的属性“{propertyName}”写入失败。");
            }

            model.EditRebuild3();
        }

        /// <summary>
        /// 读取模型指定配置下的自定义属性；不存在时返回 null。
        /// </summary>
        public string GetConfigurationCustomProperty(
            IModelDoc2 model,
            string configurationName,
            string propertyName)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (string.IsNullOrWhiteSpace(configurationName))
                throw new ArgumentException(nameof(configurationName));
            if (string.IsNullOrWhiteSpace(propertyName))
                throw new ArgumentException(nameof(propertyName));

            return ReadCustomPropertyFromManager(
                model.Extension?.CustomPropertyManager[configurationName],
                propertyName);
        }

        /// <summary>
        /// 删除模型指定配置下的自定义属性；属性不存在时视为成功。
        /// </summary>
        public void DeleteConfigurationCustomProperty(
            IModelDoc2 model,
            string configurationName,
            string propertyName)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (string.IsNullOrWhiteSpace(configurationName))
                throw new ArgumentException(nameof(configurationName));
            if (string.IsNullOrWhiteSpace(propertyName))
                throw new ArgumentException(nameof(propertyName));

            var customPropertyManager = model.Extension
                ?.CustomPropertyManager[configurationName];
            if (customPropertyManager == null)
            {
                throw new InvalidOperationException(
                    $"目标模型配置“{configurationName}”不支持自定义属性删除。");
            }

            var deleteResult = customPropertyManager.Delete2(propertyName);
            if (deleteResult != (int)swCustomInfoDeleteResult_e.swCustomInfoDeleteResult_OK
                && deleteResult
                    != (int)swCustomInfoDeleteResult_e.swCustomInfoDeleteResult_NotPresent)
            {
                throw new InvalidOperationException(
                    $"删除配置“{configurationName}”的属性“{propertyName}”失败；result={deleteResult}。");
            }

            model.EditRebuild3();
        }

        /// <summary>
        /// 从指定自定义属性管理器读取属性文本。
        /// </summary>
        private static string ReadCustomPropertyFromManager(CustomPropertyManager customPropertyManager, string propertyName)
        {
            // 实现迁入未加密的界面运行时，保留原调用入口。
            return CKBusbarBridge.Drivers.BridgeModelReadOperations.ReadCustomPropertyFromManager(customPropertyManager, propertyName);
        }

        /// <summary>
        /// 在原始值与求值结果之间选择更可靠的属性文本。
        /// </summary>
        private static string SelectCustomPropertyText(string rawValue, string resolvedValue)
        {
            // 实现迁入界面运行时；正式生成与反读共用相同规则。
            return BridgeModelReadOperations.SelectCustomPropertyText(rawValue, resolvedValue);
        }

        /// <summary>
        /// 去掉 SolidWorks 文本属性可能携带的外层引号。
        /// </summary>
        private static string NormalizeCustomPropertyStoredText(string value)
        {
            // 实现迁入界面运行时；正式生成与反读共用相同规则。
            return BridgeModelReadOperations.NormalizeCustomPropertyStoredText(value);
        }

        /// <summary>
        /// 按组件名称模式批量设置装配体中组件的压缩状态。
        /// 组件名称支持“包含匹配”，可用于按段排文件夹/子装配体名称统一压缩或解压。
        /// </summary>
        /// <param name="assemblyModel">目标装配体模型。</param>
        /// <param name="componentNamePatterns">要匹配的组件名称模式集合。</param>
        /// <param name="suppress">true=压缩；false=解压到已解析状态。</param>
        /// <param name="rebuildAfterChange">修改压缩状态后是否立即重建；批量驱动可设为 false 并在全部修改后统一重建。</param>
        /// <returns>尝试处理的组件数量。</returns>
        public int SetAssemblyComponentSuppressionByNames(
            IModelDoc2 assemblyModel,
            IEnumerable<string> componentNamePatterns,
            bool suppress,
            bool rebuildAfterChange = true)
        {
            if (assemblyModel == null) throw new ArgumentNullException(nameof(assemblyModel));
            if (componentNamePatterns == null) throw new ArgumentNullException(nameof(componentNamePatterns));
            if (assemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException("目标模型不是装配体，无法设置组件压缩状态。");
            }

            var normalizedPatterns = componentNamePatterns
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(NormalizeComponentName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedPatterns.Count == 0)
            {
                return 0;
            }

            var matchedFolderFeatures = FindMatchedFolderFeatures(assemblyModel, normalizedPatterns);
            if (matchedFolderFeatures.Count > 0)
            {
                assemblyModel.ClearSelection2(true);

                var append = false;
                foreach (var folderFeature in matchedFolderFeatures)
                {
                    if (folderFeature == null) continue;

                    try
                    {
                        if (folderFeature.Select2(append, -1))
                        {
                            append = true;
                        }
                    }
                    catch
                    {
                    }
                }

                if (append)
                {
                    if (suppress)
                    {
                        assemblyModel.EditSuppress2();
                    }
                    else
                    {
                        assemblyModel.EditUnsuppress2();
                    }

                    assemblyModel.ClearSelection2(true);
                    if (rebuildAfterChange)
                    {
                        assemblyModel.EditRebuild3();
                    }

                    return matchedFolderFeatures.Count;
                }
            }

            var assemblyDoc = assemblyModel as IAssemblyDoc;
            var components = assemblyDoc?.GetComponents(true) as object[];
            if (components == null || components.Length == 0)
            {
                return 0;
            }

            var targetSuppressionState = suppress
                ? (int)swComponentSuppressionState_e.swComponentSuppressed
                : (int)swComponentSuppressionState_e.swComponentResolved;
            var affectedComponentCount = 0;

            foreach (var component in components.OfType<IComponent2>())
            {
                if (component == null || !ComponentMatchesNamePatterns(component, normalizedPatterns))
                {
                    continue;
                }

                try
                {
                    component.SetSuppression2(targetSuppressionState);
                    affectedComponentCount++;
                }
                catch
                {
                }
            }

            if (affectedComponentCount > 0 && rebuildAfterChange)
            {
                assemblyModel.EditRebuild3();
            }

            return affectedComponentCount;
        }

        /// <summary>
        /// 仅对单个模型文档写入全局变量并重建。
        /// </summary>
        /// <param name="model">目标文档。</param>
        /// <param name="nameToValue">中文全局变量名到值的映射。</param>
        /// <param name="requireAllVariables">为 true 时，映射中任一变量未找到则抛出异常。</param>
        private void ApplyGlobalVariablesToSingleModel(
            IModelDoc2 model,
            IDictionary<string, object> nameToValue,
            bool requireAllVariables = false,
            bool rebuildAfterChange = true,
            Action<IEquationMgr, int, string, string> equationWriteObserver = null,
            IModelDoc2 writableHost = null)
        {
            new SolidWorksDocumentPersistence(_swApp).EnsureWritable(writableHost ?? model);
            var eqMgr = model.GetEquationMgr() as IEquationMgr;
            if (eqMgr == null)
            {
                if (requireAllVariables && nameToValue != null && nameToValue.Keys.Any(key => key != null))
                {
                    throw new InvalidOperationException("目标模型不支持方程式写入。");
                }

                return;
            }

            var missingVariables = new List<string>();
            foreach (var kv in nameToValue)
            {
                if (kv.Key == null) continue;

                // TrySetGlobalVariable 返回 false 表示：方程式列表中没有该全局变量
                if (!TrySetGlobalVariable(eqMgr, kv.Key, kv.Value, equationWriteObserver))
                {
                    missingVariables.Add(kv.Key);
                }
            }

            if (requireAllVariables && missingVariables.Count > 0)
            {
                throw new InvalidOperationException(
                    $"模型方程式驱动失败，未找到以下全局变量：{string.Join("、", missingVariables.Select(CabinetHoleParameters.GetParameterDisplayName))}");
            }

            if (!rebuildAfterChange)
            {
                return;
            }

            // 设置完方程式后仅重建模型；保存统一绑定到关闭文档操作。
            model.EditRebuild3();
        }

        private static bool ComponentMatchesNamePatterns(IComponent2 component, IList<string> normalizedPatterns)
        {
            if (component == null || normalizedPatterns == null || normalizedPatterns.Count == 0)
            {
                return false;
            }

            var componentIdentifiers = GetComponentIdentifiers(component)
                .Select(NormalizeComponentName)
                .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
                .ToList();

            if (componentIdentifiers.Count == 0)
            {
                return false;
            }

            return componentIdentifiers.Any(identifier => normalizedPatterns.Any(pattern => identifier.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static IEnumerable<string> GetComponentIdentifiers(IComponent2 component)
        {
            if (component == null)
            {
                yield break;
            }

            yield return component.Name2;

            string componentPath = null;
            try
            {
                componentPath = component.GetPathName();
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(componentPath))
            {
                yield return componentPath;
                yield return Path.GetFileNameWithoutExtension(componentPath);
                yield return Path.GetDirectoryName(componentPath);
            }

            string selectId = null;
            try
            {
                selectId = component.GetSelectByIDString();
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(selectId))
            {
                yield return selectId;
            }
        }

        private static List<IFeature> FindMatchedFolderFeatures(IModelDoc2 assemblyModel, IList<string> normalizedPatterns)
        {
            var matchedFeatures = new List<IFeature>();
            var visitedFeatureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            TraverseFeatures(assemblyModel?.FirstFeature() as IFeature, feature =>
            {
                if (!IsFolderFeature(feature) || !FeatureMatchesNamePatterns(feature, normalizedPatterns))
                {
                    return;
                }

                var featureName = NormalizeComponentName(feature.Name);
                if (visitedFeatureNames.Add(featureName))
                {
                    matchedFeatures.Add(feature);
                }
            });

            return matchedFeatures;
        }

        private static void TraverseFeatures(IFeature feature, Action<IFeature> visitor)
        {
            while (feature != null)
            {
                visitor?.Invoke(feature);

                var subFeature = feature.GetFirstSubFeature() as IFeature;
                if (subFeature != null)
                {
                    TraverseFeatures(subFeature, visitor);
                }

                feature = feature.GetNextFeature() as IFeature;
            }
        }

        private static bool IsFolderFeature(IFeature feature)
        {
            if (feature == null)
            {
                return false;
            }

            var featureTypeName = NormalizeComponentName(feature.GetTypeName2());
            return featureTypeName.IndexOf("folder", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool FeatureMatchesNamePatterns(IFeature feature, IList<string> normalizedPatterns)
        {
            if (feature == null || normalizedPatterns == null || normalizedPatterns.Count == 0)
            {
                return false;
            }

            var featureName = NormalizeComponentName(feature.Name);
            if (string.IsNullOrWhiteSpace(featureName))
            {
                return false;
            }

            return normalizedPatterns.Any(pattern => featureName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string NormalizeComponentName(string componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName))
            {
                return string.Empty;
            }

            return componentName
                .Replace("（", "(")
                .Replace("）", ")")
                .Replace(" ", string.Empty)
                .Trim();
        }

        /// <summary>
        /// 在给定的 <see cref="IEquationMgr"/> 中查找指定中文全局变量，并将其方程式替换为新值。
        /// </summary>
        /// <param name="eqMgr">方程式管理器。</param>
        /// <param name="chineseGlobalVarName">目标中文全局变量名（例如：柜间距）。</param>
        /// <param name="value">要写入的值（支持 double/string/bool 等）。</param>
        /// <returns>找到并成功替换返回 true；找不到返回 false。</returns>
        private static bool TrySetGlobalVariable(IEquationMgr eqMgr, string chineseGlobalVarName, object value,
            Action<IEquationMgr, int, string, string> equationWriteObserver = null)
        {
            // EquationMgr 中包含：
            // - 由“全局变量”生成的方程式
            // - 以及用户在模型里写的其他方程式
            // 因此我们只能通过“等号左侧变量名匹配”的方式定位到目标全局变量。
            var equationCount = eqMgr.GetCount();
            for (var i = 0; i < equationCount; i++)
            {
                var eq = eqMgr.Equation[i] as string;
                if (string.IsNullOrWhiteSpace(eq)) continue;

                // Global variable equation typical format:
                // "\"柜间距\"=3500"
                if (!EquationMatchesGlobalVariable(eq, chineseGlobalVarName)) continue;

                // 只要匹配到一次就直接替换并返回。
                //（同名变量在同一模型中理论上只会出现一次）
                var newEq = BuildGlobalVariableEquation(chineseGlobalVarName, value);
                eqMgr.Equation[i] = newEq;
                // 观察器先读取 setter 状态；此前不能读取其他方程属性覆盖状态。
                if (equationWriteObserver != null)
                    equationWriteObserver(eqMgr, i, eq, newEq);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 判断某条方程式是否表示指定的中文全局变量。
        /// </summary>
        /// <param name="equation">方程式文本。</param>
        /// <param name="chineseName">中文全局变量名。</param>
        /// <returns>匹配返回 true，否则返回 false。</returns>
        private static bool EquationMatchesGlobalVariable(string equation, string chineseName)
        {
            // 确保只匹配左侧变量名，避免误匹配右侧表达式。
            // 允许等号左右存在空格。
            var left = equation;
            var idx = equation.IndexOf('=');
            if (idx >= 0) left = equation.Substring(0, idx);

            left = left.Trim();
            if (left.Length >= 2 && left[0] == '"' && left[left.Length - 1] == '"')
            {
                left = left.Substring(1, left.Length - 2);
            }

            return string.Equals(left, chineseName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 将 .NET 值转换为 SolidWorks 全局变量方程式字符串。
        /// </summary>
        /// <param name="chineseName">中文全局变量名。</param>
        /// <param name="value">值。</param>
        /// <returns>可直接赋给 <see cref="IEquationMgr.Equation"/> 的方程式字符串。</returns>
        private static string BuildGlobalVariableEquation(string chineseName, object value)
        {
            // 将.NET侧的值转换为 SolidWorks 方程式字符串。
            // 注意：
            // - 数值：直接输出（用 InvariantCulture 保证小数点为 '.'）
            // - 字符串：需要使用双引号包裹，且内部引号需要转义
            // - bool：在方程式里通常用 1/0 表示开关
            if (value == null) return $"\"{chineseName}\"=\"\"";
            if (value is bool b) return $"\"{chineseName}\"={(b ? 1 : 0)}";
            if (value is string s) return $"\"{chineseName}\"=\"{s.Replace("\"", "\"\"")}\"";

            if (value is IFormattable f)
            {
                // SolidWorks方程式使用 '.' 作为小数点
                var str = f.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
                return $"\"{chineseName}\"={str}";
            }

            return $"\"{chineseName}\"={value}";
        }

        /// <summary>
        /// 在当前 SolidWorks 会话中查找是否已打开指定路径的文档。
        /// </summary>
        public IModelDoc2 FindOpenDocumentByPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            string fullPath;
            try { fullPath = Path.GetFullPath(filePath); }
            catch { fullPath = filePath; }

            return FindOpenDocumentByPathInternal(fullPath);
        }

        /// <summary>
        /// 静默保存当前 SolidWorks 会话中的全部已修改文档，等效于保存提示中的“保存所有”。
        /// 零件优先、装配体随后保存，并重复检查，避免父装配体保存后仍遗留被修改的引用文档。
        /// </summary>
        public bool SaveAllModifiedDocumentsSilently()
        {
            if (_swApp == null)
            {
                return false;
            }

            for (var pass = 0; pass < 3; pass++)
            {
                var documents = (_swApp.GetDocuments() as object[])
                    ?.OfType<IModelDoc2>()
                    .OrderBy(model =>
                    {
                        try { return model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ? 1 : 0; }
                        catch { return 0; }
                    })
                    .ToList();
                if (documents == null || documents.Count == 0)
                {
                    return true;
                }

                var foundModifiedDocument = false;
                foreach (var document in documents)
                {
                    bool requiresSave;
                    try { requiresSave = document.GetSaveFlag(); }
                    catch { continue; }
                    if (!requiresSave)
                    {
                        continue;
                    }

                    foundModifiedDocument = true;
                    var errors = 0;
                    var warnings = 0;
                    try
                    {
                        var options = swSaveAsOptions_e.swSaveAsOptions_Silent;
                        if (document.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                            options |= swSaveAsOptions_e.swSaveAsOptions_SaveReferenced;
                        document.Save3((int)options, ref errors, ref warnings);
                    }
                    catch
                    {
                        // 继续保存其余文档；关闭目标文档前还会再次检查。
                    }
                }

                if (!foundModifiedDocument)
                {
                    return true;
                }
            }

            var remainingDocuments = _swApp.GetDocuments() as object[];
            return remainingDocuments == null
                || remainingDocuments
                    .OfType<IModelDoc2>()
                    .All(document =>
                    {
                        try { return !document.GetSaveFlag(); }
                        catch { return false; }
                    });
        }

        /// <summary>
        /// 关闭当前 SolidWorks 会话中路径位于指定目录下的所有已打开文档；关闭前统一重建并保存。
        /// </summary>
        public void CloseOpenDocumentsUnderDirectory(string directoryPath)
        {
            CloseOpenDocumentsUnderDirectory(directoryPath, null);
        }

        /// <summary>
        /// 关闭指定目录下的已打开文档，但跳过 <paramref name="excludedFullPaths"/> 中的完整路径。
        /// </summary>
        public void CloseOpenDocumentsUnderDirectory(
            string directoryPath,
            IReadOnlyCollection<string> excludedFullPaths)
        {
            CloseDirectoryDocuments(directoryPath, excludedFullPaths, requireUnload: false);
        }

        /// <summary>目录清理前必须确认模型数据卸载，不能把窗口隐藏视作卸载完成。</summary>
        public void CloseOpenDocumentsUnderDirectoryConfirmed(
            string directoryPath,
            IReadOnlyCollection<string> excludedFullPaths)
        {
            CloseDirectoryDocuments(directoryPath, excludedFullPaths, requireUnload: true);
        }

        private void CloseDirectoryDocuments(string directoryPath,
            IReadOnlyCollection<string> excludedFullPaths, bool requireUnload)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) return;
            var root = Path.GetFullPath(directoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var excluded = BuildExcludedPathSet(excludedFullPaths);
            var closer = new SolidWorksDocumentConflictGuard(_swApp);
            var paths = closer.CaptureOpenPaths().Where(path =>
                path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !excluded.Contains(path)).ToArray();
            closer.CloseSavedDocuments(paths, SaveDocumentForClose, requireUnload, excludedFullPaths);
        }

        /// <summary>
        /// 关闭指定完整路径的文档并确认它已不在当前 SolidWorks 会话中。
        /// </summary>
        public void CloseDocumentByPathConfirmed(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("待关闭文档路径不能为空。", nameof(filePath));
            }

            var fullPath = Path.GetFullPath(filePath);
            new SolidWorksDocumentConflictGuard(_swApp).CloseSavedDocuments(
                new[] { fullPath }, SaveDocumentForClose, requireUnload: true);
        }


        internal IModelDoc2 FindOpenDocumentByPathConfirmed(string fullPath)
            => new SolidWorksDocumentConflictGuard(_swApp).ReadWithBusyRecheck(
                () => FindOpenDocumentByPathConfirmedCore(fullPath), "按完整路径读取待保存文档");

        private IModelDoc2 FindOpenDocumentByPathConfirmedCore(string fullPath)
        {
            var raw = _swApp.GetDocuments();
            if (raw == null) return null;
            var documents = raw as object[];
            if (documents == null)
                throw new InvalidOperationException("SolidWorks 已打开文档清单格式无效。");
            var selectedIndex = -1;
            var transferred = false;
            try
            {
                for (var index = 0; index < documents.Length; index++)
                {
                    var document = documents[index] as IModelDoc2;
                    if (document == null)
                        throw new InvalidOperationException("SolidWorks 返回了无法识别的已打开文档。");
                    var path = document.GetPathName();
                    if (string.IsNullOrWhiteSpace(path)
                        || !string.Equals(Path.GetFullPath(path), fullPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (selectedIndex >= 0)
                        throw new InvalidOperationException("已打开文档路径重复，无法确认关闭目标：" + fullPath);
                    selectedIndex = index;
                }
                transferred = true;
                return selectedIndex < 0 ? null : (IModelDoc2)documents[selectedIndex];
            }
            finally
            {
                // 仅把命中的一层引用交给调用方；其他枚举引用必须在关闭前释放。
                for (var index = 0; index < documents.Length; index++)
                    if (!transferred || index != selectedIndex) SolidWorksFileHelper.ReleaseComReference(documents[index]);
            }
        }

        private static HashSet<string> BuildExcludedPathSet(IReadOnlyCollection<string> excludedFullPaths)
        {
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (excludedFullPaths == null)
            {
                return excluded;
            }

            foreach (var path in excludedFullPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                try
                {
                    excluded.Add(Path.GetFullPath(path));
                }
                catch
                {
                    excluded.Add(path);
                }
            }

            return excluded;
        }

        /// <summary>
        /// 若指定路径文档已打开则先完整保存再关闭（优先按完整路径匹配）。
        /// </summary>
        /// <returns>找到且确认窗口关闭返回 true；未找到返回 false；保存或关闭失败抛出异常。</returns>
        public bool TryCloseDocumentByPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string fullPath;
            try { fullPath = Path.GetFullPath(filePath); }
            catch { fullPath = filePath; }

            var closer = new SolidWorksDocumentConflictGuard(_swApp);
            if (!closer.CaptureOpenPaths().Contains(fullPath, StringComparer.OrdinalIgnoreCase)) return false;
            closer.CloseSavedDocuments(new[] { fullPath }, SaveDocumentForClose);
            return true;
        }

        private void SaveDocumentForClose(string path)
        {
            var document = FindOpenDocumentByPathConfirmed(path);
            if (document == null) return;
            try { new SolidWorksDocumentPersistence(_swApp).SaveModel(document, path); }
            finally { SolidWorksFileHelper.ReleaseComReference(document); }
        }

        private IModelDoc2 FindOpenDocumentByPathInternal(string fullPath)
        {
            // 遍历当前 SolidWorks 进程中已经打开的所有文档
            // GetDocuments() 返回 object（COM），实际是 object[]，每项可转换为 IModelDoc2
            var docsObj = _swApp.GetDocuments();
            var docs = docsObj as object[];
            if (docs == null) return null;

            foreach (var d in docs)
            {
                var doc = d as IModelDoc2;
                if (doc == null) continue;
                var path = TryGetPathName(doc);
                if (string.IsNullOrEmpty(path)) continue;

                string normalizedPath;
                try { normalizedPath = Path.GetFullPath(path); }
                catch { normalizedPath = path; }

                if (string.Equals(normalizedPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return doc;
                }
            }

            return null;
        }

        /// <summary>
        /// 安全获取文档的磁盘路径（未保存文档可能返回空或抛异常）。
        /// </summary>
        /// <param name="doc">SolidWorks 文档对象。</param>
        /// <returns>文档路径；获取失败返回 null。</returns>
        private static string TryGetPathName(IModelDoc2 doc)
        {
            // 有些情况下（例如新建未保存的文档）GetPathName 可能抛异常或返回空
            // 这里用 try/catch 保证调用方逻辑稳定
            try
            {
                return doc.GetPathName();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 返回运行时Dll的路径
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        public static string RegDllPath()
        {
            try
            {
                var codeBase = Assembly.GetExecutingAssembly().CodeBase;
                var uri = new UriBuilder(codeBase);
                var path = Uri.UnescapeDataString(uri.Path);
                return Path.GetDirectoryName(path);
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
