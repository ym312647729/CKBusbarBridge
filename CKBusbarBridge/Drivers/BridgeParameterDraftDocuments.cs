using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using CKBusbarBridge.Prototype;
using CKBusbarBridge.DesignPreview;
using CKBusbarBridgePreview;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    // Plugin instance owns the registry; each entry owns its assembly, worker and GL lifetime.
    internal sealed class BridgeParameterDraftDocuments : IBridgeParameterDraftLookup, IDisposable
    {
        private static readonly ConditionalWeakTable<ISldWorks, BridgeParameterDraftDocuments> Instances = new ConditionalWeakTable<ISldWorks, BridgeParameterDraftDocuments>();
        private readonly ISldWorks _application;
        private readonly List<BridgeParameterDraftDocument> _documents = new List<BridgeParameterDraftDocument>();
        private readonly DSldWorksEvents_Event _events;
        private bool _disposing;
        private BridgeParameterDraftDocuments(ISldWorks application)
        {
            _application = application; BridgeParameterDraftAccess.Register(application, this);
            _events = application as DSldWorksEvents_Event;
            if (_events != null) _events.FileOpenPostNotify += Opened;
        }
        internal static BridgeParameterDraftDocuments For(ISldWorks application)
        {
            var owner = Instances.GetValue(application, source => new BridgeParameterDraftDocuments(source));
            if (owner._disposing) throw new InvalidOperationException("旧预览仍在释放所属视口资源，请等待视口关闭后重新打开插件。");
            return owner;
        }
        internal static void Release(ISldWorks application)
        { if (!Instances.TryGetValue(application, out var owner)) return; owner.Dispose(); if (owner._documents.Count == 0) Instances.Remove(application); }
        public BridgeDesignSession Find(IModelDoc2 model) => Get(model)?.Session;
        internal BridgeParameterDraftDocument Active => Get(_application.IActiveDoc2);
        internal BridgeParameterDraftDocument Get(IModelDoc2 model)
        {
            if (model == null || !(model is AssemblyDoc)) return null;
            var existing = _documents.FirstOrDefault(document => document.IsBound && ReferenceEquals(document.Document, model));
            if (existing != null) return existing;
            var marker = BridgeParameterDraftDocument.ReadProperty(model, BridgeParameterDraftDocument.IdentityProperty);
            if (string.IsNullOrWhiteSpace(marker))
                marker = BridgeParameterDraftDocument.ReadProperty(model, BridgeParameterDraftDocument.LegacyIdentityProperty);
            if (string.IsNullOrWhiteSpace(marker)) return null;
            RequireEmptyAssembly(model);
            var restored = BridgeParameterDraftDocument.Restore(_application, model, marker);
            Own(restored); restored.Recompute(); return restored;
        }
        private void Own(BridgeParameterDraftDocument document)
        {
            _documents.Add(document);
            document.Closed += () =>
            {
                _documents.Remove(document);
                if (_disposing && _documents.Count == 0) Instances.Remove(_application);
            };
        }
        internal BridgeParameterDraftDocument Begin(object parameters, string scenario, string orientation, string drawingMetadata,
            BridgeParameterDraftDocument expectedDraft = null, bool resetScenario = false, bool confirmDocument = false)
        {
            var activeModel = _application.IActiveDoc2;
            var existing = Get(activeModel);
            if (expectedDraft != null && !ReferenceEquals(expectedDraft, existing))
            {
                if (!confirmDocument || activeModel is AssemblyDoc)
                    throw new InvalidOperationException("请先激活当前柜体设计对应的总装。");
                existing = OpenCabinetDraftForConfirmation(expectedDraft);
                if (existing != null && expectedDraft.Session.Scenario == scenario &&
                    (existing.Session.Scenario != scenario || existing.Orientation != orientation))
                    resetScenario = true;
            }
            if (resetScenario && expectedDraft == null && existing != null)
                throw new InvalidOperationException("请先激活当前柜体设计对应的总装。");
            if (existing == null)
            {
                var session = new BridgeDesignSession(scenario, Capture(parameters));
                existing = CreateTemplateDraft(session, orientation, drawingMetadata);
                Own(existing);
            }
            else if (resetScenario)
            {
                existing.ResetCabinetScenario(parameters, scenario, orientation, drawingMetadata);
            }
            if (existing.Session.Scenario != scenario) throw new InvalidOperationException("当前预览草稿属于另一场景，请先切回目标草稿装配体。");
            existing.UpdateContext(orientation, drawingMetadata); existing.BindParameters(parameters); existing.Recompute();
            return existing;
        }
        private BridgeParameterDraftDocument OpenCabinetDraftForConfirmation(BridgeParameterDraftDocument expectedDraft)
        {
            if (expectedDraft.Session.Generating || expectedDraft.Session.RecoveryBlocked)
                throw new InvalidOperationException("柜体草稿处于生成或恢复状态，不能重新创建总装。");

            for (var openModel = _application.GetFirstDocument() as IModelDoc2; openModel != null; openModel = openModel.GetNext() as IModelDoc2)
            {
                if (!ReferenceEquals(openModel, expectedDraft.Document)) continue;
                ActivateAssembly(_application, openModel);
                return Get(openModel) ?? throw new InvalidOperationException("已打开的柜体草稿不能恢复。");
            }

            var savedPath = expectedDraft.LastSavedPath;
            if (string.IsNullOrWhiteSpace(savedPath)) return null;
            if (!File.Exists(savedPath)) throw new IOException("原柜体草稿文件已找不到：" + savedPath);
            var openErrors = 0; var openWarnings = 0;
            var reopenedModel = _application.GetOpenDocumentByName(savedPath) as IModelDoc2 ??
                _application.OpenDoc6(savedPath, (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, string.Empty, ref openErrors, ref openWarnings) as IModelDoc2;
            if (reopenedModel == null || openErrors != 0 || !BridgeGenerationFileBackup.Same(reopenedModel.GetPathName(), savedPath))
                throw new IOException("无法打开原柜体草稿：" + savedPath + "；错误=" + openErrors + "；警告=" + openWarnings);
            var reopenedDraft = Get(reopenedModel);
            if (reopenedDraft == null || reopenedDraft.Session.Id != expectedDraft.Session.Id)
                throw new InvalidOperationException("原草稿路径的总装身份已改变：" + savedPath);
            ActivateAssembly(_application, reopenedModel);
            return reopenedDraft;
        }
        private BridgeParameterDraftDocument CreateTemplateDraft(BridgeDesignSession session, string orientation, string drawingMetadata)
        {
            var bundledTemplate = Path.Combine(Path.GetDirectoryName(typeof(BridgeParameterDraftDocuments).Assembly.Location),
                "PreviewAssets", "EmptyAssembly.asmdot");
            var attempts = new List<string>(2);
            var root = TryCreateEmptyAssembly(bundledTemplate, "随插件的模板", attempts);
            if (root == null)
            {
                var defaultTemplate = _application.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
                if (!string.Equals(defaultTemplate, bundledTemplate, StringComparison.OrdinalIgnoreCase))
                    root = TryCreateEmptyAssembly(defaultTemplate, "当前 SolidWorks 的默认模板", attempts);
            }
            if (root == null)
                throw new InvalidOperationException("SolidWorks 无法创建柜体草稿（版本 " + _application.RevisionNumber() + "）。" +
                    System.Environment.NewLine + string.Join(System.Environment.NewLine, attempts));
            try
            {
                ActivateAssembly(_application, root);
                return new BridgeParameterDraftDocument(_application, root, session, orientation, drawingMetadata);
            }
            catch
            {
                try { _application.CloseDoc(root.GetTitle()); }
                catch (Exception closeError) { FaceToWallPreviewTrace.Error("关闭未初始化的柜体草稿失败", closeError); }
                throw;
            }
        }
        private IModelDoc2 TryCreateEmptyAssembly(string template, string source, ICollection<string> attempts)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                attempts.Add(source + "未设置。");
                return null;
            }
            if (!File.Exists(template))
            {
                attempts.Add(source + "文件不存在：" + template);
                return null;
            }
            var root = _application.NewDocument(template, 0, 0d, 0d) as IModelDoc2;
            if (root == null)
            {
                attempts.Add(source + "文件存在，但新建接口未返回装配体：" + template);
                return null;
            }
            if (root is AssemblyDoc assembly && ((assembly.GetComponents(false) as object[])?.Length ?? 0) == 0)
                return root;

            _application.CloseDoc(root.GetTitle());
            attempts.Add(source + "不是空总装：" + template);
            return null;
        }
        internal static void RequireEmptyAssembly(IModelDoc2 document)
        {
            if (!(document is AssemblyDoc assembly) || assembly.GetComponentCount(false) != 0)
                throw new InvalidOperationException("本项目参数草稿必须使用空总装，不能写入已安装实体的装配体。");
        }
        internal static void ActivateAssembly(ISldWorks application, IModelDoc2 assembly)
        {
            if (ReferenceEquals(application.IActiveDoc2, assembly)) return;
            var errors = 0;
            var path = assembly.GetPathName();
            var name = string.IsNullOrWhiteSpace(path) ? assembly.GetTitle() : Path.GetFileName(path);
            application.ActivateDoc3(name, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
            if (!ReferenceEquals(application.IActiveDoc2, assembly))
                throw new InvalidOperationException("SolidWorks 未能激活柜体草稿总装：" + name + "；状态=" + errors);
        }
        internal static JObject Capture(object parameters)
            => BridgeParameterSnapshotBinding.Capture(parameters);
        private int Opened(string fileName)
        {
            // The file event only queues work; all document access stays on the original SW dispatcher.
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { Get(_application.IActiveDoc2); }
                catch (Exception error) { FaceToWallPreviewTrace.Error("母线桥参数草稿重开失败", error); UiFeedback.Error(error.Message, "草稿恢复失败"); }
            })); return 0;
        }
        public void Dispose()
        {
            if (_disposing) return;
            _disposing = true;
            if (_events != null) _events.FileOpenPostNotify -= Opened;
            BridgeParameterDraftAccess.Unregister(_application);
            foreach (var document in _documents.ToArray())
            { document.Dispose(); if (document.ResourcesReleased) _documents.Remove(document); }
            if (_documents.Count == 0) Instances.Remove(_application);
        }
    }

    internal sealed class BridgeParameterDraftDocument : IBridgeDesignPreviewSession
    {
        internal const string IdentityProperty = "母线桥项目参数草稿身份";
        // 读取旧文档的属性键；新文档统一写入与客户无关的名称。
        internal const string LegacyIdentityProperty = "\u5DDD\u5F00母线桥参数草稿身份";
        private readonly ISldWorks _application;
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private readonly BridgeGeometryRuntime _runtime = new BridgeGeometryRuntime();
        private readonly BridgeBusinessDimensionEditor _dimensionEditor = new BridgeBusinessDimensionEditor();
        private BridgeOpenGlViewport _viewport;
        private PreviewViewBinding _viewBinding;
        private DAssemblyDocEvents_Event _documentEvents;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        private BridgeParameterSnapshotBinding _parameterBinding;
        private object _parameters;
        private bool _computing, _queued, _disposed, _suspended, _installing, _detached, _drawFailed;
        private bool _parametersDirty;
        private string _lastInspectionWarning;
        private bool _recommendRequested;
        private bool _viewFitted;
        private readonly List<TaskCompletionSource<bool>> _readyWaiters = new List<TaskCompletionSource<bool>>();
        private BusbarBridgeDesignWindowVM _editor;
        private BridgeUpstreamSnapshot _previousUpstream;
        private string _drawingMetadata, _orientation;
        private string _recipe;
        private JObject _bridgeGeometry;
        private JObject _logicalUpstream;
        private JObject _selectionState;
        private bool _restoringSelection;
        private float[] _lastViewOrientation;
        private System.Windows.Rect? _lastViewArea;
        // The view state produced by the last fit. An identical request on an untouched view is skipped.
        private ViewportBounds? _lastFitBounds;
        private float[] _lastFitOrientation;
        private System.Windows.Rect _lastFitArea;
        private double _lastFitScale;
        private double[] _lastFitTranslation;
        private long _candidateVersion;
        private BridgePreviewDimension[] _cabinetDimensions = Array.Empty<BridgePreviewDimension>();
        private string _lastSavedPath;
        private JObject _confirmedRecord;
        private string _confirmedScenario;
        private bool _savingSettings;
        private bool _callbacksReleased;
        internal bool ResourcesReleased => _callbacksReleased;
        internal IModelDoc2 Document { get; private set; }
        internal BridgeDesignSession Session { get; }
        internal event Action Closed;
        internal event EventHandler ParametersChanged;
        public event EventHandler Invalidated;
        public bool Enabled => !_disposed && !_suspended;
        public bool IsBound => !_disposed;
        internal bool Busy => _computing || _queued || !Session.IsReady;
        internal string Orientation => _orientation;
        internal string LastSavedPath => _lastSavedPath;
        internal BridgeParameterDraftDocument(ISldWorks application, IModelDoc2 document, BridgeDesignSession session, string orientation, string drawingMetadata)
        {
            _application = application; Document = document; Session = session; _orientation = orientation; _drawingMetadata = drawingMetadata;
            Session.DrawingMetadata = drawingMetadata; Session.CabinetOrientation = orientation;
            Session.RuntimePackageVersion = BridgeGeometryRuntime.PackageVersion;
            AttachDocument(document);
            _timer.Tick += TimerTick; Session.Changed += SessionChanged;
        }
        private void AttachDocument(IModelDoc2 document)
        {
            Document = document; _detached = false;
            _lastViewOrientation = null; _lastViewArea = null; _lastFitBounds = null;
            var view = document.ActiveView as IModelView ?? throw new InvalidOperationException("空装配体没有有效视口。");
            _viewport = new BridgeOpenGlViewport(new IntPtr(view.GetViewHWndx64())); _viewport.EditRequested += EditDimension;
            _viewport.BindEditor(_editor);
            _viewport.IdleFrameRequested += RedrawIdle;
            _viewport.ViewOrientationSettled += ViewOrientationSettled;
            _documentEvents = (DAssemblyDocEvents_Event)(AssemblyDoc)document;
            _viewBinding = new PreviewViewBinding(this, _viewport, (DModelViewEvents_Event)view, _documentEvents);
            _documentEvents.FileSaveNotify += Saving; _documentEvents.FileSavePostNotify += Saved;
            _documentEvents.ActiveConfigChangePostNotify += ConfigurationChanged;
            ((DSldWorksEvents_Event)_application).CommandCloseNotify += ViewCommandClosed;
        }
        internal void SaveRecipe()
        {
            _savingSettings = true;
            try { SaveCabinetInputs(); Persist(Document.GetPathName()); }
            finally { _savingSettings = false; }
        }
        internal async Task ConfirmCabinetInputs(bool requireReady = false)
        {
            _dispatcher.VerifyAccess();
            // Preview can start before drawing information is entered; only confirmed data may be written.
            CabinetParameterPropertyCodec.DrawingInputs(_drawingMetadata);
            if (!ReferenceEquals(_application.IActiveDoc2, Document))
                throw new InvalidOperationException("请先激活当前柜体设计对应的总装。");
            if (requireReady)
            {
                if (Busy || !Session.IsReady) throw new InvalidOperationException("草稿仍在计算，请等待最新预览完成后再生成。");
                SaveConfirmedCabinetInputs();
                return;
            }
            await EnsureReady().ConfigureAwait(false);
            await _dispatcher.InvokeAsync(SaveConfirmedCabinetInputs).Task.ConfigureAwait(false);
        }
        private void SaveConfirmedCabinetInputs()
        {
            _dispatcher.VerifyAccess();
            if (!Session.IsReady) throw new InvalidOperationException("预览修订已变化，请等待重新计算后再保存。");
            if (!ReferenceEquals(_application.IActiveDoc2, Document))
                throw new InvalidOperationException("活动总装已切换，柜体参数未保存。");
            BridgeParameterDraftDocuments.RequireEmptyAssembly(Document);
            var path = Document.GetPathName();
            var firstSave = string.IsNullOrWhiteSpace(path);
            if (firstSave)
            {
                var directory = (string)Session.Parameters()["ProjectPath"];
                if (!Directory.Exists(directory)) throw new InvalidOperationException("请先设置有效的项目根目录。");
                path = CabinetDraftSavePath.ForDrawing(directory, _drawingMetadata);
                if (File.Exists(path) || _application.GetOpenDocumentByName(path) != null)
                    throw new IOException("图号对应的总装文件已存在，请打开该草稿或修改图号：" + path);
            }
            _savingSettings = true;
            try
            {
                SaveCabinetInputs();
                var errors = 0; var warnings = 0;
                var saveOptions = (int)swSaveAsOptions_e.swSaveAsOptions_Silent;
                var saved = firstSave
                    ? Document.Extension.SaveAs(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, saveOptions, null, ref errors, ref warnings)
                    : Document.Save3(saveOptions, ref errors, ref warnings);
                if (!saved || errors != 0 || !BridgeGenerationFileBackup.Same(Document.GetPathName(), path) || !File.Exists(path))
                    throw new IOException("柜体草稿保存失败：" + path + "；错误=" + errors + "；警告=" + warnings);
                Persist(path);
                BridgeParameterDraftDocuments.ActivateAssembly(_application, Document);
            }
            finally { _savingSettings = false; }
        }
        internal void RestoreCabinetInputs(JObject parameters, string orientation, string drawingMetadata)
        {
            UpdateContext(orientation, drawingMetadata);
            if (_parameters == null) BindParameters(BridgeCabinetParameterBinding.Restore(Session.Scenario, parameters));
            else { JsonConvert.PopulateObject(parameters.ToString(Formatting.None), _parameters); BindParameters(_parameters); }
            Recompute();
        }
        internal void ResetCabinetScenario(object parameters, string scenario, string orientation, string drawingMetadata)
        {
            if (_disposed || _detached || _editor != null || Session.Generating || Session.RecoveryBlocked)
                throw new InvalidOperationException("当前草稿无法切换场景，请先结束母线桥设计或生成。");
            var snapshot = BridgeParameterDraftDocuments.Capture(parameters);
            _timer.Stop();
            _recipe = null; _bridgeGeometry = null; _logicalUpstream = null; _previousUpstream = null; _selectionState = null;
            _recommendRequested = false; _parametersDirty = true; _viewFitted = false; _lastFitBounds = null;
            _cabinetDimensions = Array.Empty<BridgePreviewDimension>();
            _candidateVersion++;
            _orientation = orientation; _drawingMetadata = drawingMetadata;
            _viewport.Clear();
            Session.ResetCabinetScenario(scenario, snapshot, orientation, drawingMetadata);
        }
        internal Action CaptureSettings()
        {
            var openingScenario = Session.Scenario;
            var openingInputs = Session.Parameters(); var openingOrientation = _orientation; var openingMetadata = _drawingMetadata;
            var openingRecipe = _recipe; var openingGeometry = _bridgeGeometry; var openingLogical = _logicalUpstream;
            var openingUpstream = _previousUpstream; var openingSelection = _selectionState;
            return () =>
            {
                if (_disposed) return;
                _timer.Stop(); _installing = true;
                try
                {
                    if (openingScenario != Session.Scenario || openingOrientation != _orientation)
                    {
                        var originalParameters = BridgeCabinetParameterBinding.Restore(openingScenario, openingInputs);
                        ResetCabinetScenario(originalParameters, openingScenario, openingOrientation, openingMetadata);
                        BindParameters(originalParameters);
                    }
                    else
                    {
                        UpdateContext(openingOrientation, openingMetadata);
                        if (_parameters == null) BindParameters(BridgeCabinetParameterBinding.Restore(openingScenario, openingInputs));
                        else { JsonConvert.PopulateObject(openingInputs.ToString(Formatting.None), _parameters); BindParameters(_parameters); }
                    }
                    _recipe = openingRecipe; _bridgeGeometry = openingGeometry; _logicalUpstream = openingLogical;
                    _previousUpstream = openingUpstream; _selectionState = openingSelection;
                    if (_editor != null)
                    {
                        if (openingRecipe != null && openingUpstream != null) _editor.Editor.Load(new BridgeGenerationRecipeCodec().RestoreStored(openingRecipe, openingUpstream));
                        else _editor.Editor.Load(null);
                    }
                    Session.UpdateDraft(_editor?.Editor.Revision ?? 0, _recipe); _parametersDirty = true;
                }
                finally { _installing = false; _timer.Start(); }
            };
        }
        internal static string ReadProperty(IModelDoc2 document, string name)
        { document.Extension.CustomPropertyManager[string.Empty].Get4(name, false, out var raw, out var resolved); return string.IsNullOrWhiteSpace(resolved) ? raw : resolved; }
        internal static void WriteProperty(IModelDoc2 document, string name, string value)
        {
            var manager = document.Extension.CustomPropertyManager[string.Empty];
            var names = (manager.GetNames() as object[] ?? Array.Empty<object>()).OfType<string>();
            manager.Get4(name, false, out var currentRaw, out var currentResolved);
            if (names.Contains(name, StringComparer.Ordinal) && currentRaw == value) return;
            var status = document.Extension.CustomPropertyManager[string.Empty].Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            document.Extension.CustomPropertyManager[string.Empty].Get4(name, false, out var raw, out var resolved);
            if (raw != value) throw new InvalidOperationException("参数草稿属性写入未通过回读：" + name + "；状态=" + status);
        }
        internal static CabinetPropertySnapshot ReadCabinetInputs(IModelDoc2 document)
        {
            var manager = document.Extension.CustomPropertyManager[string.Empty];
            var names = new HashSet<string>((manager.GetNames() as object[] ?? Array.Empty<object>()).OfType<string>(), StringComparer.Ordinal);
            return CabinetParameterPropertyCodec.Decode(name =>
            {
                if (!names.Contains(name)) return null;
                manager.Get4(name, false, out var raw, out var resolved); return raw;
            });
        }
        internal void SaveCabinetInputs()
        {
            if (!_savingSettings && !Session.RecoveryBlocked) return;
            var values = CabinetParameterPropertyCodec.Encode(Session.Scenario, Session.Parameters(), _orientation, _drawingMetadata);
            WriteProperty(Document, IdentityProperty, Session.Id.ToString("D"));
            var changed = false;
            var manager = Document.Extension.CustomPropertyManager[string.Empty];
            var names = new HashSet<string>((manager.GetNames() as object[] ?? Array.Empty<object>()).OfType<string>(), StringComparer.Ordinal);
            foreach (var value in values.OrderBy(pair => pair.Key == CabinetParameterPropertyCodec.VersionProperty ? 1 : 0))
            {
                manager.Get4(value.Key, false, out var raw, out var resolved);
                if (names.Contains(value.Key) && raw == value.Value) continue;
                WriteProperty(Document, value.Key, value.Value); changed = true;
            }
            ReadCabinetInputs(Document);
            IEnumerable<string> obsoleteProperties = CabinetParameterPropertyCodec.OptionalPersonnelProperties;
            if (_confirmedScenario != null && _confirmedScenario != Session.Scenario)
                obsoleteProperties = obsoleteProperties.Concat(CabinetParameterPropertyCodec.Fields(_confirmedScenario).Select(field => (string)field["name"]));
            var obsoleteNames = obsoleteProperties.Where(name => names.Contains(name) && !values.ContainsKey(name)).Distinct(StringComparer.Ordinal).ToArray();
            if (obsoleteNames.Length > 0)
            {
                foreach (var name in obsoleteNames) manager.Delete2(name);
                var remaining = (manager.GetNames() as object[] ?? Array.Empty<object>()).OfType<string>();
                var undeleted = obsoleteNames.Intersect(remaining, StringComparer.Ordinal).ToArray();
                if (undeleted.Length > 0) throw new InvalidOperationException("未能清除柜体草稿属性：" + string.Join("、", undeleted));
                changed = true;
            }
            _confirmedScenario = Session.Scenario;
            if (changed) Document.SetSaveFlag();
        }
        private static string Sidecar(string path, Guid id) => Path.Combine(Path.GetDirectoryName(path), ".ck-preview-" + id.ToString("N") + ".json");
        internal static BridgeParameterDraftDocument Restore(ISldWorks app, IModelDoc2 document, string identity)
        {
            if (!Guid.TryParse(identity, out var id)) throw new InvalidOperationException("参数草稿身份属性无效。");
            var inputs = ReadCabinetInputs(document);
            var session = new BridgeDesignSession(inputs.Scenario, inputs.Parameters, id);
            var restored = new BridgeParameterDraftDocument(app, document, session, inputs.Orientation, inputs.DrawingMetadata);
            restored._parametersDirty = true; restored._confirmedScenario = inputs.Scenario;
            if (string.IsNullOrWhiteSpace(document.GetPathName())) return restored;
            var path = Sidecar(document.GetPathName(), id);
            if (!File.Exists(path)) { restored._lastSavedPath = document.GetPathName(); return restored; }
            try
            {
            var state = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            if ((string)state["sessionId"] != id.ToString("D")) throw new InvalidOperationException("总装与草稿会话身份不匹配。");
            restored._confirmedRecord = (JObject)state.DeepClone();
            // Sidecars retain human bridge edits only. Cabinet inputs and all geometry are recomputed.
            restored._recipe = (string)state["recipe"];
            restored._logicalUpstream = state["logicalUpstream"] as JObject;
            restored._selectionState = state["selection"] as JObject;
            if (!string.IsNullOrWhiteSpace(restored._recipe))
            {
                if (restored._logicalUpstream == null) throw new InvalidOperationException("保存的桥体草稿缺少参数接口基线，不能推测恢复。");
                restored._previousUpstream = BridgeOfflineUpstreamAdapter.Read(restored._logicalUpstream, document.GetPathName(),
                    document.ConfigurationManager.ActiveConfiguration.Name, (string)restored.Session.Parameters().SelectToken("General.BusbarMaterial"));
            }
            }
            catch (Exception error)
            {
                restored._recipe = null; restored._logicalUpstream = null; restored._previousUpstream = null;
                FaceToWallPreviewTrace.Error("桥体边车恢复失败，柜体仍按总装输入属性重算，原边车保留", error);
                UiFeedback.Show("柜体已按总装属性恢复。桥体人工修改未能恢复，将重新推荐方案：" + error.Message, "桥体草稿恢复", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            restored._lastSavedPath = document.GetPathName(); return restored;
        }
        internal void BindParameters(object parameters)
        {
            _parameterBinding?.Dispose(); _parameters = parameters;
            _parameterBinding = new BridgeParameterSnapshotBinding(parameters); _parameterBinding.Changed += ParameterChanged;
            var revision = Session.Revision; Session.UpdateParameters(BridgeParameterDraftDocuments.Capture(parameters));
            _parametersDirty |= Session.Revision != revision;
        }
        internal void UpdateContext(string orientation, string drawingMetadata)
        {
            if (_orientation == orientation && _drawingMetadata == drawingMetadata) return;
            _parametersDirty |= _orientation != orientation; _orientation = orientation; _drawingMetadata = drawingMetadata;
            Session.CabinetOrientation = orientation; Session.DrawingMetadata = drawingMetadata; Session.Invalidate();
        }
        private void ParameterChanged(JObject parameters)
        {
            if (_disposed || Session.Generating) return;
            var revision = Session.Revision; Session.UpdateParameters(parameters);
            if (Session.Revision == revision) return;
            _parametersDirty = true;
            _timer.Stop(); _timer.Start();
        }
        private void SessionChanged(object sender, EventArgs args)
        {
            _viewport.IsStale = !Session.IsReady;
            if (_editor != null) _editor.NativePreviewBusy = Session.Failure == null;
            if (!_detached) _viewport.RequestRedraw();
        }
        internal Task EnsureReady(bool recommend = false)
        {
            if (recommend) { _recommendRequested = true; _parametersDirty = true; Session.Invalidate(); }
            if (Session.IsReady) return Task.CompletedTask;
            if (_disposed || Session.RecoveryBlocked) return Task.FromException(new InvalidOperationException(Session.Failure ?? "参数草稿已关闭。"));
            var completion = new TaskCompletionSource<bool>();
            _readyWaiters.Add(completion);
            if (!_computing) { _timer.Stop(); Session.Invalidate(); Recompute(); }
            return completion.Task;
        }
        private void TimerTick(object sender, EventArgs args) { _timer.Stop(); Recompute(); }
        private int ConfigurationChanged()
        {
            _dispatcher.BeginInvoke(new Action(() => { if (_disposed || _detached) return;
                if (Session.Generating) return;
                Session.Invalidate(); _parametersDirty = true; _timer.Stop(); _timer.Start(); }));
            return 0;
        }
        internal async void Recompute()
        {
            _dispatcher.VerifyAccess();
            if (_disposed || Session.Generating || Session.RecoveryBlocked) return;
            if (_computing) { _queued = true; return; }
            _computing = true; var requestedRevision = Session.Revision; var requestedId = Session.Id;
            BridgeMeshFrame cabinetOnlyFrame = null;
            try
            {
                while (true)
                {
                    var calculation = await _dispatcher.InvokeAsync(() =>
                    {
                        if (_disposed) return null;
                        if ((((AssemblyDoc)Document).GetComponents(false) as object[] ?? Array.Empty<object>()).Length != 0)
                            throw new InvalidOperationException("参数预览总装中出现真实组件，请移除后再继续草稿计算。");
                        _queued = false; var revision = Session.Revision; var id = Session.Id;
                        requestedRevision = revision; requestedId = id;
                        cabinetOnlyFrame = null;
                        var request = new JObject { ["sessionId"] = id.ToString("D"), ["revision"] = revision, ["scenario"] = Session.Scenario,
                            ["orientation"] = _orientation, ["modelName"] = Document.GetTitle(), ["parameters"] = Session.Parameters(), ["bridge"] = _parametersDirty ? null : _bridgeGeometry,
                            ["settings"] = new JObject { ["arcSegments"] = _viewport.Appearance.CircleSegments, ["complexArcSegments"] = _viewport.Appearance.CircleSegments,
                                ["chordToleranceMm"] = _viewport.Appearance.ChordToleranceMm, ["objectTriangleBudget"] = _viewport.Appearance.ObjectTriangleBudget,
                                ["sceneTriangleBudget"] = _viewport.Appearance.SceneTriangleBudget, ["targetFps"] = _viewport.Appearance.TargetFramesPerSecond,
                                ["interactionFrameBudgetMs"] = _viewport.Appearance.InteractionFrameBudgetMs } };
                        return _runtime.Compute(request);
                    }).Task.ConfigureAwait(false);
                    if (calculation == null) break;
                    var response = await calculation.ConfigureAwait(false);
                    var repeat = await _dispatcher.InvokeAsync(() =>
                    {
                        var id = requestedId; var revision = requestedRevision;
                        if (_disposed || id != Session.Id || revision != Session.Revision) { _queued = !_disposed; return _queued && !_disposed; }
                        var hangerRoof = response["upstream"]?["hangerRoof"]?.DeepClone();
                        response["upstream"] = SinglePrecision.Copy((JObject)response["upstream"]);
                        if (hangerRoof != null) response["upstream"]["hangerRoof"] = hangerRoof;
                        var logicalUpstream = (JObject)response["upstream"];
                        var material = (string)Session.Parameters().SelectToken("General.BusbarMaterial");
                        var upstream = !_parametersDirty && _previousUpstream != null ? _previousUpstream :
                            BridgeOfflineUpstreamAdapter.Read((JObject)response["upstream"], Document.GetPathName(), Document.ConfigurationManager.ActiveConfiguration.Name, material);
                        var frame = _runtime.CreateFrame(id, revision, response);
                        if (_parametersDirty && _editor?.Editor.CurrentSample != null && _previousUpstream != null)
                        {
                            if (_editor.HasPendingInput) { _timer.Start(); return false; }
                            cabinetOnlyFrame = frame;
                            var recommendation = _recommendRequested;
                            UpdateEditor(upstream, logicalUpstream); _parametersDirty = false; _recommendRequested = false;
                            _bridgeGeometry = ExportBridgeGeometry(_editor.Editor, upstream);
                            var updatedRecipe = new BridgeGenerationRecipeCodec().Encode(_editor.Editor);
                            _recipe = updatedRecipe;
                            _logicalUpstream = (JObject)response["upstream"]; _previousUpstream = upstream; Session.Invalidate(); _queued = true; return _queued && !_disposed;
                        }
                        if (_parametersDirty && _editor == null && !string.IsNullOrWhiteSpace(_recipe))
                        {
                            cabinetOnlyFrame = frame;
                            if (_previousUpstream == null) throw new InvalidOperationException("桥体草稿缺少原参数接口基线。");
                            var offlineEditor = new BusbarBridgeDraftEditor(); offlineEditor.Load(RestoreRecipe(_previousUpstream));
                            var planner = new BridgeCabinetPlanBuilder(authorization: CKBusbarBridge.Services.BusbarWorkerAuthorization.Resolve);
                            if (_recommendRequested) offlineEditor.Load(ReadHangerContext(planner.CreateDraftRecommendation(upstream, offlineEditor.CaptureSavedSample()), logicalUpstream));
                            else offlineEditor.RebaseParameterDraft(current => ReadHangerContext(planner.Create(_previousUpstream, upstream, current, null), logicalUpstream));
                            _recipe = new BridgeGenerationRecipeCodec().Encode(offlineEditor); _bridgeGeometry = ExportBridgeGeometry(offlineEditor, upstream);
                            _parametersDirty = false; _recommendRequested = false; _logicalUpstream = (JObject)response["upstream"]; _previousUpstream = upstream;
                            Session.UpdateDraft(offlineEditor.Revision, _recipe); Session.Invalidate(); _queued = true; return _queued && !_disposed;
                        }
                        _parametersDirty = false;
                        if (!Session.Install(id, revision, (JObject)response["project"], upstream, frame)) return _queued && !_disposed;
                        _cabinetDimensions = response["dimensions"].OfType<JObject>().Select(value => new BridgePreviewDimension(
                            (string)value["objectId"], (string)value["field"], (string)value["label"], SinglePrecision.Number(value["value"]), revision, (bool)value["editable"],
                            value["edges"].Select(edge => new BridgeDimensionEdge(edge.Select(point => new PreviewPoint3(SinglePrecision.Number(point[0]) / 1000f, SinglePrecision.Number(point[1]) / 1000f, SinglePrecision.Number(point[2]) / 1000f)))))).ToArray();
                        _viewport.Install(frame); _viewport.IsStale = false; SelectionChanged(this, EventArgs.Empty); _viewport.RequestRedraw();
                        if (!_viewFitted) { Fit(frame); _viewFitted = true; }
                        _logicalUpstream = (JObject)response["upstream"]; _previousUpstream = upstream; ParametersChanged?.Invoke(this, EventArgs.Empty);
                        if (_editor != null)
                        {
                            var inspection = (string)response["inspectionWarning"];
                            _editor.NativePreviewBusy = false; _editor.GenerationBlocked = !string.IsNullOrEmpty(inspection);
                            if (!string.IsNullOrEmpty(inspection) || _editor.Message == _lastInspectionWarning)
                                _editor.Message = inspection ?? string.Empty;
                            _lastInspectionWarning = inspection;
                        }
                        return _queued && !_disposed;
                    }).Task.ConfigureAwait(false);
                    if (!repeat) break;
                }
            }
            catch (Exception error)
            {
                if (_dispatcher.HasShutdownStarted) return;
                await _dispatcher.InvokeAsync(() =>
                {
                    if (_disposed) return;
                    if (requestedId != Session.Id || requestedRevision != Session.Revision) { _queued = true; return; }
                    // A/B base heights can be valid locally even when their new
                    // openings cannot share a horizontal bridge. Publish the
                    // new cabinets without reusing the old bridge mesh.
                    var cabinetsRefreshed = Session.RetainCabinetPreview(cabinetOnlyFrame);
                    if (cabinetsRefreshed)
                    {
                        _parametersDirty = true;
                        _cabinetDimensions = Array.Empty<BridgePreviewDimension>();
                        _viewport.Install(cabinetOnlyFrame);
                        _viewport.Dimensions(_cabinetDimensions);
                    }
                    Session.Reject(requestedId, requestedRevision, error);
                    FaceToWallPreviewTrace.Error("母线桥离线参数预览失败", error);
                    if (_editor != null) { _editor.GenerationBlocked = true;
                        _editor.Message = (cabinetsRefreshed ? "柜体和基座已更新，桥体待重算：" : "参数预览未完成：") + error.Message; }
                }).Task.ConfigureAwait(false);
            }
            finally
            {
                if (!_dispatcher.HasShutdownStarted) await _dispatcher.InvokeAsync(() =>
                {
                    _computing = false;
                    if (_queued && !_disposed) { _timer.Stop(); _timer.Start(); }
                    if (_editor != null && !_timer.IsEnabled) _editor.NativePreviewBusy = false;
                    if (!_queued && !_timer.IsEnabled)
                    {
                        var waiters = _readyWaiters.ToArray(); _readyWaiters.Clear();
                        foreach (var waiter in waiters)
                            if (Session.IsReady) waiter.TrySetResult(true);
                            else waiter.TrySetException(new InvalidOperationException(Session.Failure ?? "参数草稿尚未完成，请重读。"));
                    }
                }).Task.ConfigureAwait(false);
            }
        }
        internal void Attach(BusbarBridgeDesignWindowVM editor)
        {
            if (_editor != null) Detach(_editor); _editor = editor;
            _previousUpstream = Session.Upstream; editor.PlanChanged += DraftChanged; editor.ViewChanged += SelectionChanged;
            _viewport.BindEditor(editor);
            editor.FitRequested += FitAllRequested; editor.FitSelectionRequested += FitSelectionRequested;
            RefreshDimensions();
        }
        internal void Detach(BusbarBridgeDesignWindowVM editor)
        {
            if (!ReferenceEquals(editor, _editor)) return;
            if (editor.Editor.CurrentSample != null) _selectionState = new JObject { ["object"] = editor.Editor.SelectedId,
                ["reference"] = editor.SelectedViewportReference?.Id, ["copperSection"] = editor.SelectedCopperSection?.Id,
                ["hanger"] = editor.SelectedHangerId, ["frames"] = new JArray(editor.SelectedFrameIds) };
            editor.PlanChanged -= DraftChanged; editor.ViewChanged -= SelectionChanged; _editor = null;
            _viewport.BindEditor(null);
            editor.FitRequested -= FitAllRequested; editor.FitSelectionRequested -= FitSelectionRequested;
            ClearBridgeSelection();
        }
        private void UpdateEditor(BridgeUpstreamSnapshot upstream, JObject logicalUpstream)
        {
            if (_editor.HasPendingInput) { _queued = true; _timer.Start(); return; }
            _installing = true;
            try
            {
                var recommend = _recommendRequested;
                var planner = new BridgeCabinetPlanBuilder(authorization: CKBusbarBridge.Services.BusbarWorkerAuthorization.Resolve);
                _editor.ApplyParameterDraft(upstream, current => ReadHangerContext(recommend ? planner.CreateDraftRecommendation(upstream, current) :
                    planner.Create(_previousUpstream, upstream, current, null), logicalUpstream), recommend);
            }
            finally { _installing = false; }
        }
        internal BridgeDesignSample ReadHangerContext(BridgeDesignSample sample, JObject logicalUpstream = null)
        {
            if (sample == null) return null;
            var context = (logicalUpstream ?? _logicalUpstream)?["hangerRoof"];
            if (context?["faces"] == null)
                return sample.WithHangerRoof(null, (string)context?["problem"] ?? "尚未读取草稿横梁平面。");
            var faces = context["faces"].Select(face => new BridgeHangerRoofFace(face["point"].Values<double>().ToArray(), face["normal"].Values<double>().ToArray()));
            return sample.WithHangerRoof(new BridgeHangerRoof((string)context["identity"], faces), null);
        }
        private static JObject ExportBridgeGeometry(BusbarBridgeDraftEditor editor, BridgeUpstreamSnapshot upstream)
        {
            var source = editor.CurrentSample.Source;
            var pattern = AutoOpeningHoleDataModel.DataHelper.GetMatingFaceOpenHole(source.BusbarWidth, source.BusbarWidth, "水平",
                "铜排", "铜排", source.BusbarWidth, source.BusbarWidth);
            if (pattern == null) throw new InvalidOperationException("当前排宽缺少铜排搭接开孔工艺，请先配置开孔工艺库。");
            return BridgeParameterDraftGeometry.Export(editor, upstream, new BridgeCopperHolePattern { Location = pattern.HoleLocation,
                Style = pattern.HoleStyle, Diameter = (float)pattern.HoleD, SlotLength = (float)pattern.HoleY });
        }
        private void DraftChanged(object sender, EventArgs args)
        {
            if (_installing || _editor?.Editor.CurrentSample == null || _disposed || Session.Generating) return;
            var recipe = new BridgeGenerationRecipeCodec().Encode(_editor.Editor);
            var changed = !string.Equals(_recipe, recipe, StringComparison.Ordinal);
            if (!changed && _bridgeGeometry != null) return;
            var bridgeGeometry = ExportBridgeGeometry(_editor.Editor, Session.Upstream);
            _recipe = recipe; _bridgeGeometry = bridgeGeometry;
            Session.UpdateDraft(_editor.Editor.Revision, _recipe); _timer.Stop(); _timer.Start();
        }
        private void SelectionChanged(object sender, EventArgs args)
        {
            if (_disposed || _detached) return;
            if (_restoringSelection) return;
            if (_editor?.Editor.CurrentSample != null && _selectionState != null)
            {
                var savedSelection = _selectionState; _selectionState = null; _restoringSelection = true;
                try
                {
                    var sectionId = (string)savedSelection["copperSection"];
                    var frames = savedSelection["frames"]?.Values<string>().Where(id => _editor.Editor.Find(id)?.Kind == DesignObjectKind.Frame).ToArray() ?? Array.Empty<string>();
                    if ((string)savedSelection["reference"] is string referenceId && _editor.ViewportReferences.Any(reference => reference.Id == referenceId)) _editor.SelectViewportReference(referenceId);
                    else if (sectionId != null && _editor.Editor.FindCopperSection(sectionId) != null) _editor.SelectCopperSection(sectionId);
                    else if ((string)savedSelection["hanger"] is string hangerId && _editor.HangerRows.Any(row => row.Id == hangerId)) _editor.SelectedHangerId = hangerId;
                    else if (frames.Length > 0) _editor.SetFrameSelection(frames, (string)savedSelection["object"]);
                    else if (_editor.Editor.Find((string)savedSelection["object"]) != null) _editor.Select((string)savedSelection["object"]);
                }
                finally { _restoringSelection = false; }
            }
            var dimensions = VisibleCabinetDimensions();
            if (_editor?.Editor.CurrentSample != null)
            {
                var factory = new BridgePreviewDimensionFactory(_editor.Editor.CurrentSample, _editor.Objects, _editor.Editor, _editor.Upstream);
                dimensions = dimensions.Concat(_editor.Fields.Where(field => BridgePreviewDimensionFactory.IsDimension(field.Field)).Select(factory.Create).Where(dimension => dimension != null));
            }
            _viewport.Dimensions(dimensions);
            _viewport.RequestRedraw();
        }
        private void EditDimension(BridgeDimensionEditRequest request)
        {
            try { EditDimensionCore(request); }
            catch (Exception error) { FaceToWallPreviewTrace.Error("母线桥预览尺寸编辑失败", error); UiFeedback.Error(error.Message, "尺寸编辑失败"); }
        }
        private void EditDimensionCore(BridgeDimensionEditRequest request)
        {
            if (_disposed || _detached || !Session.IsReady || request.Dimension.IsReadOnly) return;
            if (request.Dimension.ParameterPath != null)
            {
                if (request.Dimension.Revision != Session.ReadyRevision || _editor?.HasPendingInput == true) return;
                if (_parameters == null) BindParameters(BridgeCabinetParameterBinding.Restore(Session.Scenario, Session.Parameters()));
                var expectedRevision = Session.Revision;
                var openingInputs = Session.Parameters();
                Func<string, Task<string>> apply = async text =>
                {
                    if (expectedRevision != Session.Revision) return "参数已改变，请重新双击当前尺寸。";
                    var before = Session.Parameters();
                    var error = BridgeCabinetParameterBinding.Commit(_parameters, request.Dimension.ParameterPath, text);
                    if (error != null) return error;
                    expectedRevision = Session.Revision;
                    try { await EnsureReady(); expectedRevision = Session.Revision; return null; }
                    catch (Exception failure)
                    {
                        RestoreCabinetInputs(before, _orientation, _drawingMetadata);
                        await EnsureReady(); expectedRevision = Session.Revision;
                        return failure.Message;
                    }
                };
                _dimensionEditor.Show(request.Dimension.Label, request.Dimension.Value, request.ScreenX, request.ScreenY, _viewport.WindowHandle,
                    value => expectedRevision != Session.Revision ? "参数已改变，请重新双击当前尺寸。" : BridgeCabinetParameterBinding.Validate(_parameters, request.Dimension.ParameterPath, value),
                    apply, () => RestoreCabinetInputs(openingInputs, _orientation, _drawingMetadata));
                return;
            }
            if (_editor == null || _editor.HasPendingInput || _editor.Editor.Revision != request.Dimension.Revision) return;
            var field = _editor.Fields.SingleOrDefault(row => row.ObjectId == request.Dimension.ObjectId && row.Field == request.Dimension.Field && !row.IsReadOnly);
            if (field == null) return;
            var revision = _editor.Editor.Revision;
            var restoreOpening = _editor.Editor.CaptureDraftCheckpoint();
            Func<string, Task<string>> applyField = async text =>
            {
                if (_editor.Editor.Revision != revision) return "草稿已改变，请重新双击当前尺寸。";
                var restorePrevious = _editor.Editor.CaptureDraftCheckpoint();
                try
                {
                    restoreOpening();
                    var currentField = _editor.Fields.SingleOrDefault(row => row.ObjectId == request.Dimension.ObjectId && row.Field == request.Dimension.Field && !row.IsReadOnly);
                    if (currentField == null) throw new InvalidOperationException("当前参数已不可编辑，请重新选择。");
                    // Reuse the parameter table's setter, business validation, linked
                    // planning and error recovery, including the wall async path.
                    currentField.Text = text; currentField.Commit();
                    if (_editor.PlanningBusy) await _editor.PlanningCompletion;
                    if (!string.IsNullOrEmpty(currentField.Error)) throw new InvalidOperationException(currentField.Error);
                    await EnsureReady(); revision = _editor.Editor.Revision;
                    return null;
                }
                catch (Exception failure)
                {
                    FaceToWallPreviewTrace.Error("视口尺寸沿原参数入口修改失败", failure);
                    restorePrevious(); revision = _editor.Editor.Revision;
                    await EnsureReady(); revision = _editor.Editor.Revision;
                    return failure.Message;
                }
            };
            _dimensionEditor.Show(field.Label, request.Dimension.Value, request.ScreenX, request.ScreenY, _viewport.WindowHandle,
                value => _editor.Editor.Revision != revision ? "草稿已改变，请重新双击当前尺寸。" : null,
                applyField, restoreOpening);
        }
        private int Draw(PreviewViewBinding binding)
        {
            try
            {
                var result = binding.Viewport.Draw(binding.DisplayStyle());
                if (binding.Viewport.IsDisposed && !binding.ReleaseQueued)
                {
                    binding.ReleaseQueued = true;
                    _dispatcher.BeginInvoke(new Action(() =>
                    {
                        binding.Detach();
                        if (ReferenceEquals(binding, _viewBinding) && _disposed) ReleaseCallbacks();
                    }));
                }
                return result;
            }
            catch (Exception error) { if (!_drawFailed) { _drawFailed = true; _dispatcher.BeginInvoke(new Action(() => { FaceToWallPreviewTrace.Error("母线桥OpenGL绘制失败", error); Invalidated?.Invoke(this, EventArgs.Empty); })); } return 0; }
        }
        private void RedrawIdle() { if (!_disposed && !_detached) Document.GraphicsRedraw2(); }
        private int Saving(string fileName)
        {
            try { Persist(fileName); return 0; }
            catch (Exception error) { FaceToWallPreviewTrace.Error("参数草稿保存失败", error); _dispatcher.BeginInvoke(new Action(() => UiFeedback.Error(error.Message, "草稿保存失败"))); return 1; }
        }
        private int Saved(int saveType, string fileName) { return Saving(Document.GetPathName()); }
        private void Persist(string path)
        {
            if (!_savingSettings && !Session.RecoveryBlocked)
            {
                // Ctrl+S / SaveAs may save other SW edits, but cannot accept settings still open in either design window.
                if (_confirmedRecord != null && !string.IsNullOrWhiteSpace(path)) WriteRecord(path, (JObject)_confirmedRecord.DeepClone());
                return;
            }
            // Recovery after replacing the blank assembly owns only the sidecar:
            // Document can already be closed, while the active document is a partly generated model.
            if (!_detached)
            {
                if (Session.Failure == null && Session.ReadyRevision == Session.Revision || Session.RecoveryBlocked) SaveCabinetInputs();
                else ReadCabinetInputs(Document);
            }
            if (string.IsNullOrWhiteSpace(path)) return;
            var state = Session.SaveState(path, _recipe); state["orientation"] = _orientation; state["drawingMetadata"] = _drawingMetadata; state["bridgeGeometry"] = _bridgeGeometry; state["logicalUpstream"] = _logicalUpstream; state["parametersDirty"] = _parametersDirty;
            state["selection"] = _editor?.Editor.CurrentSample == null ? _selectionState : new JObject {
                ["object"] = _editor.Editor.SelectedId, ["reference"] = _editor.SelectedViewportReference?.Id, ["copperSection"] = _editor.SelectedCopperSection?.Id,
                ["hanger"] = _editor.SelectedHangerId, ["frames"] = new JArray(_editor.SelectedFrameIds) };
            WriteRecord(path, state);
            _confirmedRecord = (JObject)state.DeepClone();
        }
        private void WriteRecord(string path, JObject state)
        {
            state["assemblyPath"] = path;
            var destination = Sidecar(path, Session.Id); var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, state.ToString(Formatting.None), new UTF8Encoding(false));
            if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
            _lastSavedPath = path;
        }
        private int Destroyed(PreviewViewBinding binding, int destroyType)
        {
            if (destroyType == (int)swDestroyNotifyType_e.swDestroyNotifyDestroy)
            {
                var destroyedBinding = binding;
                var detached = binding.DetachedForGeneration;
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    destroyedBinding.Viewport.ContextDestroyed(); destroyedBinding.Detach(); destroyedBinding.DetachDestroy();
                    if (!detached) { Dispose(); ReleaseCallbacks(); }
                }));
            }
            return 0;
        }
        internal BridgeDesignSample RestoreRecipe(BridgeUpstreamSnapshot upstream)
            => string.IsNullOrWhiteSpace(_recipe) ? null : ReadHangerContext(new BridgeGenerationRecipeCodec().RestoreStored(_recipe, upstream));
        public void Bind(BridgeUpstreamSnapshot upstream) { if (!ReferenceEquals(upstream, Session.Upstream)) throw new InvalidOperationException("草稿修订与当前预览不一致。"); }
        public void Clear() { if (_detached) return; _viewport.Clear(); Document.GraphicsRedraw2(); }
        internal void ClearBridgeSelection() { if (_detached || _disposed) return; _viewport.Dimensions(VisibleCabinetDimensions()); Document.GraphicsRedraw2(); }
        // Cabinet design shows every cabinet/base dimension; bridge design shows them only for the selected cabinet or base.
        private int _cabinetDesignWindows;
        internal void CabinetDesignOpened() { _cabinetDesignWindows++; RefreshDimensions(); }
        internal void CabinetDesignClosed() { if (_cabinetDesignWindows > 0) _cabinetDesignWindows--; RefreshDimensions(); }
        private void RefreshDimensions() { if (!_detached && !_disposed) SelectionChanged(this, EventArgs.Empty); }
        private IEnumerable<BridgePreviewDimension> VisibleCabinetDimensions()
            => BridgeCabinetDimensionVisibility.Visible(_cabinetDimensions, _editor != null && _cabinetDesignWindows == 0 && !_editor.ShowCabinetDimensions,
                new[] { _editor?.SelectedViewportReference?.Id, _editor?.Editor.SelectedId });

        public void Refresh(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, long revision, Func<bool> isCurrent, bool activate)
        {
            if (_detached) return;
            if (!Session.IsReady || !isCurrent()) return;
            _suspended = false; _viewport.Install(Session.Frame); SelectionChanged(this, EventArgs.Empty); Document.GraphicsRedraw2();
            if (activate) Fit(Session.Frame);
        }
        private float[] CurrentViewOrientation()
        {
            var view = Document.ActiveView as IModelView ?? throw new InvalidOperationException("当前总装没有可用视图。");
            var values = view.Orientation3.ArrayData as double[] ?? throw new InvalidOperationException("无法读取当前视图朝向。");
            return BridgeViewFraming.OrientationMatrix(values.Take(9).Select(value => (float)value).ToArray());
        }
        private void Fit(BridgeMeshFrame frame) => FitBounds(new ViewportBounds {
            Minimum = new ViewportVector(frame.Minimum[0], frame.Minimum[1], frame.Minimum[2]),
            Maximum = new ViewportVector(frame.Maximum[0], frame.Maximum[1], frame.Maximum[2]) });
        private static bool SameBounds(ViewportBounds a, ViewportBounds b)
            => new[] { a.Minimum.X - b.Minimum.X, a.Minimum.Y - b.Minimum.Y, a.Minimum.Z - b.Minimum.Z,
                a.Maximum.X - b.Maximum.X, a.Maximum.Y - b.Maximum.Y, a.Maximum.Z - b.Maximum.Z }.All(delta => Math.Abs(delta) <= 1.0f);
        private void FitBounds(ViewportBounds bounds)
        {
            var orientation = CurrentViewOrientation();
            var view = (IModelView)Document.ActiveView;
            var available = BridgeViewportArea.ClientArea(new IntPtr(view.GetViewHWndx64()));
            _lastViewOrientation = orientation; _lastViewArea = available;
            // The event observer records changes before calling Fit. Compare against the last actual fit instead.
            if (_lastFitBounds.HasValue && SameBounds(_lastFitBounds.Value, bounds) &&
                _lastFitOrientation.SequenceEqual(orientation) && _lastFitArea == available && view.Scale2 == _lastFitScale &&
                ((double[])view.Translation3.ArrayData).SequenceEqual(_lastFitTranslation)) return;
            _lastFitBounds = null;
            // Apply the seed zoom, scale and translation as one visible change.
            view.EnableGraphicsUpdate = false;
            try
            {
                FitBoundsCore(view, bounds, orientation, available);
                _lastFitOrientation = orientation; _lastFitArea = available;
                _lastFitScale = view.Scale2; _lastFitTranslation = (double[])view.Translation3.ArrayData;
                _lastFitBounds = bounds;
            }
            finally { view.EnableGraphicsUpdate = true; Document.GraphicsRedraw2(); }
        }
        private void FitBoundsCore(IModelView view, ViewportBounds bounds, float[] orientation, System.Windows.Rect available)
        {
            var points = BridgeViewFraming.ZoomRectangle(bounds, orientation);
            Document.ViewZoomTo2(points[0].X / 1000f, points[0].Y / 1000f, points[0].Z / 1000f,
                points[1].X / 1000f, points[1].Y / 1000f, points[1].Z / 1000f);
            // Verify the actual SW screen transform after the seed zoom. Orientation3 alone does
            // not describe the current view scale/translation. Centre the whole preview in the SW
            // viewport; the bridge window may be on another monitor and is not avoided.
            var utility = (IMathUtility)_application.GetMathUtility();
            Func<ViewportVector, IMathPoint> modelPoint = point => (IMathPoint)utility.CreatePoint(new[] {
                (double)(point.X / 1000f), (double)(point.Y / 1000f), (double)(point.Z / 1000f) });
            Func<System.Windows.Rect> projectedBounds = () =>
            {
                var screen = System.Windows.Rect.Empty; var transform = view.Transform;
                foreach (var x in new[] { bounds.Minimum.X, bounds.Maximum.X })
                    foreach (var y in new[] { bounds.Minimum.Y, bounds.Maximum.Y })
                        foreach (var z in new[] { bounds.Minimum.Z, bounds.Maximum.Z })
                        {
                            var pixel = (double[])((IMathPoint)modelPoint(new ViewportVector(x, y, z)).MultiplyTransform(transform)).ArrayData;
                            screen.Union(new System.Windows.Point((float)pixel[0], (float)pixel[1]));
                        }
                return screen;
            };
            view.Scale2 = (float)view.Scale2 * BridgeViewportArea.FitScale(projectedBounds(), available);
            var projected = projectedBounds();
            var dx = (float)(available.Left + available.Width / 2 - projected.Left - projected.Width / 2);
            var dy = (float)(available.Top + available.Height / 2 - projected.Top - projected.Height / 2);
            // Convert a pixel displacement back through SW's own transform. Then rotate and
            // scale into Translation3's view axes; no guessed row/column or pixel/metre ratio.
            var inverse = view.Transform.Inverse();
            var origin = (double[])((IMathPoint)((IMathPoint)utility.CreatePoint(new[] { 0d, 0d, 0d })).MultiplyTransform(inverse)).ArrayData;
            var shifted = (double[])((IMathPoint)((IMathPoint)utility.CreatePoint(new[] { (double)dx, (double)dy, 0d })).MultiplyTransform(inverse)).ArrayData;
            var delta = (IMathVector)utility.CreateVector(Enumerable.Range(0, 3).Select(index => (double)((float)shifted[index] - (float)origin[index])).ToArray());
            var rotated = (double[])((IMathVector)delta.MultiplyTransform(view.Orientation3)).ArrayData;
            var translation = (double[])view.Translation3.ArrayData;
            view.Translation3 = (MathVector)utility.CreateVector(Enumerable.Range(0, 3).Select(index =>
                (double)((float)translation[index] + (float)rotated[index] * (float)view.Scale2)).ToArray());
        }
        private void ViewOrientationSettled()
        {
            if (_disposed || _detached || _viewport.DisplayFrame == null) return;
            try
            {
                if (!ReferenceEquals(_application.IActiveDoc2, Document)) return;
                var orientation = CurrentViewOrientation(); var previous = _lastViewOrientation; _lastViewOrientation = orientation;
                var view = (IModelView)Document.ActiveView;
                var area = BridgeViewportArea.ClientArea(new IntPtr(view.GetViewHWndx64()));
                var areaChanged = _lastViewArea.HasValue && _lastViewArea.Value != area;
                _lastViewArea = area;
                // Refit after a docked pane moves/folds, or a standard-view shortcut.
                // Ordinary rotation, pan and zoom retain their own framing.
                if (areaChanged || previous != null && !previous.SequenceEqual(orientation) && BridgeViewFraming.IsStandardDirection(orientation))
                    Fit(_viewport.DisplayFrame);
            }
            catch (Exception error) { FaceToWallPreviewTrace.Error("标准视图变化后的预览适配失败", error); }
        }
        private void FitAllRequested(object sender, EventArgs args) => ShowView(BridgeDraftView.All);
        internal async Task PreviewScheme(BridgeDesignSample sample)
        {
            _dispatcher.VerifyAccess();
            if (!Session.IsReady || _editor == null || _editor.HasPendingInput) throw new InvalidOperationException("请等待草稿计算并提交输入后选择方案。");
            var version = ++_candidateVersion; var revision = Session.Revision; var editor = new BusbarBridgeDraftEditor(); editor.Load(sample);
            _viewport.IsPreviewOnly = true; _viewport.IsStale = true; _viewport.Dimensions(Array.Empty<BridgePreviewDimension>()); _viewport.RequestRedraw();
            _editor.NativePreviewBusy = true;
            try
            {
                var request = new JObject { ["sessionId"] = Session.Id.ToString("D"), ["revision"] = revision, ["scenario"] = Session.Scenario,
                    ["orientation"] = _orientation, ["modelName"] = Document.GetTitle(), ["parameters"] = Session.Parameters(),
                    ["bridge"] = ExportBridgeGeometry(editor, Session.Upstream),
                    ["settings"] = new JObject { ["arcSegments"] = _viewport.Appearance.CircleSegments, ["complexArcSegments"] = _viewport.Appearance.CircleSegments,
                        ["chordToleranceMm"] = _viewport.Appearance.ChordToleranceMm, ["objectTriangleBudget"] = _viewport.Appearance.ObjectTriangleBudget,
                        ["sceneTriangleBudget"] = _viewport.Appearance.SceneTriangleBudget, ["targetFps"] = _viewport.Appearance.TargetFramesPerSecond,
                        ["interactionFrameBudgetMs"] = _viewport.Appearance.InteractionFrameBudgetMs } };
                var response = await _runtime.Compute(request).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || version != _candidateVersion || revision != Session.Revision) return;
                    _viewport.Install(_runtime.CreateFrame(Session.Id, revision, response)); _viewport.IsStale = false; Fit(_viewport.DisplayFrame); _viewport.RequestRedraw();
                }).Task.ConfigureAwait(false);
            }
            finally
            {
                if (!_dispatcher.HasShutdownStarted) await _dispatcher.InvokeAsync(() =>
                {
                    if (!_disposed && version == _candidateVersion)
                    { _viewport.IsStale = !Session.IsReady; if (_editor != null) _editor.NativePreviewBusy = false; }
                }).Task.ConfigureAwait(false);
            }
        }
        internal void RestorePreview()
        {
            ++_candidateVersion;
            if (_disposed || _detached || Session.RecoveryBlocked) return;
            _suspended = false;
            _viewport.IsPreviewOnly = false; _viewport.IsStale = !Session.IsReady;
            if (Session.Frame != null) _viewport.Install(Session.Frame);
            if (_editor != null) _editor.NativePreviewBusy = false;
            SelectionChanged(this, EventArgs.Empty);
        }
        private void FitSelectionRequested(object sender, EventArgs args) => ShowView(BridgeDraftView.Selected);
        internal void ShowView(BridgeDraftView view)
        {
            if (_disposed || _detached || Session.Frame == null) return;
            if (view == BridgeDraftView.Top) Document.ShowNamedView2("", (int)swStandardViews_e.swTopView);
            else if (view == BridgeDraftView.Front) Document.ShowNamedView2("", (int)swStandardViews_e.swFrontView);
            else if (view == BridgeDraftView.Isometric) Document.ShowNamedView2("", (int)swStandardViews_e.swIsometricView);
            var selected = view == BridgeDraftView.Selected ? _viewport.SelectionBounds() : null;
            if (selected.HasValue)
            {
                var bounds = selected.Value;
                // A handle/issue can have zero thickness; a display-only margin keeps it visible.
                var minimum = bounds.Minimum - new ViewportVector(20f, 20f, 20f); var maximum = bounds.Maximum + new ViewportVector(20f, 20f, 20f);
                FitBounds(new ViewportBounds { Minimum = minimum, Maximum = maximum });
            }
            else Fit(_viewport.DisplayFrame ?? Session.Frame);
            _viewport.RequestRedraw();
        }
        private int ViewCommandClosed(int command, int reason)
        {
            // Native command ids are supplied by the installed SW interop contract.
            var viewCommand = (SolidWorks.Interop.swcommands.swCommands_e)command;
            if (viewCommand != SolidWorks.Interop.swcommands.swCommands_e.swCommands_Front &&
                viewCommand != SolidWorks.Interop.swcommands.swCommands_e.swCommands_Back &&
                viewCommand != SolidWorks.Interop.swcommands.swCommands_e.swCommands_Top &&
                viewCommand != SolidWorks.Interop.swcommands.swCommands_e.swCommands_Bottom &&
                viewCommand != SolidWorks.Interop.swcommands.swCommands_e.swCommands_Left &&
                viewCommand != SolidWorks.Interop.swcommands.swCommands_e.swCommands_Right &&
                viewCommand != SolidWorks.Interop.swcommands.swCommands_e.swCommands_Isometric &&
                viewCommand != SolidWorks.Interop.swcommands.swCommands_e.swCommands_Trimetric &&
                viewCommand != SolidWorks.Interop.swcommands.swCommands_e.swCommands_Dimetric) return 0;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_disposed || _detached || Session.Frame == null) return;
                try { if (ReferenceEquals(_application.IActiveDoc2, Document)) Fit(_viewport.DisplayFrame ?? Session.Frame); }
                catch (Exception error) { FaceToWallPreviewTrace.Error("参数草稿标准视图适配失败", error); }
            }));
            return 0;
        }
        public void Dispose()
        {
            if (_disposed) return;
            if (Session.Generating) throw new InvalidOperationException("正式生成结束前不能释放草稿会话。");
            _disposed = true; _timer.Stop(); _timer.Tick -= TimerTick;
            foreach (var waiter in _readyWaiters) waiter.TrySetCanceled();
            _readyWaiters.Clear();
            _dimensionEditor.Dispose();
            if (_editor != null) Detach(_editor);
            _parameterBinding?.Dispose();
            Session.Changed -= SessionChanged; _runtime.Dispose(); _viewport.EditRequested -= EditDimension; _viewport.Dispose();
            _viewport.IdleFrameRequested -= RedrawIdle;
            // Keep the callback until context-owned lists have actually been released.
            if (_viewport.IsDisposed) ReleaseCallbacks();
            else if (!_detached) Document.GraphicsRedraw2();
            _documentEvents.FileSaveNotify -= Saving; _documentEvents.FileSavePostNotify -= Saved;
            _documentEvents.ActiveConfigChangePostNotify -= ConfigurationChanged;
            ((DSldWorksEvents_Event)_application).CommandCloseNotify -= ViewCommandClosed;
        }
        private void ReleaseCallbacks()
        {
            if (_callbacksReleased || !_viewport.IsDisposed) return;
            _viewBinding.Detach(); _viewBinding.DetachDestroy();
            _callbacksReleased = true; Closed?.Invoke();
        }
        private sealed class PreviewViewBinding
        {
            private readonly BridgeParameterDraftDocument _owner;
            private readonly DModelViewEvents_Event _events;
            private readonly DAssemblyDocEvents_Event _documentEvents;
            private bool _detached, _destroyDetached;
            internal BridgeOpenGlViewport Viewport { get; }
            internal bool ReleaseQueued { get; set; }
            internal bool DetachedForGeneration { get; set; }
            private BridgeViewportDisplayStyle _lastDisplayStyle = BridgeViewportDisplayStyle.ShadedWithEdges;
            internal BridgeViewportDisplayStyle DisplayStyle()
            {
                if (_owner._disposed || DetachedForGeneration) return _lastDisplayStyle;
                // IModelView uses swViewDisplayMode_e, not the drawing-view swDisplayMode_e.
                switch ((swViewDisplayMode_e)((IModelView)_events).DisplayMode)
                {
                    case swViewDisplayMode_e.swViewDisplayMode_Wireframe: return _lastDisplayStyle = BridgeViewportDisplayStyle.Wireframe;
                    case swViewDisplayMode_e.swViewDisplayMode_HiddenLinesRemoved: return _lastDisplayStyle = BridgeViewportDisplayStyle.HiddenLinesRemoved;
                    case swViewDisplayMode_e.swViewDisplayMode_HiddenLinesGrayed: return _lastDisplayStyle = BridgeViewportDisplayStyle.HiddenLinesVisible;
                    case swViewDisplayMode_e.swViewDisplayMode_ShadedWithEdges: return _lastDisplayStyle = BridgeViewportDisplayStyle.ShadedWithEdges;
                    default: return _lastDisplayStyle = BridgeViewportDisplayStyle.Shaded;
                }
            }
            internal PreviewViewBinding(BridgeParameterDraftDocument owner, BridgeOpenGlViewport viewport, DModelViewEvents_Event events, DAssemblyDocEvents_Event documentEvents)
            { _owner = owner; Viewport = viewport; _events = events; _documentEvents = documentEvents; _events.BufferSwapNotify += Draw; _events.ViewChangeNotify += ViewChanged; _documentEvents.DestroyNotify2 += Destroyed; }
            private int Draw() => _owner.Draw(this);
            private int ViewChanged(object transform) { Viewport.NotifyViewOrientationChanged(); return 0; }
            private int Destroyed(int destroyType) => _owner.Destroyed(this, destroyType);
            internal void Detach() { if (_detached) return; _detached = true; _events.BufferSwapNotify -= Draw; _events.ViewChangeNotify -= ViewChanged; }
            internal void DetachDestroy() { if (_destroyDetached) return; _destroyDetached = true; _documentEvents.DestroyNotify2 -= Destroyed; }
        }
    }
}
