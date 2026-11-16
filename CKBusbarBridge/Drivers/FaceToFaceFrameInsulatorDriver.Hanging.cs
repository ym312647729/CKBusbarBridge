using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using Newtonsoft.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    internal sealed partial class FaceToFaceFrameInsulatorDriver
    {
        internal const string HangingPlanProperty = "母线桥换相吊装";
        internal const string HangingSpacingProperty = "母线桥吊装原相间距";
        internal const string FloorPlanProperty = "母线桥小偏移单相支撑";
        private const string HangingGroupReferencePrefix = "母线桥吊装组";
        internal static bool IsHangingInsulator(Component2 component) =>
            ReadMountPlan(component)?.IsFloorStanding == false;

        internal static BridgeInsulatorMountPlan ReadMountPlan(Component2 component)
        {
            var reader = ReferencedModelValueReader.FromComponent(component);
            var text = reader.TryProperty(FloorPlanProperty);
            if (string.IsNullOrWhiteSpace(text)) text = reader.TryProperty(HangingPlanProperty);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonConvert.DeserializeObject<BridgeInsulatorMountPlan>(text, new JsonSerializerSettings {
                TypeNameHandling = TypeNameHandling.None, ObjectCreationHandling = ObjectCreationHandling.Replace })
                ?? throw new InvalidOperationException("换相吊装参数无法读取：" + component.Name2);
        }

        private string PrepareMountedInsulators(string project, FaceToFaceFrameInsulatorDrivePlan drive)
        {
            var template = InsulatorDriver.ResolveConventionalInsulatorAssemblyPath(TemplateRoot());
            // 多高度组可能替换共享子装配。每轮先生成独立副本，不能再次把异高组按共享模板驱动。
            var isolation = Path.Combine(_isolateBridgeOutputs ? "母线桥" : "", "H" + Guid.NewGuid().ToString("N").Substring(0, 16));
            try
            {
                var paths = PrepareInstallationModels(drive,
                    (index, mount) => PrepareMountedInstallation(project, template, drive, isolation, index, mount),
                    (index, offset) => Path.GetFullPath(_insulatorDriver.PrepareOffsetThreePadInsulator(
                        project, Path.GetDirectoryName(template), drive.BusbarSpec, drive.PhaseSpacing,
                        drive.InsulatorHeight, drive.BracketHostSpan, drive.SinglePhaseRootCount, null,
                        Path.Combine(isolation, "S" + (index + 1)),
                        "H" + Guid.NewGuid().ToString("N").Substring(0, 12), offset)));
                _preparedOffsetSets[paths[0]] = paths;
                return paths[0];
            }
            catch (Exception primary)
            {
                try
                {
                    var cleanup = new FaceToFaceFrameScopedDocumentCleaner(new SolidWorksFaceToFaceFrameDocumentSession(_swApp))
                        .RequestCloseDocumentsUnderDirectory(Path.Combine(project, "绝缘子", isolation));
                    FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(primary, cleanup, isolation, "换相吊装准备");
                }
                catch (Exception cleanup)
                { FaceToFaceFrameCleanupDiagnostics.AppendData(primary, FaceToFaceFrameCleanupDiagnostics.RollbackWarningDataKey, cleanup.ToString()); }
                throw;
            }
        }

        internal static string[] PrepareInstallationModels(FaceToFaceFrameInsulatorDrivePlan drive,
            Func<int, BridgeInsulatorMountPlan, string> prepareMounted, Func<int, double, string> prepareOffset)
        {
            if (drive == null) throw new ArgumentNullException(nameof(drive));
            if (drive.Mounts == null || drive.Mounts.Count == 0)
                throw new ArgumentException("逐位准备模型需要非空的安装计划。", nameof(drive));
            if (prepareMounted == null) throw new ArgumentNullException(nameof(prepareMounted));
            if (prepareOffset == null) throw new ArgumentNullException(nameof(prepareOffset));
            return drive.Mounts.Select((mount, index) => mount == null && drive.UseOffsetTemplate
                ? prepareOffset(index, drive.TransverseOffsets[index]) : prepareMounted(index, mount)).ToArray();
        }

        private string PrepareMountedInstallation(string project, string template, FaceToFaceFrameInsulatorDrivePlan drive,
            string isolation, int index, BridgeInsulatorMountPlan mount)
        {
            var height = mount == null ? drive.InsulatorHeight : mount.BodyHeights.First(h => h > 0);
            var path = PrepareMountedCopy(project, template, drive, height,
                Path.Combine(isolation, "S" + (index + 1)), "H" + Guid.NewGuid().ToString("N").Substring(0, 12), mount);
            if (mount == null) return path;
            var model = (IModelDoc2)_swApp.GetOpenDocumentByName(path);
            var preparedRoots = new List<string>();
            ReplaceDifferentHeightGroups(model, project, template, drive, mount, height, preparedRoots);
            var writer = new SolidWorksDriver(_swApp);
            writer.SetCustomProperty(model, mount.IsFloorStanding ? FloorPlanProperty : HangingPlanProperty, JsonConvert.SerializeObject(mount));
            writer.SetCustomProperty(model, HangingSpacingProperty, drive.PhaseSpacing.ToString("R", CultureInfo.InvariantCulture));
            SaveMountedModel(model);
            _driveRecovery?.ConsolidateMountedDrive(model, preparedRoots, (installedModel, register) =>
                _insulatorDriver.DriveMountedInsulatorRecovery(installedModel, mount, drive.BracketHostSpan - 20,
                    drive.BusbarThickness, drive.SinglePhaseRootCount, register));
            return path;
        }

        private string PrepareMountedCopy(string project, string template, FaceToFaceFrameInsulatorDrivePlan drive,
            double height, string isolation, string suffix, BridgeInsulatorMountPlan mount)
        {
            var suppressed = mount == null ? null : Enumerable.Range(0, 3).Where(i => mount.BodyHeights[i] <= 0).Select(i => i + 1).ToArray();
            var path = _insulatorDriver.PrepareUniversalThreePadInsulatorFromTemplateDirectory(false, project,
                Path.GetDirectoryName(template), drive.BusbarSpec, drive.PhaseSpacing, height, drive.BracketHostSpan,
                drive.SinglePhaseRootCount, null, isolation, templateAssemblyFileName: Path.GetFileName(template),
                modelFileNameSuffix: suffix, suppressedLBracketPadComponentIndexes: suppressed);
            var model = _swApp.GetOpenDocumentByName(path) as IModelDoc2;
            if (model == null || !InsulatorDriver.IsUniversalInsulator(model))
                throw new InvalidOperationException("换相吊装必须使用支持逐组定位的通用绝缘子模板：" + path);
            if (mount != null)
                _insulatorDriver.DriveUniversalInsulator(model, new UniversalInsulatorDrivePlan(
                    new UniversalInsulatorLayout(drive.BracketHostSpan - 20, mount.Transverse), false,
                    height, drive.BusbarThickness, drive.SinglePhaseRootCount, suppressed), doc => { }, false);
            SaveMountedModel(model);
            return Path.GetFullPath(path);
        }

        private void ReplaceDifferentHeightGroups(IModelDoc2 model, string project, string template,
            FaceToFaceFrameInsulatorDrivePlan drive, BridgeInsulatorMountPlan mount, double commonHeight,
            ICollection<string> preparedRoots)
        {
            for (var index = 0; index < 3; index++)
            {
                var height = mount.BodyHeights[index];
                if (height <= 0 || Math.Abs(height - commonHeight) <= .01) continue;
                // 子装配及其本体都来自独立PackAndGo副本，避免修改另一相或另一个安装位。
                var outputRoot = Path.GetFullPath(Path.Combine(project, "绝缘子")) + Path.DirectorySeparatorChar;
                var positionRoot = Directory.GetParent(Path.GetDirectoryName(model.GetPathName())).FullName;
                if (!positionRoot.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("吊装逐相高度副本超出本次绝缘子输出树。");
                var path = PrepareMountedCopy(project, template, drive, height,
                    Path.Combine(positionRoot.Substring(outputRoot.Length), "V" + (index + 1)),
                    "H" + Guid.NewGuid().ToString("N").Substring(0, 12), null);
                preparedRoots.Add(path);
                var replacement = MountedGroups((IModelDoc2)_swApp.GetOpenDocumentByName(path))[index];
                var target = MountedGroups(model)[index];
                var replacementPath = replacement.GetPathName();
                SWHelper.EnsureAssemblyEditingContext(_swApp, model);
                model.ClearSelection2(true);
                try
                {
                    if (!target.Select4(false, null, false) || !((AssemblyDoc)model).ReplaceComponents2(
                        replacement.GetPathName(), replacement.ReferencedConfiguration, false,
                        (int)swReplaceComponentsConfiguration_e.swReplaceComponentsConfiguration_ManuallySelect, true))
                        throw new InvalidOperationException("换相吊装逐相高度副本替换失败：" + target.Name2);
                }
                finally { model.ClearSelection2(true); }
                // 更换模型后SW可能重编号；把原模板组角色写入组件引用，再用实际配合位置验收。
                var installed = ((((AssemblyDoc)model).GetComponents(true) as object[]) ?? Array.Empty<object>()).OfType<Component2>()
                    .Single(c => string.Equals(c.GetPathName(), replacementPath, StringComparison.OrdinalIgnoreCase));
                installed.ComponentReference = HangingGroupReferencePrefix + (index + 1).ToString(CultureInfo.InvariantCulture);
                if (!TryMountedGroupIndex(installed, out var savedIndex) || savedIndex != index + 1)
                    throw new InvalidOperationException("换相吊装逐相替换后组身份恢复失败：" + model.GetPathName());
                if (!model.EditRebuild3()) throw new InvalidOperationException("换相吊装逐相高度替换后求值失败：" + model.GetPathName());
            }
        }

        private void SaveMountedModel(IModelDoc2 model)
        {
            var errors = 0; var warnings = 0;
            if (!model.Save3((int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                ref errors, ref warnings) || errors != 0)
                throw new InvalidOperationException("换相吊装副本保存失败：" + model.GetPathName() + "；错误码=" + errors);
        }

        internal static Component2[] MountedGroups(IModelDoc2 model)
        {
            var result = new Component2[3];
            foreach (var child in ((((AssemblyDoc)model).GetComponents(true) as object[]) ?? Array.Empty<object>()).OfType<Component2>())
                if (TryMountedGroupIndex(child, out var index))
                {
                    if (result[index - 1] != null) throw new InvalidOperationException("换相吊装模板组重复：" + model.GetPathName());
                    result[index - 1] = child;
                }
            if (result.Any(c => c == null)) throw new InvalidOperationException("换相吊装模板缺少三个原安装组：" + model.GetPathName());
            return result;
        }

        internal static bool TryMountedGroupIndex(Component2 component, out int index)
        {
            var reference = component.ComponentReference;
            if (reference != null && reference.StartsWith(HangingGroupReferencePrefix, StringComparison.Ordinal))
            {
                if (!int.TryParse(reference.Substring(HangingGroupReferencePrefix.Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out index) || index < 1 || index > 3)
                    throw new InvalidOperationException("吊装模板组引用无效：" + component.Name2);
                return true;
            }
            return InsulatorDriver.TryResolveLBracketPadComponentIndex(component, out index);
        }

        internal static void ValidateMountedInsulator(Component2 component, BridgeInsulatorMountPlan expected, int roots, double thickness)
        {
            var actual = ReadMountPlan(component);
            if (actual?.Identity != expected.Identity) throw new InvalidOperationException("实际吊装参数与方案不符：" + component.Name2);
            var model = component.GetModelDoc2() as IModelDoc2;
            if (model == null || model.ConfigurationManager.ActiveConfiguration.Name != component.ReferencedConfiguration)
                throw new InvalidOperationException("吊装绝缘子的活动配置与实例引用配置不一致：" + component.Name2);
            var reader = ReferencedModelValueReader.FromComponent(component);
            var layout = InsulatorDriver.ReadUniversalLayout(reader);
            var groups = MountedGroups(model);
            for (var i = 0; i < 3; i++)
            {
                if (Math.Abs(layout.BracketLength / 2 - layout.Distances[i] - expected.Transverse[i]) > .01 ||
                    groups[i].IsSuppressed() != (expected.BodyHeights[i] <= 0))
                    throw new InvalidOperationException("吊装绝缘子的逐相位置或压缩状态不符：" + component.Name2);
                if (expected.BodyHeights[i] <= 0) continue;
                var values = ReferencedModelValueReader.FromComponent(groups[i]);
                if (Math.Abs(values.Global("绝缘子高度") - expected.BodyHeights[i]) > .01 ||
                    Math.Abs(values.Global("排厚") - thickness) > .01 ||
                    values.InsulatorGroupRootCount() != roots)
                    throw new InvalidOperationException("吊装绝缘子的本体高度或垫块状态不符：" + groups[i].Name2);
                var transform = groups[i].Transform2?.ArrayData as double[];
                if (transform == null || Math.Abs(transform[11] * 1000 - expected.Transverse[i]) > .01)
                    throw new InvalidOperationException("吊装绝缘子组实际变换不符：" + groups[i].Name2);
                var body = ((groups[i].GetChildren() as object[]) ?? Array.Empty<object>()).OfType<Component2>()
                    .Single(c => !c.IsSuppressed() && Path.GetFileNameWithoutExtension(c.GetPathName())
                        .StartsWith("绝缘子-", StringComparison.Ordinal));
                if (Math.Abs(ReferencedModelValueReader.FromComponent(body).Dimension("D2@草图1") - expected.BodyHeights[i]) > .01)
                    throw new InvalidOperationException("吊装绝缘子实际本体尺寸不符：" + body.Name2);
            }
        }
    }
}
