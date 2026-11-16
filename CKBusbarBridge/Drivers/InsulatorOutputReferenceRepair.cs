using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    // 仅处理模板契约明确列出的当前副本；不搜索磁盘，也不访问旧项目。
    internal sealed class InsulatorOutputReferenceRepair
    {
        internal void Repair(IEnumerable<string> outputFiles, Func<string, string[]> readReferences,
            Func<string, bool> exists, Func<string, bool> isOpen, Func<string, string, string, bool> replace)
        {
            var files = outputFiles.Select(Path.GetFullPath).ToArray();
            if (files.Length == 0) throw new InvalidOperationException("绝缘子输出文件契约为空。");
            var directory = Path.GetDirectoryName(files[0]);
            if (files.Any(p => !string.Equals(Path.GetDirectoryName(p), directory, StringComparison.OrdinalIgnoreCase)) ||
                files.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
                throw new InvalidOperationException("绝缘子输出文件契约不唯一或跨目录。");
            var targets = files.ToDictionary(Path.GetFileName, p => p, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
                if (!exists(file)) throw new FileNotFoundException("当前项目缺少绝缘子契约文件：" + file, file);

            // 先校验整批，避免未知引用、缺件或打开状态使修复只完成一半。
            var rewrites = new List<Tuple<string, string, string>>();
            foreach (var file in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                foreach (var reference in readReferences(file))
                {
                    var target = Resolve(reference, targets);
                    if (!string.Equals(reference, target, StringComparison.OrdinalIgnoreCase))
                        rewrites.Add(Tuple.Create(file, reference, target));
                }
            if (rewrites.Count > 0 && files.Any(isOpen))
                throw new InvalidOperationException("绝缘子旧引用需要迁移，请先关闭当前副本及其宿主装配体：" + directory);
            foreach (var item in rewrites)
                if (!replace(item.Item1, item.Item2, item.Item3))
                    throw new InvalidOperationException("绝缘子引用迁移失败：" + item.Item1 + "；" + item.Item2 + " → " + item.Item3);

            // API 返回成功仍需重读磁盘；包含压缩件和工程图的直接引用都必须落在当前契约内。
            foreach (var file in files)
                foreach (var reference in readReferences(file))
                    if (!string.Equals(reference, Resolve(reference, targets), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("绝缘子迁移后仍残留旧磁盘引用：" + file + "；" + reference);
        }

        private string Resolve(string reference, IReadOnlyDictionary<string, string> targets)
        {
            if (string.IsNullOrWhiteSpace(reference) || !targets.TryGetValue(Path.GetFileName(reference), out var target))
                throw new InvalidOperationException("绝缘子副本包含契约外引用，无法确定当前项目文件：" + reference);
            return target;
        }
    }
}
