using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Helpers;
using CKBusbarBridgePreview;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    internal sealed class BridgeReferencedModelParameters : IBridgeCopperParameterSource
    {
        internal static IReadOnlyDictionary<IMate2,bool> MateSuppression(IModelDoc2 model,string configuration,bool coordinateOnly=false)
        {
            var result=new Dictionary<IMate2,bool>();
            for(var feature=model.FirstFeature() as Feature;feature!=null;feature=feature.GetNextFeature() as Feature)
            {
                if(feature.GetTypeName2()!="MateGroup") continue;
                ReadMateFeatures(feature.GetFirstSubFeature() as Feature,configuration,result,coordinateOnly);
                break;
            }
            return result;
        }
        private static void ReadMateFeatures(Feature feature,string configuration,Dictionary<IMate2,bool> result,bool coordinateOnly)
        {
            for(var current=feature;current!=null;current=current.GetNextSubFeature() as Feature)
            {
                var mate=current.GetSpecificFeature2() as IMate2;
                if(mate!=null && (!coordinateOnly || mate.Type==(int)swMateType_e.swMateCOORDINATE))
                {
                    var state=current.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration,new[]{configuration}) as bool[];
                    if(state==null || state.Length!=1) throw new InvalidOperationException("实际配合的压缩状态无法读取："+current.Name);
                    result[mate]=state[0];
                }
                else if(current.GetTypeName2()=="FtrFolder")
                    ReadMateFeatures(current.GetFirstSubFeature() as Feature,configuration,result,coordinateOnly);
            }
        }
        private readonly IModelDoc2 _model;
        private readonly ReferencedModelValueReader _reader;
        private readonly string _configuration;
        private readonly Dictionary<string, double> _values = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string,string> _properties=new Dictionary<string,string>(StringComparer.Ordinal);
        private readonly Dictionary<string,bool> _suppressed=new Dictionary<string,bool>(StringComparer.Ordinal);
        private readonly Dictionary<string,bool> _features=new Dictionary<string,bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, BridgeCopperBaseFlangeParameters> _baseFlanges = new Dictionary<string, BridgeCopperBaseFlangeParameters>(StringComparer.Ordinal);
        private readonly Dictionary<string, BridgeCopperSheetMetalParameters> _sheetMetals = new Dictionary<string, BridgeCopperSheetMetalParameters>(StringComparer.Ordinal);
        private IReadOnlyList<string> _baseFlangeNames;
        internal BridgeReferencedModelParameters(Component2 component, Func<string, bool> allowUnsavedDocument = null)
        {
            _reader = ReferencedModelValueReader.FromComponent(component);
            _configuration = _reader.Configuration;
            _model = _reader.Model;
            new BridgeBusinessReadPolicy(allowUnsavedDocument).RequireSaved(_model.GetSaveFlag(), component.GetPathName());
        }
        internal IModelDoc2 Model => _model;
        public IReadOnlyList<string> BaseFlangeNames()
        {
            if (_baseFlangeNames != null) return _baseFlangeNames;
            var names = new List<string>();
            for (var feature = _model.FirstFeature() as Feature; feature != null; feature = feature.GetNextFeature() as Feature)
                if (feature.GetTypeName2() == "SMBaseFlange") names.Add(feature.Name);
            return _baseFlangeNames = names.ToArray();
        }
        public BridgeCopperBaseFlangeParameters BaseFlange(string name)
        {
            if (_baseFlanges.TryGetValue(name, out var cached)) return cached;
            var definition = OriginalDefinition(name, "SMBaseFlange") as IBaseFlangeFeatureData;
            if (definition == null) throw DefinitionError(name);
            return _baseFlanges[name] = new BridgeCopperBaseFlangeParameters {
                Length = definition.D1OffsetDistance * 1000,
                Thickness = definition.Thickness * 1000,
                Radius = definition.BendRadius * 1000
            };
        }
        public BridgeCopperSheetMetalParameters SheetMetal(string name)
        {
            if (_sheetMetals.TryGetValue(name, out var cached)) return cached;
            var definition = OriginalDefinition(name, "SheetMetal") as ISheetMetalFeatureData;
            if (definition == null) throw DefinitionError(name);
            return _sheetMetals[name] = new BridgeCopperSheetMetalParameters {
                Thickness = definition.Thickness * 1000,
                Radius = definition.BendRadius * 1000
            };
        }
        private object OriginalDefinition(string name, string type)
        {
            new BridgeBusinessReadPolicy().RequireFeatureConfiguration(
                _model.ConfigurationManager.ActiveConfiguration.Name, _configuration, _model.GetTitle() + "/" + name);
            var feature = ((PartDoc)_model).FeatureByName(name) as Feature;
            if (feature == null || feature.GetTypeName2() != type || Suppressed(name)) throw DefinitionError(name);
            // 只读取生成器原写入字段；不进入编辑/回滚状态，也不调用ModifyDefinition或保存。
            return feature.GetDefinition();
        }
        private InvalidOperationException DefinitionError(string name) => new InvalidOperationException(
            "引用配置缺少可读取的原驱动特征定义：" + _model.GetTitle() + "/" + _configuration + "/" + name);
        double IBridgeCopperParameterSource.Dimension(params string[] names) => Dimension(names);
        bool IBridgeCopperParameterSource.Suppressed(string name) => Suppressed(name);
        internal bool Suppressed(string name)
        {
            if(_suppressed.TryGetValue(name,out var cached)) return cached;
            return _suppressed[name]=_reader.Suppressed(name);
        }
        internal bool HasFeature(string name)
        {
            if(_features.TryGetValue(name,out var value)) return value;
            return _features[name]=(_model.GetType()==(int)swDocumentTypes_e.swDocPART ?
                ((PartDoc)_model).FeatureByName(name) : ((AssemblyDoc)_model).FeatureByName(name))!=null;
        }
        internal double Dimension(params string[] names)
        {
            foreach (var name in names)
            {
                if (_values.TryGetValue(name, out var cached)) return cached;
                if (_reader.TryDimension(name, out var value)) return _values[name] = value;
            }
            throw new InvalidOperationException("引用配置缺少原驱动尺寸：" + _model.GetTitle() + "/" + _configuration + "/" + string.Join("、", names));
        }
        internal double Global(string name)
        {
            if (_values.TryGetValue(name, out var cached)) return cached;
            return _values[name] = _reader.Global(name);
        }
        internal string Property(string name)
        {
            if(_properties.TryGetValue(name,out var cached)) return cached;
            foreach (var configuration in new[] { _configuration, string.Empty })
            {
                _model.Extension.CustomPropertyManager[configuration].Get2(name, out var raw, out var resolved);
                if (!string.IsNullOrWhiteSpace(resolved)) return _properties[name]=resolved.Trim();
                if (!string.IsNullOrWhiteSpace(raw)) return _properties[name]=raw.Trim();
            }
            return _properties[name]=null;
        }
        internal FaceToFaceFramePhysicalSnapshot Frame(Component2 component, FaceToWallFrameTemplateKind kind)
        {
            var width = Global("基架宽度"); var length = Global("基架长度"); var height = Global("基架高度");
            var identity = FaceToFaceFramePathClassifier.ResolveStoredTemplateIdentity(component.GetPathName(),
                Property(FrameTemplateIdentityMetadata.PropertyName));
            var small = identity == FrameTemplateIdentity.FaceToFaceSmallOffset032 ||
                CabinetAssemblyDimensionReader.HasGlobalVariable(_model, "进孔偏移尺寸")
                ? new FaceToFaceSmallOffsetFrameDrivePlan(width, (int)Global("进孔偏移方向"), Global("进孔偏移尺寸"),
                    (int)Global("出孔偏移方向"), Global("出孔偏移尺寸")) : null;
            var positions = new FrameInsulatorPositionReadback().Read(
                new SolidWorksFrameInsulatorPositionSource(component, length, name => Dimension(name)));
            var entryText = kind == FaceToWallFrameTemplateKind.Large1 ? Property(FaceToWallFrameDrivePlanner.WallEntryDistancePropertyName) : null;
            return new FaceToFaceFramePhysicalSnapshot(width, length, height, positions, small, identity,
                kind == FaceToWallFrameTemplateKind.Large2 ? Global(FaceToWallFrameDrivePlanner.SideCoverPlateWidthVariableName) : (double?)null,
                SolidWorksFaceToFaceFrameReadOperations.ParseWallEntryDistance(entryText, component.Name2), !string.IsNullOrWhiteSpace(entryText));
        }
        private bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
