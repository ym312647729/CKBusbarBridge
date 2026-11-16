using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CKBusbarBridge.Helpers
{
    internal enum BusbarGenerationDurationType
    {
        OrdinaryStraight,
        BridgeStraight,
        Milled,
        VerticalBend,
        PlanarBend,
        SubassemblyInsertion
    }

    /// <summary>
    /// 保存上一次生成中各类步骤的平均耗时，并据此估算下一次总耗时。
    /// </summary>
    internal static class BusbarGenerationDurationProfileStore
    {
        private const long DefaultSingleBusbarDurationMilliseconds = 3000L;
        private const long MaximumSingleBusbarDurationMilliseconds =
            60L * 60L * 1000L;
        private const string ProfileFolderName = "CKBusbarBridge";
        private const string ProfileFileName =
            "busbar-generation-duration-profile.json";

        private static readonly Logger ProfileLogger =
            LogManager.GetCurrentClassLogger();
        private static readonly object ProfileFileLock = new object();

        internal static long EstimateTotalDurationMilliseconds(
            IEnumerable<BusbarGenerationDurationType> durationTypes)
        {
            var requestedDurationTypes = (durationTypes ??
                    Enumerable.Empty<BusbarGenerationDurationType>())
                .ToList();
            if (requestedDurationTypes.Count == 0)
            {
                return 0L;
            }

            lock (ProfileFileLock)
            {
                var durationProfile = LoadProfileCore();
                var estimatedTotalDurationMilliseconds = 0L;
                foreach (var durationType in requestedDurationTypes)
                {
                    estimatedTotalDurationMilliseconds +=
                        ResolveAverageDurationMilliseconds(
                            durationProfile,
                            durationType);
                }

                return estimatedTotalDurationMilliseconds;
            }
        }

        internal static void SaveLastGenerationAverages(
            IReadOnlyDictionary<BusbarGenerationDurationType,
                List<long>> elapsedMillisecondsByType)
        {
            if (elapsedMillisecondsByType == null ||
                elapsedMillisecondsByType.All(durationSamples =>
                    durationSamples.Value == null ||
                    durationSamples.Value.Count == 0))
            {
                return;
            }

            lock (ProfileFileLock)
            {
                try
                {
                    var durationProfile = LoadProfileCore();
                    foreach (var durationSamples in elapsedMillisecondsByType)
                    {
                        var validElapsedMilliseconds =
                            (durationSamples.Value != null
                                ? (IEnumerable<long>)durationSamples.Value
                                : Enumerable.Empty<long>())
                            .Where(elapsedMilliseconds =>
                                elapsedMilliseconds > 0L &&
                                elapsedMilliseconds <=
                                MaximumSingleBusbarDurationMilliseconds)
                            .ToList();
                        if (validElapsedMilliseconds.Count == 0)
                        {
                            continue;
                        }

                        var averageDurationMilliseconds = (long)Math.Round(
                            validElapsedMilliseconds.Average(
                                elapsedMilliseconds =>
                                    (double)elapsedMilliseconds),
                            MidpointRounding.AwayFromZero);
                        durationProfile.AverageMillisecondsByType[
                                durationSamples.Key.ToString()] =
                            Math.Max(1L, averageDurationMilliseconds);
                        durationProfile.SampleCountByType[
                                durationSamples.Key.ToString()] =
                            validElapsedMilliseconds.Count;
                    }

                    durationProfile.UpdatedUtc = DateTime.UtcNow;
                    var profileFilePath = ResolveProfileFilePath();
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(profileFilePath));
                    File.WriteAllText(
                        profileFilePath,
                        JsonConvert.SerializeObject(
                            durationProfile,
                            Formatting.Indented),
                        new UTF8Encoding(false));

                    ProfileLogger.Info(
                        "铜排生成单耗记录已更新：{0}。{1}",
                        profileFilePath,
                        BuildProfileSummary(durationProfile));
                }
                catch (Exception profileSaveException)
                {
                    ProfileLogger.Warn(
                        profileSaveException,
                        "更新铜排生成单耗记录失败，本次生成结果不受影响。");
                }
            }
        }

        internal static string ResolveDurationTypeDisplayName(
            BusbarGenerationDurationType durationType)
        {
            switch (durationType)
            {
                case BusbarGenerationDurationType.BridgeStraight:
                    return "桥式直排";
                case BusbarGenerationDurationType.Milled:
                    return "铣排";
                case BusbarGenerationDurationType.VerticalBend:
                    return "立弯排";
                case BusbarGenerationDurationType.PlanarBend:
                    return "带折弯排";
                case BusbarGenerationDurationType.SubassemblyInsertion:
                    return "子装配体保存插入";
                default:
                    return "普通直排";
            }
        }

        private static BusbarGenerationDurationProfile LoadProfileCore()
        {
            try
            {
                var profileFilePath = ResolveProfileFilePath();
                if (!File.Exists(profileFilePath))
                {
                    return CreateDefaultProfile();
                }

                var durationProfile = JsonConvert.DeserializeObject<
                    BusbarGenerationDurationProfile>(
                    File.ReadAllText(profileFilePath, Encoding.UTF8));
                return NormalizeProfile(durationProfile);
            }
            catch (Exception profileLoadException)
            {
                ProfileLogger.Warn(
                    profileLoadException,
                    "读取铜排生成单耗记录失败，本次使用默认单耗估算。");
                return CreateDefaultProfile();
            }
        }

        private static BusbarGenerationDurationProfile NormalizeProfile(
            BusbarGenerationDurationProfile durationProfile)
        {
            var normalizedProfile = durationProfile ??
                new BusbarGenerationDurationProfile
                {
                    Version = 1
                };
            normalizedProfile.AverageMillisecondsByType =
                normalizedProfile.AverageMillisecondsByType ??
                new Dictionary<string, long>();
            normalizedProfile.SampleCountByType =
                normalizedProfile.SampleCountByType ??
                new Dictionary<string, int>();
            foreach (BusbarGenerationDurationType durationType in
                Enum.GetValues(typeof(BusbarGenerationDurationType)))
            {
                var durationTypeKey = durationType.ToString();
                if (!normalizedProfile.AverageMillisecondsByType.TryGetValue(
                        durationTypeKey,
                        out var averageDurationMilliseconds) ||
                    averageDurationMilliseconds <= 0L ||
                    averageDurationMilliseconds >
                    MaximumSingleBusbarDurationMilliseconds)
                {
                    normalizedProfile.AverageMillisecondsByType[
                            durationTypeKey] =
                        DefaultSingleBusbarDurationMilliseconds;
                }
            }

            return normalizedProfile;
        }

        private static BusbarGenerationDurationProfile CreateDefaultProfile()
        {
            return NormalizeProfile(new BusbarGenerationDurationProfile
            {
                Version = 1,
                AverageMillisecondsByType = new Dictionary<string, long>(),
                SampleCountByType = new Dictionary<string, int>()
            });
        }

        private static long ResolveAverageDurationMilliseconds(
            BusbarGenerationDurationProfile durationProfile,
            BusbarGenerationDurationType durationType)
        {
            if (durationProfile.AverageMillisecondsByType.TryGetValue(
                    durationType.ToString(),
                    out var averageDurationMilliseconds))
            {
                return averageDurationMilliseconds;
            }

            return DefaultSingleBusbarDurationMilliseconds;
        }

        private static string ResolveProfileFilePath()
        {
            var localApplicationDataDirectory =
                System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(
                localApplicationDataDirectory,
                ProfileFolderName,
                ProfileFileName);
        }

        private static string BuildProfileSummary(
            BusbarGenerationDurationProfile durationProfile)
        {
            return string.Join(
                "，",
                Enum.GetValues(typeof(BusbarGenerationDurationType))
                    .Cast<BusbarGenerationDurationType>()
                    .Select(durationType =>
                        ResolveDurationTypeDisplayName(durationType) + "=" +
                        ResolveAverageDurationMilliseconds(
                            durationProfile,
                            durationType) + "ms"));
        }
    }

    internal sealed class BusbarGenerationDurationProfile
    {
        public int Version { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public Dictionary<string, long> AverageMillisecondsByType
            { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, int> SampleCountByType
            { get; set; } = new Dictionary<string, int>();
    }
}
