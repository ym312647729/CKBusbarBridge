using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.Drivers
{
    internal sealed class SideToSideFrameReadBinding
    {
        public string Instance { get; set; }
        public string Path { get; set; }
        public string Configuration { get; set; }
        public FrameTemplateIdentity TemplateIdentity { get; set; }

        internal void RequireActual(string instance, string path, string configuration)
        {
            if (Instance != instance || !BridgeGenerationFileBackup.Same(Path, path) || Configuration != configuration)
                throw new InvalidOperationException("侧对侧转换的旧基架实例、文件或引用配置不符：" + instance);
        }
    }

    // 授权来自已核验正式配方、实例映射和修改前实物，不能由目标场景或当前文件夹推断。
    internal static class SideToSideFrameTransition
    {
        // 当前实物反读不依赖历史安装转换授权；只记录实际实例、文件、配置和文件模板身份。
        // 此结果仅用于读取，不持久化为修改前安装链或待同步授权。
        internal static SideToSideFrameReadBinding[] CapturePhysical(IEnumerable<SideToSideFrameReadBinding> actual)
        {
            if (actual == null) throw new ArgumentNullException(nameof(actual));
            var result = actual.Select(item =>
            {
                if (item == null || !FaceToFaceFramePathClassifier.TryGetStoredTemplateIdentity(item.Path, out var identity))
                    throw new InvalidOperationException("侧对侧实物基架模板无法识别：" + item?.Instance + "；路径=" + item?.Path);
                return new SideToSideFrameReadBinding { Instance = item.Instance, Path = item.Path,
                    Configuration = item.Configuration, TemplateIdentity = identity };
            }).ToArray();
            RequireFamily(result);
            return result;
        }

        internal static SideToSideFrameReadBinding[] Capture(string recipe,
            IReadOnlyDictionary<string, string> instances, BridgeModelState state)
        {
            var scenario = JObject.Parse(recipe).Value<string>("Scenario");
            if (scenario != "SideToSideBase1" && scenario != "SideToSideBase2")
                throw new InvalidOperationException("侧对侧转换的正式配方场景无效。");
            var objects = new BridgeGenerationRecipeCodec().ReadSavedObjects(recipe);
            var frames = objects.Where(o => o.Kind == DesignObjectKind.Frame).ToArray();
            if (instances == null || instances.Count != frames.Length)
                throw new InvalidOperationException("侧对侧转换缺少完整正式基架实例映射。");
            var factory = new BridgeDraftGenerationPlanFactory();
            var bindings = frames.Select(frame =>
            {
                if (!instances.TryGetValue(frame.Id, out var name) || string.IsNullOrWhiteSpace(name) ||
                    state?.Instances == null || !state.Instances.TryGetValue(name, out var instance) ||
                    !instance.IsRoot || instance.Suppressed || !state.Documents.TryGetValue(instance.Document, out var document) ||
                    string.IsNullOrWhiteSpace(document.Path) || string.IsNullOrWhiteSpace(document.Configuration))
                    throw new InvalidOperationException("侧对侧转换缺少修改前基架实物身份：" + frame.DisplayName);
                var identity = factory.FrameInput(frame, objects).TemplateIdentity;
                if (!FaceToFaceFramePathClassifier.TryGetStoredTemplateIdentity(document.Path, out var fileIdentity) || identity != fileIdentity)
                    throw new InvalidOperationException("侧对侧转换的正式模板与修改前实物不符：" + name);
                return new SideToSideFrameReadBinding { Instance = name, Path = document.Path,
                    Configuration = document.Configuration, TemplateIdentity = identity };
            }).ToArray();
            RequireFamily(bindings);
            return bindings;
        }

        internal static SideToSideFrameBaseKind? RequireFamily(IEnumerable<SideToSideFrameReadBinding> bindings)
        {
            var items = bindings?.ToArray() ?? throw new InvalidOperationException("缺少侧对侧旧基架读取契约。");
            if (items.Any(b => b == null || string.IsNullOrWhiteSpace(b.Instance) || string.IsNullOrWhiteSpace(b.Path) ||
                    string.IsNullOrWhiteSpace(b.Configuration)) || items.Select(b => b.Instance).Distinct(StringComparer.Ordinal).Count() != items.Length)
                throw new InvalidOperationException("侧对侧旧基架读取契约缺失或重复。");
            if (items.Length == 0) return null;
            var fixedCount = items.Count(b => b.TemplateIdentity == FrameTemplateIdentity.SideToSidePhaseChange029);
            if (fixedCount != 0 && fixedCount != items.Length || items.Any(b =>
                    b.TemplateIdentity != FrameTemplateIdentity.SideToSidePhaseChange029 &&
                    b.TemplateIdentity != FrameTemplateIdentity.Regular024 &&
                    b.TemplateIdentity != FrameTemplateIdentity.FaceToFaceSmallOffset032 &&
                    b.TemplateIdentity != FrameTemplateIdentity.FaceToFaceLargeOffsetCorner028))
                throw new InvalidOperationException("侧对侧旧基架模板混装或不受支持。");
            return fixedCount > 0 ? SideToSideFrameBaseKind.Base1 : SideToSideFrameBaseKind.Base2;
        }

        internal static void RequireSame(SideToSideFrameReadBinding[] expected, SideToSideFrameReadBinding[] actual)
        {
            RequireFamily(actual);
            if (expected.Length != actual.Length) throw new InvalidOperationException("侧对侧转换旧基架清单被修改。");
            foreach (var binding in expected)
            {
                var found = actual.SingleOrDefault(b => b.Instance == binding.Instance);
                if (found == null || found.TemplateIdentity != binding.TemplateIdentity)
                    throw new InvalidOperationException("侧对侧转换旧基架模板被修改：" + binding.Instance);
                binding.RequireActual(found.Instance, found.Path, found.Configuration);
            }
        }
    }
}
