using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridge.Helpers
{
    public static class SWHelper
    {
        /// <summary>
        /// 驱动装配体尺寸/方程式后执行完整重建，使子零件上的装配体上下文参考（如直排长度）同步更新。
        /// 等价于手动 Ctrl+Q，比单独调用 <see cref="IModelDoc2.EditRebuild3"/> 更可靠。
        /// </summary>
        /// <param name="assemblyModel">待重建的装配体模型文档。</param>
        public static void FinalizeAssemblyModelRebuild(IModelDoc2 assemblyModel)
        {
            if (assemblyModel == null) return;

            try
            {
                assemblyModel.ForceRebuild3(false);
            }
            catch
            {
                // ignore
            }

            try
            {
                assemblyModel.EditRebuild3();
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// 激活装配体并退出零部件原位编辑模式，确保后续 AddComponent 等装配体级 API 可用。
        /// </summary>
        /// <param name="swApp">SolidWorks 应用对象。</param>
        /// <param name="assemblyModel">目标装配体模型文档。</param>
        public static void EnsureAssemblyEditingContext(ISldWorks swApp, IModelDoc2 assemblyModel)
        {
            if (assemblyModel == null) return;

            TryActivateAssemblyDocument(swApp, assemblyModel);

            for (var attempt = 0; attempt < 5; attempt++)
            {
                TryExitAssemblyEditMode(assemblyModel);

                var activeDoc = swApp?.IActiveDoc2;
                if (activeDoc == null ||
                    activeDoc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    break;
                }

                TryActivateAssemblyDocument(swApp, assemblyModel);
            }

            try
            {
                assemblyModel.ClearSelection2(true);
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// 退出装配体零部件原位编辑模式。
        /// </summary>
        /// <param name="assemblyModel">装配体模型文档。</param>
        public static void TryExitAssemblyEditMode(IModelDoc2 assemblyModel)
        {
            if (assemblyModel == null) return;

            var assemblyDoc = assemblyModel as AssemblyDoc;
            if (assemblyDoc == null) return;

            try
            {
                assemblyDoc.EditAssembly();
            }
            catch
            {
                // ignore
            }
        }

        private static void TryActivateAssemblyDocument(ISldWorks swApp, IModelDoc2 assemblyModel)
        {
            if (swApp == null || assemblyModel == null) return;

            try
            {
                var activateErrors = 0;
                swApp.ActivateDoc3(
                    assemblyModel.GetTitle(),
                    true,
                    (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                    ref activateErrors);
                return;
            }
            catch
            {
                // ignore
            }

            try
            {
                var assemblyPath = assemblyModel.GetPathName();
                if (!string.IsNullOrWhiteSpace(assemblyPath))
                {
                    swApp.ActivateDoc(assemblyPath);
                }
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// 尝试按名称删除装配体中的指定配合（Mate）。
        /// </summary>
        /// <param name="asmModel">装配体模型文档。</param>
        /// <param name="mateNames">要删除的配合名称集合。</param>
        public static void TryDeleteAssemblyMatesByName(IModelDoc2 asmModel, IEnumerable<string> mateNames)
        {
            if (asmModel == null) return;
            if (mateNames == null) return;

            foreach (var mateName in mateNames)
            {
                if (string.IsNullOrWhiteSpace(mateName)) continue;

                try
                {
                    asmModel.ClearSelection2(true);

                    var selected = false;
                    try
                    {
                        selected = asmModel.Extension != null && asmModel.Extension.SelectByID2(mateName, "MATE", 0, 0, 0, false, 0, null, 0);
                    }
                    catch
                    {
                        selected = false;
                    }

                    if (!selected)
                    {
                        try
                        {
                            var feat = FindFeature(asmModel, mateName, null);
                            if (feat != null)
                            {
                                selected = feat.Select2(false, -1);
                            }
                        }
                        catch
                        {
                            selected = false;
                        }
                    }

                    if (!selected) continue;

                    try
                    {
                        asmModel.EditDelete();
                    }
                    catch
                    {
                        // ignore
                    }
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    try { asmModel.ClearSelection2(true); } catch { }
                }
            }
        }

        /// <summary>
        /// 遍历特征树查找特征（名称优先，类型兜底）。
        /// </summary>
        /// <param name="model">目标模型文档。</param>
        /// <param name="expectedName">期望的特征名称；若命中则立即返回。</param>
        /// <param name="expectedTypeName2">期望的 <see cref="IFeature.GetTypeName2"/> 类型名称；作为兜底匹配。</param>
        /// <returns>找到则返回特征对象，找不到返回 null。</returns>
        public static IFeature FindFeature(IModelDoc2 model, string expectedName, string expectedTypeName2)
        {
            if (model == null) return null;

            // 遍历特征树：FirstFeature -> GetNextFeature。
            // 1) 若能按名称匹配到目标，直接返回。
            // 2) 若名称匹配不到，则按 GetTypeName2() 匹配特征类型作为兜底返回。
            IFeature typeMatch = null;
            IFeature feat = (IFeature)model.FirstFeature();
            while (feat != null)
            {
                string name = null;
                string typeName2 = null;
                try { name = feat.Name; } catch { }
                try { typeName2 = feat.GetTypeName2(); } catch { }

                if (!string.IsNullOrEmpty(expectedName) &&
                    !string.IsNullOrEmpty(name) &&
                    string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    return feat;
                }

                if (typeMatch == null &&
                    !string.IsNullOrEmpty(expectedTypeName2) &&
                    !string.IsNullOrEmpty(typeName2) &&
                    string.Equals(typeName2, expectedTypeName2, StringComparison.OrdinalIgnoreCase))
                {
                    typeMatch = feat;
                }

                feat = (IFeature)feat.GetNextFeature();
            }

            return typeMatch;
        }
    }
}
