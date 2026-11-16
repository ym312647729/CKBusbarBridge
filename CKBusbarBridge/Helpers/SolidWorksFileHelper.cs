using Microsoft.VisualBasic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridge.Drivers
{
    public class SolidWorksFileHelper
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private SldWorks swApp = null;

        public SolidWorksFileHelper(SldWorks mSldWorks)
        {
            swApp = mSldWorks;
        }

        internal static bool RequiresDocumentSaveToNames(
            string renameSuffix,
            IEnumerable<string> excludeFileNameList)
        {
            // 文件名后缀由 PackAndGo.AddSuffix 原生处理。仅排除文档时才需要重新提交
            // SaveToNames；为追加后缀手工提交整个数组，会使部分 SolidWorks 版本只保存
            // 模型而跳过工程图。
            return excludeFileNameList != null
                && excludeFileNameList.Any(x => !string.IsNullOrWhiteSpace(x));
        }

        private const long PackAndGoFreeSpaceReserveBytes = 64L * 1024L * 1024L;

        internal static long CalculateRequiredPackAndGoFreeSpace(long sourceDocumentBytes)
        {
            if (sourceDocumentBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceDocumentBytes));
            }

            return sourceDocumentBytes > long.MaxValue - PackAndGoFreeSpaceReserveBytes
                ? long.MaxValue
                : sourceDocumentBytes + PackAndGoFreeSpaceReserveBytes;
        }

        private static void EnsurePackAndGoTargetHasFreeSpace(
            string targetPath,
            IEnumerable<string> sourceDocumentNames)
        {
            var targetRoot = Path.GetPathRoot(Path.GetFullPath(targetPath));
            if (string.IsNullOrWhiteSpace(targetRoot)
                || targetRoot.Length < 2
                || targetRoot[1] != ':')
            {
                // DriveInfo 对部分 UNC/映射路径不可用，交由 PackAndGo 自身处理。
                return;
            }

            long sourceDocumentBytes = 0;
            if (sourceDocumentNames != null)
            {
                foreach (var sourcePath in sourceDocumentNames
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(sourcePath))
                    {
                        continue;
                    }

                    var fileBytes = new FileInfo(sourcePath).Length;
                    sourceDocumentBytes = sourceDocumentBytes > long.MaxValue - fileBytes
                        ? long.MaxValue
                        : sourceDocumentBytes + fileBytes;
                }
            }

            var requiredBytes = CalculateRequiredPackAndGoFreeSpace(sourceDocumentBytes);
            var drive = new DriveInfo(targetRoot);
            if (!drive.IsReady || drive.AvailableFreeSpace >= requiredBytes)
            {
                return;
            }

            throw new IOException(
                $"PackAndGo 目标磁盘空间不足：{targetRoot} 当前可用 {FormatByteSize(drive.AvailableFreeSpace)}，"
                + $"本次至少需要 {FormatByteSize(requiredBytes)}（源文档 {FormatByteSize(sourceDocumentBytes)}，"
                + $"另保留 {FormatByteSize(PackAndGoFreeSpaceReserveBytes)} 临时空间）。请释放磁盘空间或更换项目根目录后重试。");
        }

        private static string FormatByteSize(long bytes)
        {
            const double megabyte = 1024d * 1024d;
            const double gigabyte = 1024d * 1024d * 1024d;
            return bytes >= gigabyte
                ? (bytes / gigabyte).ToString("0.##", CultureInfo.InvariantCulture) + " GB"
                : (bytes / megabyte).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>
        /// 调用 SolidWorks 的 MakeIndependent。该方法在 <see cref="IAssemblyDoc"/> 上，
        /// 不在 <see cref="IModelDoc2"/> 上；误调后者会抛出“子过程或函数未定义”。
        /// </summary>
        internal static bool InvokeMakeIndependent(IModelDoc2 model, string independentAssemblyPath)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            if (string.IsNullOrWhiteSpace(independentAssemblyPath))
            {
                throw new ArgumentException("独立模型路径不能为空。", nameof(independentAssemblyPath));
            }

            var assembly = model as IAssemblyDoc;
            if (assembly == null)
            {
                throw new InvalidOperationException(
                    "MakeIndependent 仅适用于装配体文档，当前文档类型不支持。");
            }

            return assembly.MakeIndependent(independentAssemblyPath);
        }

        /// <summary>
        /// 将源模型同目录下的工程图复制到独立模型目录，并更新模型引用。
        /// </summary>
        internal static void CopyDrawingForIndependentModel(
            ISldWorks swApp,
            string sourceModelPath,
            string targetModelPath)
        {
            if (swApp == null)
            {
                throw new ArgumentNullException(nameof(swApp));
            }

            if (string.IsNullOrWhiteSpace(sourceModelPath)
                || string.IsNullOrWhiteSpace(targetModelPath))
            {
                return;
            }

            var sourceDirectory = Path.GetDirectoryName(sourceModelPath);
            var targetDirectory = Path.GetDirectoryName(targetModelPath);
            if (string.IsNullOrWhiteSpace(sourceDirectory)
                || string.IsNullOrWhiteSpace(targetDirectory))
            {
                return;
            }

            var sourceDrawingPath = Path.Combine(
                sourceDirectory,
                Path.GetFileNameWithoutExtension(sourceModelPath) + DrawingFileExtension);
            if (!File.Exists(sourceDrawingPath))
            {
                return;
            }

            Directory.CreateDirectory(targetDirectory);
            var targetDrawingPath = Path.Combine(
                targetDirectory,
                Path.GetFileNameWithoutExtension(targetModelPath) + DrawingFileExtension);
            if (!File.Exists(targetDrawingPath))
            {
                File.Copy(sourceDrawingPath, targetDrawingPath);
            }

            TryReplaceDrawingReference(swApp, targetDrawingPath, sourceModelPath, targetModelPath);
        }

        private static bool TryReplaceDrawingReference(
            ISldWorks swApp,
            string drawingPath,
            string oldReferencedPath,
            string newReferencedPath)
        {
            try
            {
                if (swApp.ReplaceReferencedDocument(drawingPath, oldReferencedPath, newReferencedPath))
                {
                    return true;
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                return swApp.ReplaceReferencedDocument(
                    drawingPath,
                    Path.GetFileName(oldReferencedPath),
                    newReferencedPath);
            }
            catch
            {
                // ignore
                return false;
            }
        }

        private static bool IsSolidWorksModelPath(string path)
        {
            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".SLDASM", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".SLDPRT", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDrawingPath(string path)
        {
            return string.Equals(
                Path.GetExtension(path),
                DrawingFileExtension,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 某些 SolidWorks 版本会在 SavePackAndGo 返回成功时遗漏 IncludeDrawings
        /// 枚举出的工程图。此时从源工程图补齐目标文件，并将图纸引用从源模型切换到
        /// PackAndGo 后的目标模型，避免留下仍指向模板目录的工程图。
        /// </summary>
        private void RecoverMissingPackAndGoDrawings(
            string[] sourceDocumentNames,
            string[] targetDocumentNames,
            IEnumerable<int> missingDrawingIndexes)
        {
            if (sourceDocumentNames == null
                || targetDocumentNames == null
                || sourceDocumentNames.Length != targetDocumentNames.Length)
            {
                return;
            }

            var modelTargetsBySourcePath = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            var modelTargetsByFileName = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < sourceDocumentNames.Length; index++)
            {
                var sourcePath = sourceDocumentNames[index];
                var targetPath = targetDocumentNames[index];
                if (string.IsNullOrWhiteSpace(sourcePath)
                    || string.IsNullOrWhiteSpace(targetPath)
                    || !IsSolidWorksModelPath(sourcePath)
                    || !IsSolidWorksModelPath(targetPath))
                {
                    continue;
                }

                var fullSourcePath = Path.GetFullPath(sourcePath);
                modelTargetsBySourcePath[fullSourcePath] = targetPath;
                modelTargetsByFileName[Path.GetFileName(sourcePath)] = targetPath;
            }

            foreach (var index in missingDrawingIndexes.Distinct())
            {
                if (index < 0 || index >= sourceDocumentNames.Length)
                {
                    continue;
                }

                var sourceDrawingPath = sourceDocumentNames[index];
                var targetDrawingPath = targetDocumentNames[index];
                if (!IsDrawingPath(sourceDrawingPath)
                    || !IsDrawingPath(targetDrawingPath)
                    || !File.Exists(sourceDrawingPath)
                    || File.Exists(targetDrawingPath))
                {
                    continue;
                }

                var targetDirectory = Path.GetDirectoryName(targetDrawingPath);
                if (string.IsNullOrWhiteSpace(targetDirectory))
                {
                    continue;
                }

                Directory.CreateDirectory(targetDirectory);
                File.Copy(sourceDrawingPath, targetDrawingPath, false);

                try
                {
                    var dependencies = swApp.GetDocumentDependencies2(
                        sourceDrawingPath,
                        true,
                        true,
                        false) as string[];
                    if (dependencies == null)
                    {
                        throw new InvalidOperationException(
                            $"无法读取源工程图引用：{sourceDrawingPath}");
                    }

                    var replacementCandidates = new Dictionary<string, List<string>>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var dependency in dependencies)
                    {
                        if (string.IsNullOrWhiteSpace(dependency)
                            || !IsSolidWorksModelPath(dependency))
                        {
                            continue;
                        }

                        string replacementPath;
                        var fullDependencyPath = Path.IsPathRooted(dependency)
                            ? Path.GetFullPath(dependency)
                            : null;
                        if (!string.IsNullOrWhiteSpace(fullDependencyPath)
                            && modelTargetsBySourcePath.TryGetValue(
                                fullDependencyPath,
                                out replacementPath))
                        {
                            AddReplacementCandidate(
                                replacementCandidates,
                                replacementPath,
                                dependency);
                            continue;
                        }

                        if (modelTargetsByFileName.TryGetValue(
                            Path.GetFileName(dependency),
                            out replacementPath))
                        {
                            AddReplacementCandidate(
                                replacementCandidates,
                                replacementPath,
                                dependency);
                        }
                    }

                    foreach (var replacement in replacementCandidates)
                    {
                        var replaced = replacement.Value.Any(oldReference =>
                            TryReplaceDrawingReference(
                                swApp,
                                targetDrawingPath,
                                oldReference,
                                replacement.Key));
                        if (!replaced)
                        {
                            throw new InvalidOperationException(
                                $"无法更新工程图引用：{targetDrawingPath}；旧引用候选：{string.Join("、", replacement.Value)}；新引用：{replacement.Key}");
                        }
                    }
                }
                catch
                {
                    try
                    {
                        File.Delete(targetDrawingPath);
                    }
                    catch
                    {
                        // 保留原始引用修复异常；外层事务仍会清理本次生成目录。
                    }

                    throw;
                }
            }
        }

        private static void AddReplacementCandidate(
            IDictionary<string, List<string>> candidates,
            string targetPath,
            string oldReference)
        {
            List<string> references;
            if (!candidates.TryGetValue(targetPath, out references))
            {
                references = new List<string>();
                candidates[targetPath] = references;
            }

            if (!references.Contains(oldReference, StringComparer.OrdinalIgnoreCase))
            {
                references.Add(oldReference);
            }
        }

        /// <summary>
        /// 确认 SolidWorks 实际返回的文档就是请求路径对应的文档。
        /// </summary>
        /// <remarks>
        /// SolidWorks 会按文档标题复用已打开模型；不同目录中存在同名文件时，
        /// <c>OpenDoc6</c> 可能返回另一份已加载模型。必须在继续操作前拦截，
        /// 否则会修改当前装配体之外的错误模型。
        /// </remarks>
        internal static void ValidateOpenedDocumentPath(
            string requestedPath,
            string actualPath,
            int errors,
            int warnings)
        {
            var requestedFullPath = Path.GetFullPath(requestedPath);
            if (string.IsNullOrWhiteSpace(actualPath))
            {
                throw new InvalidOperationException(
                    $"SolidWorks 已返回文档对象，但无法确认其磁盘路径。请求：{requestedFullPath}；错误码：{errors}；警告码：{warnings}。");
            }

            var actualFullPath = Path.GetFullPath(actualPath);
            if (!string.Equals(
                requestedFullPath,
                actualFullPath,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SolidWorks 返回了同名但不同路径的文档，已停止当前操作。请求：{requestedFullPath}；实际：{actualFullPath}；错误码：{errors}；警告码：{warnings}。");
            }
        }

        private const string DrawingFileExtension = ".SLDDRW";

        /// <summary>
        /// 若与模型同目录下存在同名工程图（.SLDDRW），则将其重命名为新基名对应的工程图。
        /// </summary>
        /// <param name="directory">模型文件所在目录。</param>
        /// <param name="oldBaseName">重命名前的文件名（不含扩展名）。</param>
        /// <param name="newBaseName">重命名后的文件名（不含扩展名）。</param>
        /// <returns>工程图已存在且重命名成功返回 true，否则返回 false。</returns>
        public static bool TryRenameCompanionDrawing(string directory, string oldBaseName, string newBaseName)
        {
            if (string.IsNullOrWhiteSpace(directory) ||
                string.IsNullOrWhiteSpace(oldBaseName) ||
                string.IsNullOrWhiteSpace(newBaseName))
            {
                return false;
            }

            if (string.Equals(oldBaseName, newBaseName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var oldDrawingPath = Path.Combine(directory, oldBaseName + DrawingFileExtension);
            var newDrawingPath = Path.Combine(directory, newBaseName + DrawingFileExtension);

            if (!File.Exists(oldDrawingPath))
            {
                return false;
            }

            if (File.Exists(newDrawingPath))
            {
                return false;
            }

            try
            {
                File.Move(oldDrawingPath, newDrawingPath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 若工程图已在 SolidWorks 中打开则先尝试关闭，再执行同名工程图重命名。
        /// </summary>
        public bool TryRenameCompanionDrawingWithClose(string directory, string oldBaseName, string newBaseName)
        {
            if (string.IsNullOrWhiteSpace(directory) ||
                string.IsNullOrWhiteSpace(oldBaseName) ||
                string.IsNullOrWhiteSpace(newBaseName))
            {
                return false;
            }

            var oldDrawingPath = Path.Combine(directory, oldBaseName + DrawingFileExtension);
            if (File.Exists(oldDrawingPath))
            {
                TryCloseDrawingDocument(oldDrawingPath);
            }

            return TryRenameCompanionDrawing(directory, oldBaseName, newBaseName);
        }

        private void TryCloseDrawingDocument(string drawingPath)
        {
            if (swApp == null || string.IsNullOrWhiteSpace(drawingPath))
            {
                return;
            }

            try
            {
                swApp.CloseDoc(drawingPath);
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// PackAndGo 并在打包过程中批量改名（通过 PackAndGo 原生 AddSuffix 设置后缀）。
        /// </summary>
        /// <param name="sourceFile">源文件路径</param>
        /// <param name="targetPath">输出目录</param>
        /// <param name="taskNumber">任务号</param>
        /// <param name="serial3">流水号（三位段号等）</param>
        /// <param name="ExcludeFileNameList">过滤不打包文件名关键字列表（命中则对应 SaveToName 置空）</param>
        public void CopyFileUsePackAndGo3(string sourceFile, string targetPath, string taskNumber, string serial3, List<string> ExcludeFileNameList = null)
        {
            var suffix = string.IsNullOrWhiteSpace(taskNumber) || string.IsNullOrWhiteSpace(serial3)
                ? null
                : $".{taskNumber}.{serial3}";
            CopyFileUsePackAndGoWithSuffix(sourceFile, targetPath, suffix, ExcludeFileNameList);
        }

        /// <summary>
        /// PackAndGo 并在打包过程中为所有文档追加文件名后缀（如 <c>.5GX355001.0001</c>）。
        /// </summary>
        /// <param name="renameSuffix">追加的文件名后缀；为空则不改名。</param>
        /// <param name="ensureLeadingDot">为 true 时，若后缀不以点开头则自动补点（图号后缀场景）；为 false 时按原样追加（如 <c>-205-120-180</c>）。</param>
        public void CopyFileUsePackAndGoWithSuffix(string sourceFile, string targetPath, string renameSuffix, List<string> ExcludeFileNameList = null, bool ensureLeadingDot = true)
        {
            CopyFileUsePackAndGoWithSuffixCore(sourceFile, targetPath, renameSuffix, ExcludeFileNameList, ensureLeadingDot, false);
        }

        internal void CopyFileUsePackAndGoIncludingSuppressed(string sourceFile, string targetPath)
        {
            CopyFileUsePackAndGoWithSuffixCore(sourceFile, targetPath, string.Empty, null, false, true);
        }

        private void CopyFileUsePackAndGoWithSuffixCore(string sourceFile, string targetPath, string renameSuffix,
            List<string> ExcludeFileNameList, bool ensureLeadingDot, bool includeSuppressed)
        {
            if (swApp == null)
            {
                throw new InvalidOperationException("未获取到 SolidWorks 应用程序对象，无法执行 PackAndGo。");
            }

            if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
            {
                throw new FileNotFoundException($"PackAndGo 源文件不存在：{sourceFile}", sourceFile);
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("PackAndGo 目标目录不能为空。", nameof(targetPath));
            }

            Directory.CreateDirectory(targetPath);

            ModelDoc2 swModelDoc = null;
            ModelDocExtension swModelDocExt = null;
            PackAndGo swPackAndGo = null;
            var openedByHelper = false;
            var documentConflicts = new SolidWorksDocumentConflictGuard(swApp);
            var previouslyOpenPaths = documentConflicts.CaptureOpenPaths();
            string[] sourceDocumentNames = null;
            Exception packFailure = null;
            try
            {
                var openFile = sourceFile;
                int warnings = 0;
                int errors = 0;

                // 优先按完整路径枚举已打开文档，避免 SolidWorks 的同名查找返回其他目录的模型。
                documentConflicts.PrepareForOpen(openFile);
                swModelDoc = FindOpenDocumentByPath(openFile);
                if (swModelDoc == null)
                {
                    var documentType = ResolveDocumentType(openFile);
                    swModelDoc = swApp.OpenDoc6(
                        openFile,
                        documentType,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                        "",
                        ref errors,
                        ref warnings) as ModelDoc2;
                    openedByHelper = swModelDoc != null;
                }

                if (swModelDoc == null)
                {
                    throw new InvalidOperationException(
                        $"SolidWorks 无法打开 PackAndGo 源文件：{openFile}（错误码：{errors}，警告码：{warnings}）。");
                }

                ValidateOpenedDocumentPath(
                    openFile,
                    swModelDoc.GetPathName(),
                    errors,
                    warnings);

                swModelDocExt = swModelDoc.Extension as ModelDocExtension;
                if (swModelDocExt == null)
                {
                    throw new InvalidOperationException($"无法获取模型扩展对象：{openFile}");
                }

                // Get Pack and Go object
                Debug.Print("Pack and Go");
                swPackAndGo = swModelDocExt.GetPackAndGo() as PackAndGo;
                if (swPackAndGo == null)
                {
                    throw new InvalidOperationException($"无法创建 PackAndGo 对象：{openFile}");
                }

                swPackAndGo.IncludeDrawings = true;
                swPackAndGo.IncludeSimulationResults = false;
                swPackAndGo.IncludeToolboxComponents = false;
                if (includeSuppressed) swPackAndGo.IncludeSuppressed = true;
                swPackAndGo.FlattenToSingleFolder = true;

                var suffix = string.IsNullOrWhiteSpace(renameSuffix)
                    ? null
                    : (!ensureLeadingDot || renameSuffix.StartsWith(".", StringComparison.Ordinal) ? renameSuffix : "." + renameSuffix);
                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    // 使用 SolidWorks 原生后缀功能，让模型、工程图及内部引用由 PackAndGo
                    // 统一改名。不要为追加后缀手工重写整个 SaveToNames 数组。
                    swPackAndGo.AddSuffix = suffix;
                }

                // IncludeDrawings 和 AddSuffix 会改变文档集合及目标名称，必须在它们之后取数。
                var namesCount = swPackAndGo.GetDocumentNamesCount();
                Debug.Print("  Number of PackAndGo documents: " + namesCount);
                if (namesCount <= 0)
                {
                    throw new InvalidOperationException($"PackAndGo 未找到可保存的模型文档：{openFile}");
                }

                object getSourceFileNames;
                sourceDocumentNames = swPackAndGo.GetDocumentNames(out getSourceFileNames)
                    ? getSourceFileNames as string[]
                    : null;
                if (sourceDocumentNames == null)
                {
                    throw new InvalidOperationException($"PackAndGo 无法读取源文件列表，不能确认同名文档清理范围：{openFile}");
                }

                EnsurePackAndGoTargetHasFreeSpace(targetPath, sourceDocumentNames);

                // Set folder where to save the files
                if (!swPackAndGo.SetSaveToName(true, targetPath))
                {
                    throw new InvalidOperationException($"PackAndGo 无法设置目标目录：{targetPath}");
                }

                // Get current save-to paths and filenames of the assembly's documents
                object getFileNames;
                object getDocumentStatus;
                if (!swPackAndGo.GetDocumentSaveToNames(out getFileNames, out getDocumentStatus))
                {
                    throw new InvalidOperationException($"PackAndGo 无法读取目标文件列表：{openFile}");
                }

                var pgGetFileNames = getFileNames as string[];
                if (pgGetFileNames == null || pgGetFileNames.Length == 0)
                {
                    throw new InvalidOperationException($"PackAndGo 返回的目标文件列表为空：{openFile}");
                }

                var documentStatuses = getDocumentStatus as Array;
                Func<int, bool> isVirtualDocument = index =>
                {
                    if (documentStatuses != null
                        && index >= 0
                        && index < documentStatuses.Length)
                    {
                        try
                        {
                            var documentStatus = Convert.ToInt32(documentStatuses.GetValue(index), CultureInfo.InvariantCulture);
                            if (documentStatus == (int)swPackAndGoDocumentStatus_e.swPackAndGoDocumentStatus_Virtual)
                            {
                                return true;
                            }
                        }
                        catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
                        {
                            // 某些 SolidWorks 版本返回的 COM 数组元素类型不固定，继续使用路径标记判断。
                        }
                    }

                    // 部分版本未完整返回文档类型，但会在保存路径中使用“^文件名”表示装配体内部的虚拟组件。
                    var saveToPath = index >= 0 && index < pgGetFileNames.Length
                        ? pgGetFileNames[index]
                        : null;
                    return !string.IsNullOrWhiteSpace(saveToPath)
                        && saveToPath.IndexOf('^') >= 0;
                };

                // 过滤不打包
                if (ExcludeFileNameList != null && ExcludeFileNameList.Count > 0)
                {
                    pgGetFileNames = pgGetFileNames.Select(x =>
                    {
                        if (string.IsNullOrEmpty(x))
                        {
                            return x;
                        }

                        if (ExcludeFileNameList.Any(y => !string.IsNullOrWhiteSpace(y)
                            && x.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            return string.Empty;
                        }

                        return x;
                    }).ToArray();
                }

                var expectedOutputIndexes = Enumerable.Range(0, pgGetFileNames.Length)
                    .Where(index => !string.IsNullOrWhiteSpace(pgGetFileNames[index])
                        && !isVirtualDocument(index))
                    .ToArray();
                if (expectedOutputIndexes.Length == 0)
                {
                    throw new InvalidOperationException($"PackAndGo 的全部文件都被排除，未生成任何输出：{openFile}");
                }

                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    var suffixNotApplied = expectedOutputIndexes
                        .Select(index => pgGetFileNames[index])
                        .Where(path => !Path.GetFileNameWithoutExtension(path)
                            .EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (suffixNotApplied.Length > 0)
                    {
                        throw new InvalidOperationException(
                            $"PackAndGo 未能对 {suffixNotApplied.Length} 个目标文件应用后缀 {suffix}：{string.Join("；", suffixNotApplied)}");
                    }
                }

                // 后缀由 AddSuffix 原生处理；只有需要排除文件时才提交 SaveToNames。
                var requiresDocumentSaveToNames = RequiresDocumentSaveToNames(
                    renameSuffix,
                    ExcludeFileNameList);
                if (requiresDocumentSaveToNames
                    && !swPackAndGo.SetDocumentSaveToNames(pgGetFileNames))
                {
                    throw new InvalidOperationException($"PackAndGo 无法应用目标文件列表：{openFile}");
                }

                if (!requiresDocumentSaveToNames)
                {
                    Debug.Print("  Skip SetDocumentSaveToNames: target file list is unchanged.");
                }

                // 使用最终清单，包含后缀、排除项和虚拟组件过滤；不关闭本次打包所需的源文档。
                documentConflicts.PrepareForSave(
                    expectedOutputIndexes.Select(index => pgGetFileNames[index]),
                    sourceDocumentNames.Concat(new[] { openFile }));
                var statuses = swModelDocExt.SavePackAndGo(swPackAndGo) as int[];
                if (statuses == null)
                {
                    throw new InvalidOperationException($"PackAndGo 未返回保存状态：{openFile}");
                }

                // SavePackAndGo 返回的是保存状态集合，但部分 SolidWorks 版本返回的状态数量
                // 可能少于 GetDocumentSaveToNames 的文件数量，不能按目标文件索引强制一一对应。
                // 已返回的非成功状态仍视为失败；所有目标文件是否完整则在下方通过落盘检查确认。
                var hasNonPhysicalOutputs = pgGetFileNames.Any(string.IsNullOrWhiteSpace)
                    || Enumerable.Range(0, pgGetFileNames.Length).Any(isVirtualDocument);
                var failedStatuses = statuses
                    .Where(status => status != (int)swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_Succeed
                        && !(hasNonPhysicalOutputs
                            && status == (int)swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_SaveToEmpty))
                    .Select(status => ((swPackAndGoSaveStatus_e)status).ToString())
                    .ToArray();
                if (failedStatuses.Length > 0)
                {
                    throw new IOException(
                        $"PackAndGo 返回 {failedStatuses.Length} 个失败状态：{string.Join("；", failedStatuses)}");
                }

                var missingOutputIndexes = expectedOutputIndexes
                    .Where(index => !File.Exists(pgGetFileNames[index]))
                    .ToArray();
                var missingDrawingIndexes = missingOutputIndexes
                    .Where(index => IsDrawingPath(pgGetFileNames[index]))
                    .ToArray();
                if (missingDrawingIndexes.Length > 0)
                {
                    RecoverMissingPackAndGoDrawings(
                        sourceDocumentNames,
                        pgGetFileNames,
                        missingDrawingIndexes);
                }

                var missingModelOutputs = expectedOutputIndexes
                    .Select(index => pgGetFileNames[index])
                    .Where(path => !IsDrawingPath(path) && !File.Exists(path))
                    .ToArray();
                var missingDrawingOutputs = expectedOutputIndexes
                    .Select(index => pgGetFileNames[index])
                    .Where(path => IsDrawingPath(path) && !File.Exists(path))
                    .ToArray();
                if (missingModelOutputs.Length > 0 || missingDrawingOutputs.Length > 0)
                {
                    var details = new List<string>();
                    if (missingModelOutputs.Length > 0)
                    {
                        details.Add(
                            $"模型文件 {missingModelOutputs.Length} 个：{string.Join("；", missingModelOutputs)}");
                    }

                    if (missingDrawingOutputs.Length > 0)
                    {
                        details.Add(
                            $"工程图 {missingDrawingOutputs.Length} 个：{string.Join("；", missingDrawingOutputs)}");
                    }

                    throw new FileNotFoundException(
                        $"PackAndGo 返回成功，但目标文件未完整落盘。{string.Join("；", details)}",
                        missingModelOutputs.FirstOrDefault() ?? missingDrawingOutputs[0]);
                }
            }
            catch (Exception exception)
            {
                packFailure = exception;
                Logger.Error(exception, "PackAndGo 失败：源={0}；目标={1}", sourceFile, targetPath);
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("PackAndGo 失败", exception, sourceFile + " -> " + targetPath);
                throw;
            }
            finally
            {
                // PackAndGo 和扩展对象由本方法取得，先释放其 COM 引用，再关闭源文档。
                // 否则 SolidWorks 已执行完保存，Windows 仍可能认为临时模型目录正在使用。
                ReleaseComReference(swPackAndGo);
                ReleaseComReference(swModelDocExt);

                if (openedByHelper)
                {
                    ReleaseComReference(swModelDoc);
                    try
                    {
                        // 按实际源清单释放本次临时加载的文档；不保存模板，也不关闭调用前已打开的文档。
                        documentConflicts.DiscardOpenedDocuments(
                            (sourceDocumentNames ?? new string[0]).Concat(new[] { sourceFile }), previouslyOpenPaths);
                    }
                    catch (Exception cleanupException)
                    {
                        Logger.Error(cleanupException, "PackAndGo 源文档缓存清理失败：{0}", sourceFile);
                        CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("PackAndGo 源文档清理失败", cleanupException, sourceFile);
                        if (packFailure == null) throw;
                        packFailure.Data["PackAndGoSourceCleanupWarning"] = cleanupException.ToString();
                    }
                }
            }
        }

        private ModelDoc2 FindOpenDocumentByPath(string filePath)
        {
            var fullPath = Path.GetFullPath(filePath);
            var documents = swApp.GetDocuments() as object[];
            if (documents == null)
            {
                return null;
            }

            foreach (var item in documents)
            {
                var document = item as ModelDoc2;
                if (document == null)
                {
                    continue;
                }

                try
                {
                    var documentPath = document.GetPathName();
                    if (!string.IsNullOrWhiteSpace(documentPath)
                        && string.Equals(
                            Path.GetFullPath(documentPath),
                            fullPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // 仅按完整路径复用已打开文档，避免把其他目录的同名模型当成源文件。
                        return document;
                    }
                }
                catch
                {
                    // 某个未保存或已失效文档不影响继续查找指定路径。
                }
            }

            return null;
        }

        internal static void ReleaseComReference(object comObject)
        {
            if (comObject == null || !Marshal.IsComObject(comObject))
            {
                return;
            }

            try
            {
                // 只释放本方法取得的一层 COM 引用，不使用 FinalReleaseComObject，
                // 避免同一 RCW 仍被 SolidWorks 或调用方共享时使其提前失效。
                Marshal.ReleaseComObject(comObject);
            }
            catch
            {
                // 资源释放失败不能覆盖 PackAndGo 的原始成功或失败结果。
            }
        }

        private static int ResolveDocumentType(string filePath)
        {
            var extension = Path.GetExtension(filePath);
            if (string.Equals(extension, ".SLDPRT", StringComparison.OrdinalIgnoreCase))
            {
                return (int)swDocumentTypes_e.swDocPART;
            }

            if (string.Equals(extension, ".SLDDRW", StringComparison.OrdinalIgnoreCase))
            {
                return (int)swDocumentTypes_e.swDocDRAWING;
            }

            if (string.Equals(extension, ".SLDASM", StringComparison.OrdinalIgnoreCase))
            {
                return (int)swDocumentTypes_e.swDocASSEMBLY;
            }

            throw new NotSupportedException($"PackAndGo 不支持该文件类型：{filePath}");
        }

        /// <summary>
        /// 不打开文件,复制文件
        /// </summary>
        /// <param name="sourcefile">源文件路径</param>
        /// <param name="target">目标文件</param>
        /// <param name="ReplaceFrom">需要替换的文件</param>
        /// <param name="ReplaceTo">替换成什么</param>
        /// <param name="likeFile">是否模糊匹配</param>
        /// <param name="ReplaceFrom2">替换字符2</param>
        /// <param name="ReplaceTo2">第二次替换成什么</param>
        /// <param name="likeFile2">模糊匹配?</param>
        public void CopySolidworksFile(string sourcefile, string target, string ReplaceFrom = "", string ReplaceTo = "", bool likeFile = true, string ReplaceFrom2 = "", string ReplaceTo2 = "", bool likeFile2 = false)
        {
            target = System.IO.Path.GetDirectoryName(target) + @"\";

            int sourcecount = 0;

            string[] sourcefiles = null;

            string[] targetfiles = null;

            object[] depends;

            var sourcefileName = System.IO.Path.GetFileName(sourcefile);
            var oldSourcefiles = sourcefiles;
            sourcefiles = new string[sourcecount + 1];
            if (oldSourcefiles != null)
                Array.Copy(oldSourcefiles, sourcefiles, Math.Min(sourcecount + 1, oldSourcefiles.Length));
            var oldTargetfiles = targetfiles;
            targetfiles = new string[sourcecount + 1];
            if (oldTargetfiles != null)
                Array.Copy(oldTargetfiles, targetfiles, Math.Min(sourcecount + 1, oldTargetfiles.Length));
            sourcefiles[sourcecount] = sourcefile;

            if (ReplaceFrom != "")
            {
                if (likeFile == true)
                    targetfiles[sourcecount] = target + sourcefileName.Replace(ReplaceFrom, ReplaceTo);
                else if (sourcefileName.ToUpper() == ReplaceFrom.ToUpper() && likeFile2 == true)
                    targetfiles[sourcecount] = target + sourcefileName.Replace(ReplaceFrom, ReplaceTo).Replace(ReplaceFrom2, ReplaceTo2);
                else
                    targetfiles[sourcecount] = target + sourcefileName.Replace(ReplaceFrom2, ReplaceTo2);
            }
            else
            {
                targetfiles[sourcecount] = target + sourcefileName;
            }

            if (FileSystem.Dir(targetfiles[sourcecount]) != "")
            {
                if (Microsoft.VisualBasic.Interaction.MsgBox(targetfiles[sourcecount] + "已经存在,是否替换?", Constants.vbYesNo, "文件打包") == Constants.vbNo)
                {
                    return;
                }
            }

            try
            {
                FileSystem.FileCopy(sourcefiles[sourcecount], targetfiles[sourcecount]);
            }
            catch (Exception ex)
            {
            }

            sourcecount = sourcecount + 1;

            if (FileSystem.Dir(Strings.Replace(sourcefile, ".SLDASM", ".SLDDRW")) != "")
            {
                oldSourcefiles = sourcefiles;
                sourcefiles = new string[sourcecount + 1];
                if (oldSourcefiles != null)
                    Array.Copy(oldSourcefiles, sourcefiles, Math.Min(sourcecount + 1, oldSourcefiles.Length));
                oldTargetfiles = targetfiles;
                targetfiles = new string[sourcecount + 1];
                if (oldTargetfiles != null)
                    Array.Copy(oldTargetfiles, targetfiles, Math.Min(sourcecount + 1, oldTargetfiles.Length));

                sourcefiles[sourcecount] = Strings.Replace(sourcefile, ".SLDASM", ".SLDDRW");
                sourcefileName = System.IO.Path.GetFileName(sourcefiles[sourcecount]);
                if (ReplaceFrom != "")
                {
                    if (likeFile == true)
                        targetfiles[sourcecount] = target + sourcefileName.Replace(ReplaceFrom, ReplaceTo);
                    else if (sourcefileName.ToUpper() == ReplaceFrom.ToUpper() && likeFile2 == true)
                        targetfiles[sourcecount] = target + sourcefileName.Replace(ReplaceFrom, ReplaceTo).Replace(ReplaceFrom2, ReplaceTo2);
                    else
                        targetfiles[sourcecount] = target + sourcefileName.Replace(ReplaceFrom2, ReplaceTo2);
                }
                else
                    targetfiles[sourcecount] = target + sourcefileName;

                //if (FileSystem.Dir(targetfiles[sourcecount]) != "")
                //{
                //    if (Interaction.MsgBox(targetfiles[sourcecount] + "已经存在,是否替换?", Constants.vbYesNo, "文件打包") == Constants.vbNo)
                //        return;
                //}

                try
                {
                    FileSystem.FileCopy(sourcefiles[sourcecount], targetfiles[sourcecount]);
                }
                catch (Exception ex)
                {
                }
                sourcecount = sourcecount + 1;
            }

            depends = (string[])swApp.GetDocumentDependencies2(sourcefile, true, true, false);

            if (depends == null)
                return;
            bool bRet;
            var idx = 1;

            while (idx <= Information.UBound(depends))
            {
                oldSourcefiles = sourcefiles;
                sourcefiles = new string[sourcecount + 1];
                if (oldSourcefiles != null)
                    Array.Copy(oldSourcefiles, sourcefiles, Math.Min(sourcecount + 1, oldSourcefiles.Length));
                oldTargetfiles = targetfiles;
                targetfiles = new string[sourcecount + 1];
                if (oldTargetfiles != null)
                    Array.Copy(oldTargetfiles, targetfiles, Math.Min(sourcecount + 1, oldTargetfiles.Length));

                sourcefiles[sourcecount] = depends[idx].ToString();
                sourcefileName = System.IO.Path.GetFileName(depends[idx].ToString());
                if (ReplaceFrom != "")
                {
                    if (likeFile == true)
                        targetfiles[sourcecount] = target + sourcefileName.Replace(ReplaceFrom, ReplaceTo);
                    else if (sourcefileName.ToUpper() == ReplaceFrom.ToUpper() && likeFile2 == true)
                        targetfiles[sourcecount] = target + sourcefileName.Replace(ReplaceFrom, ReplaceTo).Replace(ReplaceFrom2, ReplaceTo2);
                    else
                        targetfiles[sourcecount] = target + sourcefileName.Replace(ReplaceFrom2, ReplaceTo2);
                }
                else
                    targetfiles[sourcecount] = target + sourcefileName;

                try
                {
                    FileSystem.FileCopy(sourcefiles[sourcecount], targetfiles[sourcecount]);
                }
                catch (Exception ex)
                {
                }

                idx = idx + 2;
                sourcecount = sourcecount + 1;
            }

            // swApp.SendMsgToUser("Done")

            for (int n = 0; n <= sourcecount - 1; n++)
            {
                var NewName = targetfiles[n];

                var RefQ = swApp.GetDocumentDependenciesCount(NewName, 1, 1) / (double)2;

                // Debug.Print(NewName & "--->参考文件有  " & RefQ)

                if (RefQ > 0)
                {
                    var q = 0;
                    for (q = 0; q <= sourcecount - 1; q++)
                    {
                        if (n == q)
                        {
                        }
                        else
                            bRet = swApp.ReplaceReferencedDocument(targetfiles[n], System.IO.Path.GetFileName(sourcefiles[q]), targetfiles[q]);
                    }
                }
            }
        }


        /// <summary>
        /// 打包零件 工程图
        /// </summary>
        public void CopyFileUsePackAndGo(string sourceFile, string targetPath, string ReplaceFrom, string ReplaceTo)
        {

            try
            {
                ModelDoc2 swModelDoc = default(ModelDoc2);
                ModelDocExtension swModelDocExt = default(ModelDocExtension);
                PackAndGo swPackAndGo = default(PackAndGo);
                string openFile = null;
                bool status = false;
                int warnings = 0;
                int errors = 0;
                int i = 0;
                int namesCount = 0;
                string myPath = null;
                int[] statuses = null;

                // Open assembly
                openFile = sourceFile;

                //swApp.DocumentVisible(false,(int)swDocumentTypes_e.swDocASSEMBLY);

                if (openFile.ToUpper().EndsWith(".SLDPRT") || openFile.ToUpper().EndsWith(".sldprt"))
                {
                    swModelDoc = (ModelDoc2)swApp.OpenDoc6(openFile, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
                }
                else if (openFile.ToUpper().EndsWith(".SLDDRW") || openFile.ToUpper().EndsWith(".slddrw"))
                {
                    swModelDoc = (ModelDoc2)swApp.OpenDoc6(openFile, (int)swDocumentTypes_e.swDocDRAWING, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
                }
                else
                {
                    swModelDoc = (ModelDoc2)swApp.OpenDoc6(openFile, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
                }

                swModelDocExt = (ModelDocExtension)swModelDoc.Extension;

                // Get Pack and Go object
                Debug.Print("Pack and Go");
                swPackAndGo = (PackAndGo)swModelDocExt.GetPackAndGo();

                // Get number of documents in assembly
                namesCount = swPackAndGo.GetDocumentNamesCount();
                Debug.Print("  Number of model documents: " + namesCount);

                // Include any drawings, SOLIDWORKS Simulation results, and SOLIDWORKS Toolbox components
                swPackAndGo.IncludeDrawings = true;
                Debug.Print(" Include drawings: " + swPackAndGo.IncludeDrawings);
                swPackAndGo.IncludeSimulationResults = false;
                Debug.Print(" Include SOLIDWORKS Simulation results: " + swPackAndGo.IncludeSimulationResults);
                swPackAndGo.IncludeToolboxComponents = false;
                Debug.Print(" Include SOLIDWORKS Toolbox components: " + swPackAndGo.IncludeToolboxComponents);

                // Get current paths and filenames of the assembly's documents
                object fileNames;
                object[] pgFileNames = new object[namesCount - 1];
                status = swPackAndGo.GetDocumentNames(out fileNames);
                pgFileNames = (object[])fileNames;

                Debug.Print("");
                Debug.Print("  Current path and filenames: ");
                if ((pgFileNames != null))
                {
                    for (i = 0; i <= pgFileNames.GetUpperBound(0); i++)
                    {
                        Debug.Print("    The path and filename is: " + pgFileNames[i]);
                    }
                }

                // Get current save-to paths and filenames of the assembly's documents
                object pgFileStatus;
                status = swPackAndGo.GetDocumentSaveToNames(out fileNames, out pgFileStatus);
                pgFileNames = (object[])fileNames;
                Debug.Print("");
                Debug.Print("  Current default save-to filenames: ");
                if ((pgFileNames != null))
                {
                    for (i = 0; i <= pgFileNames.GetUpperBound(0); i++)
                    {
                        Debug.Print("   The path and filename is: " + pgFileNames[i]);
                    }
                }

                // Set folder where to save the files
                myPath = targetPath;
                status = swPackAndGo.SetSaveToName(true, myPath);

                // Flatten the Pack and Go folder structure; save all files to the root directory
                swPackAndGo.FlattenToSingleFolder = true;

                // Add a prefix and suffix to the filenames
                //swPackAndGo.AddPrefix = Prefix;
                //swPackAndGo.AddSuffix = Suffix;

                // Verify document paths and filenames after adding prefix and suffix
                object getFileNames;
                object getDocumentStatus;
                string[] pgGetFileNames = new string[namesCount - 1];

                status = swPackAndGo.GetDocumentSaveToNames(out getFileNames, out getDocumentStatus);
                pgGetFileNames = (string[])getFileNames;

                Debug.Print("");
                Debug.Print("  My Pack and Go path and filenames after adding prefix and suffix: ");
                for (i = pgGetFileNames.Count() - 1; i >= 0; i--)
                {
                    if (!pgGetFileNames[i].ToUpper().Contains("XXXXX") && !pgGetFileNames[i].ToUpper().Contains(ReplaceFrom.ToUpper()))
                    {
                        pgGetFileNames[i] = pgFileNames[i].ToString().ToUpper();
                    }
                    else
                    {
                        pgGetFileNames[i] = pgGetFileNames[i].ToUpper().Replace(ReplaceFrom, ReplaceTo);
                        Debug.Print("    My path and filename is: " + pgGetFileNames[i]);
                    }

                    // Debug.Print("    My path and filename is: " + pgGetFileNames[i]);

                    //string tempFolder = System.IO.Path.GetDirectoryName(pgGetFileNames[i]);
                    //string tempName = System.IO.Path.GetFileNameWithoutExtension(pgGetFileNames[i]);
                    //string tempExt = System.IO.Path.GetExtension(pgGetFileNames[i]);

                    //if (tempName.Contains(Prefix + " "))
                    //{
                    //    pgGetFileNames[i] = $@"{tempFolder}+ {tempName.Replace(Prefix + " ", Prefix)}{tempExt}";
                    //}
                }

                // Pack and Go
                swPackAndGo.SetDocumentSaveToNames(pgGetFileNames);

                statuses = (int[])swModelDocExt.SavePackAndGo(swPackAndGo);

                swApp.CloseDoc(openFile);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"PackAndGo 执行失败。源文件：{sourceFile}；目标目录：{targetPath}",
                    ex);
            }

            //swApp.CloseAllDocuments(true);
            //swApp.DocumentVisible(true, (int)swDocumentTypes_e.swDocASSEMBLY);
        }


        /// <summary>
        /// 打包零件 工程图
        /// </summary>
        public void CopyFileUsePackAndGo2(string sourceFile, string targetPath, string ReplaceFrom, string ReplaceTo, List<string> ExcludeFileNameList = null)
        {

            try
            {
                ModelDoc2 swModelDoc = default(ModelDoc2);
                ModelDocExtension swModelDocExt = default(ModelDocExtension);
                PackAndGo swPackAndGo = default(PackAndGo);
                string openFile = null;
                bool status = false;
                int warnings = 0;
                int errors = 0;
                int i = 0;
                int namesCount = 0;
                string myPath = null;
                int[] statuses = null;

                openFile = sourceFile;

                if (openFile.ToUpper().EndsWith(".SLDPRT") || openFile.ToUpper().EndsWith(".SLDPRT"))
                    swModelDoc = (ModelDoc2)swApp.OpenDoc6(openFile, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
                else if (openFile.ToUpper().EndsWith(".SLDDRW") || openFile.ToUpper().EndsWith(".SLDDRW"))
                    swModelDoc = (ModelDoc2)swApp.OpenDoc6(openFile, (int)swDocumentTypes_e.swDocDRAWING, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
                else
                    swModelDoc = (ModelDoc2)swApp.OpenDoc6(openFile, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);

                // 如果当前文档已打开，就切换过去
                if (warnings == 128)
                {
                    swModelDoc = swApp.IActivateDoc3(openFile, true, ref errors);
                }

                swModelDocExt = (ModelDocExtension)swModelDoc.Extension;

                // Get Pack and Go object
                Debug.Print("Pack and Go");
                swPackAndGo = (PackAndGo)swModelDocExt.GetPackAndGo();

                // Get number of documents in assembly
                namesCount = swPackAndGo.GetDocumentNamesCount();
                Debug.Print("  Number of model documents: " + namesCount);

                // Include any drawings, SOLIDWORKS Simulation results, and SOLIDWORKS Toolbox components
                swPackAndGo.IncludeDrawings = true;
                Debug.Print(" Include drawings: " + swPackAndGo.IncludeDrawings);
                swPackAndGo.IncludeSimulationResults = false;
                Debug.Print(" Include SOLIDWORKS Simulation results: " + swPackAndGo.IncludeSimulationResults);
                swPackAndGo.IncludeToolboxComponents = false;
                Debug.Print(" Include SOLIDWORKS Toolbox components: " + swPackAndGo.IncludeToolboxComponents);

                // Get current paths and filenames of the assembly's documents
                object fileNames;
                object[] pgFileNames = new object[namesCount - 1];
                status = swPackAndGo.GetDocumentNames(out fileNames);
                pgFileNames = (object[])fileNames;

                Debug.Print("");
                Debug.Print("  Current path and filenames: ");
                if ((pgFileNames != null))
                {
                    for (i = 0; i <= pgFileNames.GetUpperBound(0); i++)
                    {
                        Debug.Print("    The path and filename is: " + pgFileNames[i]);
                    }
                }

                // Get current save-to paths and filenames of the assembly's documents
                object pgFileStatus;
                status = swPackAndGo.GetDocumentSaveToNames(out fileNames, out pgFileStatus);
                pgFileNames = (object[])fileNames;
                Debug.Print("");
                Debug.Print("  Current default save-to filenames: ");
                if ((pgFileNames != null))
                {
                    for (i = 0; i <= pgFileNames.GetUpperBound(0); i++)
                    {
                        Debug.Print("   The path and filename is: " + pgFileNames[i]);
                    }
                }

                // Set folder where to save the files
                myPath = targetPath;
                status = swPackAndGo.SetSaveToName(true, myPath);

                // Flatten the Pack and Go folder structure; save all files to the root directory
                swPackAndGo.FlattenToSingleFolder = true;

                // Add a prefix and suffix to the filenames
                //swPackAndGo.AddPrefix = Prefix;
                //swPackAndGo.AddSuffix = Suffix;

                // Verify document paths and filenames after adding prefix and suffix
                object getFileNames;
                object getDocumentStatus;
                string[] pgGetFileNames = new string[namesCount - 1];

                status = swPackAndGo.GetDocumentSaveToNames(out getFileNames, out getDocumentStatus);
                pgGetFileNames = (string[])getFileNames;
                // 如果ExcludeFileNameList不为空并且Count>0,则需要在pgGetFileNames中将对应的内容设置为空字符串，匹配规则为pgGetFileNames[i]中包含ExcludeFileNameList中的任意一个元素
                if (ExcludeFileNameList != null && ExcludeFileNameList.Count > 0)
                {
                    Debug.Print("");
                    Debug.Print(" ExcludeFileNameList 不为空，需要过滤不打包: ");
                    //pgGetFileNames = pgGetFileNames.Where(x => !ExcludeFileNameList.Any(y => x.ToString().ToUpper().Contains(Path.GetFileNameWithoutExtension(y.ToUpper())))).ToArray();
                    pgGetFileNames = pgGetFileNames.Select(x =>
                    {
                        if (ExcludeFileNameList.Any(y => x.ToUpper().Contains(y.ToUpper())))
                        {
                            x = "";
                        }
                        return x;
                    }).ToArray();


                    Debug.Print("");
                    Debug.Print("  ExcludeFileNameList filter after default save-to filenames: ");
                    if ((pgGetFileNames != null))
                    {
                        for (i = 0; i <= pgGetFileNames.GetUpperBound(0); i++)
                        {
                            Debug.Print("   The path and filename is: " + pgGetFileNames[i]);
                        }
                    }
                }


                Debug.Print("");
                Debug.Print("  My Pack and Go path and filenames after adding prefix and suffix: ");
                for (i = pgGetFileNames.Count() - 1; i >= 0; i--)
                {
                    if (!pgGetFileNames[i].ToUpper().Contains("XXXXX") && !pgGetFileNames[i].ToUpper().Contains(ReplaceFrom.ToUpper()))
                    {
                        pgGetFileNames[i] = pgFileNames[i].ToString();
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(ReplaceFrom))
                        {
                            pgGetFileNames[i] = pgGetFileNames[i].Replace(ReplaceFrom, ReplaceTo);
                            Debug.Print("    My path and filename is: " + pgGetFileNames[i]);
                        }
                    }

                    // Debug.Print("    My path and filename is: " + pgGetFileNames[i]);

                    //string tempFolder = System.IO.Path.GetDirectoryName(pgGetFileNames[i]);
                    //string tempName = System.IO.Path.GetFileNameWithoutExtension(pgGetFileNames[i]);
                    //string tempExt = System.IO.Path.GetExtension(pgGetFileNames[i]);

                    //if (tempName.Contains(Prefix + " "))
                    //{
                    //    pgGetFileNames[i] = $@"{tempFolder}+ {tempName.Replace(Prefix + " ", Prefix)}{tempExt}";
                    //}
                }

                // Pack and Go
                swPackAndGo.SetDocumentSaveToNames(pgGetFileNames);

                //swPackAndGo.GetDocumentSaveToNames(out fileNames, out pgFileStatus);
                //pgFileNames = (object[])fileNames;
                //Debug.Print("");
                //Debug.Print("  Current default save-to filenames22222222222: ");
                //if ((pgFileNames != null))
                //{
                //    for (i = 0; i <= pgFileNames.GetUpperBound(0); i++)
                //    {
                //        Debug.Print("   The path and filename is: " + pgFileNames[i]);
                //    }
                //}


                statuses = (int[])swModelDocExt.SavePackAndGo(swPackAndGo);

                swApp.CloseDoc(openFile);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"PackAndGo 执行失败。源文件：{sourceFile}；目标目录：{targetPath}",
                    ex);
            }

            //swApp.CloseAllDocuments(true);
            //swApp.DocumentVisible(true, (int)swDocumentTypes_e.swDocASSEMBLY);
        }


        /// <summary>
        /// 打包装配体
        /// </summary>
        public void CopyFileUsePackAndGo(string sourceFile, string targetPath, List<string> ReplaceFrom, string ReplaceTo)
        {
            try
            {
                ModelDoc2 swModelDoc = default(ModelDoc2);
                ModelDocExtension swModelDocExt = default(ModelDocExtension);
                PackAndGo swPackAndGo = default(PackAndGo);
                string openFile = null;
                bool status = false;
                int warnings = 0;
                int errors = 0;
                int i = 0;
                int namesCount = 0;
                string myPath = null;
                int[] statuses = null;

                // Open assembly
                openFile = sourceFile;

                //swApp.DocumentVisible(false,(int)swDocumentTypes_e.swDocASSEMBLY);

                if (openFile.ToUpper().EndsWith(".SLDPRT"))
                {
                    swModelDoc = (ModelDoc2)swApp.OpenDoc6(openFile, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
                }
                else if (openFile.ToUpper().EndsWith(".SLDDRW"))
                {
                    swModelDoc = (ModelDoc2)swApp.OpenDoc6(openFile, (int)swDocumentTypes_e.swDocDRAWING, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
                }
                else
                {
                    swModelDoc = (ModelDoc2)swApp.OpenDoc6(openFile, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
                }

                swModelDocExt = (ModelDocExtension)swModelDoc.Extension;

                // Get Pack and Go object
                Debug.Print("Pack and Go");
                swPackAndGo = (PackAndGo)swModelDocExt.GetPackAndGo();

                // Get number of documents in assembly
                namesCount = swPackAndGo.GetDocumentNamesCount();
                Debug.Print("  Number of model documents: " + namesCount);

                // Include any drawings, SOLIDWORKS Simulation results, and SOLIDWORKS Toolbox components
                swPackAndGo.IncludeDrawings = true;
                Debug.Print(" Include drawings: " + swPackAndGo.IncludeDrawings);
                swPackAndGo.IncludeSimulationResults = false;
                Debug.Print(" Include SOLIDWORKS Simulation results: " + swPackAndGo.IncludeSimulationResults);
                swPackAndGo.IncludeToolboxComponents = false;
                Debug.Print(" Include SOLIDWORKS Toolbox components: " + swPackAndGo.IncludeToolboxComponents);

                // Get current paths and filenames of the assembly's documents
                object fileNames;
                object[] pgFileNames = new object[namesCount - 1];
                status = swPackAndGo.GetDocumentNames(out fileNames);
                pgFileNames = (object[])fileNames;

                Debug.Print("");
                Debug.Print("  Current path and filenames: ");
                if ((pgFileNames != null))
                {
                    for (i = 0; i <= pgFileNames.GetUpperBound(0); i++)
                    {
                        Debug.Print("    The 2 path and filename is: " + pgFileNames[i]);
                    }
                }

                // Get current save-to paths and filenames of the assembly's documents
                object pgFileStatus;
                status = swPackAndGo.GetDocumentSaveToNames(out fileNames, out pgFileStatus);
                pgFileNames = (object[])fileNames;
                Debug.Print("");
                Debug.Print("  Current default save-to filenames: ");
                if ((pgFileNames != null))
                {
                    for (i = 0; i <= pgFileNames.GetUpperBound(0); i++)
                    {
                        Debug.Print("   The 3 path and filename is: " + pgFileNames[i]);
                    }
                }

                // Set folder where to save the files
                myPath = targetPath;
                status = swPackAndGo.SetSaveToName(true, myPath);

                // Flatten the Pack and Go folder structure; save all files to the root directory
                swPackAndGo.FlattenToSingleFolder = true;

                // Add a prefix and suffix to the filenames
                //swPackAndGo.AddPrefix = Prefix;
                //swPackAndGo.AddSuffix = Suffix;

                // Verify document paths and filenames after adding prefix and suffix
                object getFileNames;
                object getDocumentStatus;
                string[] pgGetFileNames = new string[namesCount - 1];

                status = swPackAndGo.GetDocumentSaveToNames(out getFileNames, out getDocumentStatus);
                pgGetFileNames = (string[])getFileNames;

                Debug.Print("");
                Debug.Print("  My Pack and Go path and filenames after adding prefix and suffix: ");
                for (i = pgGetFileNames.Count() - 1; i >= 0; i--)
                {
                    for (int j = 0; j < ReplaceFrom.Count; j++)
                    {
                        var replaceF = ReplaceFrom[j];

                        if (pgGetFileNames[i].ToUpper().Contains(replaceF.ToUpper()))
                        {
                            pgGetFileNames[i] = pgGetFileNames[i].ToUpper().Replace(replaceF, ReplaceTo);
                        }

                    }




                }

                // Pack and Go
                swPackAndGo.SetDocumentSaveToNames(pgGetFileNames);

                statuses = (int[])swModelDocExt.SavePackAndGo(swPackAndGo);

                swApp.CloseDoc(swApp.IActiveDoc2.GetPathName());
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"PackAndGo 执行失败。源文件：{sourceFile}；目标目录：{targetPath}",
                    ex);
            }

            //swApp.CloseAllDocuments(true);
            //swApp.DocumentVisible(true, (int)swDocumentTypes_e.swDocASSEMBLY);
        }

    }
}
