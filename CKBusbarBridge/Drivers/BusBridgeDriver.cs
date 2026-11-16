using AutoOpeningHoleBase;
using CKBusbarBridge.Drivers;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.models;
using Newtonsoft.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using static CKBusbarBridge.Helpers.SWHelper;

namespace CKBusbarBridge
{
    public class BusBridgeDriver
    {


        private ISldWorks swApp;

        /// <summary>
        /// 基架模板中固定引用、不参与改名的零件（文件名不含扩展名）。
        /// </summary>
        private static readonly HashSet<string> FrameTemplateExcludeRenameParts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "8K8.932.001",
            };

        public BusBridgeDriver(ISldWorks _swApp)
        {
            swApp = _swApp;
        }



        /// <summary>
        /// 深度拷贝
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="obj"></param>
        /// <returns></returns>
        public T DeepCopy<T>(T obj)
        {
            //如果是字符串或值类型则直接返回
            if (obj == null || obj is string || obj.GetType().IsValueType) return obj;

            object retval;

            using (MemoryStream ms = new MemoryStream())

            {

                XmlSerializer xml = new XmlSerializer(typeof(T));

                xml.Serialize(ms, obj);

                ms.Seek(0, SeekOrigin.Begin);

                retval = xml.Deserialize(ms);

                ms.Close();

            }
            return (T)retval;
        }

        /// <summary>
        /// 安全保存模型，避免外部保存事件中的异常直接中断当前驱动流程。
        /// </summary>
        /// <param name="swModel">需要保存的模型。</param>
        /// <param name="throwOnFailure">true=保存失败时抛出异常；false=仅记录并返回失败。</param>
        /// <param name="saveScene">保存场景描述，便于调试输出定位。</param>
        /// <returns>保存是否成功。</returns>
        private bool TrySaveAndCloseModel(ModelDoc2 swModel, bool throwOnFailure, string saveScene)
        {
            if (swModel == null) return false;

            string modelIdentity = string.Empty;
            try
            {
                modelIdentity = swModel.GetPathName();
                if (string.IsNullOrWhiteSpace(modelIdentity))
                {
                    modelIdentity = swModel.GetTitle();
                }
            }
            catch
            {
                modelIdentity = "<unknown>";
            }

            try
            {
                new SolidWorksDriver(swApp).TryCloseDocumentByPath(swModel.GetPathName());
                return true;
            }
            catch (Exception ex)
            {
                Debug.Print($@"{saveScene}: save-and-close failed -> {modelIdentity}, {ex.GetType().FullName}: {ex.Message}");

                if (throwOnFailure)
                {
                    throw new InvalidOperationException($@"保存模型失败: {modelIdentity}", ex);
                }

                return false;
            }
        }

        /// <summary>
        /// 删除当前总装配体下已插入的生成基架组件，避免重复装配。
        /// </summary>
        /// <param name="frameRootPath">项目目录下的“基架”根目录。</param>
        public void RemoveGeneratedFrameComponents(string frameRootPath)
        {
            if (string.IsNullOrWhiteSpace(frameRootPath) || !Directory.Exists(frameRootPath)) return;

            var swModel = swApp.IActiveDoc2 as ModelDoc2;
            var swAssembly = swModel as AssemblyDoc;
            if (swModel == null || swAssembly == null) return;
            if (swModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return;

            var topLevelComponents = (object[])swAssembly.GetComponents(false);
            if (topLevelComponents == null || topLevelComponents.Length == 0) return;

            var needDeleteComponents = topLevelComponents
                .OfType<Component2>()
                .Where(component => component != null)
                .Where(component =>
                {
                    var componentPath = component.GetPathName();
                    return !string.IsNullOrWhiteSpace(componentPath)
                        && componentPath.StartsWith(frameRootPath, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (needDeleteComponents.Count == 0) return;

            swModel.ClearSelection2(true);

            var append = false;
            foreach (var component in needDeleteComponents)
            {
                var selectName = component.GetSelectByIDString();
                if (string.IsNullOrWhiteSpace(selectName)) continue;

                var selected = swModel.Extension.SelectByID2(selectName, "COMPONENT", 0, 0, 0, append, 0, null, 0);
                if (selected)
                {
                    append = true;
                }
            }

            if (append)
            {
                swAssembly.DeleteSelections((int)swDeleteSelectionOptions_e.swDelete_Children);
                swModel.EditRebuild3();
            }

            swModel.ClearSelection2(true);
        }

        /// <summary>
        /// 将指定基架装配体插入到当前总装配体，并按预览方向定位后固定。
        /// </summary>
        /// <param name="frameAssemblyPath">基架装配体完整路径。</param>
        /// <param name="centerX">基架中心点X坐标（毫米）。</param>
        /// <param name="centerY">基架中心点Y坐标（毫米）。</param>
        /// <param name="centerZ">基架中心点Z坐标（毫米）。</param>
        /// <param name="isReversed">是否反向安装。</param>
        public Component2 InsertFrameAssemblyIntoActiveAssembly(string frameAssemblyPath, double centerX, double centerY, double centerZ, bool isReversed)
        {
            if (string.IsNullOrWhiteSpace(frameAssemblyPath))
            {
                throw new ArgumentException("基架装配体路径不能为空。", nameof(frameAssemblyPath));
            }

            if (!File.Exists(frameAssemblyPath))
            {
                throw new FileNotFoundException($@"基架装配体文件不存在: {frameAssemblyPath}", frameAssemblyPath);
            }

            var swModel = swApp.IActiveDoc2 as ModelDoc2;
            var swAssembly = swModel as AssemblyDoc;
            if (swModel == null || swAssembly == null || swModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException("插入基架需要当前活动文档为总装配体。");
            }

            var componentFileName = Path.GetFileName(frameAssemblyPath);
            if (string.IsNullOrWhiteSpace(componentFileName))
            {
                throw new InvalidOperationException($@"无法从路径中解析基架装配体名称: {frameAssemblyPath}");
            }

            Component2 component = null;
            ModelDoc2 openedComponentModel = null;
            try
            {
                var swDriver = new SolidWorksDriver(swApp);
                try
                {
                    openedComponentModel = swDriver.OpenOrActivateDocument(frameAssemblyPath) as ModelDoc2;
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException($@"打开待装配基架失败: {frameAssemblyPath} ({ex.Message})");
                }

                int activateErrors = 0;
                swApp.ActivateDoc3(swModel.GetTitle(), true, (int)swRebuildOnActivation_e.swRebuildActiveDoc, ref activateErrors);

                component = swAssembly.AddComponent5(
                    componentFileName,
                    (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                    "",
                    false,
                    "",
                    centerX / 1000.0,
                    centerY / 1000.0,
                    centerZ / 1000.0);
            }
            finally
            {
                if (openedComponentModel != null)
                {
                    try
                    {
                        TrySaveAndCloseModel(
                            openedComponentModel,
                            throwOnFailure: false,
                            saveScene: "InsertFrameAssemblyIntoActiveAssembly");
                    }
                    catch
                    {
                        // ignore
                    }

                    int reactivateErrors = 0;
                    swApp.ActivateDoc3(swModel.GetTitle(), true, (int)swRebuildOnActivation_e.swRebuildActiveDoc, ref reactivateErrors);
                }
            }

            if (component == null)
            {
                throw new InvalidOperationException($@"插入基架装配体失败: {frameAssemblyPath}");
            }

            if (isReversed)
            {
                ApplyReverseTransform(component);
            }

            swModel.ClearSelection2(true);
            component.Select(false);
            swAssembly.FixComponent();
            swModel.ClearSelection2(true);
            return component;
        }

        private void ApplyReverseTransform(Component2 component)
        {
            if (component == null) return;

            var currentTransform = component.Transform2;
            if (currentTransform == null) return;

            var transformData = (double[])currentTransform.ArrayData;
            if (transformData == null || transformData.Length < 13) return;

            transformData[0] = -1;
            transformData[1] = 0;
            transformData[2] = 0;
            transformData[3] = 0;
            transformData[4] = 1;
            transformData[5] = 0;
            transformData[6] = 0;
            transformData[7] = 0;
            transformData[8] = -1;
            transformData[12] = 1;

            var mathUtility = swApp.IGetMathUtility();
            if (mathUtility == null) return;

            var reverseTransform = mathUtility.CreateTransform(transformData) as MathTransform;
            if (reverseTransform == null) return;

            component.Transform2 = reverseTransform;
        }

        /// <summary>
        /// 根据模版文件，创建新的装配体（支持外部传入合同号用于文件改名）。
        /// </summary>
        /// <param name="templateFilePath"> 模版文件路径 </param>
        /// <param name="saveFolderPath"> 保存路径 </param>
        /// <param name="inputSize"> 输入尺寸 </param>
        /// <param name="frameSerial"> 基架序号 </param>
        /// <param name="busBridgeDimSizeDefineDictionary"> 尺寸定义 </param>
        /// <param name="taskNumber">合同号（用于改名：原名称+合同号+三位流水号，与「图号生成信息」一致）。</param>
        /// <param name="needUpdateProperties">驱动完成后需要写入总装配体的自定义属性（可为空）；
        /// 在尺寸驱动、组件改名完成之后、保存关闭之前写入，确保改动随保存一起落盘。</param>
        /// <param name="frameGlobalVariableMap">基架装配体方程式全局变量名到值的映射（可为空）；
        /// 非空时改为通过写入装配体方程式全局变量来驱动基架尺寸（如面对墙场景的“基架宽度/基架长度/基架高度”），
        /// 此时不再按 <paramref name="busBridgeDimSizeDefineList"/> 逐零件偏移驱动。</param>
        /// <remarks>
        /// 若 <paramref name="saveFolderPath"/> 下已存在同名基架装配体（模板名.任务号.三位流水号），
        /// 则跳过 PackAndGo 与组件/文件改名，直接打开已有装配体执行尺寸驱动并保存。
        /// </remarks>
        public void CreateBusBridge(string templateFilePath, string saveFolderPath, Dictionary<string, double> inputSize, string frameSerial, List<BusBridgeDimSizeDefine> busBridgeDimSizeDefineList, string taskNumber, Dictionary<string, string> needUpdateProperties = null, IDictionary<string, double> frameGlobalVariableMap = null)
        {
            if (string.IsNullOrEmpty(templateFilePath)) return;
            if (string.IsNullOrEmpty(saveFolderPath)) return;
            if (inputSize == null) return;
            if (busBridgeDimSizeDefineList == null) return;

            if (string.IsNullOrWhiteSpace(frameSerial))
            {
                throw new ArgumentException("frameSerial不能为空", nameof(frameSerial));
            }

            if (string.IsNullOrWhiteSpace(taskNumber))
            {
                throw new InvalidOperationException(@"合同号不能为空，请先在母线桥设计中通过「图号生成」填写并确认。");
            }

            // 基架序号统一格式化为两位（如 "2"->"02"；"02"->"02"）
            var frameSerial2 = frameSerial;
            if (int.TryParse(frameSerial, out var frameSerialNumber))
            {
                frameSerial2 = frameSerialNumber.ToString("D2");
            }

            // 流水号：三位（这里流水号可以用基架序号）
            var frameSerial3 = frameSerial;
            if (int.TryParse(frameSerial, out var frameSerial3Number))
            {
                frameSerial3 = frameSerial3Number.ToString("D3");
            }

            //var newFileName = $@"{figureNumber}{Path.GetExtension(templateFilePath)}";
            if (!Directory.Exists(saveFolderPath)) Directory.CreateDirectory(saveFolderPath);

            // 若目录下已存在同名基架装配体，则跳过 PackAndGo 与改名，直接打开并执行驱动。
            var templateBaseName = Path.GetFileNameWithoutExtension(templateFilePath);
            var templateExt = Path.GetExtension(templateFilePath);
            // 改名规则：模板名.任务号.三位流水号
            var expectedAsmPath = Path.Combine(saveFolderPath, $@"{templateBaseName}.{taskNumber}.{frameSerial3}{templateExt}");
            var reuseExistingFrame = File.Exists(expectedAsmPath);

            swApp.CloseAllDocuments(false);

            // 深拷贝busBridgeDimSizeDefineDictionary
            List<BusBridgeDimSizeDefine> busBridgeDimSizeDefineDictionaryCopy = DeepCopy(busBridgeDimSizeDefineList);

            // 尺寸偏移为 0 的参数不参与驱动（会同步裁剪 busBridgeDimSizeDefineDictionaryCopy）
            Dictionary<string, double> notModifyInputSize = inputSize.Where(x => x.Value == 0).ToDictionary(x => x.Key, y => y.Value);
            ExcludeNotRename(notModifyInputSize, busBridgeDimSizeDefineDictionaryCopy);

            SolidWorksFileHelper swHelper = null;
            string oldFileName = null;
            string newFileName = null;
            string oldAsmBaseName = null;
            string newAsmBaseName = null;
            ModelDoc2 swModel;

            if (reuseExistingFrame)
            {
                try
                {
                    swModel = new SolidWorksDriver(swApp).OpenOrActivateDocument(expectedAsmPath) as ModelDoc2;
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException($@"打开模型失败: {expectedAsmPath} ({ex.Message})");
                }
            }
            else
            {
                // PackAndGo 复制到项目目录的零件均需加「任务号.流水号」后缀；仅固定引用零件（如 8K8.932.001）不改名。
                // 注意：ExcludeNotRename 用于“是否驱动尺寸”，不能用于控制改名——标准尺寸时 Length/Width/Height 均为 0，
                // 会误把全部 8GX 挡板零件列入排除名单。
                swHelper = new SolidWorksFileHelper((SldWorks)swApp);
                // 复制模板装配体及其引用零件到 saveFolderPath 时，不排除任何模型文件
                swHelper.CopyFileUsePackAndGoWithSuffix(templateFilePath, saveFolderPath, string.Empty, new List<string>());

                // 重命名装配体（规则：原名称（即图号）+任务号+三位流水号）
                oldFileName = Path.Combine(saveFolderPath, Path.GetFileName(templateFilePath));
                oldAsmBaseName = Path.GetFileNameWithoutExtension(oldFileName);
                newAsmBaseName = $@"{oldAsmBaseName}.{taskNumber}.{frameSerial3}";
                newFileName = Path.Combine(Path.GetDirectoryName(oldFileName), $@"{newAsmBaseName}{Path.GetExtension(oldFileName)}");

                if (!File.Exists(oldFileName))
                {
                    throw new FileNotFoundException($@"PackAndGo复制后的装配体文件不存在: {oldFileName}", oldFileName);
                }

                // 打开（含去轻量化）
                try
                {
                    swModel = new SolidWorksDriver(swApp).OpenOrActivateDocument(oldFileName) as ModelDoc2;
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException($@"打开模型失败: {oldFileName} ({ex.Message})");
                }
            }

            // 更新尺寸：
            // - 提供全局变量映射时（如面对墙场景），改为写入装配体方程式全局变量来驱动；
            // - 否则按尺寸定义逐零件做偏移驱动。
            if (frameGlobalVariableMap != null && frameGlobalVariableMap.Count > 0)
            {
                DriveFrameByGlobalVariables(swModel, frameGlobalVariableMap);
            }
            else
            {
                ModifyDim(swModel, inputSize, busBridgeDimSizeDefineDictionaryCopy);
            }

            var topAsm = swModel as AssemblyDoc;
            if (topAsm == null)
            {
                throw new InvalidOperationException("CreateBusBridge 仅支持装配体模板");
            }

            if (!reuseExistingFrame)
            {
                var excludeRenameSet = new HashSet<string>(FrameTemplateExcludeRenameParts, StringComparer.OrdinalIgnoreCase);
                RenameFrameTemplateComponents(swModel, topAsm, swHelper, taskNumber, frameSerial3, excludeRenameSet);
            }

            // 在重建保存前写入指定的自定义属性（不存在则新增，存在则覆盖），
            // 保证属性与本次驱动结果在同一份保存中落盘。
            if (needUpdateProperties != null && needUpdateProperties.Count > 0)
            {
                UpdateProperties(swModel, needUpdateProperties);
            }

            swModel.EditRebuild3();
            TrySaveAndCloseModel(swModel, true, "CreateBusBridge");

            // 重命名总装配体文件（文件系统层面改名），规则：原名称（即图号）+任务号+三位流水号
            // 组件改名已完成后再改总装文件名，避免引用关系未落盘导致的问题
            if (reuseExistingFrame)
            {
                return;
            }

            if (!string.Equals(oldFileName, newFileName, StringComparison.OrdinalIgnoreCase))
            {
                // 若目标装配体文件已存在，则直接复用（避免再次驱动导致报错）
                if (File.Exists(newFileName))
                {
                    try { File.Delete(oldFileName); } catch { /* ignore */ }
                    return;
                }

                File.Move(oldFileName, newFileName);
                swHelper.TryRenameCompanionDrawingWithClose(
                    Path.GetDirectoryName(oldFileName),
                    oldAsmBaseName,
                    newAsmBaseName);
            }
        }

        /// <summary>
        /// 通过写入基架装配体的方程式全局变量来驱动基架尺寸。
        /// 仅写入全局变量，重建与保存交由 <see cref="CreateBusBridge"/> 后续统一处理。
        /// </summary>
        /// <param name="swModel">已打开的基架装配体模型。</param>
        /// <param name="frameGlobalVariableMap">全局变量名到值的映射（如“基架宽度/基架长度/基架高度”）。</param>
        private void DriveFrameByGlobalVariables(ModelDoc2 swModel, IDictionary<string, double> frameGlobalVariableMap)
        {
            if (swModel == null) return;
            if (frameGlobalVariableMap == null || frameGlobalVariableMap.Count == 0) return;

            var variableMap = frameGlobalVariableMap.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            new SolidWorksDriver(swApp).ApplyGlobalVariablesRequired(swModel, variableMap, rebuildAfterChange: false);
        }

        /// <summary>
        /// 排除不需要重命名的零件（即不涉及修改的模版文件）
        /// </summary>
        /// <param name="excludeInputSize"> 不需要修改的尺寸（A?B?C?） </param>
        /// <param name="busBridgeDimSizeDefineDictionary"> 尺寸定义 </param>
        /// <returns></returns>
        private List<string> ExcludeNotRename(Dictionary<string, double> excludeInputSize, List<BusBridgeDimSizeDefine> busBridgeDimSizeDefineDictionary)
        {
            var resultList = new List<string>();

            if (excludeInputSize.Count() == 0)
            {
                return resultList;
            }
            //排除不需要重命名的零件,规则为inputSize.value = 0，即不需要变化的零件，inputSize和busBridgeDimSizeDefineDictionary的匹配关系为inputSize.Key = BusBridgeDimSizeDefine.SizeName
            foreach (var item in excludeInputSize)
            {
                var sizeDefine = busBridgeDimSizeDefineDictionary.FirstOrDefault(x => x.SizeName == item.Key);
                if (sizeDefine != null)
                {
                    // sizeDefine.SizeDefine.Select(x => x.Name)需要取Name中split @的最后一个
                    resultList.AddRange(sizeDefine.SizeDefine.Select(x => x.Name.Split('@').Last().ToUpper()));
                }
                // 同时在busBridgeDimSizeDefineDictionary中删除
                if (sizeDefine != null)
                {
                    busBridgeDimSizeDefineDictionary.Remove(sizeDefine);
                }
            }

            // busBridgeDimSizeDefineDictionary的各个SizeDefine中可能存在交叉的情况，resultList中存在的元素，在item.Value == 0的sizeDefine中可能存在，那么此时应该在resultList中删除
            foreach (var item in busBridgeDimSizeDefineDictionary)
            {
                resultList.RemoveAll(x => item.SizeDefine.Select(y => y.Name.Split('@').Last().ToUpper()).Contains(x));
            }

            // 去重
            return resultList.Select(x => Path.GetFileNameWithoutExtension(x)).Distinct().ToList();
        }

        /// <summary>
        /// 更新属性
        /// </summary>
        /// <param name="swModel"></param>
        /// <param name="needUpdateProperties"></param>
        private void UpdateProperties(ModelDoc2 swModel, Dictionary<string, string> needUpdateProperties)
        {
            foreach (var property in needUpdateProperties)
            {
                string old = swModel.GetCustomInfoValue("", property.Key);
                if (old != null)
                {
                    swModel.DeleteCustomInfo2("", property.Key);
                }
                swModel.AddCustomInfo3("", property.Key, 30, property.Value);
            }
        }

        /// <summary>
        /// 修改当前打开的装配体尺寸（装配体内的零件，根据尺寸定义的规则）
        /// </summary>
        /// <param name="swModel"></param>
        /// <param name="inputSize"> 输入尺寸 </param>
        /// <param name="busBridgeDimSizeDefineDictionary"> 尺寸定义 </param>
        private void ModifyDim(ModelDoc2 swModel, Dictionary<string, double> inputSize, List<BusBridgeDimSizeDefine> busBridgeDimSizeDefines)
        {
            if (swModel == null) return;
            if (inputSize == null) return;
            if (busBridgeDimSizeDefines == null) return;

            var asmDir = Path.GetDirectoryName(swModel.GetPathName());

            // key: 目标文件名(如 8GX.051.003.SLDPRT) / value: 该文件内需要修改的尺寸列表
            var updateByTargetFile = new Dictionary<string, List<Tuple<string, double>>>(StringComparer.OrdinalIgnoreCase);

            foreach (var busBridgeDimSizeDefine in busBridgeDimSizeDefines)
            {
                if (busBridgeDimSizeDefine == null) continue;

                if (!inputSize.TryGetValue(busBridgeDimSizeDefine.SizeName, out double sizeDouble)) continue;

                Debug.Print($@"CreateBusBridge BusBridgeDimSizeDefine {busBridgeDimSizeDefine.Id}: {busBridgeDimSizeDefine.SizeName}");

                if (busBridgeDimSizeDefine.SizeDefine == null) continue;

                foreach (var sizeDefine in busBridgeDimSizeDefine.SizeDefine)
                {
                    if (sizeDefine == null) continue;

                    Debug.Print($@"CreateBusBridge sizeDefine {sizeDefine.Name}: {sizeDefine.DisplayName} : {sizeDefine.Scale} : {sizeDefine.Func}");

                    var dimValue = sizeDouble * sizeDefine.Scale;
                    if (!string.IsNullOrEmpty(sizeDefine.Func))
                    {
                        dimValue = Convert.ToDouble(new DataTable().Compute($@"{dimValue}{sizeDefine.Func}", string.Empty));
                    }

                    dimValue = sizeDefine.BaseValue + dimValue;

                    // sizeDefine.Name 例："D1@草图1@8GX.051.003.SLDPRT"
                    var parts = (sizeDefine.Name ?? string.Empty)
                        .Split(new[] { "@" }, StringSplitOptions.RemoveEmptyEntries);

                    // 不符合“参数@特征@模型名”的，按旧逻辑直接在装配体里尝试
                    if (parts.Length < 3)
                    {
                        swModel.UpdateDimemsion(sizeDefine.Name, dimValue);
                        continue;
                    }

                    var targetFileName = parts[parts.Length - 1];
                    var dimNameWithoutModel = string.Join("@", parts.Take(parts.Length - 1));

                    if (string.IsNullOrWhiteSpace(targetFileName) || string.IsNullOrWhiteSpace(dimNameWithoutModel))
                    {
                        swModel.UpdateDimemsion(sizeDefine.Name, dimValue);
                        continue;
                    }

                    // 若装配体无路径，无法定位对应零件；回退装配体更新
                    if (string.IsNullOrWhiteSpace(asmDir))
                    {
                        swModel.UpdateDimemsion(sizeDefine.Name, dimValue);
                        continue;
                    }

                    // 只有目标文件存在才纳入批量更新，否则回退装配体更新
                    var targetPath = Path.Combine(asmDir, targetFileName);
                    if (!File.Exists(targetPath))
                    {
                        swModel.UpdateDimemsion(sizeDefine.Name, dimValue);
                        continue;
                    }

                    if (!updateByTargetFile.TryGetValue(targetFileName, out var list))
                    {
                        list = new List<Tuple<string, double>>();
                        updateByTargetFile[targetFileName] = list;
                    }
                    list.Add(Tuple.Create(dimNameWithoutModel, dimValue));
                }
            }

            // 按零件批量打开 -> 更新多个尺寸 -> 保存/关闭（每个零件只打开一次）
            foreach (var kv in updateByTargetFile)
            {
                var targetFileName = kv.Key;
                var updates = kv.Value;
                if (updates == null || updates.Count == 0) continue;

                var targetPath = Path.Combine(asmDir, targetFileName);
                if (!File.Exists(targetPath)) continue;

                var targetDoc = new SolidWorksDriver(swApp).TryOpenOrActivateDocument(targetPath) as ModelDoc2;
                if (targetDoc == null)
                {
                    // 打不开则回退：尝试把原始全名丢回装配体更新（尽量保持旧行为）
                    foreach (var u in updates)
                    {
                        swModel.UpdateDimemsion(u.Item1 + "@" + targetFileName, u.Item2);
                    }
                    continue;
                }

                foreach (var u in updates)
                {
                    targetDoc.UpdateDimemsion(u.Item1, u.Item2);
                }

                targetDoc.EditRebuild3();
                TrySaveAndCloseModel(targetDoc, false, "ModifyDim.ChildDocument");
            }
        }

        /// <summary>
        /// 重命名基架模板装配体内的零件/子装配体（规则：模板名.任务号.三位流水号）。
        /// 同一磁盘文件只处理一次，避免多实例重复 RenameDocument；属性与引用在总装保存时一并落盘。
        /// </summary>
        private void RenameFrameTemplateComponents(
            ModelDoc2 swModel,
            AssemblyDoc topAsm,
            SolidWorksFileHelper swHelper,
            string taskNumber,
            string frameSerial3,
            HashSet<string> excludeRenameSet)
        {
            try
            {
                topAsm.ResolveAllLightWeightComponents(false);
            }
            catch
            {
                // ignore
            }

            var assemblyPath = swModel.GetPathName();
            var taskSuffix = $@".{taskNumber}.{frameSerial3}";
            var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var allComponents = (object[])topAsm.GetComponents(false);
            if (allComponents == null || allComponents.Length == 0)
            {
                return;
            }

            try { swModel.ClearSelection2(true); } catch { }

            foreach (var component in allComponents.OfType<Component2>())
            {
                if (component == null)
                {
                    continue;
                }

                if (!TryGetFrameComponentRenamePlan(
                        component,
                        assemblyPath,
                        taskSuffix,
                        taskNumber,
                        frameSerial3,
                        excludeRenameSet,
                        processedPaths,
                        out var oldBaseName,
                        out var newName,
                        out var compPath,
                        out var swModelPart))
                {
                    continue;
                }

                var selName = component.GetSelectByIDString();
                if (string.IsNullOrWhiteSpace(selName))
                {
                    Debug.Print($@"{oldBaseName}: 无法获取 SelectByIDString");
                    continue;
                }

                var selected = swModel.Extension.SelectByID2(selName, "COMPONENT", 0, 0, 0, false, 0, null, 0);
                if (!selected)
                {
                    Debug.Print($@"{oldBaseName}: SelectByID2失败, sel=({selName})");
                    continue;
                }

                var renameSta = swModel.Extension.RenameDocument(newName);
                try { swModel.ClearSelection2(true); } catch { }

                if (renameSta != 0)
                {
                    Debug.Print($@"{oldBaseName}: RenameDocument失败, sta={renameSta}, sel=({selName})");
                    continue;
                }

                Debug.Print($@"{oldBaseName}: 改名成功 -> {newName}");
                swModelPart.AddPropertyToModel("参考文件名", oldBaseName, component.ReferencedConfiguration);

                if (!string.IsNullOrWhiteSpace(compPath))
                {
                    processedPaths.Add(compPath);
                    swHelper.TryRenameCompanionDrawingWithClose(
                        Path.GetDirectoryName(compPath),
                        oldBaseName,
                        newName);
                }
            }
        }

        /// <summary>
        /// 判断组件是否需要重命名，并生成新文件名（不含扩展名）。
        /// </summary>
        private static bool TryGetFrameComponentRenamePlan(
            Component2 component,
            string assemblyPath,
            string taskSuffix,
            string taskNumber,
            string frameSerial3,
            HashSet<string> excludeRenameSet,
            HashSet<string> processedPaths,
            out string oldBaseName,
            out string newName,
            out string compPath,
            out ModelDoc2 swModelPart)
        {
            oldBaseName = null;
            newName = null;
            compPath = null;
            swModelPart = null;

            swModelPart = component.IGetModelDoc();
            if (swModelPart == null)
            {
                return false;
            }

            try { compPath = component.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(compPath))
            {
                try { compPath = swModelPart.GetPathName(); } catch { }
            }

            if (IsFrameFixedReferencePart(compPath))
            {
                Debug.Print($@"{Path.GetFileName(compPath ?? string.Empty)}: 固定引用零件，跳过改名");
                MarkFrameComponentProcessed(processedPaths, compPath);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(compPath))
            {
                if (!string.IsNullOrWhiteSpace(assemblyPath)
                    && string.Equals(compPath, assemblyPath, StringComparison.OrdinalIgnoreCase))
                {
                    MarkFrameComponentProcessed(processedPaths, compPath);
                    return false;
                }

                if (processedPaths.Contains(compPath))
                {
                    return false;
                }
            }

            oldBaseName = GetFrameComponentTemplateBaseName(component, compPath);
            if (string.IsNullOrWhiteSpace(oldBaseName))
            {
                return false;
            }

            if (oldBaseName.EndsWith(taskSuffix, StringComparison.OrdinalIgnoreCase))
            {
                Debug.Print($@"{oldBaseName}: 已包含任务号+流水号 {taskSuffix}，跳过改名");
                MarkFrameComponentProcessed(processedPaths, compPath);
                return false;
            }

            string refFileName = swModelPart.GetCustomInfoValue(component.ReferencedConfiguration, "参考文件名");
            if (!string.IsNullOrWhiteSpace(refFileName))
            {
                Debug.Print($@"{oldBaseName}: 已存在参考文件名({refFileName})，跳过改名");
                MarkFrameComponentProcessed(processedPaths, compPath);
                return false;
            }

            var instanceBaseName = GetComponentInstanceBaseName(component);
            if (IsFrameFixedReferencePart(compPath, oldBaseName, instanceBaseName)
                || excludeRenameSet.Contains(oldBaseName)
                || (!string.IsNullOrWhiteSpace(instanceBaseName) && excludeRenameSet.Contains(instanceBaseName)))
            {
                Debug.Print($@"{oldBaseName}: 在排除重命名列表中，跳过改名");
                MarkFrameComponentProcessed(processedPaths, compPath);
                return false;
            }

            newName = $@"{oldBaseName}.{taskNumber}.{frameSerial3}";
            return true;
        }

        /// <summary>
        /// 获取组件对应的模板基名（优先磁盘文件名，其次实例名）。
        /// </summary>
        private static string GetFrameComponentTemplateBaseName(Component2 component, string compPath)
        {
            if (!string.IsNullOrWhiteSpace(compPath))
            {
                return Path.GetFileNameWithoutExtension(compPath);
            }

            return GetComponentInstanceBaseName(component);
        }

        /// <summary>
        /// 从组件实例名提取模板基名（去除实例后缀及子装配体路径前缀）。
        /// </summary>
        private static string GetComponentInstanceBaseName(Component2 component)
        {
            var nameLastSegment = (component?.Name2 ?? string.Empty).Split('/').LastOrDefault() ?? string.Empty;
            return nameLastSegment.SplitBeforeLast(@"-");
        }

        private static void MarkFrameComponentProcessed(HashSet<string> processedPaths, string compPath)
        {
            if (processedPaths == null || string.IsNullOrWhiteSpace(compPath))
            {
                return;
            }

            processedPaths.Add(compPath);
        }

        /// <summary>
        /// 判断是否为基架模板中的固定引用零件（不参与 PackAndGo 后改名，如 8K8.932.001.SLDPRT）。
        /// </summary>
        private static bool IsFrameFixedReferencePart(string compPath, string fileBaseName = null, string instanceBaseName = null)
        {
            if (!string.IsNullOrWhiteSpace(compPath)
                && FrameTemplateExcludeRenameParts.Contains(Path.GetFileNameWithoutExtension(compPath)))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(fileBaseName) && FrameTemplateExcludeRenameParts.Contains(fileBaseName))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(instanceBaseName)
                && FrameTemplateExcludeRenameParts.Contains(instanceBaseName);
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
                var assembly = Assembly.GetExecutingAssembly();
                // 字节加载的程序集没有 DLL 路径，CodeBase 可能指向 mscorlib，不能据此定位模板。
                if (string.IsNullOrWhiteSpace(assembly.Location)) return System.Environment.CurrentDirectory;
                var codeBase = assembly.CodeBase;
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

    public class Constant
    {
        public const string Length = "Length";
        public const string Width = "Width";
        public const string Height = "Height";
    }
}
