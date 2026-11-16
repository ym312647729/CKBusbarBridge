using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.Prototype;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Services
{
    internal sealed partial class FaceToWallPreviewPlanning
    {
        internal double WallCopperEnd(FaceToWallPlanningInput planning, double overlap)
            => SpecialSpan(planning, overlap) + planning.Request.Groups[0].StartTerminal.RouteAnchor.X - overlap;

        internal double SpanForBaseJoint(FaceToWallPlanningInput planning, double rear, double overlap)
        {
            if (!planning.DirectBaseLap)
                throw new InvalidOperationException("只有特殊段直接搭接基座L排时，才通过L排断点联动特殊段长度。");
            return WallCopperEnd(planning, overlap) + overlap - rear;
        }

        private FaceToWallPlanningInput CreateSpanInput(FaceToWallUpstreamGeometry upstream,
            FaceToWallPlanningInput previous, double span, BusbarAlgorithmWorkerRequestOptions options)
        {
            if (upstream == null || previous == null || previous.IsPhaseSwitched != upstream.IsPhaseSwitched)
                throw new InvalidOperationException("特殊段规划缺少一致的固定接口及相序，请重读上游。");
            if (double.IsNaN(span) || double.IsInfinity(span) || span <= upstream.OverlapLength)
                throw new InvalidOperationException("特殊段跨度必须为大于搭接长度的有限数值。");
            if (span <= upstream.OverlapLength + previous.Request.Groups.Max(g => g.EndTerminal.Members[0].FixedBusbar.EndLapWidth) + .00001d)
                throw new InvalidOperationException("目标长度不足以保留两端搭接及墙端虚拟接口前置空间，当前草稿保留。");
            var near = WallCopperEnd(previous, upstream.OverlapLength) - span;
            var rear = near + upstream.OverlapLength;
            var prefix = previous.PrefixGroups.Select(CopySpanGroup).ToList();
            if (prefix.Count == 0) throw new InvalidOperationException("当前方案缺少真实的特殊段始端连接排。");
            var absorbed = 0;
            while (prefix.Count > 1 && !CanKeepSpanTail(prefix.Last(), rear))
            {
                // 取消整组三相搭接；不能保留不足直尾的壳，也不能按相分别改变拓扑。
                prefix.RemoveAt(prefix.Count - 1); absorbed++;
            }
            var direct = prefix.Count == 1 && prefix[0].DisplayName == "柜端连接段";
            if (direct) ResolveBaseJointRearX(upstream, rear, prefix);
            else if (!CanKeepSpanTail(prefix.Last(), rear))
                throw new InvalidOperationException("目标长度无法保留合法的普通排直尾，当前草稿保留。");
            foreach (var bar in prefix.Last().Busbars)
            {
                bar.PointList.Last().X = rear;
                if (bar.GetDevelopedLength() > 3000d + .000001d)
                    throw new InvalidOperationException("联动后的铜排展开长度超过3000 mm，当前草稿保留。");
            }
            var heightOffset = prefix.Last().Busbars.Min(b => b.PointList.Last().Y - b.BusBarThickness / 2d) -
                upstream.Cabinet.CalculateConnectionHeight();
            var result = CreateInputWithSupportHeight(upstream, null, near, options, prefix, heightOffset,
                upstream.AppliedWallVerticalClearance, direct ? rear : previous.BaseJointRearX);
            if (Math.Abs(SpecialSpan(result, upstream.OverlapLength) - span) > .00001d)
                throw new InvalidOperationException("目标特殊段长度与真实接口不一致，当前草稿保留。");
            result.RequestedSpecialSpan = span;
            result.AbsorbedOrdinaryCount = previous.AbsorbedOrdinaryCount + absorbed;
            // 最小值属于原布局，换接口/拓扑后必须重算，不能作为全局下限拒绝新候选。
            return result;
        }

        private bool CanKeepSpanTail(AlgorithmBusbarGenerationGroup group, double rear)
            => group.Busbars.Count > 0 && group.Busbars.All(bar =>
                rear - LastStraightStart(bar).X >= TerminalStraightReserve(bar) - .000001d);

        private AlgorithmBusbarGenerationGroup CopySpanGroup(AlgorithmBusbarGenerationGroup group)
            => new AlgorithmBusbarGenerationGroup { DisplayName = group.DisplayName, IsBridge = group.IsBridge,
                RestrictSourceReuseToCurrentGroup = group.RestrictSourceReuseToCurrentGroup,
                Busbars = Array.AsReadOnly(group.Busbars.Select(BridgeNativePreviewCopy.Copy).ToArray()) };
    }

    internal sealed class FaceToWallExactSpanPlanning
    {
        internal FaceToWallMinimumSpanPlan TryCurrent(FaceToWallUpstreamGeometry upstream,
            FaceToWallPlanningInput previous, LinkageRouteWorkerSolutionDto solution, FaceToWallPlanningInput target,
            Func<FaceToWallPlanningInput, LinkageRouteWorkerSolutionDto, BridgeDesignSample> create,
            CancellationToken cancellation)
        {
            if (!SameInterfaces(previous.Request, target.Request)) return null;
            try
            {
                var layout = new FaceToWallMinimumSpan(previous.Request, solution, upstream.OverlapLength, cancellation);
                return Accept(upstream, target, layout, create, cancellation);
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
            {
                FaceToWallPreviewTrace.Error("当前布局不能满足目标长度，继续搜索其他布局", error);
                return null;
            }
        }

        internal FaceToWallMinimumSpanPlan Complete(FaceToWallUpstreamGeometry upstream, FaceToWallPlanningInput target,
            LinkageRouteWorkerComparisonResultDto result,
            Func<FaceToWallPlanningInput, LinkageRouteWorkerSolutionDto, BridgeDesignSample> create,
            CancellationToken cancellation)
        {
            var failures = new List<string>();
            foreach (var solution in new FaceToWallWorkerSolutions().ReadSuccessful(result, target.Request))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    return Accept(upstream, target,
                        new FaceToWallMinimumSpan(target.Request, solution, upstream.OverlapLength, cancellation), create, cancellation);
                }
                catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
                {
                    FaceToWallPreviewTrace.Error("面对墙目标长度候选未通过完整校验", error);
                    failures.Add(error.Message);
                }
            }
            throw new InvalidOperationException("本次搜索未找到满足目标长度及完整安装约束的方案，当前草稿保留。" + string.Join("；", failures.Distinct()));
        }

        private FaceToWallMinimumSpanPlan Accept(FaceToWallUpstreamGeometry upstream, FaceToWallPlanningInput target,
            FaceToWallMinimumSpan layout, Func<FaceToWallPlanningInput, LinkageRouteWorkerSolutionDto, BridgeDesignSample> create,
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var length = target.RequestedSpecialSpan ?? throw new InvalidOperationException("精确重规划缺少目标长度。");
            var solution = layout.CreateSolution(length);
            target.MinimumSpecialSpan = layout.MinimumLength;
            var sample = create(target, solution);
            var actual = new PrototypeBusbarBridgeDesignProvider().CaptureWallReplacementInput(sample, sample.Objects);
            if (Math.Abs(new FaceToWallPreviewPlanning().SpecialSpan(actual, upstream.OverlapLength) - length) > .00001d)
                throw new InvalidOperationException("后续适配改变了目标特殊段长度，不能提交。");
            var issues = sample.Issues.Concat(new BusbarBridgeFrameJointClearance().Validate(sample, sample.Objects))
                .Concat(new FaceToWallSupportValidation().Validate(sample, sample.Objects))
                .Concat(new BridgeRaisedLapPlanner().Apply(sample, sample.Objects, allowLift: false).Issues)
                .Where(i => i.BlocksGeneration).Select(i => i.Message).Distinct().ToArray();
            if (issues.Length > 0) throw new InvalidOperationException(string.Join("；", issues));
            return new FaceToWallMinimumSpanPlan(target, new[] { new FaceToWallPreviewCandidate(solution, sample) });
        }

        private bool SameInterfaces(LinkageRouteWorkerRequestDto first, LinkageRouteWorkerRequestDto second)
        {
            // 桥向位置可变化；排面、层位、成员及算法约束必须一致，才可复用当前动作顺序。
            var a = Newtonsoft.Json.Linq.JObject.FromObject(first);
            var b = Newtonsoft.Json.Linq.JObject.FromObject(second);
            foreach (var root in new[] { a, b })
            {
                root.Remove("SearchOptions"); root.Remove("ContainmentMinimum"); root.Remove("ContainmentMaximum");
                foreach (var group in root["Groups"])
                {
                    group["StartTerminal"]["RouteAnchor"]["X"] = 0d;
                    foreach (var member in group["StartTerminal"]["Members"])
                        foreach (var point in member["FixedBusbar"]["PointList"]) point["X"] = 0d;
                }
            }
            return Newtonsoft.Json.Linq.JToken.DeepEquals(a, b);
        }
    }
}
