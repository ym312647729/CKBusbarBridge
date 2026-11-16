using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    public sealed class BridgeHangerRow : NotificationObject
    {
        private readonly BusbarBridgeDesignWindowVM _owner;
        private string _distanceText;
        private bool _remove;
        private string _inputError;
        internal BridgeHangerPlacement Placement { get; }
        public string Id => Placement.Id;
        public string Name => Placement.Name;
        public string FrameLabel => Placement.FrameLabel;
        public string SectionLabel => Placement.HasSection || Placement.Setting.Section.HasValue
            ? BridgeHangerSection.Label(Placement.Section) : "待确认";
        public string Status => InputError != null ? "输入无效" : IsDirty ? "待提交" : Placement.Status;
        public string Problem => InputError ?? Placement.Problem;
        public IEnumerable<BridgeDesignObject> Candidates => Placement.Candidates.Count > 1 ? Placement.Candidates : Array.Empty<BridgeDesignObject>();
        public bool CanChooseFrame => Placement.Candidates.Count > 1;
        public string CandidateId => Placement.Frame?.Id;
        internal string SavedDistance => Placement.Setting.Distance?.ToString("0.0", CultureInfo.InvariantCulture) ?? "";
        public bool IsDirty => DistanceText != SavedDistance;
        public string DistanceText
        {
            get => _distanceText;
            set
            {
                if (_distanceText == value) return;
                _distanceText = value;
                InputError = null;
                RaisePropertyChanged(nameof(DistanceText));
                RaisePropertyChanged(nameof(IsDirty));
                _owner.HangerInputChanged();
            }
        }
        public string InputError
        {
            get => _inputError;
            internal set { _inputError = value; RaisePropertyChanged(nameof(Status)); RaisePropertyChanged(nameof(Problem)); }
        }
        public bool Remove
        {
            get => _remove;
            set
            {
                if (_remove == value) return;
                _remove = value;
                RaisePropertyChanged(nameof(Remove));
                _owner.HangerInputChanged();
            }
        }
        internal BridgeHangerRow(BusbarBridgeDesignWindowVM owner, BridgeHangerPlacement placement)
        { _owner = owner; Placement = placement; _distanceText = SavedDistance; }

        internal void RestorePendingInput(BridgeHangerRow previous)
        {
            if (previous.IsDirty) { _distanceText = previous.DistanceText; _inputError = previous.InputError; }
            _remove = previous.Remove;
        }
    }

    public sealed partial class BusbarBridgeDesignWindowVM
    {
        internal const int HangerTab = 4;
        private IReadOnlyList<BridgeHangerRow> _hangerRows = Array.Empty<BridgeHangerRow>();
        private IReadOnlyList<BridgeHangerPlacement> _rowSource;
        private string _selectedHangerId;
        private readonly Dictionary<BridgeHangerSectionKind, string> _hangerCountInputs = new Dictionary<BridgeHangerSectionKind, string>();
        private string _hangerCountError;
        private string _committingHangerId;
        internal Func<BridgeUpstreamSnapshot, BridgeDesignSample, BridgeDesignSample> ReadHangerContext { get; set; }

        public IReadOnlyList<BridgeHangerRow> HangerRows
        {
            get
            {
                var placements = Editor.HangerPlacements;
                if (!ReferenceEquals(_rowSource, placements))
                {
                    var previous = _rowSource == null ? new Dictionary<string, BridgeHangerRow>() :
                        _hangerRows.ToDictionary(row => row.Id, StringComparer.Ordinal);
                    _rowSource = placements;
                    _hangerRows = placements.Select(item =>
                    {
                        var row = new BridgeHangerRow(this, item);
                        if (item.Id != _committingHangerId && previous.TryGetValue(item.Id, out var old)) row.RestorePendingInput(old);
                        return row;
                    }).ToArray();
                }
                return _hangerRows;
            }
        }
        public string HangerCountText
        {
            get => GetHangerCountText(BridgeHangerSectionKind.Whole);
            set => SetHangerCountText(BridgeHangerSectionKind.Whole, value);
        }
        public string BeforeOffsetHangerCount
        {
            get => GetHangerCountText(BridgeHangerSectionKind.BeforeOffset);
            set => SetHangerCountText(BridgeHangerSectionKind.BeforeOffset, value);
        }
        public string AcrossOffsetHangerCount
        {
            get => GetHangerCountText(BridgeHangerSectionKind.AcrossOffset);
            set => SetHangerCountText(BridgeHangerSectionKind.AcrossOffset, value);
        }
        public string AfterOffsetHangerCount
        {
            get => GetHangerCountText(BridgeHangerSectionKind.AfterOffset);
            set => SetHangerCountText(BridgeHangerSectionKind.AfterOffset, value);
        }
        public bool HasHangerSections => Editor.HasHangerSections;
        public bool HasWholeHangerCount => !HasHangerSections;
        public string HangerDistanceBasis => HasHangerSections
            ? "前、后段从桥起始边沿主桥向量取；中间段从首个连接基架入口沿横向量取。"
            : "沿原前进方向，从首节基架起始边量至安装中心线。";
        private IEnumerable<BridgeHangerSectionKind> HangerCountKinds => HasHangerSections
            ? new[] { BridgeHangerSectionKind.BeforeOffset, BridgeHangerSectionKind.AcrossOffset, BridgeHangerSectionKind.AfterOffset }
            : new[] { BridgeHangerSectionKind.Whole };
        private int CurrentHangerCount(BridgeHangerSectionKind kind) => HangerRows.Count(row => row.Placement.Section == kind &&
            (row.Placement.HasSection || row.Placement.Setting.Section == kind));
        private string GetHangerCountText(BridgeHangerSectionKind kind) =>
            _hangerCountInputs.TryGetValue(kind, out var input) ? input : CurrentHangerCount(kind).ToString(CultureInfo.InvariantCulture);
        private void SetHangerCountText(BridgeHangerSectionKind kind, string text)
        {
            if (GetHangerCountText(kind) == text) return;
            if (text == CurrentHangerCount(kind).ToString(CultureInfo.InvariantCulture)) _hangerCountInputs.Remove(kind);
            else _hangerCountInputs[kind] = text;
            _hangerCountError = null;
            HangerInputChanged();
        }
        public string HangerCountError => _hangerCountError;
        public string HangerCountSummary => HangerRows.Count == 0 ? "不布置吊架" :
            "有效定位：" + HangerRows.Count(row => row.Placement.IsValid && !row.IsDirty) + " / " + HangerRows.Count +
            (Editor.HasChanges || Editor.Sample?.ReadbackBaseline == null ? " · 草稿待生成" : " · 已生成");
        public bool IsReducingHangers => HangerCountKinds.Any(kind => TryHangerCount(kind, out var count) && count < CurrentHangerCount(kind)) ||
            HangerRows.Any(row => !HangerCountKinds.Contains(row.Placement.Section) ||
                !row.Placement.HasSection && !row.Placement.Setting.Section.HasValue);
        public bool CanConfirmHangerStart => Editor.HangerPlacements.Any(row => row.Status == "基准失效");
        internal bool HasPendingHangerInput => HangerCountKinds.Any(kind =>
            GetHangerCountText(kind) != CurrentHangerCount(kind).ToString(CultureInfo.InvariantCulture)) ||
            HangerRows.Any(row => row.IsDirty || row.Remove);
        private int _supportTabIndex;
        public int SupportTabIndex
        {
            get => _supportTabIndex;
            set { if (_supportTabIndex == value) return; _supportTabIndex = value; RaisePropertyChanged(nameof(SupportTabIndex)); }
        }
        public string SelectedHangerId
        {
            get => HangerRows.Any(row => row.Id == _selectedHangerId) ? _selectedHangerId : null;
            set
            {
                if (value == null || !HangerRows.Any(row => row.Id == value)) return;
                if (value == _selectedHangerId) { SupportTabIndex = 1; return; }
                if (Fields.Any(input => input.IsDirty)) { RaisePropertyChanged(nameof(SelectedHangerId)); return; }
                _selectedHangerId = value;
                _locatedIssue = null;
                _selectedFrameIds.Clear(); _selectedCopperSectionId = null; _sameSource = false;
                Editor.SelectedId = null;
                SupportTabIndex = 1;
                if (IsParameterDraftMode) SelectedTab = HangerTab;
                RefreshFields(); RefreshSelectionBindings();
                ViewChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public BridgeHangerPlacement SelectedHanger => HangerRows.FirstOrDefault(row => row.Id == SelectedHangerId)?.Placement;
        private IReadOnlyList<KeyValuePair<string, string>> SelectedHangerDetails
        {
            get
            {
                var hanger = SelectedHanger;
                if (hanger == null) return Array.Empty<KeyValuePair<string, string>>();
                var frame = hanger.Frame;
                return new[]
                {
                    Detail("所在基架", hanger.FrameLabel),
                    Detail("基架长 × 宽 × 高（mm）", frame == null ? "—" : string.Join(" × ",
                        new[] { DesignField.Length, DesignField.Width, DesignField.Height }.Select(dimensionField => Dimension(frame.DriveValue(dimensionField))))),
                    Detail("吊架下横梁长 / 安装高（mm）", hanger.Plan == null ? "—" :
                        Dimension(hanger.Plan.Pose.Width + 200.0) + " / " + Dimension(hanger.Plan.InstallationHeight)),
                    Detail("偏移位置（mm）", Dimension(hanger.Transverse)),
                    Detail("吊架角钢长（mm）", Dimension(hanger.Plan?.AngleLength)),
                    Detail("布置区段", hanger.HasSection ? BridgeHangerSection.Label(hanger.Section) : "待确认"),
                    Detail("状态", hanger.Problem ?? hanger.Status)
                };
            }
        }

        private static KeyValuePair<string, string> Detail(string label, string value) => new KeyValuePair<string, string>(label, value);
        private static string Dimension(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
        private bool TryHangerCount(BridgeHangerSectionKind kind, out int count) =>
            int.TryParse(GetHangerCountText(kind), NumberStyles.None, CultureInfo.InvariantCulture, out count);
        internal void HangerInputChanged()
        {
            foreach (var name in new[] { nameof(HangerCountText), nameof(BeforeOffsetHangerCount), nameof(AcrossOffsetHangerCount),
                nameof(AfterOffsetHangerCount), nameof(HangerCountError), nameof(HangerCountSummary), nameof(IsReducingHangers) })
                RaisePropertyChanged(name);
            PendingChanged();
        }
        private void ResetHangerInputs()
        {
            _selectedHangerId = null;
            _hangerCountInputs.Clear();
            _hangerCountError = null;
            _rowSource = null;
        }
        internal void ApplyHangerCount()
        {
            var counts = new Dictionary<BridgeHangerSectionKind, int>();
            foreach (var kind in HangerCountKinds)
            {
                if (!TryHangerCount(kind, out var count))
                {
                    _hangerCountError = BridgeHangerSection.Label(kind) + "数量必须是非负整数。";
                    HangerInputChanged();
                    return;
                }
                counts.Add(kind, count);
            }
            if (Editor.CurrentSample == null)
                _hangerCountError = "请先读取并规划母线桥。";
            else if (HangerRows.Any(row => row.IsDirty))
                _hangerCountError = "请先提交或取消吊架距离输入。";
            else
            {
                var remove = HangerRows.Where(row => row.Remove).Select(row => row.Id).ToArray();
                var result = Editor.SetHangerCounts(counts, remove);
                _hangerCountError = result.Success ? null : result.Message;
                if (result.Success)
                {
                    _hangerCountInputs.Clear(); _rowSource = null;
                    RaisePropertyChanged(nameof(HangerRows));
                    NotifyHangerSelection();
                    Run(result);
                }
            }
            HangerInputChanged();
        }
        internal void CancelHangerCount()
        {
            _hangerCountInputs.Clear(); _hangerCountError = null;
            foreach (var row in HangerRows) row.Remove = false;
            HangerInputChanged();
        }
        internal void CommitHangerDistance(BridgeHangerRow row)
        {
            if (!HangerRows.Contains(row) || !row.IsDirty) return;
            if (!double.TryParse(row.DistanceText, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance) ||
                !HangerVectors.Finite(distance) || distance < 0)
            { row.InputError = "请输入非负且有限的距离。"; return; }
            BridgeEditResult result;
            _committingHangerId = row.Id;
            try { result = Editor.SetHangerDistance(row.Id, distance); }
            finally { _committingHangerId = null; }
            if (!result.Success) row.InputError = result.Message;
            else row.DistanceText = row.SavedDistance;
            HangerInputChanged();
        }
        internal void ChooseHangerFrame(BridgeHangerRow row, string frameId)
        {
            if (row.CandidateId != frameId && !HasPendingHangerInput) Run(Editor.SetHangerFrame(row.Id, frameId));
        }
        internal void StepHangerDistance(BridgeHangerRow row, int direction)
        {
            var current = row.Placement.Setting.Distance;
            if (row.IsDirty)
            {
                if (!double.TryParse(row.DistanceText, NumberStyles.Float, CultureInfo.InvariantCulture, out var typed)) return;
                current = typed;
            }
            var target = (current ?? 0.0) + direction * 0.1;
            if (!HangerVectors.Finite(target) || target < 0) return;
            if (row.IsDirty) { row.DistanceText = target.ToString("R", CultureInfo.InvariantCulture); return; }
            Run(Editor.SetHangerDistance(row.Id, target));
        }
        internal void ConfirmHangerStart()
        {
            if (!HasPendingInput && CanConfirmHangerStart &&
                _confirm("将当前第一节基架的起始边作为新零位，已有吊架距离保持原值。是否应用？"))
                Run(Editor.ConfirmHangerStart());
        }
        private void NotifyHangerSelection()
        {
            foreach (var name in new[] { nameof(SelectedHangerId), nameof(SelectedHanger) })
                RaisePropertyChanged(name);
        }
    }
}
