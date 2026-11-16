using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 侧对侧基座模型复制与驱动器。每个位置都输出独立模型树。
    /// </summary>
    internal sealed class SideToSideBaseDriver : ISideToSideBaseDriver
    {
        private readonly ISideToSideBaseModelOperations _operations;
        private readonly ISideToSideBaseInsulatorOperations _insulatorOperations;

        internal SideToSideBaseDriver(ISideToSideBaseModelOperations operations)
            : this(operations, null)
        {
        }

        internal SideToSideBaseDriver(
            ISideToSideBaseModelOperations operations,
            ISideToSideBaseInsulatorOperations insulatorOperations)
        {
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            _insulatorOperations = insulatorOperations;
        }

        public string PrepareModel(
            SideToSideBaseModelPlan plan,
            string sourceAssemblyPath,
            string outputDirectory,
            IDrawingNumberGenerator drawingNumbers)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (string.IsNullOrWhiteSpace(sourceAssemblyPath) || !File.Exists(sourceAssemblyPath))
            {
                throw new FileNotFoundException(
                    "待复制的侧对侧基座模板不存在：" + sourceAssemblyPath,
                    sourceAssemblyPath);
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("侧对侧基座输出目录不能为空。", nameof(outputDirectory));
            }

            string drawingNumber = null;
            var suffix = string.Empty;
            var nameAction = BaseModelNamingPolicy.Resolve(
                hasDrawingNumberSuffix: false,
                targetIsTemplateDefault: plan.IsTemplateDefault);
            var needsDrawingNumber =
                nameAction == BaseModelNameActionKind.AddDrawingNumber;
            if (needsDrawingNumber)
            {
                if (drawingNumbers == null)
                {
                    throw new ArgumentNullException(nameof(drawingNumbers));
                }

                drawingNumber = drawingNumbers.PeekNextDrawingNumber();
                suffix = drawingNumbers.PeekNextFileNameSuffix();
            }

            _operations.PackAndGoWithSuffix(sourceAssemblyPath, outputDirectory, suffix);
            var outputPath = BuildOutputAssemblyPath(
                sourceAssemblyPath,
                outputDirectory,
                suffix);
            if (!File.Exists(outputPath))
            {
                throw new FileNotFoundException(
                    "侧对侧基座 PackAndGo 后未找到总装配体：" + outputPath,
                    outputPath);
            }

            if (needsDrawingNumber)
            {
                drawingNumbers.CommitNext(drawingNumber);
            }

            var hasElevatedBase = plan.Kind == SideToSideBaseModelKind.Base1
                ? false
                : plan.Kind == SideToSideBaseModelKind.Base2
                    ? true
                    : (bool?)null;
            if (plan.IsTemplateDefault && !hasElevatedBase.HasValue)
            {
                return outputPath;
            }

            // 基座1无增高基座、基座2有增高基座，即使普通基座参数等于模板默认值，
            // 仍需切换并驱动对应侧封板；默认参数时不重复写入普通基座全局变量。
            // 非默认普通基座、增高基座分别整组写入各自必需变量。
            // 驱动成功后把已打开模型移交给绝缘子装配/总装阶段，避免重复打开。
            _operations.ApplyRequiredVariablesAndSave(
                outputPath,
                plan.IsTemplateDefault ? null : plan.GlobalVariables,
                hasElevatedBase,
                keepModelOpenAfterSave: true);
            return outputPath;
        }

        /// <summary>
        /// 在已驱动的基座或增高基座中写入定位尺寸并装配绝缘子。
        /// 该操作属于基座驱动流程，复用 PrepareModel 保持打开的宿主模型。
        /// </summary>
        public void InstallInsulators(
            string hostAssemblyPath,
            IReadOnlyList<string> insulatorAssemblyPaths,
            IReadOnlyList<SideToSideInsulatorPlacementPlan> placements)
        {
            if (_insulatorOperations == null)
            {
                throw new InvalidOperationException("未配置侧对侧基座绝缘子装配操作。");
            }

            if (string.IsNullOrWhiteSpace(hostAssemblyPath))
            {
                throw new ArgumentException("绝缘子宿主基座路径不能为空。", nameof(hostAssemblyPath));
            }

            if (insulatorAssemblyPaths == null || insulatorAssemblyPaths.Count == 0)
            {
                throw new ArgumentException("绝缘子装配体路径不能为空。", nameof(insulatorAssemblyPaths));
            }

            if (placements == null || placements.Count == 0)
            {
                throw new ArgumentException("绝缘子安装计划不能为空。", nameof(placements));
            }

            if (insulatorAssemblyPaths.Count != placements.Count)
            {
                throw new ArgumentException(
                    "绝缘子装配体路径数量必须与安装计划数量一致。",
                    nameof(insulatorAssemblyPaths));
            }

            if (insulatorAssemblyPaths.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException(
                    "绝缘子装配体路径不能包含空值。",
                    nameof(insulatorAssemblyPaths));
            }

            var hostSession = _insulatorOperations.OpenHostAssembly(hostAssemblyPath);
            var inserted = new List<object>();
            var succeeded = false;
            try
            {
                // 定位尺寸写入属于基座驱动，必须在插入绝缘子之前完成。
                foreach (var placement in placements)
                {
                    _insulatorOperations.WritePlacement(hostSession, placement);
                }

                // 每个安装位置使用与其索引对应的独立模型；绝缘子驱动后已经关闭，
                // 因此在 AddComponent5 前逐一按路径重新加载。
                for (var index = 0; index < placements.Count; index++)
                {
                    var placement = placements[index];
                    var insulatorAssemblyPath = insulatorAssemblyPaths[index];
                    _insulatorOperations.LoadComponentForInsertion(
                        hostSession,
                        insulatorAssemblyPath);
                    var component = _insulatorOperations.InsertComponent(
                        hostSession,
                        insulatorAssemblyPath);
                    if (component == null)
                    {
                        throw new InvalidOperationException(
                            "插入侧对侧基座绝缘子组件失败：" + insulatorAssemblyPath);
                    }

                    inserted.Add(component);
                    _insulatorOperations.AddCoordinateMate(
                        hostSession,
                        component,
                        placement.HostCoordinateName,
                        placement.ChildCoordinateName);
                }

                succeeded = true;
            }
            catch
            {
                _insulatorOperations.RollbackInsertedComponents(hostSession, inserted);
                throw;
            }
            finally
            {
                // 成功时继续交由总装阶段复用并关闭；失败时立即关闭宿主。
                _insulatorOperations.CompleteHost(
                    hostSession,
                    hostAssemblyPath,
                    succeeded,
                    keepHostOpen: succeeded,
                    closeInsertedComponentDocuments: true);
            }
        }

        internal static string BuildOutputAssemblyPath(
            string sourceAssemblyPath,
            string outputDirectory,
            string suffix)
        {
            return Path.Combine(
                Path.GetFullPath(outputDirectory),
                Path.GetFileNameWithoutExtension(sourceAssemblyPath)
                    + (suffix ?? string.Empty)
                    + Path.GetExtension(sourceAssemblyPath));
        }
    }

    /// <summary>
    /// 侧对侧基座 SolidWorks 操作实现。复用已验证的 PackAndGo、必需变量写入、侧封板切换、
    /// 重建、保存和关闭逻辑；基座1固定传入“无增高基座”状态。
    /// </summary>
    internal sealed class SolidWorksSideToSideBaseModelOperations
        : ISideToSideBaseModelOperations
    {
        private readonly SolidWorksFaceToFaceBaseModelOperations _operations;

        internal SolidWorksSideToSideBaseModelOperations(
            ISldWorks swApp,
            string keepOpenAssemblyPath)
        {
            _operations = new SolidWorksFaceToFaceBaseModelOperations(
                swApp,
                keepOpenAssemblyPath);
        }

        public void PackAndGoWithSuffix(
            string assemblyPath,
            string outputDirectory,
            string suffix)
        {
            _operations.PackAndGoWithSuffix(assemblyPath, outputDirectory, suffix);
        }

        public void ApplyRequiredVariablesAndSave(
            string assemblyPath,
            IReadOnlyDictionary<string, double> variables,
            bool? hasElevatedBase,
            bool keepModelOpenAfterSave)
        {
            _operations.ApplyModelChangesAndSave(
                assemblyPath,
                BuildVariableMap(variables),
                hasElevatedBase,
                keepModelOpenAfterSave);
        }

        internal static IReadOnlyDictionary<string, object> BuildVariableMap(
            IReadOnlyDictionary<string, double> variables)
        {
            if (variables == null)
            {
                return null;
            }

            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in variables)
            {
                values[pair.Key] = pair.Value;
            }

            return values;
        }
    }
}
