using AutoOpeningHoleBase;
using AutoOpeningHoleDataModel;
using CKBusbarBridgePreview;
using CKBusbarBridgePreview.OpenHole;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Shapes;

namespace CKBusbarBridge.Helpers
{
    public class OpenHoleHelper
    {
        /// <summary>
        /// 使用旧版铜排开孔流程刷新指定铜排装配体。
        /// 该入口只负责进度提示和视图保护，几何开孔由 DQOpenHoles 执行。
        /// </summary>
        public void OpenLegacyHole(
            ISldWorks swApp,
            Component2 busbarAssembly,
            BusDuctData busDuctData,
            bool applyWallBushingHoleMapping)
        {
            OpenLegacyHoleCore(
                swApp,
                busbarAssembly,
                busDuctData,
                applyWallBushingHoleMapping);
        }

        /// <summary>
        /// 刷新活动总装配体中除柜顶伸出排以外的全部铜排。
        /// </summary>
        public void OpenLegacyHole(
            ISldWorks swApp,
            BusDuctData busDuctData,
            bool applyWallBushingHoleMapping,
            bool requireComplete = false,
            Action<string> report = null)
        {
            OpenLegacyHoleCore(
                swApp,
                null,
                busDuctData,
                applyWallBushingHoleMapping, requireComplete, report);
        }

        private void OpenLegacyHoleCore(
            ISldWorks swApp,
            Component2 busbarAssembly,
            BusDuctData busDuctData,
            bool applyWallBushingHoleMapping,
            bool requireComplete = false,
            Action<string> report = null)
        {
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));
            if (busDuctData == null || busDuctData.BendSetting == null)
                throw new ArgumentNullException(nameof(busDuctData));

            var modelViewReset = new ModelViewReset((SldWorks)swApp);
            modelViewReset.SaveModelView();
            SolidWorksProgressToastHandle progressToast = null;
            try
            {
                if (report == null) progressToast = SolidWorksProgressToast.Begin("铜排开孔");
                progressToast?.Report(new SolidWorksProgressToastUpdate
                {
                    Stage = "正在刷新铜排开孔",
                    Current = 0,
                    Total = 0,
                    Detail = "正在读取铜排组件"
                });

                swApp.OpenAllBusbarHoles(
                    busbarAssembly,
                    new List<Component2>(),
                    busDuctData,
                    applyWallBushingHoleMapping,
                    (current, total, detail) =>
                    {
                        if (report != null)
                        {
                            report(string.Format("正在开铜排孔：{0}/{1} {2}", current, total, detail));
                            return;
                        }
                        progressToast.Report(new SolidWorksProgressToastUpdate
                        {
                            Stage = "正在刷新铜排开孔",
                            Current = current,
                            Total = total,
                            Detail = string.IsNullOrWhiteSpace(detail)
                                ? string.Format("正在处理：{0}/{1}", current, total)
                                : string.Format("正在处理：{0}/{1}  {2}", current, total, detail)
                        });
                    }, requireComplete);
            }
            finally
            {
                progressToast?.Finish();
                modelViewReset.ResetModelView(null);
            }
        }

        public void OpenHole(ISldWorks solidWorksApplication)
        {
            if (solidWorksApplication == null)
            {
                throw new ArgumentNullException(nameof(solidWorksApplication));
            }

            var activeModel = (ModelDoc2)solidWorksApplication.ActiveDoc;
            if (activeModel == null)
            {
                SolidWorksProgressToast.ShowWarning(
                    "铜排开孔",
                    "当前没有可处理的活动模型。");
                return;
            }

            var modelViewReset = new ModelViewReset((SldWorks)solidWorksApplication);
            modelViewReset.SaveModelView();
            SolidWorksProgressToastHandle holeProgressToast = null;

            try
            {
                //var selectedComponent = activeModel.ISelectionManager.GetSelectedObjectsComponent3(1, -1);

                //MatingFaceUtility.UpdateMatingFaceAndOpenHoleInAssembly2(
                //    (SldWorks)solidWorksApplication,
                //    selectedComponent);


                int selectedObjectCount =
                    activeModel.ISelectionManager.GetSelectedObjectCount2(-1);

                var allSelectedBusbarComponents = new List<Component2>();

                if (selectedObjectCount == 0)
                {
                    if (!CurrentSettings.CurrentSettingsBase.IsUserNewVersionHole)
                    {
                        SolidWorksProgressToast.ShowWarning(
                            "铜排开孔",
                            "请先选择要刷新的铜排组件。");
                        return;
                    }
                    else
                    {
                        if (!UiFeedback.Confirm("未选择时将刷新所有铜排的开孔，较耗时，是否继续?", "操作提示:"))
                        {
                            return;
                        }

                        // 获取装配体中所有组件
                        AssemblyDoc assemblyDoc = activeModel as AssemblyDoc;
                        if (assemblyDoc == null)
                        {
                            throw new InvalidOperationException(
                                "请在装配体中刷新铜排开孔。");
                        }

                        object[] components =
                            (object[])assemblyDoc.GetComponents(false);
                        foreach (object compObj in components)
                        {
                            Component2 component = compObj as Component2;
                            if (component != null && component.IsBusbarPart())
                            {
                                allSelectedBusbarComponents.Add(component);
                            }
                        }
                    }

                }
                else
                {
                    for (int selectedObjectIndex = 0;
                        selectedObjectIndex < selectedObjectCount;
                        selectedObjectIndex++)
                    {
                        Component2 selectedComponent =
                            activeModel.ISelectionManager.GetSelectedObjectsComponent3(
                                selectedObjectIndex + 1,
                                -1);
                        if (selectedComponent != null)
                        {
                            allSelectedBusbarComponents.Add(selectedComponent);
                        }
                    }
                }

                allSelectedBusbarComponents = allSelectedBusbarComponents
                    .Where(component =>
                        component != null &&
                        GeneratedBusbarSubassemblyIdentity
                            .IsUnderGeneratedSubassembly(component))
                    .ToList();

                // swApp 进度条
                solidWorksApplication.GetUserProgressBar(
                    out UserProgressBar userProgressBar);
                int allComponentsCount = allSelectedBusbarComponents.Count;
                userProgressBar.Start(0, allComponentsCount, "正在刷新开孔");
                holeProgressToast = SolidWorksProgressToast.Begin("铜排开孔");
                holeProgressToast.Report(new SolidWorksProgressToastUpdate
                {
                    Stage = "正在刷新铜排开孔",
                    Current = 0,
                    Total = allComponentsCount,
                    Detail = string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        "共 {0} 个铜排零件",
                        allComponentsCount)
                });

                int index = 0;

                if (CurrentSettings.CurrentSettingsBase.IsUserNewVersionHole)
                {
                    var autoOpeningHoleHelper =
                        new AutoOpeningHoleHelper(solidWorksApplication);
                    var executedComponents = new List<Component2>();
                    foreach (Component2 component in allSelectedBusbarComponents)
                    {
                        autoOpeningHoleHelper.SmartHoleProcess(
                            component,
                            excludeComponentList: executedComponents);

                        executedComponents.Add(component);

                        userProgressBar.UpdateProgress(index);
                        index++;
                        userProgressBar.UpdateTitle($@"正在处理： {index}/{allComponentsCount}");
                        holeProgressToast.Report(new SolidWorksProgressToastUpdate
                        {
                            Stage = "正在刷新铜排开孔",
                            Current = index,
                            Total = allComponentsCount,
                            Detail = string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                "正在处理：{0}/{1}",
                                index,
                                allComponentsCount)
                        });

                    }
                }
                else
                {
                    foreach (var component in allSelectedBusbarComponents)
                    {
                        MatingFaceUtility.UpdateMatingFaceAndOpenHoleInAssembly(
                            (SldWorks)solidWorksApplication,
                            component);

                        userProgressBar.UpdateProgress(index);
                        index++;
                        userProgressBar.UpdateTitle($@"正在处理： {index}/{allComponentsCount}");
                        holeProgressToast.Report(new SolidWorksProgressToastUpdate
                        {
                            Stage = "正在刷新铜排开孔",
                            Current = index,
                            Total = allComponentsCount,
                            Detail = string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                "正在处理：{0}/{1}",
                                index,
                                allComponentsCount)
                        });
                    }
                }


            }
            finally
            {
                holeProgressToast?.Finish();
                modelViewReset.ResetModelView(null);
            }
        }
    }
}
