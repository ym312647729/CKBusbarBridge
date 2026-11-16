using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Prototype;
using Newtonsoft.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    /// <summary>在原生分源和二次驱动时保全装配体写入下级组件的尺寸。</summary>
    internal sealed class SolidWorksParentDimensionDriver
    {
        private const string PropertyName = "母线桥上级尺寸依赖";
        private readonly ISldWorks _app;
        private readonly Func<string, bool> _owns;

        internal SolidWorksParentDimensionDriver(ISldWorks app, Func<string, bool> owns)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _owns = owns ?? throw new ArgumentNullException(nameof(owns));
        }

        internal static string[] ProjectReferences(IModelDoc2 model, string projectDirectory) =>
            Components(model, includeSuppressed: true).Select(component => component.GetPathName())
                .Where(path => BridgeGenerationFileBackup.Under(projectDirectory, path) && File.Exists(path))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        internal static string[] ProjectReferenceDirectories(ISldWorks app, string path, string projectDirectory)
        {
            if (!SolidWorksBaseBridgeAngleDriver.IsOrdinaryBasePath(path)) return Array.Empty<string>();
            var model = app.GetOpenDocumentByName(path) as IModelDoc2;
            var openedHere = model == null;
            var error = 0;
            var warning = 0;
            if (openedHere)
                model = app.OpenDoc6(path, (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                    string.Empty, ref error, ref warning) as IModelDoc2;
            if (model == null || !BridgeGenerationFileBackup.Same(path, model.GetPathName()))
                throw new InvalidOperationException("无法核对待备份的普通基座引用：" + path + "，错误码=" + error);
            try
            {
                ((IAssemblyDoc)model).ResolveAllLightWeightComponents(false);
                return ProjectReferences(model, projectDirectory).Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            finally { if (openedHere) app.CloseDoc(model.GetTitle()); }
        }

        internal sealed class ScopeSnapshot
        {
            internal string Owner { get; }
            internal string Family { get; }
            internal string Configuration { get; }
            internal ParentDimensionDependency[] Rules { get; }

            internal ScopeSnapshot(string owner, string family, string configuration, ParentDimensionDependency[] rules)
            {
                Owner = owner;
                Family = family;
                Configuration = configuration;
                Rules = rules;
            }
        }

        internal ScopeSnapshot[] Capture(IModelDoc2 root, IComponent2 changing)
        {
            var all = Components(root).Select(component => (Component: component, Address: Address(component), File: PhysicalModel(component))).ToArray();
            var address = Address(changing);
            if (!all.Any(item => item.Address == address))
                throw new InvalidOperationException("独立化对象不在本次上级尺寸驱动树中：" + changing.Name2);
            var files = new HashSet<string>(all.Where(item => UnderAddress(item.Address, address)).Select(item => item.File), StringComparer.OrdinalIgnoreCase);
            var affected = all.Where(item => files.Contains(item.File)).Select(item => item.Component).ToArray();
            var scopes = Scopes(root, affected).Select(scope => (Scope: scope,
                Targets: scope.Components.Select(component => (Component: component, Address: Address(component), File: PhysicalModel(component),
                    Reference: NativeReference(component))).ToArray(), Equations: scope.Equations().Select(equation =>
                    (Equation: equation, File: PhysicalModel(equation.Target), Inputs: EquationInputs(equation.Expression).ToArray())).ToArray())).ToArray();
            // 上级方程还可能读取分源件的尺寸。保留依赖链上的目标，即便它们的引用文件没有改变。
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var item in scopes)
                {
                    var references = new HashSet<string>(item.Targets.Where(target => files.Contains(target.File))
                        .Select(target => target.Reference).Where(reference => reference != null), StringComparer.OrdinalIgnoreCase);
                    foreach (var equation in item.Equations.Where(equation => equation.Inputs.Any(input => references.Contains(input.Reference))))
                        changed |= files.Add(equation.File);
                }
            }
            var snapshots = new List<ScopeSnapshot>();
            foreach (var item in scopes)
            {
                var scope = item.Scope;
                var related = item.Targets.Where(target => files.Contains(target.File)).ToArray();
                if (related.Length == 0) continue;
                var saved = ReadRules(scope.Model);
                var added = new List<ParentDimensionDependency>();
                foreach (var equation in item.Equations.Where(equation => files.Contains(equation.File)))
                {
                    // 方程显式指向一个实例时，所有引用同一文件和配置的实例此前都共用了该尺寸。
                    foreach (var target in related.Where(target => target.File.Equals(equation.File, StringComparison.OrdinalIgnoreCase)))
                        added.Add(scope.Rule(target.Component, equation.Equation.Dimension, equation.Equation.Expression));
                }
                if (added.Count == 0 && !saved.Any(rule => related.Any(target => target.Address == rule.Target)))
                    continue;
                RequireOwned(scope.Model.GetPathName());
                var rules = ParentDimensionDependency.Merge(added.Concat(saved), scope.Model.GetPathName());
                foreach (var rule in rules) RequireOwned(scope.ResolveTarget(rule.Target).GetPathName());
                snapshots.Add(new ScopeSnapshot(scope.Address, Family(scope.Model.GetPathName()), scope.Configuration, rules));
            }
            return snapshots.ToArray();
        }

        internal void Restore(IModelDoc2 root, IReadOnlyList<ScopeSnapshot> snapshots)
        {
            if (snapshots.Count == 0) return;
            var all = Components(root);
            var owners = new List<(EquationScope Scope, ParentDimensionDependency[] Rules)>();
            foreach (var snapshot in snapshots)
            {
                var scope = snapshot.Owner.Length == 0 ? new EquationScope(root) :
                    new EquationScope(Resolve(all, snapshot.Owner, root.GetPathName()));
                if (!string.Equals(Family(scope.Model.GetPathName()), snapshot.Family, StringComparison.OrdinalIgnoreCase)
                    || scope.Configuration != snapshot.Configuration)
                    throw new InvalidOperationException("独立化后上级方程所属模型或引用配置已变更：" + scope.Model.GetPathName());
                owners.Add((scope, snapshot.Rules));
            }
            Apply(root, owners, persist: true);
        }

        internal void Synchronize(IModelDoc2 root, IEnumerable<IComponent2> branches = null,
            IEnumerable<IComponent2[]> legacyPeers = null, IEnumerable<(IComponent2 Target, string Dimension)> legacyTargets = null)
        {
            var all = Components(root);
            var branchAddresses = branches?.Select(Address).ToArray();
            var affected = branchAddresses == null ? all : all.Where(component =>
                branchAddresses.Any(branch => UnderAddress(Address(component), branch))).ToArray();
            var owners = new List<(EquationScope Scope, ParentDimensionDependency[] Rules)>();
            foreach (var scope in Scopes(root, affected, trackedOnly: true))
            {
                var rules = ReadRules(scope.Model);
                if (scope.Address.Length == 0 && legacyPeers != null)
                    rules = AddLegacyPeers(scope, rules, legacyPeers);
                if (scope.Address.Length == 0 && legacyTargets != null)
                    rules = AddRequiredLegacyRules(scope, rules, legacyTargets);
                if (SolidWorksBaseBridgeAngleDriver.IsOrdinaryBasePath(scope.Model.GetPathName()))
                    rules = AddRequiredLegacyRules(scope, rules, BaseAngleDimensions(scope).Concat(scope.Equations()
                        .Where(equation => !scope.CanResolveInputs(equation.Expression)).Select(equation => (equation.Target, equation.Dimension))));
                if (rules.Length > 0) owners.Add((scope, rules));
            }
            Apply(root, owners, persist: true);
        }

        internal void VerifySharedBaseDimensions(IModelDoc2 root, IEnumerable<IComponent2> mappedBases)
        {
            var branches = mappedBases.ToArray();
            if (branches.Any(branch => branch == null || branch.GetParent() != null ||
                !SolidWorksBaseBridgeAngleDriver.IsOrdinaryBasePath(branch.GetPathName())))
                throw new InvalidOperationException("共用尺寸校验需要已核验的总装顶层普通基座实例。");
            if (branches.Select(PhysicalModel).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) return;
            var all = Components(root);
            var used = branches.SelectMany(branch => all.Where(component => UnderAddress(Address(component), Address(branch)))
                .Select(component => (Owner: PhysicalModel(branch), Part: PhysicalModel(component), Component: component))).ToArray();
            var common = new HashSet<string>(used.GroupBy(item => item.Part, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(item => item.Owner).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .Select(group => group.Key), StringComparer.OrdinalIgnoreCase);
            if (common.Count == 0) return;

            var owners = new List<(EquationScope Scope, ParentDimensionDependency[] Rules)>();
            foreach (var scope in Scopes(root, used.Where(item => common.Contains(item.Part)).Select(item => item.Component), trackedOnly: true))
            {
                var targets = new HashSet<string>(scope.Components.Where(component => common.Contains(PhysicalModel(component)))
                    .Select(Address), StringComparer.Ordinal);
                var rules = ReadRules(scope.Model).Where(rule => targets.Contains(rule.Target)).Concat(scope.Equations()
                    .Where(equation => targets.Contains(Address(equation.Target)))
                    .Select(equation => scope.Rule(equation.Target, equation.Dimension, equation.Expression))).ToArray();
                if (rules.Length != 0) owners.Add((scope, rules));
            }
            Apply(root, owners, persist: false, verifyOnly: true);
        }

        private static IEnumerable<(IComponent2 Target, string Dimension)> BaseAngleDimensions(EquationScope scope)
        {
            var template = Family(scope.Model.GetPathName()).Split('.').Last();
            var owner = scope.Components.Where(component => component.GetParent() == null &&
                Family(component.GetPathName()) == "6GX.355." + template).ToArray();
            if (owner.Length != 1) throw new InvalidOperationException("无法唯一定位普通基座的尺寸驱动子装配体：" + scope.Identity);
            var profiles = template == "030" ? new[] { "8GX.160.152", "8GX.160.155" } : new[] { "8GX.160.008", "8GX.160.A08" };
            foreach (var profile in profiles)
            {
                var targets = scope.Components.Where(component => component.GetParent() != null &&
                    Address(component.GetParent()) == Address(owner[0]) && Family(component.GetPathName()) == profile).ToArray();
                if (targets.Length != 1) throw new InvalidOperationException("无法唯一定位普通基座上级驱动角钢：" + scope.Identity + "/" + profile);
                yield return (targets[0], "D1@草图1");
            }
        }

        private ParentDimensionDependency[] AddRequiredLegacyRules(EquationScope scope, ParentDimensionDependency[] rules,
            IEnumerable<(IComponent2 Target, string Dimension)> targets)
        {
            var additions = new List<ParentDimensionDependency>();
            var equations = scope.Equations();
            var missing = new List<(IComponent2 Target, string Dimension)>();
            foreach (var target in targets)
            {
                var key = Address(target.Target) + "|" + target.Dimension;
                if (rules.Any(rule => rule.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
                var live = equations.Where(equation => equation.Dimension == target.Dimension && scope.CanResolveInputs(equation.Expression) &&
                    PhysicalModel(equation.Target).Equals(PhysicalModel(target.Target), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (live.Length != 0)
                {
                    additions.AddRange(live.Select(equation => scope.Rule(target.Target, target.Dimension, equation.Expression)));
                    continue;
                }
                var original = scope.OrphanedEquation(target.Target, target.Dimension);
                if (original == null || !scope.CanResolveInputs(original)) missing.Add(target);
                else additions.Add(scope.Rule(target.Target, target.Dimension, original));
            }
            if (missing.Count > 0) additions.AddRange(ReadTemplateRules(scope, missing));
            return ParentDimensionDependency.Merge(rules.Concat(additions), scope.Identity);
        }

        private ParentDimensionDependency[] ReadTemplateRules(EquationScope actual, IReadOnlyList<(IComponent2 Target, string Dimension)> missing)
        {
            var family = Family(actual.Model.GetPathName());
            var folder = SolidWorksBaseBridgeAngleDriver.IsOrdinaryBasePath(actual.Model.GetPathName()) ? "基座模板" : "基架模板";
            var templateRoot = Path.Combine(TemplateArchiveCacheManager.GetTemplateRootPath(), folder);
            var matches = Directory.GetFiles(templateRoot, family + ".SLDASM", SearchOption.AllDirectories);
            if (matches.Length != 1) throw new InvalidOperationException("无法唯一定位历史模型的原生方程模板：" + actual.Identity);
            var path = matches[0];
            var template = _app.GetOpenDocumentByName(path) as IModelDoc2;
            var openedHere = template == null;
            var error = 0;
            var warning = 0;
            if (openedHere)
                template = _app.OpenDoc6(path, (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                    actual.Configuration, ref error, ref warning) as IModelDoc2;
            if (template == null || !BridgeGenerationFileBackup.Same(template.GetPathName(), path))
                throw new InvalidOperationException("无法读取已核验的原生方程模板：" + path + "，错误码=" + error);
            try
            {
                ((IAssemblyDoc)template).ResolveAllLightWeightComponents(false);
                var source = new EquationScope(template, configuration: actual.Configuration);
                var equations = source.Equations();
                return missing.Select(item =>
                {
                    var reference = source.ResolveTarget(Address(item.Target));
                    if (Family(reference.GetPathName()) != Family(item.Target.GetPathName()) ||
                        reference.ReferencedConfiguration != item.Target.ReferencedConfiguration)
                        throw new InvalidOperationException("历史模型与方程模板的原生实例或配置不符：" + item.Target.Name2);
                    var rule = ParentDimensionDependency.Merge(equations.Where(equation => equation.Dimension == item.Dimension &&
                            PhysicalModel(equation.Target).Equals(PhysicalModel(reference), StringComparison.OrdinalIgnoreCase))
                        .Select(equation => source.Rule(reference, item.Dimension, equation.Expression)), source.Identity).SingleOrDefault();
                    if (rule == null) throw new InvalidOperationException("模板缺少历史模型的上级尺寸方程：" + item.Target.Name2 + "/" + item.Dimension);
                    foreach (var input in rule.Inputs) actual.ResolveInput(input);
                    return rule;
                }).ToArray();
            }
            finally
            {
                if (openedHere) _app.CloseDoc(template.GetTitle());
                if (!BridgeGenerationFileBackup.Same(_app.IActiveDoc2?.GetPathName(), actual.Model.GetPathName()))
                    SWHelper.EnsureAssemblyEditingContext(_app, actual.Model);
            }
        }

        internal int ChooseCommonSource(IModelDoc2 root, IComponent2 first, IComponent2 second, bool requiresParentDrive)
        {
            var addresses = new[] { Address(first), Address(second) };
            var keep = new[] { true, true };
            var hasParentDrive = false;
            foreach (var scope in Scopes(root, new[] { first, second }))
            {
                var local = addresses.Select(address => RelativeAddress(address, scope.Address)).ToArray();
                if (local.Any(address => address == null)) return -1;
                var equations = scope.Equations();
                var saved = ReadRules(scope.Model);
                var references = equations.Where(equation => local.Contains(Address(equation.Target))).ToArray();
                var rules = ParentDimensionDependency.Merge(saved.Where(rule => local.Contains(rule.Target))
                    .Concat(references.Select(equation => scope.Rule(equation.Target, equation.Dimension, equation.Expression))), scope.Identity);
                foreach (var group in rules.GroupBy(rule => rule.Dimension, StringComparer.OrdinalIgnoreCase))
                {
                    hasParentDrive = true;
                    var pair = group.ToArray();
                    if (pair.Length != 2 || !pair[0].SameSource(pair[1])) return -1;
                    var bound = references.Where(equation => equation.Dimension.Equals(group.Key, StringComparison.OrdinalIgnoreCase))
                        .Select(equation => Address(equation.Target)).Distinct().ToArray();
                    // 共用后只保留一个已经有效的原生写入点；多个写入点的模型继续分别驱动。
                    if (bound.Length != 1) return -1;
                    for (var index = 0; index < keep.Length; index++) keep[index] &= bound[0] == local[index];
                }
                // 其他方程还读取被替换实例时，暂不改变该引用关系。
                var expressions = scope.ActiveExpressions();
                for (var index = 0; index < keep.Length; index++)
                {
                    var dropped = NativeReference(scope.ResolveTarget(local[1 - index]));
                    if (dropped != null && expressions.Any(expression => expression.IndexOf("@" + dropped + "\"",
                        StringComparison.OrdinalIgnoreCase) >= 0)) keep[index] = false;
                    if (saved.Any(rule => rule.Inputs.Any(input => input.Target == local[1 - index]))) keep[index] = false;
                }
            }
            return requiresParentDrive && !hasParentDrive ? -1 : Array.FindIndex(keep, allowed => allowed);
        }

        internal static ScopeSnapshot[] RemapRestoredSource(IReadOnlyList<ScopeSnapshot> snapshots, string before, string after)
        {
            if (before == after) return snapshots.ToArray();
            return snapshots.Select(snapshot =>
            {
                var original = RelativeAddress(before, snapshot.Owner);
                var replacement = RelativeAddress(after, snapshot.Owner);
                if (original == null || replacement == null) return snapshot;
                if (snapshot.Rules.Any(rule => rule.Inputs.Any(input => input.Target == original)))
                    throw new InvalidOperationException("恢复共用后原方程输入引用发生变化：" + before);
                return new ScopeSnapshot(snapshot.Owner, snapshot.Family, snapshot.Configuration, snapshot.Rules
                    .Select(rule => rule.Target == original ? rule.ForTarget(replacement) : rule).ToArray());
            }).ToArray();
        }

        private static string RelativeAddress(string address, string owner) => owner.Length == 0 ? address :
            address.StartsWith(owner + "/", StringComparison.Ordinal) ? address.Substring(owner.Length + 1) : null;

        private static IEnumerable<(string Token, string Dimension, string Reference)> EquationInputs(string expression)
        {
            foreach (var token in Regex.Matches(expression, "\"([^\"]+)\"").Cast<Match>()
                .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal))
                if (ParentDimensionDependency.TryReadEquation("\"" + token + "\"=0", out var dimension, out var reference, out _))
                    yield return (token, dimension, reference);
        }

        private static ParentDimensionDependency[] AddLegacyPeers(EquationScope scope, ParentDimensionDependency[] rules,
            IEnumerable<IComponent2[]> peers)
        {
            var additions = new List<ParentDimensionDependency>();
            var equations = scope.Equations();
            foreach (var pair in peers)
            {
                if (pair.Length < 2 || pair.Select(PhysicalModel).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
                    continue;
                var addresses = new HashSet<string>(pair.Select(Address), StringComparer.Ordinal);
                var inputs = equations.Where(equation => addresses.Contains(Address(equation.Target)))
                    .GroupBy(equation => equation.Dimension, StringComparer.OrdinalIgnoreCase);
                foreach (var input in inputs)
                {
                    var expressions = input.Select(equation => equation.Expression).Distinct(StringComparer.Ordinal).ToArray();
                    if (expressions.Length != 1)
                        throw new InvalidOperationException("已分源对角件的原生长度驱动存在冲突：" + scope.Model.GetPathName() + "/" + input.Key);
                    foreach (var component in pair)
                        additions.Add(scope.Rule(component, input.Key, expressions[0]));
                }
            }
            return ParentDimensionDependency.Merge(rules.Concat(additions), scope.Model.GetPathName());
        }

        private void Apply(IModelDoc2 root, IReadOnlyList<(EquationScope Scope, ParentDimensionDependency[] Rules)> owners,
            bool persist, bool verifyOnly = false)
        {
            if (owners.Count == 0) return;
            var bindings = new List<DimensionBinding>();
            var records = new List<(IModelDoc2 Model, ParentDimensionDependency[] Rules)>();
            foreach (var owner in owners.GroupBy(item => item.Scope.Identity, StringComparer.OrdinalIgnoreCase))
            {
                var scope = owner.First().Scope;
                RequireOwned(scope.Model.GetPathName());
                var rules = ParentDimensionDependency.Merge(owner.SelectMany(item => item.Rules), scope.Model.GetPathName());
                foreach (var rule in rules)
                {
                    var target = scope.ResolveTarget(rule.Target);
                    if (!string.Equals(Family(target.GetPathName()), rule.Family, StringComparison.OrdinalIgnoreCase)
                        || target.ReferencedConfiguration != rule.Configuration)
                        throw new InvalidOperationException("上级尺寸驱动目标身份或引用配置已变更：" + target.Name2);
                    RequireOwned(target.GetPathName());
                    if (target.IsSuppressed()) continue;
                    var model = TargetModel(target);
                    var dimension = RequireDimension(model, rule.Dimension);
                    var inputs = rule.Inputs.Select(input => PhysicalModel(scope.ResolveInput(input)) + "|" + input.Dimension).ToArray();
                    bindings.Add(new DimensionBinding(scope, rule, target.GetPathName(), dimension.GetType(), inputs));
                }
                if (persist) records.Add((scope.Model, rules));
            }

            var byTarget = bindings.ToLookup(binding => binding.Key, StringComparer.OrdinalIgnoreCase);
            var layers = ParentDimensionDependency.OrderWrites(byTarget.Select(group => group.Key),
                key => byTarget[key].SelectMany(binding => binding.Inputs));
            RebindInputAliases(root, bindings, verifyOnly);
            var verified = new List<DimensionUpdate>();
            foreach (var layer in layers)
            {
                var writes = new List<DimensionUpdate>();
                var currentEquations = new Dictionary<string, (ObservedEquation[] Equations, ILookup<string, ObservedEquation> Targets)>(
                    StringComparer.OrdinalIgnoreCase);
                var values = new Dictionary<(string Owner, string Expression, int Kind), double>();
                foreach (var key in layer)
                {
                    var group = byTarget[key].Select(binding =>
                    {
                        var scope = binding.Scope;
                        if (!currentEquations.TryGetValue(scope.Identity, out var observed))
                        {
                            var equations = scope.Equations();
                            observed = (equations, equations.ToLookup(equation => PhysicalModel(equation.Target) + "|" + equation.Dimension,
                                StringComparer.OrdinalIgnoreCase));
                            currentEquations.Add(scope.Identity, observed);
                        }
                        var rule = binding.Rule;
                        var active = observed.Targets[key].ToArray();
                        if (active.Any(equation => scope.Rule(equation.Target, equation.Dimension, equation.Expression).SourceExpression
                            != rule.SourceExpression))
                            throw new InvalidOperationException("实际原生方程与已核验驱动冲突：" + key);
                        var source = (scope.Identity, rule.SourceExpression, binding.Kind);
                        if (!values.TryGetValue(source, out var desired))
                        {
                            desired = scope.Evaluate(_app, rule, binding.Kind, observed.Equations);
                            values.Add(source, desired);
                        }
                        return new DimensionUpdate(binding.Path, rule.Configuration, rule.Dimension, binding.Kind, desired, active.Length > 0);
                    }).ToArray();
                    var expected = group[0];
                    if (group.Any(update => update.Kind != expected.Kind || !Close(update.Value, expected.Value, expected.Kind)))
                        throw new InvalidOperationException("共用零件的上级尺寸目标冲突：" + expected.Label);
                    var actual = ReadDimension(OpenReference(expected), expected);
                    if (!Close(actual, expected.Value, expected.Kind))
                    {
                        if (verifyOnly || group.Any(update => update.Native)) RequireClose(actual, expected);
                        writes.Add(expected);
                    }
                    verified.Add(expected);
                }
                WriteDimensions(root, writes);
            }
            foreach (var update in verified) RequireClose(ReadDimension(OpenReference(update), update), update);
            foreach (var record in records) WriteRules(record.Model, record.Rules);
            if (!BridgeGenerationFileBackup.Same(_app.IActiveDoc2?.GetPathName(), root.GetPathName()))
                SWHelper.EnsureAssemblyEditingContext(_app, root);
        }

        private void RebindInputAliases(IModelDoc2 root, IEnumerable<DimensionBinding> bindings, bool verifyOnly)
        {
            var changed = false;
            foreach (var group in bindings.Where(binding => binding.Rule.Inputs.Length > 0)
                .GroupBy(binding => binding.Scope.Identity, StringComparer.OrdinalIgnoreCase))
            {
                var scope = group.First().Scope;
                var byTarget = group.ToLookup(binding => binding.Key, StringComparer.OrdinalIgnoreCase);
                var replacements = new List<(ObservedEquation Equation, ParentDimensionDependency Rule)>();
                foreach (var equation in scope.Equations().Where(equation => !scope.CanResolveInputs(equation.Expression)))
                {
                    var target = PhysicalModel(equation.Target) + "|" + equation.Dimension;
                    var rules = byTarget[target].Select(binding => binding.Rule).ToArray();
                    if (rules.Length == 0) continue;
                    string updated = null;
                    foreach (var rule in rules)
                    {
                        if (!scope.TryRebindInputAliases(equation.Expression, rule, out var expression) ||
                            updated != null && updated != expression)
                            throw new InvalidOperationException("原生方程的失联输入与拆分前身份不符：" + scope.Identity + "/" + equation.Dimension);
                        updated = expression;
                    }
                    replacements.Add((equation, rules[0]));
                }
                if (replacements.Count == 0) continue;
                if (verifyOnly) throw new InvalidOperationException("共用尺寸仍引用失联的方程输入：" + scope.Identity);
                scope.WriteInputAliases(_app, replacements);
                changed = true;
            }
            if (!changed) return;
            SWHelper.EnsureAssemblyEditingContext(_app, root);
            RequireRebuild(root);
        }

        private IModelDoc2 OpenReference(DimensionUpdate update) => _app.GetOpenDocumentByName(update.Path) as IModelDoc2
            ?? throw new InvalidOperationException("上级驱动目标文档已失效：" + update.Label);

        private void WriteDimensions(IModelDoc2 root, IReadOnlyList<DimensionUpdate> writes)
        {
            if (writes.Count == 0) return;
            var driver = new SolidWorksDriver(_app);
            foreach (var group in writes.GroupBy(update => update.Path + "|" + update.Configuration, StringComparer.OrdinalIgnoreCase))
            {
                var target = group.First();
                var model = driver.OpenOrActivateDocument(target.Path, rebuildAfterResolve: false);
                if (model.ConfigurationManager.ActiveConfiguration.Name != target.Configuration)
                    throw new InvalidOperationException("上级尺寸同步需要激活实际引用配置：" + target.Label);
                if (model is IAssemblyDoc) SWHelper.EnsureAssemblyEditingContext(_app, model);
                if (!BridgeGenerationFileBackup.Same(_app.IActiveDoc2?.GetPathName(), target.Path))
                    throw new InvalidOperationException("上级尺寸同步目标文档未激活：" + target.Label);
                foreach (var update in group)
                {
                    var dimension = RequireDimension(model, update.Dimension);
                    if (dimension.SetSystemValue3(update.Value, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null) != 0)
                        throw new InvalidOperationException("无法同步失联尺寸：" + update.Label);
                }
                (model.GetEquationMgr() as IEquationMgr)?.EvaluateAll();
                RequireRebuild(model);
                foreach (var update in group) RequireClose(ReadDimension(model, update), update);
            }

            SWHelper.EnsureAssemblyEditingContext(_app, root);
            RequireRebuild(root);
            foreach (var update in writes)
            {
                RequireClose(ReadDimension(OpenReference(update), update), update);
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("上级尺寸已按原方程同步", update.Label + "，目标 " + Display(update.Value, update.Kind));
            }
        }

        private sealed class EquationScope
        {
            internal IModelDoc2 Model { get; }
            internal string Address { get; }
            internal string Configuration { get; }
            internal string Identity => Model.GetPathName() + "|" + Configuration;
            internal IComponent2[] Components { get; }
            private ILookup<string, IComponent2> ByReference { get; }
            private ILookup<string, IComponent2> ByAddress { get; }

            internal EquationScope(IModelDoc2 model, string address = "", string configuration = null)
            {
                Model = model;
                Address = address;
                Configuration = configuration ?? model.ConfigurationManager.ActiveConfiguration.Name;
                if (model.ConfigurationManager.ActiveConfiguration.Name != Configuration)
                    throw new InvalidOperationException("上级方程文档未处于实际引用配置：" + Identity);
                var all = SolidWorksParentDimensionDriver.Components(model, includeSuppressed: true);
                ByAddress = all.ToLookup(SolidWorksParentDimensionDriver.Address, StringComparer.Ordinal);
                Components = all.Where(component => !component.IsSuppressed()).ToArray();
                ByReference = Components.Select(component => (Component: component, Reference: NativeReference(component)))
                    .Where(item => item.Reference != null).ToLookup(item => item.Reference, item => item.Component, StringComparer.OrdinalIgnoreCase);
            }

            internal EquationScope(IComponent2 component)
                : this(TargetModel(component), SolidWorksParentDimensionDriver.Address(component), component.ReferencedConfiguration) { }

            internal IComponent2 ResolveTarget(string address)
            {
                var matches = ByAddress[address].ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException("无法唯一反读上级尺寸驱动实例：" + Identity + "/" + address);
                return matches[0];
            }

            internal ObservedEquation[] Equations()
            {
                var manager = Model.GetEquationMgr() as IEquationMgr;
                if (manager == null) return Array.Empty<ObservedEquation>();
                var result = new List<ObservedEquation>();
                for (var index = 0; index < manager.GetCount(); index++)
                {
                    if (manager.Suppression[index] || !ParentDimensionDependency.TryReadEquation(manager.Equation[index],
                            out var dimension, out var reference, out var expression)) continue;
                    var matches = ByReference[reference].ToArray();
                    if (matches.Length > 1) throw new InvalidOperationException("原生方程实例引用不唯一：" + reference);
                    if (matches.Length == 1) result.Add(new ObservedEquation(index, matches[0], dimension, expression));
                }
                return result.ToArray();
            }

            internal string[] ActiveExpressions()
            {
                var manager = Model.GetEquationMgr() as IEquationMgr;
                if (manager == null) return Array.Empty<string>();
                return Enumerable.Range(0, manager.GetCount()).Where(index => !manager.Suppression[index])
                    .Select(index => manager.Equation[index]).Where(text => !string.IsNullOrWhiteSpace(text) && text.Contains("="))
                    .Select(text => text.Substring(text.IndexOf('=') + 1)).ToArray();
            }

            internal string OrphanedEquation(IComponent2 target, string dimension)
            {
                var wanted = NativeReference(target);
                if (wanted == null) return null;
                var familyPath = ReferenceFamilyPath(wanted);
                if (familyPath == null || Components.Count(component =>
                    ReferenceFamilyPath(NativeReference(component)) == familyPath) != 1) return null;
                var manager = Model.GetEquationMgr() as IEquationMgr;
                if (manager == null) return null;
                var matches = new List<string>();
                for (var index = 0; index < manager.GetCount(); index++)
                    if (!manager.Suppression[index] && ParentDimensionDependency.TryReadEquation(manager.Equation[index],
                        out var name, out var reference, out var expression) && name == dimension &&
                        !ByReference.Contains(reference) && ReferenceFamilyPath(reference) == familyPath) matches.Add(expression);
                if (matches.Count > 1) throw new InvalidOperationException("历史模型原生方程引用存在多个候选：" + Identity + "/" + dimension);
                return matches.SingleOrDefault();
            }

            internal ParentDimensionDependency Rule(IComponent2 component, string dimension, string expression)
            {
                var inputs = new List<ParentDimensionInput>();
                foreach (var reference in EquationInputs(expression))
                {
                    var matches = ByReference[reference.Reference].ToArray();
                    if (matches.Length != 1)
                        throw new InvalidOperationException("无法唯一定位上级方程的实际输入：" + Identity + "/" + reference.Token);
                    var input = matches[0];
                    inputs.Add(new ParentDimensionInput(reference.Token, SolidWorksParentDimensionDriver.Address(input),
                        Family(input.GetPathName()), input.ReferencedConfiguration, reference.Dimension));
                }
                return new ParentDimensionDependency(SolidWorksParentDimensionDriver.Address(component), Family(component.GetPathName()),
                    component.ReferencedConfiguration, dimension, expression, inputs.ToArray());
            }

            internal bool CanResolveInputs(string expression) => EquationInputs(expression).All(input => ByReference[input.Reference].Count() == 1);

            internal bool TryRebindInputAliases(string expression, ParentDimensionDependency rule, out string updated) =>
                rule.TryRewriteObservedInputs(expression, ActualInputToken, reference => ByReference.Contains(reference), out updated);

            private string ActualInputToken(ParentDimensionInput input)
            {
                var actual = ResolveInput(input);
                var reference = NativeReference(actual);
                if (reference == null || ByReference[reference].Count() != 1)
                    throw new InvalidOperationException("无法取得原生方程输入的唯一实际引用：" + actual.Name2);
                return input.Dimension + "@" + reference;
            }

            internal void WriteInputAliases(ISldWorks app, IEnumerable<(ObservedEquation Equation, ParentDimensionDependency Rule)> replacements)
            {
                SWHelper.EnsureAssemblyEditingContext(app, Model);
                if (!BridgeGenerationFileBackup.Same(app.IActiveDoc2?.GetPathName(), Model.GetPathName()))
                    throw new InvalidOperationException("原生方程所属装配体未激活：" + Identity);
                var manager = (IEquationMgr)Model.GetEquationMgr();
                var active = new EquationScope(Model, Address, Configuration);
                foreach (var replacement in replacements)
                {
                    var target = SolidWorksParentDimensionDriver.Address(replacement.Equation.Target);
                    var current = active.FindEquation(target, replacement.Equation.Dimension);
                    if (!active.TryRebindInputAliases(current.Expression, replacement.Rule, out var expression))
                        throw new InvalidOperationException("实际原生方程与已核验的输入不符：" + Identity + "/" + target + "/" + current.Dimension);
                    if (current.Expression == expression) continue;
                    var index = current.Index;
                    var old = manager.Equation[index];
                    if (!ParentDimensionDependency.TryReadEquation(old, out var dimension, out var reference, out var originalExpression) ||
                        dimension != current.Dimension || !string.Equals(reference, NativeReference(current.Target), StringComparison.OrdinalIgnoreCase) ||
                        originalExpression != current.Expression)
                        throw new InvalidOperationException("原生方程输入更新前实际行已变化：" + Identity + "/" + index);
                    var text = old.Substring(0, old.IndexOf('=') + 1) + " " + expression;
                    if (Model.GetConfigurationCount() == 1) manager.Equation[index] = text;
                    else if (manager.SetEquationAndConfigurationOption(index, text, (int)swInConfigurationOpts_e.swSpecifyConfiguration,
                        new[] { Configuration }) < 0)
                        throw new InvalidOperationException("无法更新引用配置中的原生方程输入：" + Identity + "/" + index);
                    var status = manager.Status;
                    // 自动求解可能重新排列方程，不能把写入前的行号当作持久身份。
                    if (status < 0 || active.FindEquation(target, dimension).Expression != expression)
                        throw new InvalidOperationException("原生方程输入更新反读失败：" + Identity + "/" + index + "，状态=" + status);
                }
                manager.EvaluateAll();
                RequireRebuild(Model);
            }

            private ObservedEquation FindEquation(string address, string dimension)
            {
                var component = ResolveTarget(address);
                var name = NativeReference(component);
                var manager = (IEquationMgr)Model.GetEquationMgr();
                var matches = new List<ObservedEquation>();
                for (var index = 0; index < manager.GetCount(); index++)
                {
                    if (manager.Suppression[index] || !ParentDimensionDependency.TryReadEquation(manager.Equation[index],
                        out var observed, out var reference, out var expression) || observed != dimension ||
                        !string.Equals(reference, name, StringComparison.OrdinalIgnoreCase)) continue;
                    matches.Add(new ObservedEquation(index, component, dimension, expression));
                }
                if (matches.Count != 1) throw new InvalidOperationException("无法唯一定位原生方程当前行：" + Identity + "/" + address + "/" + dimension);
                return matches[0];
            }

            internal IComponent2 ResolveInput(ParentDimensionInput input)
            {
                var actual = ResolveTarget(input.Target);
                if (!string.Equals(Family(actual.GetPathName()), input.Family, StringComparison.OrdinalIgnoreCase)
                    || actual.ReferencedConfiguration != input.Configuration || actual.IsSuppressed())
                    throw new InvalidOperationException("原生方程输入身份或配置已变化：" + actual.Name2);
                return actual;
            }

            internal double Evaluate(ISldWorks app, ParentDimensionDependency rule, int kind, IReadOnlyList<ObservedEquation> equations)
            {
                var expression = rule.RewriteInputs(ActualInputToken);
                var matching = equations.FirstOrDefault(equation => equation.Dimension == rule.Dimension && equation.Expression == expression);
                if (matching != null)
                {
                    var active = (IEquationMgr)Model.GetEquationMgr();
                    var index = matching.Index;
                    if (index >= active.GetCount() || active.Suppression[index] ||
                        !ParentDimensionDependency.TryReadEquation(active.Equation[index], out var dimension, out var reference, out var observed) ||
                        dimension != matching.Dimension || !string.Equals(reference, NativeReference(matching.Target), StringComparison.OrdinalIgnoreCase) ||
                        observed != expression)
                    {
                        var actual = FindEquation(SolidWorksParentDimensionDriver.Address(matching.Target), matching.Dimension);
                        if (actual.Expression != expression)
                            throw new InvalidOperationException("原生尺寸求值前方程已变化：" + Identity + "/" + rule.Dimension);
                        index = actual.Index;
                    }
                    return ReadValue(active, index, kind);
                }

                // 失联的原表达式仍由 SolidWorks 在原装配配置中求值，避免软件复写模板公式。
                SWHelper.EnsureAssemblyEditingContext(app, Model);
                if (!BridgeGenerationFileBackup.Same(app.IActiveDoc2?.GetPathName(), Model.GetPathName()))
                    throw new InvalidOperationException("原生方程所属装配体未激活：" + Identity);
                var manager = (IEquationMgr)Model.GetEquationMgr();
                var temporaryName = "\"DQ驱动求值" + Guid.NewGuid().ToString("N") + "\"";
                var temporary = temporaryName + " = " + expression;
                var added = Model.GetConfigurationCount() == 1 ? manager.Add2(-1, temporary, false) :
                    manager.Add3(-1, temporary, false, (int)swInConfigurationOpts_e.swThisConfiguration, null);
                if (added < 0) throw new InvalidOperationException("无法求值原上级尺寸方程：" + Identity + "/" + rule.Dimension);
                Exception failure = null;
                try
                {
                    manager.EvaluateAll();
                    return ReadValue(manager, TemporaryRow(), kind);
                }
                catch (Exception error) { failure = error; throw; }
                finally
                {
                    try
                    {
                        if (manager.Delete(TemporaryRow()) < 0)
                            throw new InvalidOperationException("无法移除原生尺寸临时求值变量：" + Identity);
                    }
                    catch (Exception cleanup) when (failure != null) { failure.Data["ParentDimensionEvaluationCleanup"] = cleanup; }
                }

                int TemporaryRow()
                {
                    bool Matches(int row)
                    {
                        var text = manager.Equation[row];
                        var equals = text?.IndexOf('=') ?? -1;
                        return equals > 0 && text.Substring(0, equals).Trim() == temporaryName;
                    }
                    if (added < manager.GetCount() && Matches(added)) return added;
                    var matches = Enumerable.Range(0, manager.GetCount()).Where(Matches).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException("无法定位临时求值变量：" + Identity);
                    return matches[0];
                }
            }

            private double ReadValue(IEquationMgr manager, int index, int kind)
            {
                var value = manager.Value[index];
                if (manager.Status != index || double.IsNaN(value) || double.IsInfinity(value))
                    throw new InvalidOperationException("上级原生方程求值失败：" + Identity + "/" + index);
                if (kind == (int)swDimensionParamType_e.swDimensionParamTypeInteger) return value;
                var unit = kind == (int)swDimensionParamType_e.swDimensionParamTypeDoubleLinear
                    ? swUserUnitsType_e.swLengthUnit : swUserUnitsType_e.swAngleUnit;
                var factor = (Model.GetUserUnit((int)unit) as IUserUnit)?.GetConversionFactor() ?? 0d;
                if (!(factor > 0d) || double.IsInfinity(factor))
                    throw new InvalidOperationException("无法读取原生方程单位：" + Identity);
                return value / factor;
            }
        }

        private sealed class DimensionBinding
        {
            internal EquationScope Scope { get; }
            internal ParentDimensionDependency Rule { get; }
            internal string Path { get; }
            internal int Kind { get; }
            internal string[] Inputs { get; }
            internal string Key => System.IO.Path.GetFullPath(Path) + "|" + Rule.Configuration + "|" + Rule.Dimension;

            internal DimensionBinding(EquationScope scope, ParentDimensionDependency rule, string path, int kind, string[] inputs)
            { Scope = scope; Rule = rule; Path = path; Kind = kind; Inputs = inputs; }
        }

        private sealed class ObservedEquation
        {
            internal int Index { get; }
            internal IComponent2 Target { get; }
            internal string Dimension { get; }
            internal string Expression { get; }
            internal ObservedEquation(int index, IComponent2 target, string dimension, string expression)
            { Index = index; Target = target; Dimension = dimension; Expression = expression; }
        }

        private sealed class DimensionUpdate
        {
            internal string Path { get; }
            internal string Configuration { get; }
            internal string Dimension { get; }
            internal int Kind { get; }
            internal double Value { get; }
            internal bool Native { get; }
            internal string Label => Path + "/" + Configuration + "/" + Dimension;
            internal DimensionUpdate(string path, string configuration, string dimension, int kind, double value, bool native)
            { Path = path; Configuration = configuration; Dimension = dimension; Kind = kind; Value = value; Native = native; }
        }

        private static IEnumerable<EquationScope> Scopes(IModelDoc2 root, IEnumerable<IComponent2> components, bool trackedOnly = false)
        {
            yield return new EquationScope(root);
            var owners = new Dictionary<string, IComponent2>(StringComparer.Ordinal);
            foreach (var component in components)
                for (var owner = IsAssembly(component) ? component : component.GetParent(); owner != null; owner = owner.GetParent())
                    owners[Address(owner)] = owner;
            foreach (var owner in owners.Values)
            {
                var model = owner.GetModelDoc2() as IModelDoc2;
                if (model == null && trackedOnly) continue;
                if (trackedOnly && !SolidWorksBaseBridgeAngleDriver.IsOrdinaryBasePath(owner.GetPathName()) &&
                    string.IsNullOrWhiteSpace(PropertyValue(Properties(model, owner.ReferencedConfiguration), PropertyName))) continue;
                yield return new EquationScope(owner);
            }
        }

        internal static string Address(IComponent2 component) => string.Join("/", Ancestors(component)
            .Reverse().Select(item => item.GetID().ToString(CultureInfo.InvariantCulture)));

        private static bool UnderAddress(string candidate, string parent) => candidate == parent ||
            candidate.StartsWith(parent + "/", StringComparison.Ordinal);

        private static IEnumerable<IComponent2> Ancestors(IComponent2 component)
        {
            for (var item = component; item != null; item = item.GetParent()) yield return item;
        }

        private static string NativeReference(IComponent2 component)
        {
            var fields = Ancestors(component).Select(item => ParentDimensionDependency.ComponentReference(
                item.Name2.Split('/').Last(), IsAssembly(item))).ToArray();
            return fields.Any(field => field == null) ? null : string.Join("@", fields);
        }

        private static string ReferenceFamilyPath(string reference)
        {
            if (reference == null) return null;
            var parts = reference.Split('@');
            var result = new List<string>();
            foreach (var part in parts)
            {
                var instance = part.LastIndexOf('<');
                if (instance <= 0) return null;
                result.Add(string.Join(".", part.Substring(0, instance).Split('.').Take(3)).ToUpperInvariant());
            }
            return string.Join("/", result);
        }

        private static string PhysicalModel(IComponent2 component) => Path.GetFullPath(component.GetPathName()) + "|" + component.ReferencedConfiguration;
        private static bool IsAssembly(IComponent2 component) => string.Equals(Path.GetExtension(component.GetPathName()), ".SLDASM", StringComparison.OrdinalIgnoreCase);

        private static string Family(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var segments = name.Split('.');
            return segments.Length < 3 ? name : string.Join(".", segments.Take(3));
        }

        private static IComponent2[] Components(IModelDoc2 root, bool includeSuppressed = false) =>
            (((IAssemblyDoc)root).GetComponents(false) as object[] ?? Array.Empty<object>()).OfType<IComponent2>()
                .Where(component => (includeSuppressed || !component.IsSuppressed()) && !string.IsNullOrWhiteSpace(component.GetPathName())).ToArray();

        private static IComponent2 Resolve(IEnumerable<IComponent2> components, string address, string owner)
        {
            var matches = components.Where(component => Address(component) == address).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("无法唯一反读上级尺寸驱动实例：" + owner + "/" + address);
            return matches[0];
        }

        private static IModelDoc2 TargetModel(IComponent2 component) => component.GetModelDoc2() as IModelDoc2
            ?? throw new InvalidOperationException("上级尺寸驱动模型未解析：" + component.Name2);

        private static Dimension RequireDimension(IModelDoc2 model, string name) => model.Parameter(name) as Dimension
            ?? throw new InvalidOperationException("上级尺寸驱动的原尺寸不存在：" + model.GetPathName() + "/" + name);

        private static double ReadDimension(IModelDoc2 model, DimensionUpdate update)
        {
            var values = RequireDimension(model, update.Dimension).GetSystemValue3((int)swInConfigurationOpts_e.swSpecifyConfiguration,
                new[] { update.Configuration }) as double[];
            if (values?.Length != 1 || double.IsNaN(values[0]) || double.IsInfinity(values[0]))
                throw new InvalidOperationException("无法反读上级驱动目标尺寸：" + update.Label);
            return values[0];
        }

        private static double Engineering(double value, int kind)
        {
            switch ((swDimensionParamType_e)kind)
            {
                case swDimensionParamType_e.swDimensionParamTypeDoubleLinear: return value * 1000d;
                case swDimensionParamType_e.swDimensionParamTypeDoubleAngular: return value * 180d / Math.PI;
                case swDimensionParamType_e.swDimensionParamTypeInteger: return value;
                default: throw new InvalidOperationException("上级方程目标尺寸的单位不支持：" + kind);
            }
        }

        private static string Display(double value, int kind) => Engineering(value, kind).ToString("0.0", CultureInfo.InvariantCulture)
            + (kind == (int)swDimensionParamType_e.swDimensionParamTypeDoubleLinear ? " mm" :
                kind == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular ? "°" : string.Empty);

        private static bool Close(double actual, double desired, int kind) => Math.Abs(Engineering(actual - desired, kind)) <= BridgeDesignPrecision.LengthToleranceMm;

        private static void RequireClose(double actual, DimensionUpdate update)
        {
            if (!Close(actual, update.Value, update.Kind))
                throw new InvalidOperationException("上级驱动尺寸反读不符：" + update.Label + "，实际 " + Display(actual, update.Kind)
                    + "，目标 " + Display(update.Value, update.Kind) + "。");
        }

        private static void RequireRebuild(IModelDoc2 model)
        {
            if (!model.EditRebuild3()) throw new InvalidOperationException("上级尺寸同步后模型求解失败：" + model.GetPathName());
        }

        private void RequireOwned(string path)
        {
            if (!_owns(path)) throw new InvalidOperationException("上级尺寸驱动引用位于本次事务之外：" + path);
        }

        private static ICustomPropertyManager Properties(IModelDoc2 model, string configuration = null) =>
            model.Extension.CustomPropertyManager[configuration ?? model.ConfigurationManager.ActiveConfiguration.Name];

        private static string ChunkName(int index) => PropertyName + "." + index.ToString(CultureInfo.InvariantCulture);

        private static string PropertyValue(ICustomPropertyManager properties, string name)
        {
            properties.Get4(name, false, out var raw, out _);
            return raw;
        }

        private static ParentDimensionDependency[] ReadRules(IModelDoc2 model)
        {
            var properties = Properties(model);
            var manifest = PropertyValue(properties, PropertyName);
            if (string.IsNullOrWhiteSpace(manifest)) return Array.Empty<ParentDimensionDependency>();
            try
            {
                var raw = ParentDimensionStorage.Decode(manifest, index => PropertyValue(properties, ChunkName(index)));
                var rules = JsonConvert.DeserializeObject<ParentDimensionDependency[]>(raw);
                if (rules == null || rules.Any(rule => rule == null))
                    throw new InvalidOperationException("上级尺寸驱动记录包含空规则。");
                return rules;
            }
            catch (Exception error) when (error is JsonException || error is InvalidOperationException || error is FormatException)
            { throw new InvalidOperationException("无法读取上级尺寸驱动记录：" + model.GetPathName(), error); }
        }

        private static void WriteRules(IModelDoc2 model, IReadOnlyList<ParentDimensionDependency> rules)
        {
            var value = JsonConvert.SerializeObject(rules);
            var properties = Properties(model);
            var previous = PropertyValue(properties, PropertyName);
            if (ParentDimensionStorage.Decode(previous, index => PropertyValue(properties, ChunkName(index))) == value) return;
            var chunks = ParentDimensionStorage.Encode(value);
            for (var index = 0; index < chunks.Length; index++) Write(ChunkName(index), chunks[index]);
            Write(PropertyName, ParentDimensionStorage.Manifest(chunks.Length));
            for (var index = chunks.Length; index < ParentDimensionStorage.Count(previous); index++)
            {
                var name = ChunkName(index);
                properties.Delete2(name);
                if (!string.IsNullOrEmpty(PropertyValue(properties, name)))
                    throw new InvalidOperationException("无法清理已替换的上级尺寸记录：" + model.GetPathName() + "/" + name);
            }

            void Write(string name, string text)
            {
                if (PropertyValue(properties, name) == text) return;
                properties.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, text, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                if (PropertyValue(properties, name) != text)
                    throw new InvalidOperationException("无法保存上级尺寸驱动记录：" + model.GetPathName() + "/" + name);
            }
        }
    }
}
