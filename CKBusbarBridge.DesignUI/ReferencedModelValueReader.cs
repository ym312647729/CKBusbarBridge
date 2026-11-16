using CKBusbarBridge.Models;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    /// <summary>一次反读中的模型及引用配置。隐藏不影响有效性，不切换或写入模型配置。</summary>
    internal sealed class ReferencedModelValueReader
    {
        internal IModelDoc2 Model { get; }
        internal string Configuration { get; }
        internal string Identity { get; }

        internal ReferencedModelValueReader(IModelDoc2 model, string configuration = null, string identity = null)
        {
            Model = model ?? throw new InvalidOperationException("反读失败：模型文档不可访问。");
            Configuration = configuration ?? model.ConfigurationManager?.ActiveConfiguration?.Name;
            Identity = identity ?? model.GetTitle();
            if (string.IsNullOrWhiteSpace(Configuration) || model.GetConfigurationByName(Configuration) == null)
                throw Failure("引用配置", "配置不存在或不可访问");
        }

        internal static ReferencedModelValueReader FromComponent(IComponent2 component)
        {
            if (component == null) throw new InvalidOperationException("反读失败：必需组件不存在。");
            if (component.IsSuppressed())
                throw new InvalidOperationException("反读失败：必需组件已压缩：" + component.Name2);
            var model = component.GetModelDoc2() as IModelDoc2;
            if (model == null)
                throw new InvalidOperationException("反读失败：组件模型未解析或不可访问：" + component.Name2);
            var configuration = component.ReferencedConfiguration;
            if (string.IsNullOrWhiteSpace(configuration))
                throw new InvalidOperationException("反读失败：组件引用配置缺失：" + component.Name2);
            return new ReferencedModelValueReader(model, configuration, component.Name2);
        }

        internal static ReferencedModelValueReader FromPath(IModelDoc2 root, params string[] path)
        {
            var component = CabinetAssemblyDimensionReader.GetComponentByPath(root, path);
            if (component == null)
                throw new InvalidOperationException("反读失败：必需组件不存在：" + string.Join("/", path));
            return FromComponent(component);
        }

        internal double Dimension(string name)
        {
            if (!TryDimension(name, out var value)) throw Failure(name, "原驱动尺寸不存在或配置值无效");
            return value;
        }

        internal bool TryDimension(string name, out double value)
        {
            value = 0;
            try
            {
                var dimension = Model.Parameter(name) as Dimension;
                if (dimension == null) return false;
                var values = dimension.GetSystemValue3((int)swInConfigurationOpts_e.swSpecifyConfiguration,
                    new[] { Configuration }) as double[];
                if (values == null || values.Length != 1 || !Finite(values[0])) return false;
                var kind = dimension.GetType();
                if (kind == (int)swDimensionParamType_e.swDimensionParamTypeDoubleLinear)
                    value = values[0] * 1000d;
                else if (kind == (int)swDimensionParamType_e.swDimensionParamTypeInteger)
                    value = values[0];
                else if (kind == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular)
                    value = values[0] * 180d / Math.PI;
                else throw Failure(name, "尺寸类型未知，不能确定数值单位");
                return Finite(value);
            }
            catch (COMException ex) { throw Failure(name, "尺寸读取失败", ex); }
        }

        internal double Global(string name)
        {
            if (!TryGlobal(name, out var value)) throw Failure(name, "原驱动全局变量不存在或无法读取引用配置的求值结果");
            return value;
        }

        internal bool TryGlobal(string name, out double value)
        {
            value = 0;
            try
            {
                var manager = Model.GetEquationMgr() as IEquationMgr;
                if (manager == null) return false;
                for (var index = 0; index < manager.GetCount(); index++)
                {
                    var equation = manager.Equation[index];
                    if (!CabinetAssemblyDimensionReader.EquationDefinesGlobalVariable(equation, name)) continue;
                    if (manager.Suppression[index])
                        throw Failure(name, "原驱动方程在引用配置中已压缩");
                    if (string.Equals(Model.ConfigurationManager?.ActiveConfiguration?.Name, Configuration,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        value = manager.Value[index];
                        if (manager.Status != index) throw Failure(name, "原驱动方程求值失败");
                        return Finite(value);
                    }

                    // 当前文档配置的求值结果不能冒充组件引用配置；优先读取原变量驱动的配置尺寸。
                    for (var driven = 0; driven < manager.GetCount(); driven++)
                    {
                        if (manager.Suppression[driven]
                            || manager.GetConfigurationOption(driven) != (int)swInConfigurationOpts_e.swAllConfiguration) continue;
                        var text = manager.Equation[driven];
                        var equals = text.IndexOf('=');
                        if (equals < 0 || text.Substring(equals + 1).Trim() != "\"" + name + "\"") continue;
                        var dimensionName = text.Substring(0, equals).Trim().Trim('"');
                        if (dimensionName.Contains("@") && TryDimension(dimensionName, out value)) return true;
                    }
                    // 仅完整的、对所有配置生效的常量可跨配置读取；表达式不得提取首个数字。
                    if (manager.GetConfigurationOption(index) == (int)swInConfigurationOpts_e.swAllConfiguration)
                        return CabinetAssemblyDimensionReader.TryParseEquationNumericValue(equation, out value);
                    return false;
                }
                return TryUniversalParameter(name, out value);
            }
            catch (COMException ex) { throw Failure(name, "全局变量读取失败", ex); }
        }

        private bool TryUniversalParameter(string name, out double value)
        {
            value = 0;
            if (name != "相间距" && name != "绝缘子高度" && name != "排厚") return false;
            if (!TryGlobal(UniversalInsulatorLayout.DistanceNames[0], out var first)) return false;
            if (name == "相间距")
            {
                var length = Global("支架长度");
                var layout = new UniversalInsulatorLayout(length,
                    UniversalInsulatorLayout.DistanceNames.Select(n => length / 2d - Global(n)));
                value = layout.PhaseSpacing();
            }
            else value = SharedInsulator().Global(name);
            return true;
        }

        internal ReferencedModelValueReader SharedInsulator()
        {
            BridgeModelReadOperations.EnsureAssemblyFullyResolved(Model);
            var config = Model.GetConfigurationByName(Configuration) as SolidWorks.Interop.sldworks.Configuration;
            var root = config?.GetRootComponent3(false);
            var children = (root?.GetChildren() as object[] ?? Array.Empty<object>()).OfType<Component2>()
                .Where(c => System.IO.Path.GetFileNameWithoutExtension(c.GetPathName()).StartsWith("绝缘子垫块", StringComparison.Ordinal)).ToArray();
            if (children.Length != 3 || children.Select(c => c.GetPathName() + "|" + c.ReferencedConfiguration)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                throw Failure("绝缘子子件", "三组实例没有共用唯一模型和引用配置");
            var enabled = children.Where(c => !c.IsSuppressed()).ToArray();
            if (enabled.Length == 0) throw Failure("绝缘子子件", "没有有效的绝缘子组");
            return FromComponent(enabled[0]);
        }

        internal int SharedInsulatorRootCount()
            => SharedInsulator().InsulatorGroupRootCount();

        internal int InsulatorGroupRootCount()
        {
            var shared = this;
            BridgeModelReadOperations.EnsureAssemblyFullyResolved(shared.Model);
            if (shared.Model.ConfigurationManager.ActiveConfiguration.Name != shared.Configuration)
                throw Failure("垫块根数", "共享子件的活动配置与引用配置不一致");
            Func<string, Component2[]> pads = name =>
            {
                var mate = (((AssemblyDoc)shared.Model).FeatureByName(name) as Feature)?.GetSpecificFeature2() as Mate2;
                if (mate == null) throw Failure(name, "共享绝缘子缺少原距离配合");
                return Enumerable.Range(0, mate.GetMateEntityCount()).Select(i => mate.MateEntity(i).ReferenceComponent)
                    .Where(c => c != null && System.IO.Path.GetFileNameWithoutExtension(c.GetPathName()).StartsWith("8UQ.584.013", StringComparison.Ordinal))
                    .GroupBy(c => c.Name2, StringComparer.Ordinal).Select(g => g.First()).ToArray();
            };
            var first = pads("距离1"); var middle = pads("距离2"); var last = pads("距离3");
            if (first.Length != 1 || middle.Length != 2 || last.Length != 2 || !middle.Any(c => c.Name2 == first[0].Name2))
                throw Failure("垫块根数", "基础垫块距离配合链无效");
            var second = middle.Single(c => c.Name2 != first[0].Name2);
            if (!last.Any(c => c.Name2 == second.Name2)) throw Failure("垫块根数", "第三距离配合未连接中间垫块");
            var third = last.Single(c => c.Name2 != second.Name2);
            if (third.Name2 == first[0].Name2) throw Failure("垫块根数", "距离配合链存在重复实例");
            var count = 1; var suppressedSeen = false;
            foreach (var component in new[] { first[0], second, third })
            {
                // Reference特征的压缩标志不能代表组件状态，必须读已解析组件本身。
                if (component.GetSuppression() == (int)swComponentSuppressionState_e.swComponentSuppressed) suppressedSeen = true;
                else
                {
                    if (suppressedSeen) throw Failure("垫块根数", "基础垫块不是连续启用状态");
                    count++;
                }
            }
            return count;
        }

        internal bool Suppressed(string featureName)
        {
            try
            {
                var feature = (Model.GetType() == (int)swDocumentTypes_e.swDocPART
                    ? ((PartDoc)Model).FeatureByName(featureName)
                    : ((AssemblyDoc)Model).FeatureByName(featureName)) as Feature;
                if (feature == null) throw Failure(featureName, "未找到原驱动特征");
                var states = feature.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration,
                    new[] { Configuration }) as bool[];
                if (states == null || states.Length != 1) throw Failure(featureName, "特征压缩状态不可读取");
                return states[0];
            }
            catch (COMException ex) { throw Failure(featureName, "特征压缩状态读取失败", ex); }
        }

        internal string Property(string name)
        {
            return TryProperty(name) ?? throw Failure(name, "原模型属性缺失");
        }

        internal string TryProperty(string name)
        {
            try
            {
                var extension = Model.Extension;
                if (extension == null) throw Failure(name, "模型属性管理器不可访问");
                foreach (var configuration in new[] { Configuration, string.Empty }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var manager = extension.CustomPropertyManager[configuration];
                    if (manager == null) throw Failure(name, "配置属性管理器不可访问");
                    manager.Get2(name, out var raw, out var resolved);
                    var value = string.IsNullOrWhiteSpace(resolved) ? raw : resolved;
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
                return null;
            }
            catch (COMException ex) { throw Failure(name, "原模型属性读取失败", ex); }
        }

        private InvalidOperationException Failure(string parameter, string reason, Exception inner = null) =>
            new InvalidOperationException("模型反读失败：" + Identity + "；配置=" + Configuration +
                "；参数=" + CabinetHoleParameters.GetParameterDisplayName(parameter) + "；" + reason + "。", inner);

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
