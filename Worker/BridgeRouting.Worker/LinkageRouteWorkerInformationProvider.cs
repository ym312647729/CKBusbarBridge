using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using RouteLinkBar.Algorithm.Transport;

namespace RouteLinkBar.Algorithm.Worker
{
    /// <summary>从 Worker 内嵌资源读取结构化版本信息，并校验日志结构。</summary>
    internal static class LinkageRouteWorkerInformationProvider
    {
        private const int SupportedChangelogSchemaVersion = 1;
        private const string WorkerChangelogResourceName =
            "RouteLinkBar.Algorithm.Worker.WorkerChangelog.json";

        private static readonly HashSet<string> SupportedChangeLevelTokens =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Initial",
                "Patch",
                "Major"
            };

        private static readonly HashSet<string> SupportedCompatibilityTokens =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Baseline",
                "BackwardCompatible",
                "HostUpdateRequired"
            };

        private static readonly Lazy<LinkageRouteWorkerInformationDto>
            CachedWorkerInformation =
                new Lazy<LinkageRouteWorkerInformationDto>(
                    LoadAndValidateWorkerInformation,
                    LazyThreadSafetyMode.ExecutionAndPublication);

        public static LinkageRouteWorkerInformationDto GetWorkerInformation()
        {
            return CachedWorkerInformation.Value;
        }

        private static LinkageRouteWorkerInformationDto LoadAndValidateWorkerInformation()
        {
            var workerAssembly = typeof(LinkageRouteWorkerInformationProvider).Assembly;
            string workerChangelogJson;
            using (var workerChangelogResourceStream = workerAssembly
                .GetManifestResourceStream(WorkerChangelogResourceName))
            {
                if (workerChangelogResourceStream == null)
                {
                    throw new InvalidDataException(
                        "算法 Worker 缺少内嵌更新日志资源：" +
                        WorkerChangelogResourceName);
                }

                using (var workerChangelogReader = new StreamReader(
                    workerChangelogResourceStream,
                    new UTF8Encoding(false, true),
                    true,
                    4096,
                    false))
                {
                    workerChangelogJson = workerChangelogReader.ReadToEnd();
                }
            }

            var loadedWorkerInformation = LinkageRouteWorkerMessageCodec
                .DeserializeWorkerInformation(workerChangelogJson);
            ValidateWorkerInformation(workerAssembly, loadedWorkerInformation);
            return loadedWorkerInformation;
        }

        private static void ValidateWorkerInformation(
            Assembly workerAssembly,
            LinkageRouteWorkerInformationDto workerInformation)
        {
            if (workerInformation == null)
            {
                throw new InvalidDataException("算法 Worker 更新日志无法读取。");
            }

            if (workerInformation.SchemaVersion != SupportedChangelogSchemaVersion)
            {
                throw new InvalidDataException(string.Format(
                    CultureInfo.InvariantCulture,
                    "算法 Worker 更新日志结构版本不受支持：文档={0}，程序={1}。",
                    workerInformation.SchemaVersion,
                    SupportedChangelogSchemaVersion));
            }

            var workerAssemblyName = workerAssembly.GetName();
            var actualWorkerName = workerAssemblyName.Name;
            if (!string.Equals(
                    workerInformation.WorkerName,
                    actualWorkerName,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(string.Format(
                    CultureInfo.InvariantCulture,
                    "算法 Worker 更新日志名称不一致：文档={0}，程序集={1}。",
                    workerInformation.WorkerName,
                    actualWorkerName));
            }

            var workerReleaseNotes = workerInformation.Releases;
            if (workerReleaseNotes == null || workerReleaseNotes.Count == 0)
            {
                throw new InvalidDataException("算法 Worker 更新日志没有版本记录。");
            }

            var currentWorkerReleaseNote = workerReleaseNotes[0];
            if (currentWorkerReleaseNote == null ||
                !string.Equals(
                    currentWorkerReleaseNote.Version,
                    workerInformation.WorkerVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "算法 Worker 更新日志首条版本必须等于当前 WorkerVersion。");
            }

            if (currentWorkerReleaseNote.ProtocolVersion !=
                workerInformation.ProtocolVersion)
            {
                throw new InvalidDataException(
                    "算法 Worker 当前版本记录的协议版本与文档顶层不一致。");
            }

            ValidateReleaseNotes(workerReleaseNotes);
        }

        private static void ValidateReleaseNotes(
            IList<LinkageRouteWorkerReleaseNoteDto> workerReleaseNotes)
        {
            var recordedWorkerVersions = new HashSet<string>(StringComparer.Ordinal);
            Version previousWorkerReleaseVersion = null;
            for (var workerReleaseIndex = 0;
                workerReleaseIndex < workerReleaseNotes.Count;
                workerReleaseIndex++)
            {
                var workerReleaseNote = workerReleaseNotes[workerReleaseIndex];
                if (workerReleaseNote == null ||
                    !TryParseFourPartVersion(
                        workerReleaseNote.Version,
                        out var parsedWorkerReleaseVersion))
                {
                    throw new InvalidDataException(
                        "算法 Worker 更新日志包含无效的四段式版本号。");
                }

                if (!recordedWorkerVersions.Add(workerReleaseNote.Version))
                {
                    throw new InvalidDataException(
                        "算法 Worker 更新日志包含重复版本：" +
                        workerReleaseNote.Version);
                }

                if (previousWorkerReleaseVersion != null &&
                    previousWorkerReleaseVersion.CompareTo(
                        parsedWorkerReleaseVersion) <= 0)
                {
                    throw new InvalidDataException(
                        "算法 Worker 更新日志必须按版本从新到旧排列。");
                }

                if (!DateTime.TryParseExact(
                        workerReleaseNote.ReleaseDate,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out _))
                {
                    throw new InvalidDataException(
                        "算法 Worker 更新日志包含无效发布日期：" +
                        workerReleaseNote.ReleaseDate);
                }

                if (!SupportedChangeLevelTokens.Contains(
                    workerReleaseNote.ChangeLevel))
                {
                    throw new InvalidDataException(
                        "算法 Worker 更新日志包含不支持的 ChangeLevel：" +
                        workerReleaseNote.ChangeLevel);
                }

                if (!SupportedCompatibilityTokens.Contains(
                    workerReleaseNote.Compatibility))
                {
                    throw new InvalidDataException(
                        "算法 Worker 更新日志包含不支持的 Compatibility：" +
                        workerReleaseNote.Compatibility);
                }

                if (workerReleaseNote.ProtocolVersion <= 0)
                {
                    throw new InvalidDataException(
                        "算法 Worker 更新日志包含无效的协议版本：" +
                        workerReleaseNote.Version);
                }

                if (workerReleaseNote.Changes == null ||
                    workerReleaseNote.Changes.Count == 0)
                {
                    throw new InvalidDataException(
                        "算法 Worker 更新日志版本没有变更内容：" +
                        workerReleaseNote.Version);
                }

                for (var releaseChangeIndex = 0;
                    releaseChangeIndex < workerReleaseNote.Changes.Count;
                    releaseChangeIndex++)
                {
                    if (string.IsNullOrWhiteSpace(
                        workerReleaseNote.Changes[releaseChangeIndex]))
                    {
                        throw new InvalidDataException(
                            "算法 Worker 更新日志包含空变更内容：" +
                            workerReleaseNote.Version);
                    }
                }

                previousWorkerReleaseVersion = parsedWorkerReleaseVersion;
            }
        }

        private static bool TryParseFourPartVersion(
            string versionText,
            out Version parsedVersion)
        {
            parsedVersion = null;
            if (string.IsNullOrWhiteSpace(versionText) ||
                versionText.Split('.').Length != 4 ||
                !Version.TryParse(versionText, out var candidateVersion) ||
                candidateVersion.Build < 0 ||
                candidateVersion.Revision < 0 ||
                !string.Equals(
                    candidateVersion.ToString(4),
                    versionText,
                    StringComparison.Ordinal))
            {
                return false;
            }

            parsedVersion = candidateVersion;
            return true;
        }
    }
}
