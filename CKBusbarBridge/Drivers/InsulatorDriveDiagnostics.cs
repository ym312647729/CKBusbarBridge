using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CKBusbarBridge.Helpers;
using SolidWorks.Interop.sldworks;

namespace CKBusbarBridge.Drivers
{
    /// <summary>只观察本轮通用绝缘子驱动；不求值方程，不重建或保存模型。</summary>
    internal sealed class InsulatorDriveDiagnostics
    {
        private readonly ISldWorks _application;
        private readonly IModelDoc2 _rootModel;
        private readonly string _operationId = "通用绝缘子-" + Guid.NewGuid().ToString("N");
        private readonly ProjectLocalTestLog _localLog = ProjectLocalTestLog.Default;

        internal InsulatorDriveDiagnostics(ISldWorks application, IModelDoc2 rootModel)
        {
            _application = application;
            _rootModel = rootModel;
        }

        internal void Start(UniversalInsulatorDrivePlan plan, bool offsetLayout)
        {
            _localLog.Write(_operationId, "驱动开始", () =>
            {
                var driverAssembly = typeof(InsulatorDriver).Assembly;
                return "诊断版本=绝缘子驱动-20261030-3；程序集=" + driverAssembly.Location
                    + "；模块标识=" + driverAssembly.ManifestModule.ModuleVersionId
                    + "；线程=" + Thread.CurrentThread.ManagedThreadId
                    + "；线程模型=" + Thread.CurrentThread.GetApartmentState()
                    + "；CommandInProgress=" + _application.CommandInProgress
                    + "；L型支架=" + plan.UseLBracket + "；偏移布局=" + offsetLayout
                    + "；本体高度=" + Number(plan.BodyHeight) + "；排厚=" + Number(plan.Thickness)
                    + "；期望支架长度=" + Number(plan.Layout.BracketLength)
                    + "；期望三组距边=" + string.Join(",", plan.Layout.Distances.Select(Number))
                    + System.Environment.NewLine + DescribeModel(_rootModel, true);
            });
        }

        internal void Capture(string stage, IModelDoc2 targetModel)
        {
            _localLog.Write(_operationId, stage, () => DescribeContext(targetModel)
                + System.Environment.NewLine + DescribeModel(targetModel, true)
                + (ReferenceEquals(targetModel, _rootModel) ? string.Empty
                    : System.Environment.NewLine + "顶层连续观察：" + DescribeModel(_rootModel, true)));
        }

        internal void EquationWritten(IModelDoc2 targetModel, int equationIndex,
            string previousEquation, string requestedEquation, int setterStatus, string writtenEquation)
        {
            _localLog.Write(_operationId, "单条方程写入后", () =>
            {
                return DescribeContext(targetModel)
                    + System.Environment.NewLine + "方程索引=" + equationIndex + "；setter后状态=" + setterStatus
                    + "；原方程=" + previousEquation + "；请求方程=" + requestedEquation
                    + "；实际方程=" + writtenEquation;
            });
        }

        internal void Readback(IModelDoc2 targetModel, string source, double actual, double expected)
        {
            _localLog.Write(_operationId, "原校验反读", () => DescribeContext(targetModel)
                + System.Environment.NewLine + "检查项=" + source + "；实际=" + Number(actual)
                + "；期望=" + Number(expected));
        }

        internal void Write(string stage, Func<string> createDetail)
        {
            _localLog.Write(_operationId, stage, createDetail);
        }

        private string DescribeContext(IModelDoc2 targetModel)
        {
            return "目标=" + DescribeModel(targetModel, false)
                + System.Environment.NewLine + "实际活动=" + DescribeModel(_application.ActiveDoc as IModelDoc2, false);
        }

        private string DescribeModel(IModelDoc2 model, bool includeEquations)
        {
            if (model == null) return "<模型为空>";
            var modelDetail = new StringBuilder();
            modelDetail.Append("文件=").Append(model.GetPathName()).Append("；标题=").Append(model.GetTitle())
                .Append("；活动配置=").Append(model.ConfigurationManager?.ActiveConfiguration?.Name)
                .Append("；只读=").Append(model.IsOpenedReadOnly()).Append("；需保存=").Append(model.GetSaveFlag());
            if (!includeEquations) return modelDetail.ToString();
            modelDetail.Append("；配置列表=").Append(string.Join(",", model.GetConfigurationNames() as string[] ?? Array.Empty<string>()));
            var equationManager = model.GetEquationMgr() as IEquationMgr;
            if (equationManager == null) return modelDetail.Append("；无方程式管理器").ToString();
            modelDetail.Append("；自动重建=").Append(equationManager.AutomaticRebuild)
                .Append("；自动排序=").Append(equationManager.AutomaticSolveOrder)
                .Append("；连接外部方程文件=").Append(equationManager.LinkToFile)
                .Append("；方程文件=").Append(equationManager.FilePath);
            var equationCount = equationManager.GetCount();
            for (var equationIndex = 0; equationIndex < equationCount; equationIndex++)
                modelDetail.AppendLine().Append("  方程[").Append(equationIndex).Append("]；全局变量=")
                    .Append(equationManager.GlobalVariable[equationIndex])
                    .Append("；压缩=").Append(ReadOptional(() => equationManager.Suppression[equationIndex]))
                    .Append("；禁用=").Append(ReadOptional(() => equationManager.Disabled[equationIndex]))
                    .Append("；配置范围=").Append(ReadOptional(() => equationManager.GetConfigurationOption(equationIndex)))
                    .Append("；文本=").Append(equationManager.Equation[equationIndex]);
            if (model is AssemblyDoc modelAssembly)
            {
                modelDetail.AppendLine().Append("  原位编辑目标=")
                    .Append(ReadOptional(() => (modelAssembly.GetEditTarget() as IModelDoc2)?.GetPathName()))
                    .Append("；原位编辑组件=").Append(ReadOptional(() => modelAssembly.GetEditTargetComponent()?.Name2));
                var components = (modelAssembly.GetComponents(true) as object[] ?? Array.Empty<object>()).OfType<Component2>();
                foreach (var component in components.OrderBy(item => item.Name2, StringComparer.Ordinal))
                    modelDetail.AppendLine().Append("  组件=").Append(component.Name2)
                        .Append("；文件=").Append(component.GetPathName())
                        .Append("；引用配置=").Append(component.ReferencedConfiguration)
                        .Append("；压缩=").Append(component.IsSuppressed());
            }
            return modelDetail.ToString();
        }

        private static string ReadOptional(Func<object> readValue)
        {
            try { return Convert.ToString(readValue(), CultureInfo.InvariantCulture); }
            catch (COMException readError)
            {
                // 旧模板可能不支持某个诊断属性，保留读取失败信息及其余证据。
                return "<诊断读取失败：" + readError.ErrorCode.ToString("X8", CultureInfo.InvariantCulture)
                    + "；" + readError.Message + ">";
            }
        }

        private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
