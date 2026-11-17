using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;

namespace CKBusbarBridge.Prototype
{
    /// <summary>原型命令不调用旧命令的活动窗口/宿主恢复逻辑；直接调用也检查状态。</summary>
    internal sealed class BridgeDesignCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;
        public BridgeDesignCommand(Action execute, Func<bool> canExecute) { _execute = execute; _canExecute = canExecute; }
        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
        public bool CanExecute(object parameter) => _canExecute();
        public void Execute(object parameter) { if (CanExecute(parameter)) _execute(); }
    }

    public sealed class BridgeDesignFieldVM : NotificationObject
    {
        private string _text;
        private string _error;
        private readonly Action<BridgeDesignFieldVM> _submit;
        private readonly Action _textChanged;
        public string ObjectId { get; }
        public DesignField Field { get; }
        public string Label { get; }
        public string RecommendedText { get; private set; }
        public string CommittedText { get; private set; }
        public bool IsReadOnly { get; }
        public bool IsDisplayOnly { get; }
        internal long? SourceRevision { get; set; }
        internal string ReferenceObjectId { get; set; }
        public bool IsChoice => Field == DesignField.Installation && !IsReadOnly;
        public bool IsFixedInstallation => Field == DesignField.Installation && IsReadOnly;
        public ICommand RemoveInsulatorCommand { get; internal set; }
        public bool HasInsulatorRemoveAction => RemoveInsulatorCommand != null;
        public IReadOnlyList<string> Choices { get; } = Array.AsReadOnly(new[] { "正装", "反装" });
        public bool IsDirty => Text != CommittedText;
        public string Text
        {
            get => _text;
            set { if (_text == value) return; _text = value; Error = null; RaisePropertyChanged(nameof(Text)); RaisePropertyChanged(nameof(IsDirty)); _textChanged(); }
        }
        public string Error { get => _error; private set { _error = value; RaisePropertyChanged(nameof(Error)); } }
        internal bool CanReuse(BridgeDesignFieldVM field) => ObjectId == field.ObjectId && Field == field.Field &&
            Label == field.Label && IsReadOnly == field.IsReadOnly && HasInsulatorRemoveAction == field.HasInsulatorRemoveAction;
        internal void RefreshFrom(BridgeDesignFieldVM field)
        {
            var staleInput = SourceRevision.HasValue && SourceRevision != field.SourceRevision && IsDirty;
            SourceRevision = field.SourceRevision; ReferenceObjectId = field.ReferenceObjectId;
            var textChanged = _text != field.Text;
            var recommendationChanged = RecommendedText != field.RecommendedText;
            var wasDirty = IsDirty;
            _text = field.Text; CommittedText = field.CommittedText; RecommendedText = field.RecommendedText;
            if (textChanged) RaisePropertyChanged(nameof(Text));
            if (recommendationChanged) RaisePropertyChanged(nameof(RecommendedText));
            if (wasDirty != IsDirty) RaisePropertyChanged(nameof(IsDirty));
            if (_error != null) Error = null;
            if (staleInput) Error = "草稿已变化，原输入已取消，请重新输入。";
        }
        public BridgeDesignFieldVM(BridgeDesignObject node, BridgeDesignObject recommended, DesignField field,
            Action<BridgeDesignFieldVM> submit, Action textChanged)
        {
            ObjectId = node.Id; Field = field; _submit = submit; _textChanged = textChanged;
            Label = LabelFor(field); IsReadOnly = !node.EditableFields.Contains(field);
            _text = CommittedText = FormatValue(node, field);
            RecommendedText = FormatValue(recommended, field);
        }
        public BridgeDesignFieldVM(BridgeDesignObject node, BridgeDesignObject recommended, DesignField field,
            Action<BridgeDesignFieldVM> submit, Action textChanged, string label, string currentText, string recommendedText, bool isReadOnly)
            : this(node, recommended, field, submit, textChanged)
        {
            Label = label; _text = CommittedText = currentText; RecommendedText = recommendedText; IsReadOnly = isReadOnly;
        }
        public BridgeDesignFieldVM(string objectId, DesignField field, Action<BridgeDesignFieldVM> submit,
            Action textChanged, string label, string currentText, string recommendedText, bool isReadOnly)
        {
            ObjectId = objectId; Field = field; _submit = submit; _textChanged = textChanged;
            Label = label; _text = CommittedText = currentText; RecommendedText = recommendedText; IsReadOnly = isReadOnly;
        }
        internal BridgeDesignFieldVM(string objectId, string label, string value)
            : this(objectId, default, null, null, label, value, "—", true) { IsDisplayOnly = true; }
        public void Commit() { if (IsDirty && !IsReadOnly) _submit(this); }
        public void Complete(BridgeEditResult result)
        {
            if (result.Success) CommittedText = Text;
            else
            {
                // 草稿事务已拒绝修改；恢复有效输入，避免待提交状态禁用整个编辑界面。
                _text = CommittedText;
                RaisePropertyChanged(nameof(Text));
            }
            Error = result.Success ? null : result.Message;
            RaisePropertyChanged(nameof(IsDirty));
            _textChanged();
        }
        public void Cancel() { Error = null; Text = CommittedText; }
        private static string FormatValue(BridgeDesignObject node, DesignField field)
        {
            if (field == DesignField.Installation) return node.IsReversed ? "反装" : "正装";
            // 数量是离散值；长度、宽度、高度、定位和角度等尺寸统一固定显示一位小数。
            return node.Value(field).ToString(field == DesignField.InsulatorCount ? "0" : "0.0", CultureInfo.InvariantCulture);
        }
        private string LabelFor(DesignField field)
        {
            switch (field)
            {
                case DesignField.X: return "X (mm)";
                case DesignField.Y: return "Y (mm)";
                case DesignField.Z: return "Z (mm)";
                case DesignField.Length: return "基架长度 (mm)";
                case DesignField.Width: return "基架宽度 (mm)";
                case DesignField.Height: return "基架高度 (mm)";
                case DesignField.Installation: return "安装方式";
                case DesignField.Direction: return "平面方向 (°)";
                case DesignField.InsulatorCount: return "绝缘子数量（套）";
                case DesignField.JointDistanceFromPrevious: return "距前断点距离 (mm)";
                case DesignField.PreviousSegmentLength: return "前段排长度 (mm)";
                case DesignField.NextSegmentLength: return "后段排长度 (mm)";
                case DesignField.CopperSegmentLength: return "段排长度（含搭接，mm）";
                case DesignField.BaseJointPenetrationDistance: return "断点伸入基座距离 (mm)";
                case DesignField.BaseJointEdgeDistance: return "距基座边距离 (mm)";
                case DesignField.WallEntryDistance: return "入墙距离 (mm)";
                case DesignField.WallVerticalClearance: return "上下墙距 (mm)";
                case DesignField.WallHorizontalClearance: return "左右墙距 (mm)";
                default: return field.ToString();
            }
        }
    }

    public sealed partial class BusbarBridgeDesignWindowVM : NotificationObject, IDisposable
    {
        private readonly PrototypeBusbarBridgeDesignProvider _provider;
        private readonly Func<string, bool> _confirm;
        private readonly IBridgeUpstreamReader _upstreamReader;
        public BridgeUpstreamSnapshot Upstream { get; private set; }
        public bool HasUpstreamReader => _upstreamReader != null;
        public bool IsModelPlannedPreview => Editor?.Sample?.Source.IsModelDerived == true;
        public string DataNotice => HasUpstreamReader
            ? IsModelPlannedPreview
                ? "上游信息：总装模型只读反读 · 预览：路径规划结果 · 尚未写入模型"
                : "上游信息：总装模型只读反读 · 预览：等待规划或独立演示 · 不改写模型"
            : "演示数据 · 未接正式算法 · 不读取、不改写当前模型";
        private string _message = "请选择并载入示例；本窗口不读取模型。";
        private string _loadState = "未载入示例";
        private bool _sameSource;
        private bool _sideView;
        private bool _showReferences = true;
        private bool _showFrames = true;
        private bool _showInsulators = true;
        private bool _showBusbars = true;
        private bool _showHandles;
        private string _phase = "全部";
        private int _selectedTab;
        private bool _isDragging;
        private bool _committingField;
        private string _selectedCopperSectionId;
        private readonly HashSet<string> _selectedFrameIds = new HashSet<string>();
        public IReadOnlyCollection<string> SelectedFrameIds => Array.AsReadOnly(_selectedFrameIds.OrderBy(id => Editor.Find(id)?.FramePositionIndex ?? 0).ToArray());
        public bool HasMultipleFrames => _selectedFrameIds.Count > 1;
        public string FrameSelectionSummary => "共 " + FrameObjects.Count() + " 节基架 · 已选 " + _selectedFrameIds.Count + " 节";
        public string FrameConnectionSummary => string.Join("\n", Editor.FrameConnectionIssues.Select(issue => issue.Message));
        public BusbarBridgeDraftEditor Editor { get; }
        public BridgeDesignParameters DesignParameters => Editor.Sample?.Parameters;
        public double? DisplayOverlapLengthMm => Upstream != null
            ? _provider.GetUpstreamOverlapLength(Upstream) : DesignParameters?.OverlapLengthMm;
        public IReadOnlyList<string> SampleNames => _provider.SampleNames;
        public string SelectedSample { get; set; } = "面对面基础";
        public IReadOnlyList<string> Phases { get; } = Array.AsReadOnly(new[] { "全部", "A", "B", "C" });
        public IReadOnlyList<BridgeDesignObject> Objects { get; private set; } = Array.Empty<BridgeDesignObject>();
        public IReadOnlyList<BridgeCopperSection> CopperSections => Editor.CopperSections;
        public BridgeCopperSection SelectedCopperSection => Editor.FindCopperSection(_selectedCopperSectionId);
        // 使用稳定ID联动列表，避免重算生成新段排对象后丢失选中状态。
        public string SelectedCopperSectionId
        {
            get => _selectedCopperSectionId;
            set { if (value != null && value != _selectedCopperSectionId) SelectCopperSection(value); }
        }
        public IEnumerable<BridgeDesignObject> JointObjects => Objects.Where(o => o.IsJointGroup && o.IsEditable &&
            Editor.JointRepresentative(o.Id)?.Id == o.Id).OrderBy(o => o.X);
        public string SelectedJointId
        {
            get => JointObjects.Any(o => o.Id == Editor.SelectedId) ? Editor.SelectedId : null;
            set { if (value != null && value != Editor.SelectedId) Select(value); }
        }
        public IEnumerable<BridgeDesignObject> RouteObjects => Objects.Where(o => o.Kind != DesignObjectKind.FixedRoutePoint && (o.Phase != null || o.Kind == DesignObjectKind.FixedEnd) &&
            (!o.IsGrouped || Editor.JointRepresentative(o.Id)?.Id == o.Id));
        public IEnumerable<BridgeDesignObject> FrameObjects => Objects.Where(o => o.Kind == DesignObjectKind.Frame)
            .OrderBy(o => o.FramePositionIndex);
        public ObservableCollection<BridgeDesignFieldVM> Fields { get; } = new ObservableCollection<BridgeDesignFieldVM>();
        public IReadOnlyList<BridgeDesignIssue> Issues { get; private set; } = Array.Empty<BridgeDesignIssue>();
        public string SourceSummary => HasUpstreamReader
            ? Upstream?.Summary ?? "等待从总装配体读取上游信息。"
            : Editor.Sample?.Source.Summary ?? "未载入示例。\n柜体/偏移/规格/相别均来自柜体驱动，只读。";
        public string SceneAndSpanSummary
        {
            get
            {
                var scenario = Upstream?.Scenario ?? Editor.Sample?.Source.Scenario ?? "未识别";
                var length = _provider.GetTargetFrameLength(Upstream);
                return "场景：" + scenario + "    母线桥跨度：" +
                    (length.HasValue ? length.Value.ToString("0.0", CultureInfo.InvariantCulture) + " mm"
                        : "未读取");
            }
        }
        public string PlanningNotice => IsModelPlannedPreview
            ? "首尾L排连接上游伸出排；A/B基座L排断点可调，断点以后截面为基准。断点最佳间距2000 mm，单根展开长度不超过3000 mm（含搭接）。仅需换相时在第一段规划换相，仅有偏移时在中间段规划偏移；两者均无时只规划普通连接排。基架及架内绝缘子后置规划。"
            : "当前为独立演示路径。普通段一头桥排头、一头直排头，L排除外；偏移过渡、方向及孔位仍待正式工程校验。";
        public string FrameSummary => "基架 " + Objects.Count(o => o.Kind == DesignObjectKind.Frame) + " 节 · 长度合计 " +
            Objects.Where(o => o.Kind == DesignObjectKind.Frame).Sum(o => o.Length).ToString("0.0", CultureInfo.InvariantCulture) +
            " mm";
        public string Status => (HasUpstreamReader ? (Upstream?.State ?? "上游待读取") + " · 模型场景：" + (Upstream?.Scenario ?? "未识别") + " | " : "") +
            (_loadState == "示例已载入" && Editor.HasChanges ? "存在人工调整" : _loadState) +
            " · " + (IsModelPlannedPreview ? "规划场景：" : "演示场景：") +
            (Editor.Sample?.Source.Scenario ?? "无") + " · 草稿版本 " + Editor.Revision + " · 工程可行性待联合校验";
        public string Message { get => _message; set { _message = value; RaisePropertyChanged(nameof(Message)); if (GenerationBlocked) NotifyGenerationAvailability(); } }
        public bool HasPendingInput => GenerationBusy || PlanningBusy || _isDragging || HasPendingHangerInput || Fields.Any(f => f.IsDirty);
        private bool _generationBusy;
        public bool GenerationBusy
        {
            get => _generationBusy;
            internal set { _generationBusy = value; RaisePropertyChanged(string.Empty); CommandManager.InvalidateRequerySuggested(); }
        }
        private bool _isDraftDocumentActive = true;
        internal bool IsDraftDocumentActive
        {
            get => _isDraftDocumentActive;
            set
            {
                if (_isDraftDocumentActive == value) return;
                _isDraftDocumentActive = value; RaisePropertyChanged(nameof(IsEditingEnabled));
                RaisePropertyChanged(nameof(CanPreview)); RaisePropertyChanged(nameof(CanNativePreview)); NotifyGenerationAvailability();
            }
        }
        public bool IsEditingEnabled => IsDraftDocumentActive && !GenerationBusy && !PlanningBusy;
        internal Action RepairModel { get; set; }
        internal Func<BridgeUpstreamSnapshot, BridgeDesignSample, BridgeDesignSample> RestoreGeneratedModel { get; set; }
        internal bool IsParameterDraftMode { get; set; }
        public bool HasUnsavedChanges => Editor.HasChanges || HasWallBoundaryChanges || GenerationBusy || _isDragging || HasPendingHangerInput || Fields.Any(f => f.IsDirty);
        public bool CanPreview => Objects.Count > 0 && !HasPendingInput;
        private bool _nativePreviewAvailable;
        public bool NativePreviewAvailable
        {
            get => _nativePreviewAvailable;
            set { _nativePreviewAvailable = value; RaisePropertyChanged(nameof(NativePreviewAvailable)); RaisePropertyChanged(nameof(CanNativePreview)); }
        }
        private bool _nativePreviewBusy;
        public bool NativePreviewBusy
        {
            get => _nativePreviewBusy;
            set { _nativePreviewBusy = value; RaisePropertyChanged(nameof(NativePreviewBusy)); RaisePropertyChanged(nameof(CanNativePreview)); NotifyGenerationAvailability(); }
        }
        public bool CanNativePreview => NativePreviewAvailable && !NativePreviewBusy && IsModelPlannedPreview && CanPreview;
        private string _nativePreviewStatus;
        public string NativePreviewStatus
        {
            get => _nativePreviewStatus;
            set { _nativePreviewStatus = value; RaisePropertyChanged(nameof(NativePreviewStatus)); }
        }
        public bool CanRestoreSelection => SelectedCopperSection == null && !HasMultipleFrames && Editor.CanRestore(Editor.SelectedId);
        public bool ShowFrameActions => !HasMultipleFrames && SelectedObject?.Kind == DesignObjectKind.Frame;
        public bool ShowInsulatorActions => !HasMultipleFrames && SelectedObject?.Kind == DesignObjectKind.Insulator;
        public bool ShowCopperSectionActions => SelectedCopperSection != null;
        public bool ShowBreakpointActions => SelectedCopperSection == null && SelectedObject?.IsJointGroup == true;
        public bool SameSource
        {
            get => _sameSource;
            set
            {
                if (HasPendingInput) { Message = "请先提交或按Esc取消当前输入，再切换作用范围。"; RaisePropertyChanged(nameof(SameSource)); return; }
                _sameSource = value && !IndependentFrameChange; RaisePropertyChanged(nameof(SameSource)); RaisePropertyChanged(nameof(ScopeSummary)); RaisePropertyChanged(nameof(ScopeHint));
            }
        }
        public bool CanChangeScope => !HasMultipleFrames && !Editor.IsIndependentFrame(SelectedOwnerFrame?.Id) &&
            (SelectedObject?.Kind == DesignObjectKind.Frame || SelectedObject?.Kind == DesignObjectKind.Insulator);
        public bool IndependentFrameChange
        {
            get => Editor.IsIndependentFrame(SelectedOwnerFrame?.Id);
            set
            {
                if (!ShowFrameActions || HasPendingInput) { RaisePropertyChanged(nameof(IndependentFrameChange)); return; }
                if (value) _sameSource = false;
                Run(Editor.SetIndependentFrame(SelectedObject.Id, value));
            }
        }
        public bool IsLargeOffsetCornerSelected => SelectedObject?.FrameTemplateKey == "LargeOffsetCorner";
        public bool IsLargeOffsetConnectorSelected => Editor.IsLargeOffsetConnector(SelectedObject?.Id);
        public bool CanChooseFrameLengthAnchor => IndependentFrameChange && !IsLargeOffsetCornerSelected && !IsLargeOffsetConnectorSelected &&
            !Editor.HasFixedFrameLengthAnchor(SelectedOwnerFrame?.Id);
        public IEnumerable<string> FrameLengthAnchors => IsLargeOffsetCornerSelected
            ? new[] { "整组居中，两侧对称补偿" } : IsLargeOffsetConnectorSelected
            ? new[] { Editor.IsFrameEndFixed(SelectedObject.Id) ? "固定出口，向前补偿" : "固定入口，向后补偿" }
            : Editor.CurrentSample?.ScenarioKey == "FaceToWall"
            ? new[] { "固定柜端，向墙端调整", "固定墙端，向柜端调整" }
            : new[] { "固定B侧端，向后调整", "固定末端，向B侧调整" };
        public string FrameLengthAnchor
        {
            get => Editor.IsFrameEndFixed(SelectedOwnerFrame?.Id) ? FrameLengthAnchors.Last() : FrameLengthAnchors.First();
            set
            {
                if (!ShowFrameActions || HasPendingInput || !CanChooseFrameLengthAnchor || !FrameLengthAnchors.Contains(value)) return;
                Run(Editor.SetFrameFixedEnd(SelectedObject.Id, value == FrameLengthAnchors.Last()));
            }
        }
        public string ScopeSummary => SelectedCopperSection != null
            ? (Editor.CanAdjustCopperSectionLength(SelectedCopperSection.Id)
                ? (SelectedCopperSection.CanAdjustLength ? "修改本段铜排长度将移动本段与后一段之间的ABC联动断点，后一段反向补偿，保持两段总长合法。" :
                    Editor.IsSpecialCopperSection(SelectedCopperSection.Id) ? "调整特殊段桥向跨度将移动入口ABC联动断点，前一段反向补偿；固定端接口及搭接保持不变。" :
                    "修改末端段排长度将移动入口ABC联动断点，前一段反向补偿；固定端接口及搭接保持不变。")
                : "相邻断点固定或锁定，铜排长度只读。")
            : HasMultipleFrames ? "多选用于设置同源：仅合并选中基架，归入编号较小的名称；模板、参数和内部绝缘子布置必须一致。" : SelectedObject == null ? "未选择对象" : SelectedObject.IsJointGroup
            ? "影响范围：ABC三相同位置断点。" + (SelectedObject.IsEditable
                ? (SelectedObject.Kind == DesignObjectKind.FixedJoint
                    ? "距基座边距离：远离基座为正，进入基座为负；以后截面为基准，三相同步调整L排末端直段。"
                    : "仅沿X轴拖动，三相同步；更新左右段铜排长度，保持相间距和高度。")
                : "固定断点，只读不可调整。")
            : IsInsulatorSelection ? "仅修改当前绝缘子的完整安装对象，保持内部间距及底板贴合；前后绝缘子保持原位。"
            : IsLargeOffsetCornerSelected ? "长度修改同步两端拐角长度及全部连接基架X向宽度，连接基架Y向长度不变；保持整组居中，B、A侧普通基架各承担一半补偿。结构组联动始终生效。"
            : IsLargeOffsetConnectorSelected ? Editor.LargeOffsetConnectorLengthHint(SelectedObject.Id) + "两端拐角固定，仅在本组连接架内重分配；不受同源或独立更改开关影响。"
            : SelectedObject.Kind == DesignObjectKind.Frame ? "模型名称：" + SelectedObject.SourceName + "；" + SelectedObject.PositionLabel +
                (SameSource ? "。同源组一起修改，保留同一模型名称。" : "。仅本节，提交有效修改时先独立并分配新模型名称。") +
                (IndependentFrameChange ? "独立更改：仅调整当前基架，其他基架保持原状。" : "缺口边界优先补齐；连续链按固定端补偿，不跨空隙；长度锁定节可平移，继续寻找更远未锁定节补偿。")
            : "影响范围：" + string.Join("、", Editor.Affected(SelectedObject.Id, SameSource).Select(o => o.Name));
        public string ScopeHint => SelectedCopperSection != null
            ? (Editor.CanAdjustCopperSectionLength(SelectedCopperSection.Id)
                ? (SelectedCopperSection.CanAdjustLength ? "三相同步调整，后一段补偿，总长不变。" :
                    Editor.IsSpecialCopperSection(SelectedCopperSection.Id) ? "三相同步调整跨度，前一段补偿，固定端接口不变。" :
                    "三相同步调整长度，前一段补偿，固定端接口不变。")
                : "相邻断点固定或锁定，长度只读。")
            : HasMultipleFrames ? "参数一致的基架可设置同源。"
            : SelectedObject == null ? ""
            : SelectedObject.IsJointGroup
                ? (SelectedObject.IsEditable
                    ? (SelectedObject.Kind == DesignObjectKind.FixedJoint
                        ? (Editor.CurrentSample.Source.IsModelDerived ? "三相同步；实际搭接区须避让固定的基座/基架接缝至少200mm。" : "三相同步；距基座边：远离为正，进入为负。")
                        : "沿桥向拖动，三相同步。")
                    : "固定断点，只读。")
            : IsLargeOffsetCornerSelected ? "整组居中：两端拐角长度、连接架X向宽度同步；两侧普通架对称补偿，Y向长度不变。"
            : IsLargeOffsetConnectorSelected ? Editor.LargeOffsetConnectorLengthHint(SelectedObject.Id)
            : SelectedObject.Kind == DesignObjectKind.Frame ? (Editor.FrameGapHint(SelectedObject.Id) ?? (Editor.IsFramePositionFixed(SelectedObject.Id)
                ? "必须完整覆盖初始规划安全范围，不可删除或换位。"
                : IndependentFrameChange ? "独立更改：" + FrameLengthAnchor + "，其他基架不联动。"
                : (SameSource ? "同源参数按字段锁定规则同步；" : "修改当前基架；") + "沿连续链按固定端补偿，跳过长度锁定节，空隙阻断联动。"))
            : IsInsulatorSelection ? (SelectedObject.IsEditable ? "仅修改当前绝缘子，同步更新定位和距前距离。" : "固定绝缘子，只读。")
            : ScopeSummary;
        public string SelectionCaption => SelectedViewportReference != null ? SelectedViewportReference.DisplayName :
            SelectedHanger != null ? SelectedHanger.Name + " · " + SelectedHanger.Status :
            SelectedCopperSection != null
            ? SelectedCopperSection.Name + " · ABC三相联动 · " + (Editor.CanAdjustCopperSectionLength(SelectedCopperSection.Id) ? "可调" : "只读")
            : HasMultipleFrames ? "已选 " + _selectedFrameIds.Count + " 节基架"
            : SelectedObject == null ? "请选择对象"
            : SelectedObjectLabel + " · " + (Editor.IsLocked(SelectedObject.Id) ?
                (SelectedObject.Kind == DesignObjectKind.Frame ? "长度已锁定，可联动平移" : "已锁定") : SelectedObject.IsEditable ? "可调" : "只读");
        public string SelectionSummary => SelectedHanger != null ? SelectedHanger.Problem ?? SelectedHanger.FrameLabel : SelectedCopperSection != null ? SelectedCopperSection.Name + "\n桥内ABC三相同位置段排\n" +
            (Editor.IsCopperSectionAdjusted(SelectedCopperSection.Id) ? "已人工调整" : "推荐值") + (Editor.CanAdjustCopperSectionLength(SelectedCopperSection.Id) ? " · 可调" : " · 固定只读") :
            IsInsulatorSelection ? InsulatorSelectionSummary :
            HasMultipleFrames ? "已选多个基架：\n" + string.Join("、", Objects.Where(o => _selectedFrameIds.Contains(o.Id)).Select(o => o.DisplayName)) : SelectedObject == null ? "点击图形或表格选择对象" : SelectedObject.DisplayName + "\n" +
            SelectedObject.Role + (SelectedObject.ParentId == null ? "" : "\n所属：" + Editor.Find(SelectedObject.ParentId)?.DisplayName + "；位置相对基架，方向变化带动整组绝缘子。") +
            (Editor.IsAdjusted(SelectedObject.Id) ? "\n已人工调整" : "\n推荐值") +
            (Editor.IsLocked(SelectedObject.Id) ? (SelectedObject.Kind == DesignObjectKind.Frame ? " · 长度已锁定" : " · 已锁定") : SelectedObject.IsEditable ? " · 可调" : " · 固定只读");
        public BridgeDesignObject SelectedObject
        {
            get => Objects.FirstOrDefault(o => o.Id == Editor.SelectedId);
            set => Select(value?.Id);
        }
        public BridgeDesignObject SelectedRouteObject { get => RouteObjects.FirstOrDefault(o => o.Id == Editor.SelectedId); set { if (value != null) Select(value.Id); } }
        public BridgeDesignObject SelectedFrameObject { get => FrameObjects.FirstOrDefault(o => o.Id == Editor.SelectedId); set { if (value != null) Select(value.Id); } }
        public int SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (_selectedTab == value) return;
                if (HasPendingInput) { Message = "请先提交或取消当前输入，再切换对象子页。"; RaisePropertyChanged(nameof(SelectedTab)); return; }
                _selectedTab = value;
                RaisePropertyChanged(nameof(SelectedTab));
            }
        }
        public bool SideView { get => _sideView; set { if (_sideView == value) return; _sideView = value; ChangedView(nameof(SideView)); } }
        public bool ShowReferences { get => _showReferences; set { if (_showReferences == value) return; _showReferences = value; ChangedView(nameof(ShowReferences)); } }
        public bool ShowFrames { get => _showFrames; set { if (_showFrames == value) return; _showFrames = value; ChangedView(nameof(ShowFrames)); } }
        public bool ShowInsulators { get => _showInsulators; set { if (_showInsulators == value) return; _showInsulators = value; ChangedView(nameof(ShowInsulators)); } }
        public bool ShowBusbars { get => _showBusbars; set { if (_showBusbars == value) return; _showBusbars = value; ChangedView(nameof(ShowBusbars)); } }
        // Off: cabinet/base dimensions appear only for the selected cabinet or base. On: all cabinet-window dimensions.
        private bool _showCabinetDimensions;
        public bool ShowCabinetDimensions { get => _showCabinetDimensions; set { if (_showCabinetDimensions == value) return; _showCabinetDimensions = value; ChangedView(nameof(ShowCabinetDimensions)); } }
        public bool ShowHandles { get => _showHandles; set { if (_showHandles == value) return; _showHandles = value; ChangedView(nameof(ShowHandles)); } }
        public string PhaseFilter { get => _phase; set { if (_phase == value) return; _phase = value; ChangedView(nameof(PhaseFilter)); } }
        public bool IsPreviewObjectVisible(BridgeDesignObject node)
        {
            if (node == null) return false;
            if (node.IsInsulatorGroup || (node.Kind == DesignObjectKind.FixedInsulator &&
                (node.ModelMesh != null || node.InsulatorPlacement != null))) return ShowInsulators;
            // 相别过滤只影响铜排；基座属于桥体支撑结构，与中间基架共用显示开关。
            if (node.IsJointGroup) return node.IsEditable && ShowBusbars && (PhaseFilter == "全部" || node.Phase == PhaseFilter);
            if (node.Phase != null && PhaseFilter != "全部" && node.Phase != PhaseFilter) return false;
            switch (node.Kind)
            {
                case DesignObjectKind.Frame: case DesignObjectKind.Base: return ShowFrames;
                case DesignObjectKind.Cabinet: case DesignObjectKind.Wall: case DesignObjectKind.WallBushing: return ShowReferences;
                case DesignObjectKind.Insulator: case DesignObjectKind.FixedInsulator: return ShowInsulators;
                default: return ShowBusbars;
            }
        }
        public ICommand LoadCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }
        public ICommand LockCommand { get; }
        public ICommand RestoreCommand { get; }
        public ICommand AddFrameCommand { get; }
        public ICommand AddBreakpointCommand { get; }
        public ICommand RemoveBreakpointCommand { get; }
        public ICommand DeleteFrameCommand { get; }
        public ICommand AddInsulatorCommand { get; }
        public ICommand DeleteInsulatorCommand { get; }
        public ICommand MoveFramePreviousCommand { get; }
        public ICommand MoveFrameNextCommand { get; }
        public ICommand ReverseFrameCommand { get; }
        public ICommand SetFramesSameSourceCommand { get; }
        public ICommand GenerateCommand { get; }
        public ICommand RepairCommand { get; }
        public event EventHandler PlanChanged;
        public event EventHandler ViewChanged;
        public event EventHandler FitRequested;
        public event EventHandler FitSelectionRequested;
        public event EventHandler FrameParametersRequested;

        public BusbarBridgeDesignWindowVM(PrototypeBusbarBridgeDesignProvider provider, BusbarBridgeDraftEditor editor, Func<string, bool> confirm)
            : this(provider, editor, confirm, null)
        {
        }

        public BusbarBridgeDesignWindowVM(PrototypeBusbarBridgeDesignProvider provider, BusbarBridgeDraftEditor editor,
            Func<string, bool> confirm, IBridgeUpstreamReader upstreamReader)
        {
            _upstreamReader = upstreamReader;
            if (HasUpstreamReader) _message = "准备读取当前总装并规划母线桥方案，请稍候。";
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            Editor = editor ?? throw new ArgumentNullException(nameof(editor));
            _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
            Editor.Changed += EditorChanged;
            LoadCommand = new BridgeDesignCommand(LoadSample, () => !_isDragging && !GenerationBusy);
            UndoCommand = new BridgeDesignCommand(() => { if (!HasPendingInput) Editor.Undo(); }, () => Editor.CanUndo && !HasPendingInput);
            RedoCommand = new BridgeDesignCommand(() => { if (!HasPendingInput) Editor.Redo(); }, () => Editor.CanRedo && !HasPendingInput);
            LockCommand = new BridgeDesignCommand(ToggleLock, () => ShowFrameActions && SelectedObject.IsEditable && !HasPendingInput);
            SetFramesSameSourceCommand = new BridgeDesignCommand(() => Run(Editor.SetFramesSameSource(SelectedFrameIds)), () => HasMultipleFrames && !HasPendingInput);
            RestoreCommand = new BridgeDesignCommand(RestoreSelection, () => CanRestoreSelection && !HasPendingInput);
            AddFrameCommand = new BridgeDesignCommand(() => Run(Editor.AddFrame()), () => !HasPendingInput && Editor.CanAddFrame);
            AddBreakpointCommand = new BridgeDesignCommand(AddSelectedBreakpoint,
                () => !HasPendingInput && Editor.CanAddBreakpoint(SelectedCopperSection?.Id));
            RemoveBreakpointCommand = new BridgeDesignCommand(() => Run(Editor.RemoveBreakpoint(Editor.SelectedId)),
                () => !HasPendingInput && Editor.CanRemoveBreakpoint(Editor.SelectedId));
            DeleteFrameCommand = new BridgeDesignCommand(DeleteFrame, () => Editor.CanDeleteFrame(Editor.SelectedId) && !HasMultipleFrames && !HasPendingInput);
            AddInsulatorCommand = new BridgeDesignCommand(() => Run(Editor.AddInsulator(Editor.SelectedId)),
                () => ShowFrameActions && !HasPendingInput &&
                    SelectedObject.Value(DesignField.InsulatorCount) < 3);
            DeleteInsulatorCommand = new BridgeDesignCommand(() => Run(Editor.DeleteInsulator(Editor.SelectedId)),
                () => ShowInsulatorActions && !HasPendingInput &&
                    !BridgeSmallOffsetSupportPlanner.IsSupport(SelectedObject) &&
                    !Editor.Affected(Editor.SelectedId, false).Any(o => Editor.IsLocked(o.Id)));
            MoveFramePreviousCommand = new BridgeDesignCommand(() => Run(Editor.MoveFrame(Editor.SelectedId, -1)),
                () => SelectedObject?.Kind == DesignObjectKind.Frame && !HasMultipleFrames && !SameSource && !HasPendingInput && Editor.CanMoveFrame(Editor.SelectedId, -1));
            MoveFrameNextCommand = new BridgeDesignCommand(() => Run(Editor.MoveFrame(Editor.SelectedId, 1)),
                () => SelectedObject?.Kind == DesignObjectKind.Frame && !HasMultipleFrames && !SameSource && !HasPendingInput && Editor.CanMoveFrame(Editor.SelectedId, 1));
            ReverseFrameCommand = new BridgeDesignCommand(() => Run(Editor.SetText(Editor.SelectedId,
                DesignField.Installation, SelectedObject.IsReversed ? "正装" : "反装", SameSource)),
                () => SelectedObject?.Kind == DesignObjectKind.Frame && !BridgeOffsetCopperChange.IsMember(SelectedObject) &&
                    SelectedObject.EditableFields.Contains(DesignField.Installation) &&
                    !BridgeOffsetCopperChange.IsWidenedTerminal(SelectedObject) && !HasMultipleFrames && !HasPendingInput);
            // 仅真实模型会话注入生成入口；演示模式及未提交输入不能写入模型。
            GenerateCommand = new BridgeDesignCommand(() => GenerateModel?.Invoke(), () => !HasGenerationBlockers);
            RepairCommand = new BridgeDesignCommand(() => RepairModel?.Invoke(), () => RepairModel != null &&
                !GenerationBusy && !NativePreviewBusy && !HasPendingInput && !_isDragging &&
                Editor.CurrentSample?.ReadbackBaseline != null &&
                !Editor.CurrentSample.ReadbackBaseline.RepairPlanned &&
                (Editor.CurrentSample.ReadbackBaseline.RequiresBridgeRepair || Editor.CurrentSample.ReadbackBaseline.RequiresCabinetSynchronization));
            // 恢复/测试接缝可传入已经载入的草稿；订阅Changed不会补发此前的载入事件。
            if (Editor.CurrentSample != null) EditorChanged(Editor, EventArgs.Empty);
        }

        public bool ReadUpstream() => ReadUpstream(null);

        // 重读以当前模型为准；预览准备失败时仍保留旧草稿，准备完成后统一清除全部内存编辑。
        public bool ReadUpstream(Action prepareRead)
        {
            if (_upstreamReader == null || _isDragging || GenerationBusy) return false;
            prepareRead?.Invoke();
            // 清除旧快照后再读；异常由窗口边界收口，不能把上次模型值当成本次读取成功。
            CancelWallPlanning();
            Upstream = null; _loadState = "未载入示例";
            _selectedFrameIds.Clear(); _selectedCopperSectionId = null; _sameSource = false;
            ResetHangerInputs();
            Editor.Load(null);
            Message = "正在读取总装并规划母线桥方案，请稍候。";
            Upstream = _upstreamReader.Read();
            if (Upstream?.HasModel == true)
            {
                if (Upstream.Scenario?.Contains("面对墙") == true)
                {
                    // 有正式记录时只恢复保存的原生分件，不重新搜索Worker。
                    var restored = RestoreGeneratedModel?.Invoke(Upstream, null);
                    if (restored != null)
                    {
                        RestoreWallSelection(restored);
                        Editor.Load(restored); GenerationBlocked = !restored.SupportsFormalGeneration || restored.ReadbackBaseline?.RequiresWallCopperReplan == true;
                        _loadState = IsParameterDraftMode ? "面对墙参数草稿已恢复" : "面对墙正式模型已恢复";
                        Message = restored.ReadbackBaselineWarning ?? (IsParameterDraftMode ? "已恢复当前参数的面对墙草稿方案。" : "已核验正式产物并恢复保存的面对墙方案。");
                        ModelPreviewReady?.Invoke(this, EventArgs.Empty); FitRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else StartWallPlanning(Upstream);
                    RaisePropertyChanged(nameof(SourceSummary)); RaisePropertyChanged(nameof(Status)); RaisePropertyChanged(nameof(Upstream)); RaisePropertyChanged(nameof(SceneAndSpanSummary)); RaisePropertyChanged(nameof(DisplayOverlapLengthMm));
                    return true;
                }
                var savedSample = RestoreGeneratedModel?.Invoke(Upstream, null);
                if (savedSample != null)
                {
                    _selectedFrameIds.Clear(); _selectedCopperSectionId = null; _sameSource = false;
                    Editor.Load(savedSample); GenerationBlocked = !savedSample.SupportsFormalGeneration;
                    _loadState = IsParameterDraftMode ? "参数草稿方案已恢复" : "已保存模型业务方案已恢复";
                    Message = savedSample.ReadbackBaselineWarning ?? (IsParameterDraftMode ? "已按当前参数草稿恢复铜排、断点和基架。" : "已恢复业务方案，并按当前已保存模型更新铜排、断点和基架。");
                    ModelPreviewReady?.Invoke(this, EventArgs.Empty); FitRequested?.Invoke(this, EventArgs.Empty);
                    RaisePropertyChanged(nameof(SourceSummary)); RaisePropertyChanged(nameof(Status)); RaisePropertyChanged(nameof(Upstream));
                    RaisePropertyChanged(nameof(SceneAndSpanSummary)); RaisePropertyChanged(nameof(DisplayOverlapLengthMm));
                    return true;
                }
                var planningResult = _provider.TryCreatePlannedPreview(Upstream, out var plannedSample);
                if (planningResult.Success)
                {
                    _selectedFrameIds.Clear();
                    _selectedCopperSectionId = null;
                    _sameSource = false;
                    _loadState = "模型方案已规划";
                    if (ReadHangerContext != null) plannedSample = ReadHangerContext(Upstream, plannedSample);
                    Editor.Load(plannedSample);
                    GenerationBlocked = !plannedSample.SupportsFormalGeneration;
                    Message = plannedSample.ReadbackBaselineWarning ??
                        Upstream.State + "；已按固定端部接口、柜体相对位置、规格和相别生成路径预览。";
                    var locatedProblem = plannedSample.Issues.FirstOrDefault(i => i.BlocksGeneration && i.Location != null);
                    if (locatedProblem != null)
                        Message = "方案存在净距问题，已在预览中标红。点击下方问题可定位；修正前不能生成正式模型。";
                    if (!plannedSample.SupportsFormalGeneration)
                        Message += "；面对墙首期仅支持预览和编辑，正式模型生成尚未接入。";
                    FitRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    Message = Upstream.State + "；方案预览未生成：" + planningResult.Message;
                }
            }
            else
            {
                Message = Upstream?.State ?? "上游读取失败";
            }
            if (Upstream?.Fields.Any(field => field.Name == "绝缘距离" &&
                    field.Source.Contains("已使用默认值")) == true)
                Message += "；绝缘距离未能从总装反读，已按默认 125 mm 继续。";
            RaisePropertyChanged(nameof(SourceSummary)); RaisePropertyChanged(nameof(Status)); RaisePropertyChanged(nameof(Upstream)); RaisePropertyChanged(nameof(SceneAndSpanSummary)); RaisePropertyChanged(nameof(DisplayOverlapLengthMm));
            return Upstream?.HasModel == true;
        }

        public void ReportUpstreamFailure(string message)
        {
            Upstream = new BridgeUpstreamSnapshot(null, null, null, null, new[] { new BridgeUpstreamField("读取失败", null, message) });
            Message = "模型反读未完成：" + (string.IsNullOrWhiteSpace(message) ? "未获得具体错误，请查看日志。" : message);
            RaisePropertyChanged(nameof(SourceSummary)); RaisePropertyChanged(nameof(Status)); RaisePropertyChanged(nameof(Upstream)); RaisePropertyChanged(nameof(SceneAndSpanSummary)); RaisePropertyChanged(nameof(DisplayOverlapLengthMm));
        }

        internal void AcceptGeneratedBaseline(BridgeDesignSample sample)
        {
            // 边界重规划状态不在Editor中；必须与已保存方案一起接纳，避免关闭时误报草稿未保存。
            if (sample?.ScenarioKey == "FaceToWall") RestoreWallSelection(sample);
            _selectedFrameIds.Clear(); _selectedCopperSectionId = null; _sameSource = false;
            _loadState = "正式模型已保存并反读";
            ResetHangerInputs();
            Editor.Load(sample);
        }

        internal void ApplyGenerationResult(BridgeDesignGenerationResult result)
        {
            // 接纳反读或刷新界面失败时保持禁重试，不能继续用旧基线驱动已保存模型。
            GenerationBlocked = true;
            if (result == null) throw new InvalidOperationException("正式生成未返回可核验的执行结果，请重读实物后继续。");
            if (result.Upstream != null) { Upstream = result.Upstream; RaisePropertyChanged(nameof(Upstream)); RaisePropertyChanged(nameof(SourceSummary)); }
            if (result.Readback != null && !result.BlocksGeneration) AcceptGeneratedBaseline(result.Readback);
            GenerationBlocked = result.BlocksGeneration;
            Message = result.Message;
            NativePreviewStatus = "正式生成已结束；3D临时预览已清除。";
        }

        public void LoadSample()
        {
            if (_isDragging || PlanningBusy) return;
            if (HasUnsavedChanges && !_confirm("载入示例将放弃当前内存草稿、未提交输入及锁定，是否继续？")) return;
            CancelWallPlanning();
            var result = _provider.TryLoad(SelectedSample, out var sample);
            _selectedFrameIds.Clear();
            _selectedCopperSectionId = null;
            _sameSource = false;
            _loadState = result.Success ? (sample.Objects.Count > 0 ? "示例已载入" : "空方案") : "示例加载失败";
            ResetHangerInputs();
            Editor.Load(sample);
            Message = result.Success ? "已载入独立演示方案（并非当前总装的计算结果）；先铜排走向，再规划支撑。当前未执行算法。" : result.Message;
            FitRequested?.Invoke(this, EventArgs.Empty);
        }

        public void Select(string id)
        {
            _locatedIssue = null;
            id = Editor.JointRepresentative(id)?.Id;
            if (Editor.SelectedId == id && !HasMultipleFrames && SelectedCopperSection == null && SelectedHangerId == null)
            { if (!HasPendingInput) SelectObjectTab(Editor.Find(id)); return; }
            if (HasPendingInput) { Message = "请先提交或按Esc取消当前输入，再选择其他对象。"; RefreshSelectionBindings(); return; }
            _selectedFrameIds.Clear();
            _selectedCopperSectionId = null;
            _selectedHangerId = null;
            if (Editor.Find(id)?.Kind == DesignObjectKind.Frame) _selectedFrameIds.Add(id);
            Editor.SelectedId = id; SelectObjectTab(Editor.Find(id)); ApplyAvoidanceScopeDefault(); RefreshFields(); RefreshSelectionBindings(); ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        internal bool TrySelectObjectListTab(int tab, string firstRowId)
        {
            if (HasPendingInput)
            {
                Message = "请先提交或取消当前输入，再切换对象子页。";
                return false;
            }

            if (tab == FrameTab || tab == HangerTab)
                SupportTabIndex = tab == HangerTab ? 1 : 0;
            SelectedTab = tab;
            if (firstRowId == null) Select(null);
            else if (tab == CopperTab) SelectCopperSection(firstRowId);
            else if (tab == HangerTab) SelectedHangerId = firstRowId;
            else Select(firstRowId);
            return true;
        }

        public void LocateFrameParameters(string id)
        {
            if (HasPendingInput || Editor.Find(id)?.Kind != DesignObjectKind.Frame) return;
            Select(id);
            if (Editor.SelectedId == id) FrameParametersRequested?.Invoke(this, EventArgs.Empty);
        }

        public void SelectCopperSection(string id)
        {
            _locatedIssue = null;
            var section = Editor.FindCopperSection(id);
            if (section == null) return;
            if (_selectedCopperSectionId == id) { if (!HasPendingInput) SelectedTab = CopperTab; return; }
            if (HasPendingInput) { Message = "请先提交或按Esc取消当前输入，再选择其他对象。"; RefreshSelectionBindings(); return; }
            _selectedFrameIds.Clear(); _selectedCopperSectionId = section.Id; Editor.SelectedId = null;
            _selectedHangerId = null;
            SelectedTab = CopperTab;
            _sameSource = false; RefreshFields(); RefreshSelectionBindings(); ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleFrameSelection(string id)
        {
            if (Editor.Find(id)?.Kind != DesignObjectKind.Frame) return;
            var ids = new HashSet<string>(_selectedFrameIds);
            if (!ids.Add(id)) ids.Remove(id);
            SetFrameSelection(ids, ids.Contains(id) ? id : ids.FirstOrDefault());
        }

        public void SetFrameSelection(IEnumerable<string> ids, string activeId)
        {
            if (HasPendingInput) { Message = "请先提交或取消当前输入，再更改多选范围。"; ViewChanged?.Invoke(this, EventArgs.Empty); return; }
            var frames = Objects.Where(o => o.Kind == DesignObjectKind.Frame && (ids ?? Array.Empty<string>()).Contains(o.Id))
                .OrderBy(o => o.FramePositionIndex).ToArray();
            _selectedCopperSectionId = null;
            _selectedHangerId = null;
            _selectedFrameIds.Clear(); foreach (var frame in frames) _selectedFrameIds.Add(frame.Id);
            Editor.SelectedId = _selectedFrameIds.Contains(activeId ?? "") ? activeId : frames.FirstOrDefault()?.Id;
            if (frames.Length > 0) { SupportTabIndex = 0; SelectedTab = FrameTab; }
            ApplyAvoidanceScopeDefault(); RefreshFields(); RefreshSelectionBindings(); ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        private BridgeDesignObject SelectedOwnerFrame => SelectedObject?.Kind == DesignObjectKind.Frame ? SelectedObject : Editor.Find(SelectedObject?.ParentId);

        private void ApplyAvoidanceScopeDefault()
        {
            // 默认作用范围不能继承其他对象的勾选；不禁止用户随后主动启用同源调整。
            if (IsInsulatorSelection || SelectedOwnerFrame?.HasAutomaticInsulatorPlacement == true || IndependentFrameChange) _sameSource = false;
        }

        private BridgeIssueLocation _locatedIssue;
        private long _locatedIssueRevision = -1;
        public BridgeIssueLocation LocatedIssue => _locatedIssueRevision == Editor.Revision ? _locatedIssue : null;

        public void Locate(BridgeDesignIssue issue)
        {
            if (issue == null) return;
            if (HasPendingInput) { Message = "请先提交或取消当前输入，再定位问题。"; return; }
            if (issue.Source == "吊架") { SelectedHangerId = issue.TargetId; FitSelection(); Message = issue.Message; return; }
            var route = Editor.CurrentSample?.Routes.FirstOrDefault(r => r.Id == issue.TargetId);
            if (issue.Location != null || route != null)
            {
                SideView = issue.SideView; PhaseFilter = "全部";
                ShowFrames = ShowReferences = ShowInsulators = ShowBusbars = true;
                if (route?.CopperSectionId != null) SelectCopperSection(route.CopperSectionId);
                else if (Editor.Find(issue.Location?.RelatedObjectId) is BridgeDesignObject related)
                    Select(DesignFrameFor(related)?.Id ?? related.Id);
                _locatedIssue = issue.Location; _locatedIssueRevision = Editor.Revision;
                FitSelectionRequested?.Invoke(this, EventArgs.Empty);
                Message = issue.Source + "：" + issue.Message;
                return;
            }
            var target = Editor.Find(issue.TargetId);
            if (target == null) { Message = issue.TargetId == null ? issue.Message : "问题目标已不存在，不能定位到同名对象。"; return; }
            if (HasPendingInput) { Message = "请先提交或取消当前输入，再定位问题。"; return; }
            SideView = issue.SideView; PhaseFilter = "全部";
            ShowFrames = ShowReferences = ShowInsulators = ShowBusbars = true;
            Select(DesignFrameFor(target)?.Id ?? target.Id);
            FitSelectionRequested?.Invoke(this, EventArgs.Empty); Message = issue.Source + "：" + issue.Message;
        }

        public void FitAll() => FitRequested?.Invoke(this, EventArgs.Empty);
        public void FitSelection() => FitSelectionRequested?.Invoke(this, EventArgs.Empty);
        public void SetDragging(bool active) { _isDragging = active; PendingChanged(); }
        public void CommitDrag(string id, IDictionary<DesignField, double> changes)
        {
            _isDragging = false;
            var node = Editor.Find(id);
            if (node?.Role == "特殊段重算入口" && changes.TryGetValue(DesignField.X, out var x))
                Run(RefreshWallBoundary(x - node.X));
            else if (node?.Kind == DesignObjectKind.FixedJoint && _wallPlanningInput?.DirectBaseLap == true &&
                Upstream?.FaceToWall != null && changes.TryGetValue(DesignField.X, out var rear))
                Run(RefreshWallSpan(new CKBusbarBridge.Services.FaceToWallPreviewPlanning().SpanForBaseJoint(
                    _provider.CaptureWallReplacementInput(Editor.CurrentSample, Editor.EffectivePlan), rear, Upstream.FaceToWall.OverlapLength)));
            else Run(Editor.ApplyDrag(id, changes, SameSource));
            PendingChanged();
        }
        public bool ConfirmClose() => !HasUnsavedChanges || _confirm("草稿只在本窗口内存中保留，关闭将放弃人工调整、锁定和未提交输入。是否关闭？");
        private void ToggleLock()
        {
            if (!ShowFrameActions || HasPendingInput) return;
            if (Editor.IsLocked(SelectedObject.Id) && !_confirm("解除当前对象的人工锁定，允许后续调整？")) return;
            Run(Editor.ToggleLock(SelectedObject.Id));
        }
        private void RestoreSelection()
        {
            if (HasPendingInput || !CanRestoreSelection) return;
            if (!_confirm(SelectedObject.Kind == DesignObjectKind.Frame
                ? "恢复当前基架及架内绝缘子推荐值，保留基架长度锁定、独立更改和固定端；长度按连接链规则补偿，是否继续？"
                : "恢复当前对象及其架内绝缘子推荐值，并清除对应锁定，是否继续？")) return;
            Run(Editor.Restore(SelectedObject.Id, true, SameSource));
        }
        private void DeleteFrame()
        {
            if (SelectedObject == null || HasPendingInput) return;
            if (_confirm("删除当前基架及其架内绝缘子？仅修改草稿，不改模型；可撤销。")) Run(Editor.DeleteFrame(SelectedObject.Id));
        }
        private void CommitField(BridgeDesignFieldVM field)
        {
            // 载入/撤销替换字段集合后，旧控件迟到的失焦事件不能覆盖新草稿。
            if (_committingField || !Fields.Contains(field)) return;
            if (field.Field == DesignField.X || field.Field == DesignField.Y || field.Field == DesignField.Z)
            {
                field.Complete(new BridgeEditResult(false, "不支持直接输入对象坐标，请通过预览交互调整。"));
                return;
            }
            if (Fields.Any(f => !ReferenceEquals(f, field) && f.IsDirty))
            {
                field.Complete(new BridgeEditResult(false, "其他字段还有未提交输入，请先处理或按Esc取消。"));
                return;
            }
            _committingField = true;
            try
            {
                if (field.SourceRevision.HasValue && field.SourceRevision.Value != Editor.Revision)
                { field.Complete(new BridgeEditResult(false, "草稿已变化，请重新输入绝缘子定位。")); return; }
                if (TryRefreshWallClearance(field)) return;
                if (TryRefreshWallSpan(field)) return;
                BridgeEditResult result;
                if (field.Field == DesignField.InsulatorDistanceFromPrevious)
                {
                    result = double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance)
                        ? Editor.SetInsulatorDistanceFromPrevious(field.ObjectId, distance, field.SourceRevision, field.ReferenceObjectId)
                        : new BridgeEditResult(false, "请输入有效的距前绝缘子距离。");
                }
                else result = Editor.SetText(field.ObjectId, field.Field, field.Text, IsInsulatorSelection ? false : SameSource);
                field.Complete(result); Run(result);
                if (result.Success && field.SourceRevision.HasValue) RefreshFields();
            }
            catch (Exception error)
            {
                // 编辑事务负责回退；输入事件不能把意外异常传播到SolidWorks宿主。
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("母线桥参数修改失败", error,
                    "对象=" + field.ObjectId + "；字段=" + field.Field + "；输入=" + field.Text);
                var result = new BridgeEditResult(false, "参数修改未完成：" + error.Message);
                field.Complete(result); Run(result);
            }
            finally { _committingField = false; }
        }
        private void Run(BridgeEditResult result)
        {
            Message = result.Success ? (Editor.FrameConnectionIssues.Count > 0 ? FrameConnectionSummary :
                "草稿已更新，工程可行性待联合校验；未改写模型。") : result.Message;
        }
        private void AddSelectedBreakpoint()
        {
            var result = Editor.AddBreakpoint(SelectedCopperSection?.Id);
            Run(result);
            if (result.Success && Editor.SelectedId != null) Select(Editor.SelectedId);
        }
        private void EditorChanged(object sender, EventArgs e)
        {
            SynchronizeWallPlanningState();
            Objects = Editor.EffectivePlan;
            if (Editor.FindCopperSection(_selectedCopperSectionId) == null) _selectedCopperSectionId = null;
            if (SelectedObject != null) _selectedHangerId = null;
            if (Editor.LastAutoMovedFrameIds.Contains(SelectedOwnerFrame?.Id) || IndependentFrameChange) _sameSource = false;
            _selectedFrameIds.RemoveWhere(id => !Objects.Any(o => o.Id == id && o.Kind == DesignObjectKind.Frame));
            if (SelectedObject?.Kind == DesignObjectKind.Frame) _selectedFrameIds.Add(SelectedObject.Id);
            else _selectedFrameIds.Clear();
            SelectObjectTab(SelectedObject);
            var issues = new List<BridgeDesignIssue>(Editor.PlanningIssues);
            issues.AddRange(Editor.ClearanceIssues);
            issues.AddRange(Editor.FrameConnectionIssues);
            if (Editor.CurrentSample?.ReadbackBaseline?.RequiresBridgeRepair == true && !Editor.CurrentSample.ReadbackBaseline.RepairPlanned)
                issues.AddRange(Editor.CurrentSample.ReadbackBaseline.RepairProblems.Select(problem =>
                    new BridgeDesignIssue(problem, null, "贯通修复", false, true)));
            if (Editor.HasChanges) issues.Insert(0, new BridgeDesignIssue("草稿已更新；模型方案的铜排偏移区变化会重新计算基架尺寸与铺排。已执行架内绝缘子局部避让，工程可行性仍待联合校验。", Editor.SelectedId, "草稿状态", false, false));
            Issues = issues.AsReadOnly();
            RefreshFields(); RaisePropertyChanged(string.Empty); CommandManager.InvalidateRequerySuggested();
            PlanChanged?.Invoke(this, EventArgs.Empty); ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        private void RefreshFields()
        {
            var next = BuildFields();
            for (var index = 0; index < next.Count; index++)
            {
                if (index < Fields.Count && Fields[index].CanReuse(next[index]))
                    Fields[index].RefreshFrom(next[index]);
                else if (index < Fields.Count) Fields[index] = next[index];
                else Fields.Add(next[index]);
            }
            while (Fields.Count > next.Count) Fields.RemoveAt(Fields.Count - 1);
            if (FocusedDimensionField != null && !Fields.Contains(FocusedDimensionField)) FocusedDimensionField = null;
        }
        private BridgeDesignFieldVM _focusedDimensionField;
        internal BridgeDesignFieldVM FocusedDimensionField
        {
            get => _focusedDimensionField;
            set
            {
                if (ReferenceEquals(_focusedDimensionField, value)) return;
                _focusedDimensionField = value;
                RaisePropertyChanged(nameof(FocusedDimensionField));
            }
        }
        private IReadOnlyList<BridgeDesignFieldVM> BuildFields()
        {
            var result = new List<BridgeDesignFieldVM>();
            if (SelectedHanger is BridgeHangerPlacement hanger)
                return SelectedHangerDetails.Select(detail => new BridgeDesignFieldVM(hanger.Id, detail.Key, detail.Value)).ToArray();
            var section = SelectedCopperSection;
            if (section != null)
            {
                var recommended = Editor.RecommendedCopperSection(section.Id);
                result.Add(new BridgeDesignFieldVM(section.Id, DesignField.CopperSegmentLength, CommitField, PendingChanged,
                    Editor.IsSpecialCopperSection(section.Id) ? "特殊段桥向跨度（含搭接，mm）" :
                    "段排长度（含搭接，mm）", section.Length.ToString("0.0", CultureInfo.InvariantCulture),
                    (recommended?.Length ?? section.Length).ToString("0.0", CultureInfo.InvariantCulture), !Editor.CanAdjustCopperSectionLength(section.Id)));
                return result;
            }
            var node = SelectedObject;
            if (node == null || HasMultipleFrames) return result;
            if (node.Kind == DesignObjectKind.Insulator)
            {
                AddInsulatorFields(result, node);
                result.AddRange(SelectedInsulatorDetails.Select(detail => new BridgeDesignFieldVM(node.Id, detail.Key, detail.Value)));
                return result;
            }
            if (node.IsJointGroup && node.Kind == DesignObjectKind.FixedJoint && node.EditableFields.Contains(DesignField.X))
            {
                var current = Editor.MeasureBaseJointEdgeDistance(node.Id);
                var recommended = Editor.MeasureRecommendedBaseJointEdgeDistance(node.Id);
                if (!current.HasValue || !recommended.HasValue) return result;
                result.Add(new BridgeDesignFieldVM(node.Id, DesignField.BaseJointEdgeDistance,
                    CommitField, PendingChanged, "距基座边距离 (mm)",
                    current.Value.ToString("0.0", CultureInfo.InvariantCulture),
                    recommended.Value.ToString("0.0", CultureInfo.InvariantCulture), false));
                return result;
            }
            if (node.IsJointGroup && (node.Kind == DesignObjectKind.Lap || node.Kind == DesignObjectKind.Breakpoint))
            {
                var current = Editor.MeasureJoint(node.Id); var recommended = Editor.MeasureRecommendedJoint(node.Id);
                if (current == null || recommended == null) return result;
                Func<double, string> display = value => value.ToString("0.0", CultureInfo.InvariantCulture);
                result.Add(new BridgeDesignFieldVM(node, Editor.Recommended(node.Id), DesignField.JointDistanceFromPrevious,
                    CommitField, PendingChanged, "距前断点距离 (mm)", display(current.DistanceFromPrevious),
                    display(recommended.DistanceFromPrevious), Editor.IsLocked(node.Id)));
                result.Add(new BridgeDesignFieldVM(node, Editor.Recommended(node.Id), DesignField.PreviousSegmentLength,
                    CommitField, PendingChanged, "前段排长度 (mm)", display(current.PreviousSegmentLength),
                    display(recommended.PreviousSegmentLength), true));
                result.Add(new BridgeDesignFieldVM(node, Editor.Recommended(node.Id), DesignField.NextSegmentLength,
                    CommitField, PendingChanged, "后段排长度 (mm)", display(current.NextSegmentLength),
                    display(recommended.NextSegmentLength), true));
                return result;
            }
            // 对象坐标仅供内部几何与拖动使用，不暴露为属性输入字段。
            var fields = new List<DesignField>();
            if (node.Kind == DesignObjectKind.Frame)
            {
                fields.Add(DesignField.Length);
                if (!BridgeOffsetCopperChange.IsMember(node)) fields.Add(DesignField.Installation);
                fields.Add(DesignField.InsulatorCount);
            }
            foreach (var field in fields)
            {
                var row = new BridgeDesignFieldVM(node, Editor.Recommended(node.Id), field, CommitField, PendingChanged);
                if (field == DesignField.Length && !Editor.CanEditFrameLength(node.Id) ||
                    field == DesignField.Installation && BridgeOffsetCopperChange.IsWidenedTerminal(node) ||
                    field == DesignField.InsulatorCount && Editor.EffectivePlan.Any(o => o.ParentId == node.Id && BridgeSmallOffsetSupportPlanner.IsSupport(o)))
                    row = new BridgeDesignFieldVM(node, Editor.Recommended(node.Id), field, CommitField, PendingChanged,
                        row.Label, row.CommittedText, row.RecommendedText, true);
                result.Add(row);
            }
            if (Editor.CurrentSample.ScenarioKey == "FaceToWall" && node.FrameTemplateKey == "FaceToWallLarge1" &&
                !node.Values.ContainsKey(DesignField.WallTopClearance))
                foreach (var field in new[] { DesignField.WallEntryDistance, DesignField.WallVerticalClearance, DesignField.WallHorizontalClearance })
                    result.Add(new BridgeDesignFieldVM(node.Id, field, CommitField, PendingChanged,
                        field == DesignField.WallEntryDistance ? "入墙距离 (mm)" : field == DesignField.WallVerticalClearance ? "上下墙距 (mm)" : "左右墙距 (mm)",
                        node.Value(field).ToString("0.0", CultureInfo.InvariantCulture), Editor.Recommended(node.Id).Value(field).ToString("0.0", CultureInfo.InvariantCulture),
                         field == DesignField.WallEntryDistance && Editor.IsLocked(node.Id)));
            if (node.Kind == DesignObjectKind.Frame) AddFrameInsulatorFields(result, node.Id);
            return result;
        }
        private void PendingChanged()
        {
            RaisePropertyChanged(nameof(HasPendingInput)); RaisePropertyChanged(nameof(CanPreview)); RaisePropertyChanged(nameof(CanNativePreview)); NotifyGenerationAvailability();
        }
        private void RefreshSelectionBindings()
        {
            SyncViewportReferenceSelection();
            NotifyHangerSelection();
            RaisePropertyChanged(nameof(IndependentFrameChange)); RaisePropertyChanged(nameof(FrameLengthAnchors)); RaisePropertyChanged(nameof(FrameLengthAnchor));
            RaisePropertyChanged(nameof(IsLargeOffsetCornerSelected)); RaisePropertyChanged(nameof(CanChooseFrameLengthAnchor));
            RaisePropertyChanged(nameof(IsLargeOffsetConnectorSelected));
            RaisePropertyChanged(nameof(ShowFrameActions));
            RaisePropertyChanged(nameof(SelectedFrameInsulatorSummary));
            RaisePropertyChanged(nameof(ShowInsulatorActions));
            RaisePropertyChanged(nameof(ShowCopperSectionActions));
            RaisePropertyChanged(nameof(ShowBreakpointActions));
            foreach (var name in new[] { nameof(InsulatorObjects), nameof(SelectedInsulatorObject), nameof(SelectedInsulatorId), nameof(ShowInsulatorDetails), nameof(SelectedInsulatorDetails) }) RaisePropertyChanged(name);
            foreach (var name in new[] { nameof(CopperSections), nameof(SelectedObject), nameof(SelectedRouteObject), nameof(SelectedFrameObject), nameof(SelectedCopperSection), nameof(SelectedCopperSectionId), nameof(SelectedJointId), nameof(SelectionSummary), nameof(SelectionCaption), nameof(ScopeSummary), nameof(ScopeHint), nameof(CanChangeScope), nameof(CanRestoreSelection), nameof(SelectedFrameIds), nameof(HasMultipleFrames), nameof(FrameSelectionSummary), nameof(SameSource) }) RaisePropertyChanged(name);
            CommandManager.InvalidateRequerySuggested();
        }
        private void ChangedView(string name) { RaisePropertyChanged(name); ViewChanged?.Invoke(this, EventArgs.Empty); }
        public void Dispose() { _wallDisposed = true; CancelWallPlanning(); _wallWorker?.Dispose(); Editor.Changed -= EditorChanged; }
    }
}
