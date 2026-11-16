using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace CKBusbarBridge.Prototype
{
    public sealed partial class BusbarBridgeDesignWindowVM
    {
        private bool _generationBlocked;
        private Action _generateModel;
        internal bool GenerationBlocked
        {
            get => _generationBlocked;
            set { if (_generationBlocked == value) return; _generationBlocked = value; NotifyGenerationAvailability(); }
        }
        internal Action GenerateModel
        {
            get => _generateModel;
            set { _generateModel = value; NotifyGenerationAvailability(); }
        }

        // 与按钮共用同一组条件；编辑过程中只显示操作状态，不重新计算几何校验。
        public IReadOnlyList<BridgeDesignIssue> GenerationBlockingIssues
        {
            get
            {
                var result = new List<BridgeDesignIssue>();
                Action<string> condition = message => result.Add(new BridgeDesignIssue(message, null, "生成条件"));
                if (GenerationBusy) condition("正在生成正式模型，请等待当前操作完成。");
                if (PlanningBusy) condition("正在规划方案，请等待规划完成。");
                if (_isDragging) condition("正在拖动对象，请完成或取消拖动。");
                if (Fields.Any(input => input.IsDirty) || HasPendingHangerInput) condition("存在尚未提交的参数，请提交或按Esc取消输入。");
                if (NativePreviewBusy) condition("正在更新3D预览，请等待预览完成。");
                if (result.Count > 0) return result.AsReadOnly();
                if (GenerateModel == null) condition("当前窗口未连接正式模型生成功能。");
                if (!IsModelPlannedPreview)
                {
                    condition(Upstream?.HasModel == true
                        ? "模型已读取，方案规划失败：" + Message
                        : "尚未载入模型方案。请在SolidWorks中打开母线桥总装，再点击“重读并规划”。");
                    return result.AsReadOnly();
                }
                if (Editor.CurrentSample?.SupportsFormalGeneration != true) condition("当前方案尚不支持正式模型生成。");
                else if (GenerationBlocked) condition("正式生成暂不可用，请先处理现场并重读模型。" + Message);
                var baseline = Editor.CurrentSample?.ReadbackBaseline;
                if (baseline?.RequiresBridgeRepair == true && !baseline.RepairPlanned)
                {
                    if (baseline.RepairProblems.Any())
                        result.AddRange(baseline.RepairProblems.Select(problem => new BridgeDesignIssue(problem, null, "贯通修复")));
                    else condition("当前模型尚未贯通，请先规划贯通修复。");
                }
                result.AddRange(Editor.PlanningIssues.Where(issue => issue.BlocksGeneration));
                result.AddRange(Editor.ClearanceIssues.Where(issue => issue.BlocksGeneration));
                result.AddRange(Editor.FrameConnectionIssues);
                result.AddRange(Editor.HangerIssues);
                return result.GroupBy(issue => new { issue.Source, issue.TargetId, issue.Message })
                    .Select(group => DescribeGenerationIssue(group.First())).ToArray();
            }
        }

        public bool HasGenerationBlockers => GenerationBlockingIssues.Count > 0;
        public string GenerationBlockerSummary => "暂不能生成正式模型（" + GenerationBlockingIssues.Count + "项）";
        public string GenerationToolTip => HasGenerationBlockers
            ? string.Join("\n", GenerationBlockingIssues.Select(issue => issue.Source + "：" + issue.Message))
            : "按当前草稿先生成铜排，再生成基架及架内绝缘子并完成装配。";

        private BridgeDesignIssue DescribeGenerationIssue(BridgeDesignIssue issue)
        {
            var message = issue.Message;
            var route = Editor.CurrentSample?.Routes.FirstOrDefault(item => item.Id == issue.TargetId)
                ?? Editor.CurrentSample?.Routes.OrderByDescending(item => item.Id.Length)
                    .FirstOrDefault(item => message.Contains(item.Id));
            if (route != null)
            {
                var section = Editor.FindCopperSection(route.CopperSectionId);
                message = message.Replace(route.Id, route.Phase + "相 · " + (route.GenerationGroup ?? section?.Name ?? "铜排"));
            }
            return new BridgeDesignIssue(message.Replace(" / rim", "边框角钢"), issue.TargetId ?? route?.Id,
                issue.Source, issue.SideView, issue.BlocksGeneration).WithLocation(issue.Location);
        }

        private void NotifyGenerationAvailability()
        {
            RaisePropertyChanged(nameof(GenerationBlockingIssues)); RaisePropertyChanged(nameof(HasGenerationBlockers));
            RaisePropertyChanged(nameof(GenerationBlockerSummary)); RaisePropertyChanged(nameof(GenerationToolTip));
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
