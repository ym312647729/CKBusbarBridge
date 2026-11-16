using UiFeedback = CKBusbarBridgePreview.UiFeedback;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Newtonsoft.Json;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CKBusbarBridge.viewmodels;

namespace CKBusbarBridge.views
{
    /// <summary>
    /// 生成图号：录入合同号、图号及设计/校对/审核/批准等信息的窗口。
    /// </summary>
    public partial class GenerateDrawingNumberWindow : Window
    {
        private readonly Dictionary<TextBox, string> _validationErrors = new Dictionary<TextBox, string>();

        private bool _suppressContractSync;
        private bool _suppressDrawingSync;
        private string _lastAcceptedContractText = string.Empty;
        private string _lastCommittedContract;

        /// <summary>
        /// 「确定」且校验通过后序列化的表单 JSON，供写入总装配体自定义属性。
        /// </summary>
        public string CommittedPayloadJson { get; private set; }

        /// <summary>
        /// 用已有图号快照预填表单（打开弹窗时调用）。
        /// </summary>
        public void ApplyFromSnapshot(DrawingNumberMetadataSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            SetContractText(snapshot.ContractNumber);
            SetDrawingText(snapshot.DrawingNumber);
            DesignerTextBox.Text = snapshot.Designer ?? string.Empty;
            ProofreaderTextBox.Text = snapshot.Proofreader ?? string.Empty;
            ReviewerTextBox.Text = snapshot.Reviewer ?? string.Empty;
            ApproverTextBox.Text = snapshot.Approver ?? string.Empty;

            if (TrySplitDrawingNumber(snapshot.DrawingNumber, out var prefix, out _))
            {
                _lastCommittedContract = prefix;
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.ContractNumber))
            {
                _lastCommittedContract = NormalizeContract(snapshot.ContractNumber);
            }
        }

        /// <summary>
        /// 「确定」且校验通过后的合同号（归一化大写），与 JSON 字段「合同号」一致。
        /// </summary>
        public string CommittedContractNumber { get; private set; }

        /// <summary>
        /// 确定前检查合同号文件夹是否已占用；返回空表示可用。
        /// </summary>
        internal Func<string, string> ContractFolderConflictMessageProvider { get; set; }

        public GenerateDrawingNumberWindow()
        {
            InitializeComponent();
            DataObject.AddPastingHandler(ContractNumberTextBox, ContractNumber_Pasting);
            DataObject.AddPastingHandler(DrawingNumberTextBox, DrawingNumber_Pasting);
            DataObject.AddPastingHandler(DesignerTextBox, DigitField_Pasting);
            DataObject.AddPastingHandler(ProofreaderTextBox, DigitField_Pasting);
            DataObject.AddPastingHandler(ReviewerTextBox, DigitField_Pasting);
            DataObject.AddPastingHandler(ApproverTextBox, DigitField_Pasting);
        }

        /// <summary>
        /// 单行输入框内的 Enter 会因 <see cref="Button.IsDefaultProperty"/> 直接触发「确定」并关闭窗口。
        /// 在此处改为焦点移到下一控件，避免误认为「下一行」结果却提交关闭。
        /// </summary>
        private void GenerateDrawingNumberWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Return)
            {
                return;
            }

            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != ModifierKeys.None)
            {
                return;
            }

            if (!(Keyboard.FocusedElement is TextBox tb))
            {
                return;
            }

            var direction = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                ? FocusNavigationDirection.Previous
                : FocusNavigationDirection.Next;

            var request = new TraversalRequest(direction);
            tb.MoveFocus(request);
            e.Handled = true;
        }

        /// <summary>输入区外层为 <see cref="Border"/>；若控件模板夹着中间层则向上查找。</summary>
        private static Border FindOutlineBorder(DependencyObject d)
        {
            while (d != null)
            {
                if (d is Border border)
                {
                    return border;
                }

                d = VisualTreeHelper.GetParent(d);
            }

            return null;
        }

        private void ShowError(TextBox textBox, string message, bool focus = false)
        {
            var border = FindOutlineBorder(textBox);
            if (border != null)
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 77, 79));
            }

            _validationErrors[textBox] = message;
            textBox.ToolTip = message;
            RefreshValidationMessage();
            if (focus) textBox.Focus();
        }

        private void ClearError(TextBox textBox)
        {
            if (!_validationErrors.Remove(textBox)) return;
            FindOutlineBorder(textBox)?.ClearValue(Border.BorderBrushProperty);
            textBox.ToolTip = null;
            RefreshValidationMessage();
        }

        private void RefreshValidationMessage()
        {
            ValidationMessageTextBlock.Text = string.Join(Environment.NewLine, _validationErrors.Values);
            ValidationMessageTextBlock.Visibility = _validationErrors.Count == 0
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ContractNumber_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text))
            {
                e.CancelCommand();
                return;
            }

            var text = (string)e.DataObject.GetData(DataFormats.Text);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (!CanInsertContractText(ContractNumberTextBox, text))
            {
                e.CancelCommand();
            }
        }

        private void DrawingNumber_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text))
            {
                e.CancelCommand();
                return;
            }

            var text = (string)e.DataObject.GetData(DataFormats.Text);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (text.Any(c => !(char.IsLetterOrDigit(c) || c == '.')))
            {
                e.CancelCommand();
                ShowError(DrawingNumberTextBox, "图号不能粘贴 SolidWorks 或文件名限制的符号，只允许字母、数字和分隔点。");
            }
        }

        private static string NormalizeContract(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
        }

        /// <summary>按最后一个“.”拆分：前缀为合同号候选，后缀须为四位数字。</summary>
        private static bool TrySplitDrawingNumber(string raw, out string prefix, out string serial)
        {
            prefix = null;
            serial = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var trimmed = raw.Trim();
            var lastDot = trimmed.LastIndexOf('.');
            if (lastDot <= 0 || lastDot >= trimmed.Length - 1)
            {
                return false;
            }

            var p = trimmed.Substring(0, lastDot).Trim();
            var s = trimmed.Substring(lastDot + 1).Trim();
            if (s.Length != 4 || !s.All(char.IsDigit))
            {
                return false;
            }

            if (!IsValidContractBody(NormalizeContract(p)))
            {
                return false;
            }

            prefix = NormalizeContract(p);
            serial = s;
            return true;
        }

        private static bool IsValidContractBody(string candidate)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                return false;
            }

            return candidate.Length == 6 && IsValidContractInput(candidate);
        }

        private static bool IsValidContractInput(string candidate)
        {
            return candidate.Length <= 6
                && (candidate.Length == 0
                    || (candidate[0] >= 'A' && candidate[0] <= 'Z'
                        && candidate.All(char.IsLetterOrDigit)));
        }

        private static bool CanInsertContractText(TextBox textBox, string insertedText)
        {
            // 校验替换选区后的完整内容，避免在首位粘贴数字或插入后超过六位。
            var candidate = textBox.Text.Remove(textBox.SelectionStart, textBox.SelectionLength)
                .Insert(textBox.SelectionStart, insertedText);
            return IsValidContractInput(candidate.ToUpperInvariant());
        }

        private void SetDrawingText(string value)
        {
            _suppressDrawingSync = true;
            try
            {
                DrawingNumberTextBox.Text = value ?? string.Empty;
            }
            finally
            {
                _suppressDrawingSync = false;
            }
        }

        private void SetContractText(string value)
        {
            _suppressContractSync = true;
            try
            {
                ContractNumberTextBox.Text = value ?? string.Empty;
                _lastAcceptedContractText = ContractNumberTextBox.Text;
            }
            finally
            {
                _suppressContractSync = false;
            }
        }

        private void DigitField_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text))
            {
                e.CancelCommand();
                return;
            }

            var text = (string)e.DataObject.GetData(DataFormats.Text);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (text.Any(c => !char.IsDigit(c)))
            {
                e.CancelCommand();
            }
        }

        private void ContractNumberTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !CanInsertContractText((TextBox)sender, e.Text);
        }

        private void ContractNumberTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var tb = (TextBox)sender;
            var text = tb.Text;
            // 已回填的旧合同号可逐步删减至六位。
            if (!_suppressContractSync && text.Length > tb.MaxLength
                && (text.Length >= _lastAcceptedContractText.Length || e.Changes.Any(change => change.AddedLength > 0)))
            {
                // 当前输入事件结束后再回退，避免在输入法更新期间改写文本。
                tb.Dispatcher.InvokeAsync(() =>
                {
                    if (tb.Text == text && text != _lastAcceptedContractText)
                    {
                        SetContractText(_lastAcceptedContractText);
                    }
                });
                return;
            }

            ClearError(tb);
            var upper = text.ToUpperInvariant();
            _lastAcceptedContractText = upper;
            if (text == upper)
            {
                return;
            }

            var selectionStart = Math.Min(tb.SelectionStart, upper.Length);
            var selectionLength = Math.Min(tb.SelectionLength, upper.Length - selectionStart);
            tb.Text = upper;
            tb.Select(selectionStart, selectionLength);
        }

        /// <summary>
        /// 根据当前合同号更新 <see cref="_lastCommittedContract"/> 并同步图号。
        /// 在「确定」时必须先调用（<paramref name="showShortContractFormatError"/> 为 false），
        /// 否则在合同号框按回车会先于 <see cref="ContractNumberTextBox_LostFocus"/> 触发默认按钮，
        /// 导致校验时图号尚未自动填入而出现误报。
        /// </summary>
        private void CommitContractNumberAndSyncDrawing(bool showShortContractFormatError)
        {
            var tb = ContractNumberTextBox;
            if (string.IsNullOrWhiteSpace(tb.Text))
            {
                _lastCommittedContract = null;
                return;
            }

            if (!IsValidContractBody(NormalizeContract(tb.Text)))
            {
                if (showShortContractFormatError)
                {
                    ShowError(tb, "合同号必须为6位字母或数字，首位必须为大写字母！");
                }

                return;
            }

            var newContract = NormalizeContract(tb.Text);
            var oldCommitted = _lastCommittedContract;
            var contractKeyChanged = !string.Equals(newContract, oldCommitted, StringComparison.Ordinal);
            var drawingTrim = DrawingNumberTextBox.Text?.Trim() ?? string.Empty;

            if (contractKeyChanged && !string.IsNullOrEmpty(oldCommitted))
            {
                var oldNorm = NormalizeContract(oldCommitted);
                if (TrySplitDrawingNumber(drawingTrim, out var drawPrefix, out var serial) &&
                    string.Equals(drawPrefix, oldNorm, StringComparison.Ordinal))
                {
                    SetDrawingText($"{newContract}.{serial}");
                }
                else if (string.IsNullOrEmpty(drawingTrim))
                {
                    SetDrawingText($"{newContract}.0001");
                }
            }
            else if (contractKeyChanged && string.IsNullOrEmpty(oldCommitted))
            {
                if (string.IsNullOrEmpty(drawingTrim))
                {
                    SetDrawingText($"{newContract}.0001");
                }
            }
            else if (!contractKeyChanged && string.IsNullOrEmpty(drawingTrim))
            {
                SetDrawingText($"{newContract}.0001");
            }

            _lastCommittedContract = newContract;
        }

        private void ContractNumberTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitContractNumberAndSyncDrawing(showShortContractFormatError: true);
        }

        private void DrawingNumberTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (e.Text.Any(c => !(char.IsLetterOrDigit(c) || c == '.')))
            {
                e.Handled = true;
                ShowError(DrawingNumberTextBox, "图号只能输入字母、数字和分隔点，不能使用 SolidWorks 或文件名限制的符号。");
            }
        }

        private void DrawingNumberTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ClearError((TextBox)sender);
            if (_suppressDrawingSync)
            {
                return;
            }

            var tb = (TextBox)sender;
            var upper = tb.Text.ToUpperInvariant();
            if (tb.Text == upper)
            {
                return;
            }

            _suppressDrawingSync = true;
            try
            {
                var sel = tb.SelectionStart;
                tb.Text = upper;
                tb.SelectionStart = Math.Min(sel, tb.Text.Length);
            }
            finally
            {
                _suppressDrawingSync = false;
            }
        }

        /// <summary>
        /// 根据当前图号解析并填入合同号。在「确定」时须先于 <see cref="CommitContractNumberAndSyncDrawing"/> 调用（
        /// <paramref name="showDrawingFormatError"/> 为 false），避免在图号框按回车先于 <see cref="DrawingNumberTextBox_LostFocus"/>
        /// 触发默认按钮时已填图号却仍报合同号为空。
        /// </summary>
        private void CommitDrawingNumberAndSyncContract(bool showDrawingFormatError)
        {
            if (_suppressContractSync)
            {
                return;
            }

            var tb = DrawingNumberTextBox;
            if (string.IsNullOrWhiteSpace(tb.Text))
            {
                return;
            }

            if (!TrySplitDrawingNumber(tb.Text, out var prefix, out var serial))
            {
                if (showDrawingFormatError)
                {
                    ShowError(tb, "图号格式错误！应为：合同号.四位流水号");
                }

                return;
            }

            SetContractText(prefix);
            SetDrawingText($"{prefix}.{serial}");
            _lastCommittedContract = prefix;
        }

        private void DrawingNumberTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitDrawingNumberAndSyncContract(showDrawingFormatError: true);
        }

        private void DigitField_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (e.Text.Any(c => !char.IsDigit(c)))
            {
                e.Handled = true;
            }
        }

        private void DesignerTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ValidateFiveDigitOptional((TextBox)sender, "设计必须由5位数字组成！");
        }

        private void ProofreaderTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ValidateFiveDigitOptional((TextBox)sender, "校对必须由5位数字组成！");
        }

        private void ReviewerTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ValidateFiveDigitOptional((TextBox)sender, "审核必须由5位数字组成！");
        }

        private void ApproverTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var tb = (TextBox)sender;
            ValidateFiveDigitOptional(tb, "批准必须由5位数字组成！");
            tb.SelectionStart = tb.Text.Length;
        }

        private void DigitField_TextChanged(object sender, TextChangedEventArgs e)
        {
            ClearError((TextBox)sender);
        }

        private bool TryValidateAll()
        {
            var contract = NormalizeContract(ContractNumberTextBox.Text);
            var drawing = DrawingNumberTextBox.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(contract) && string.IsNullOrEmpty(drawing))
            {
                ShowError(ContractNumberTextBox, "请填写合同号与图号！", focus: true);
                return false;
            }

            if (string.IsNullOrEmpty(contract))
            {
                ShowError(ContractNumberTextBox, "请填写合同号！", focus: true);
                return false;
            }

            if (!IsValidContractBody(contract))
            {
                ShowError(ContractNumberTextBox, "合同号必须为6位字母或数字，首位必须为大写字母！", focus: true);
                return false;
            }

            if (string.IsNullOrEmpty(drawing))
            {
                ShowError(DrawingNumberTextBox, "请填写图号！", focus: true);
                return false;
            }

            try { CKBusbarBridge.DesignPreview.CabinetDraftSavePath.ValidateDrawingNumber(drawing); }
            catch (InvalidOperationException error) { ShowError(DrawingNumberTextBox, error.Message, focus: true); return false; }

            if (!TrySplitDrawingNumber(drawing, out var drawPrefix, out _))
            {
                ShowError(DrawingNumberTextBox, "图号格式错误！应为：合同号.四位流水号", focus: true);
                return false;
            }

            if (!string.Equals(drawPrefix, contract, StringComparison.Ordinal))
            {
                ShowError(DrawingNumberTextBox, "图号中的合同号与上方合同号不一致！", focus: true);
                return false;
            }

            if (!ValidateFiveDigitOptional(DesignerTextBox, "设计必须由5位数字组成！", focus: true))
            {
                return false;
            }

            if (!ValidateFiveDigitOptional(ProofreaderTextBox, "校对必须由5位数字组成！", focus: true))
            {
                return false;
            }

            if (!ValidateFiveDigitOptional(ReviewerTextBox, "审核必须由5位数字组成！", focus: true))
            {
                return false;
            }

            if (!ValidateFiveDigitOptional(ApproverTextBox, "批准必须由5位数字组成！", focus: true))
            {
                return false;
            }

            return true;
        }

        private bool ValidateFiveDigitOptional(TextBox box, string message, bool focus = false)
        {
            if (string.IsNullOrEmpty(box.Text))
            {
                return true;
            }

            if (box.Text.Length != 5 || !box.Text.All(char.IsDigit))
            {
                ShowError(box, message, focus);
                return false;
            }

            return true;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            CommitDrawingNumberAndSyncContract(showDrawingFormatError: false);
            CommitContractNumberAndSyncDrawing(showShortContractFormatError: false);
            if (!TryValidateAll())
            {
                return;
            }

            var normalizedContract = NormalizeContract(ContractNumberTextBox.Text);
            var conflictMessage = ContractFolderConflictMessageProvider?.Invoke(normalizedContract);
            if (!string.IsNullOrWhiteSpace(conflictMessage))
            {
                UiFeedback.Show(
                    conflictMessage,
                    "合同号已使用",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                ShowError(ContractNumberTextBox, conflictMessage, focus: true);
                return;
            }

            CommittedContractNumber = normalizedContract;

            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["合同号"] = normalizedContract,
                ["图号"] = DrawingNumberTextBox.Text?.Trim() ?? string.Empty,
                ["设计"] = DesignerTextBox.Text?.Trim() ?? string.Empty,
                ["校对"] = ProofreaderTextBox.Text?.Trim() ?? string.Empty,
                ["审核"] = ReviewerTextBox.Text?.Trim() ?? string.Empty,
                ["批准"] = ApproverTextBox.Text?.Trim() ?? string.Empty
            };
            CommittedPayloadJson = JsonConvert.SerializeObject(payload);

            DialogResult = true;
        }
    }
}
