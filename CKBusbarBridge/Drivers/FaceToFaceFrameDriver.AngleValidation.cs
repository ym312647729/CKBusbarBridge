using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    internal sealed partial class SolidWorksFaceToFaceFrameModelOperations
    {
        internal static FrameAngleInstanceState CaptureAngleInstance(IModelDoc2 assembly, IComponent2 component)
        {
            if (component == null) return null;
            var configuration = NativeDefaultModelConfiguration(assembly);
            RequireDefaultReference(assembly, assembly.ConfigurationManager.ActiveConfiguration.Name, assembly.GetPathName());
            return new FrameAngleInstanceState
            {
                Owner = assembly.GetPathName(), Id = component.GetID(), Name = component.Name2,
                Path = component.GetPathName(), Configuration = component.ReferencedConfiguration,
                Fixed = component.IsFixed(), Constrained = component.GetConstrainedStatus(),
                Transform = (component.Transform2?.ArrayData as double[])?.ToArray(),
                Mates = (component.GetMates() as object[] ?? Array.Empty<object>())
                    .Select(mate => CaptureAngleMate(assembly, mate, configuration)).OrderBy(m => m.Name, StringComparer.Ordinal).ToArray()
            };
        }

        private static FrameAngleMateState CaptureAngleMate(IModelDoc2 assembly, object value, string configuration)
        {
            var feature = value as IFeature
                ?? throw new InvalidOperationException("角钢配合无法读取特征身份：" + assembly.GetPathName());
            var suppressed = feature.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { configuration }) as bool[];
            if (suppressed?.Length != 1) throw new InvalidOperationException("角钢配合压缩状态无法读取：" + feature.Name);
            var code = feature.GetErrorCode2(out var warning);
            var state = new FrameAngleMateState { Name = feature.Name, Suppressed = suppressed[0], Error = code, Warning = code != 0 && warning };
            var mate = value as IMate2;
            if (mate != null)
            {
                state.Type = mate.Type.ToString(System.Globalization.CultureInfo.InvariantCulture);
                state.Alignment = mate.Alignment;
                state.References = Enumerable.Range(0, mate.GetMateEntityCount()).Select(i =>
                {
                    var entity = mate.MateEntity(i) as IMateEntity2
                        ?? throw new InvalidOperationException("角钢配合引用无法读取：" + feature.Name + "/" + i);
                    var reference = entity.Reference;
                    return new FrameAngleMateReference { Instance = AngleReferenceInstance(entity.ReferenceComponent),
                        Type = entity.ReferenceType2, Feature = AngleReferenceFeature(reference), Resolved = reference != null,
                        Geometry = state.Suppressed ? Array.Empty<double>() : (entity.EntityParams as double[] ?? Array.Empty<double>()).ToArray() };
                }).ToArray();
            }
            else if (value is IMateInPlace inPlace)
            {
                state.Type = "就地配合";
                state.References = Enumerable.Range(0, inPlace.GetMateEntityCount()).Select(i =>
                {
                    var name = inPlace.MateComponentName[i];
                    var component = string.IsNullOrEmpty(name) ? null : ((IAssemblyDoc)assembly).GetComponentByName(name) as IComponent2;
                    if (!string.IsNullOrEmpty(name) && component == null)
                        throw new InvalidOperationException("角钢就地配合引用组件无法解析：" + feature.Name + "/" + name);
                    var reference = inPlace.MateEntity[i];
                    return new FrameAngleMateReference { Instance = AngleReferenceInstance(component),
                        Type = inPlace.MateEntityType[i], Feature = AngleReferenceFeature(reference), Resolved = reference != null };
                }).ToArray();
            }
            else throw new InvalidOperationException("角钢存在不支持的配合类型：" + feature.Name);
            for (var display = feature.GetFirstDisplayDimension() as DisplayDimension; display != null;
                display = feature.GetNextDisplayDimension(display) as DisplayDimension)
            {
                var dimension = display.GetDimension2(0);
                var values = dimension?.GetSystemValue3((int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { configuration }) as double[];
                if (values?.Length != 1) throw new InvalidOperationException("角钢配合尺寸无法读取：" + feature.Name);
                var factor = display.Type2 == (int)swDimensionType_e.swAngularDimension ? 180d / Math.PI : 1000d;
                state.Dimensions.Add(dimension.FullName, values[0] * factor);
            }
            return state;
        }

        private static string AngleReferenceInstance(IComponent2 component)
        {
            if (component == null) return "所属装配体";
            // 文件名和 Name2 会随原生独立化变化；同一所属装配体中的实例 ID 用于核验身份。
            var ids = new List<int>();
            for (var current = component; current != null; current = current.GetParent()) ids.Insert(0, current.GetID());
            return string.Join("/", ids);
        }

        private static string AngleReferenceFeature(object reference)
        {
            var feature = reference as IFeature;
            return feature == null ? string.Empty : feature.GetTypeName2() + "/" + feature.Name;
        }
    }
}
