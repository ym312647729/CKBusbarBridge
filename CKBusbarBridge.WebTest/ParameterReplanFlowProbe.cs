using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CKBusbarBridge.Prototype;
using CKBusbarBridge.views;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.WebTest
{
    internal static class ParameterReplanFlowProbe
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static int Run()
        {
            if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            CheckPrompt(false); CheckPrompt(true);
            var host = new DraftHost();
            var window = new FrameDriverWindow((IBridgeDesignHost)host);
            try
            {
                window.Show(); Pump();
                if (host.ReadRequests.Count == 0 || host.ReadRequests.Any(replan => replan))
                    throw new InvalidOperationException("首次读取不应覆盖当前已保存方案。");
                var replanButton = Walk(window).OfType<Button>().Single(button => Equals(button.Content, "重新规划"));
                replanButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                if (host.ReadRequests.Last() != true)
                    throw new InvalidOperationException("方案为空时，按钮没有明确请求全新规划。");
            }
            finally { window.CloseForAddinShutdown(); Pump(); }
            Console.WriteLine("重规划交互通过：确认与取消、过期结果、单次提示，以及空方案时按钮强制全新规划；未调用SolidWorks。");
            return 0;
        }
        private static void CheckPrompt(bool accepted)
        {
            var prompts = 0;
            using (var model = new BusbarBridgeDesignWindowVM(new PrototypeBusbarBridgeDesignProvider(), new BusbarBridgeDraftEditor(), message =>
            {
                prompts++;
                if (!message.Contains("重新规划母排和基架") || !message.Contains("清除旧人工位置"))
                    throw new InvalidOperationException("重规划确认没有说明作用范围。");
                return accepted;
            }))
            {
                var session = new BridgeDesignSession("face-to-face", new JObject());
                var documentType = Assembly.LoadFrom(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CKBusbarBridge.dll"))
                    .GetType("CKBusbarBridge.Drivers.BridgeParameterDraftDocument", true);
                var document = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(documentType);
                documentType.GetField("<Session>k__BackingField", Members).SetValue(document, session);
                documentType.GetField("_editor", Members).SetValue(document, model);
                documentType.GetField("_offerCabinetReplan", Members).SetValue(document, true);
                var ask = documentType.GetMethod("TryRequestCabinetReplan", Members);
                if ((bool)ask.Invoke(document, new object[] { "干涉", session.Id, session.Revision + 1 }) || prompts != 0)
                    throw new InvalidOperationException("过期计算结果触发了确认。");
                var openingRevision = session.Revision;
                var result = (bool)ask.Invoke(document, new object[] { "干涉", session.Id, session.Revision });
                if (result != accepted || prompts != 1 ||
                    (bool)documentType.GetField("_recommendRequested", Members).GetValue(document) != accepted ||
                    (accepted ? session.Revision <= openingRevision : session.Revision != openingRevision))
                    throw new InvalidOperationException("确认或取消没有保留正确的参数及重规划状态。");
                ask.Invoke(document, new object[] { "干涉", session.Id, session.Revision });
                if (prompts != 1) throw new InvalidOperationException("同一轮输入出现了重复确认。");
            }
        }
        private static IEnumerable<DependencyObject> Walk(DependencyObject root)
        {
            yield return root;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                foreach (var child in Walk(VisualTreeHelper.GetChild(root, index))) yield return child;
        }
        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        private sealed class DraftHost : IBridgeDesignHost, IBridgeParameterDraftHost
        {
            internal readonly List<bool> ReadRequests = new List<bool>();
            public IntPtr OwnerHandle => IntPtr.Zero;
            public bool HasParameterDraft => true;
            public IBridgeUpstreamReader UpstreamReader { get; } = new EmptyReader();
            public event EventHandler ParameterDraftChanged { add { } remove { } }
            public Task EnsureParameterDraftReady(bool recommend = false) { ReadRequests.Add(recommend); return Task.CompletedTask; }
            public string ResolveWorkerAuthorization() => null;
            public IBridgeDesignPreviewSession CreatePreviewSession() => new EmptyPreview();
            public BridgeDesignSample RestoreSavedDraft(BridgeUpstreamSnapshot upstream, BridgeDesignSample fresh) => null;
            public BridgeDesignSample ReadHangerContext(BridgeUpstreamSnapshot upstream, BridgeDesignSample sample) => sample;
            public BridgeDesignSample PlanRepair(BridgeUpstreamSnapshot upstream, BridgeDesignSample actual) => actual;
            public BridgeWallPlanningRestore DecodeWallPlanning(string recipe) => null;
            public void ValidateWallSpanGeneration(BridgeUpstreamSnapshot upstream, BridgeDesignSample candidate) { }
            public void ShowDraftView(BridgeDraftView view) { }
            public Task PreviewDraftScheme(BridgeDesignSample sample) => Task.CompletedTask;
            public void RestoreDraftPreview() { }
            public void AttachDraftEditor(BusbarBridgeDesignWindowVM viewModel) { }
            public void DetachDraftEditor(BusbarBridgeDesignWindowVM viewModel) { }
            public Task ConfirmDraftSettings(bool requireReady = false) => Task.CompletedTask;
            public void DiscardDraftSettings() { }
        }
        private sealed class EmptyReader : IBridgeUpstreamReader
        { public BridgeUpstreamSnapshot Read() => new BridgeUpstreamSnapshot(null, null, null, null, Array.Empty<BridgeUpstreamField>()); }
        private sealed class EmptyPreview : IBridgeDesignPreviewSession
        {
            public bool Enabled => true;
            public bool IsBound => false;
            public event EventHandler Invalidated { add { } remove { } }
            public void Bind(BridgeUpstreamSnapshot upstream) { }
            public void Clear() { }
            public void Refresh(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, long revision, Func<bool> isCurrent, bool activate) { }
            public void Dispose() { }
        }
    }
}
