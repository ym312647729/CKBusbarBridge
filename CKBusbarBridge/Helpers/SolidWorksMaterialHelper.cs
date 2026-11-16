using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using NLog;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// SolidWorks 材料库路径解析及柜体驱动铜排材质名称规范化。
    /// </summary>
    public static class SolidWorksMaterialHelper
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public const string DefaultBusbarMaterial = "直角铜母线";
        public const string FullRoundEdgeBusbarMaterial = "全圆边铜母线";
        public const string BusbarMaterialDatabaseFileName = "out1.sldmat";

        private static readonly string[] MaterialDensityPropertyNames =
        {
            "Mass Density",
            "质量密度",
            "Density"
        };

        /// <summary>
        /// 将柜体驱动参数中的铜排材质规范化为已知选项。
        /// </summary>
        public static string NormalizeBusbarMaterial(string busbarMaterial)
        {
            if (string.Equals(busbarMaterial?.Trim(), FullRoundEdgeBusbarMaterial, StringComparison.Ordinal))
            {
                return FullRoundEdgeBusbarMaterial;
            }

            return DefaultBusbarMaterial;
        }

        /// <summary>
        /// 优先根据 SolidWorks「系统选项 - 文件位置 - 材质数据库」解析铜排材质文件（out1.sldmat），
        /// 未配置时再查找插件随包的大全材质库；整个过程不修改 SolidWorks 的材质库设置。
        /// </summary>
        public static bool TryResolveBusbarMaterialDatabasePath(ISldWorks swApp, out string databasePath, out string errorMessage)
        {
            databasePath = null;
            errorMessage = null;

            if (swApp == null)
            {
                errorMessage = "未获取到 SolidWorks 应用对象，无法解析材料库路径。";
                return false;
            }

            TryGetMaterialDatabaseEntries(swApp, out var materialDatabaseEntries);

            if (TryFindBusbarMaterialDatabase(materialDatabaseEntries, out databasePath))
            {
                return true;
            }

            if (TryFindBundledBusbarMaterialDatabase(out databasePath))
            {
                Logger.Info("SolidWorks 未配置铜排材质库目录，已使用插件随包大全材质库：{0}", databasePath);
                return true;
            }

            errorMessage = $"未在 SolidWorks 材质数据库设置目录中找到铜排材料库文件 {BusbarMaterialDatabaseFileName}。" +
                           "请确认已在「系统选项 - 文件位置 - 材质数据库」中添加包含该文件的文件夹（如「自定义材料」）。";
            Logger.Warn("未找到铜排材质库 {0}，跳过材质设置并保留零件当前材质。", BusbarMaterialDatabaseFileName);
            return false;
        }

        /// <summary>
        /// 在访问模型前确认材质库可完整读取且包含目标材质；失败仅记日志。
        /// </summary>
        internal static bool TryValidateBusbarMaterialDatabase(string databasePath, string materialName)
        {
            if (string.IsNullOrWhiteSpace(databasePath) || string.IsNullOrWhiteSpace(materialName))
                return false;

            try
            {
                var found = false;
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (var reader = XmlReader.Create(databasePath, settings))
                {
                    // 不能找到名称就提前返回：文件后半段损坏时同样不能交给 SolidWorks 设置。
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "material" &&
                            string.Equals(reader.GetAttribute("name"), materialName.Trim(), StringComparison.Ordinal))
                            found = true;
                    }
                }

                if (!found)
                    Logger.Warn("材质库中没有目标材质，跳过材质设置：库={0}，材质={1}", databasePath, materialName);
                return found;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                exception is XmlException || exception is ArgumentException || exception is System.Security.SecurityException)
            {
                Logger.Warn(exception, "材质库无法读取，跳过材质设置：库={0}，材质={1}", databasePath, materialName);
                return false;
            }
        }

        /// <summary>
        /// 查找插件随包的大全材质库。这里仅读取已部署文件，不修改 SolidWorks 的材质库配置。
        /// </summary>
        private static bool TryFindBundledBusbarMaterialDatabase(out string databasePath)
        {
            databasePath = null;
            var location = typeof(SolidWorksMaterialHelper).Assembly.Location;
            var assemblyDirectory = string.IsNullOrWhiteSpace(location)
                ? System.Environment.CurrentDirectory : Path.GetDirectoryName(location);
            var candidatePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings", BusbarMaterialDatabaseFileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BusbarMaterialDatabaseFileName),
                Path.Combine(assemblyDirectory,
                    "Settings", BusbarMaterialDatabaseFileName)
            };

            foreach (var candidatePath in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(candidatePath))
                {
                    databasePath = candidatePath;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 读取 SolidWorks 设置中配置的材质数据库条目（可能为 .sldmat 文件或文件夹）。
        /// </summary>
        private static bool TryGetMaterialDatabaseEntries(ISldWorks swApp, out IReadOnlyList<string> entries)
        {
            entries = Array.Empty<string>();
            try
            {
                var raw = swApp.GetMaterialDatabases() as object[];
                if (raw == null)
                {
                    return false;
                }

                entries = raw
                    .Select(item => item?.ToString())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToList();

                return entries.Count > 0;
            }
            catch (Exception exception)
            {
                Logger.Warn(exception, "无法读取 SolidWorks 材质库配置，尝试随包材质库。");
                return false;
            }
        }

        /// <summary>
        /// 在材质数据库条目（文件或文件夹）中定位铜排材料库文件 out1.sldmat。
        /// </summary>
        private static bool TryFindBusbarMaterialDatabase(IReadOnlyList<string> materialDatabaseEntries, out string databasePath)
        {
            databasePath = null;
            if (materialDatabaseEntries == null)
            {
                return false;
            }

            foreach (var entry in materialDatabaseEntries)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                var trimmed = entry.Trim();

                // 条目本身即为 .sldmat 文件
                if (File.Exists(trimmed) &&
                    string.Equals(Path.GetFileName(trimmed), BusbarMaterialDatabaseFileName, StringComparison.OrdinalIgnoreCase))
                {
                    databasePath = trimmed;
                    return true;
                }

                // 条目为文件夹，则在其中查找目标文件
                if (Directory.Exists(trimmed))
                {
                    var candidate = Path.Combine(trimmed, BusbarMaterialDatabaseFileName);
                    if (File.Exists(candidate))
                    {
                        databasePath = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 读取零件已分配材质的质量密度（kg/m³）。
        /// 优先读 SW 材料属性，其次解析材料库 sldmat，最后尝试 Mass/Volume 反推。
        /// </summary>
        public static bool TryGetPartMaterialDensityKgPerM3(
            IPartDoc partDoc,
            ModelDoc2 partModel,
            string configurationName,
            ISldWorks swApp,
            out double density,
            out string errorMessage)
        {
            density = 0;
            errorMessage = null;

            if (partModel == null)
            {
                errorMessage = "无法读取材质密度：零件模型为空。";
                return false;
            }

            if (TryGetMaterialDensityFromModelPreferences(partModel, out density))
            {
                return true;
            }

            var materialName = TryGetMaterialName(partDoc, configurationName, out var databasePath);
            if (!string.IsNullOrWhiteSpace(materialName))
            {
                if (!string.IsNullOrWhiteSpace(databasePath) &&
                    TryGetMaterialDensityFromDatabase(databasePath, materialName, out density))
                {
                    return true;
                }

                if (TryResolveBusbarMaterialDatabasePath(swApp, out var defaultDatabasePath, out _) &&
                    TryGetMaterialDensityFromDatabase(defaultDatabasePath, materialName, out density))
                {
                    return true;
                }
            }

            if (TryGetEffectiveDensityFromMassProperty(partModel, out density))
            {
                return true;
            }

            var materialHint = string.IsNullOrWhiteSpace(materialName) ? "未分配材质" : $"材质「{materialName}」";
            errorMessage = $"{materialHint}，无法读取质量密度，净重留空。";
            return false;
        }

        /// <summary>
        /// 从材料库文件（.sldmat）中读取指定材质的质量密度（kg/m³）。
        /// </summary>
        public static bool TryGetMaterialDensityFromDatabase(string databasePath, string materialName, out double density)
        {
            density = 0;
            if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(materialName))
            {
                return false;
            }

            try
            {
                var xml = File.ReadAllText(databasePath);
                var materialPattern = Regex.Escape(materialName.Trim());
                if (!Regex.IsMatch(xml, materialPattern, RegexOptions.IgnoreCase))
                {
                    return false;
                }

                var materialSectionMatch = Regex.Match(
                    xml,
                    $@"<material\b[^>]*\bname=""{materialPattern}""[^>]*>(.*?)</material>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                var materialSection = materialSectionMatch.Success
                    ? materialSectionMatch.Groups[1].Value
                    : ExtractMaterialSectionByNameElement(xml, materialName.Trim());

                if (string.IsNullOrWhiteSpace(materialSection))
                {
                    return false;
                }

                return TryReadDensityFromMaterialSection(materialSection, out density);
            }
            catch
            {
                return false;
            }
        }

        private static string ExtractMaterialSectionByNameElement(string xml, string materialName)
        {
            var match = Regex.Match(
                xml,
                $@"<name>\s*{Regex.Escape(materialName)}\s*</name>",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                return null;
            }

            var start = Math.Max(0, xml.LastIndexOf("<material", match.Index, StringComparison.OrdinalIgnoreCase));
            var end = xml.IndexOf("</material>", match.Index, StringComparison.OrdinalIgnoreCase);
            if (start < 0 || end < 0 || end <= start)
            {
                return null;
            }

            return xml.Substring(start, end - start);
        }

        private static bool TryReadDensityFromMaterialSection(string materialSection, out double density)
        {
            density = 0;
            if (string.IsNullOrWhiteSpace(materialSection))
            {
                return false;
            }

            foreach (var propertyName in MaterialDensityPropertyNames)
            {
                var match = Regex.Match(
                    materialSection,
                    $@"<(?:property|pr0)\b[^>]*\bname=""{Regex.Escape(propertyName)}""[^>]*\bvalue=""([0-9]+(?:\.[0-9]+)?)""",
                    RegexOptions.IgnoreCase);

                if (match.Success &&
                    double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out density))
                {
                    return density > 0;
                }
            }

            var pr0Match = Regex.Match(
                materialSection,
                @"<pr0>\s*([0-9]+(?:\.[0-9]+)?)\s*</pr0>",
                RegexOptions.IgnoreCase);

            if (pr0Match.Success &&
                double.TryParse(pr0Match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out density))
            {
                return density > 0;
            }

            var textMatch = Regex.Match(
                materialSection,
                @"(?:Mass\s*Density|质量密度)[^0-9]*([0-9]+(?:\.[0-9]+)?)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (textMatch.Success &&
                double.TryParse(textMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out density))
            {
                return density > 0;
            }

            return false;
        }

        private static bool TryGetMaterialDensityFromModelPreferences(ModelDoc2 partModel, out double density)
        {
            density = 0;
            try
            {
                density = partModel.GetUserPreferenceDoubleValue(
                    (int)swUserPreferenceDoubleValue_e.swMaterialPropertyDensity);
                return density > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetEffectiveDensityFromMassProperty(ModelDoc2 partModel, out double density)
        {
            density = 0;
            try
            {
                var massProp = partModel.Extension?.CreateMassProperty() as IMassProperty;
                if (massProp == null) return false;

                var volume = massProp.Volume;
                var mass = massProp.Mass;
                if (volume <= 0 || mass <= 0) return false;

                density = mass / volume;
                return density > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string TryGetMaterialName(IPartDoc partDoc, string configurationName, out string databasePath)
        {
            databasePath = null;
            if (partDoc == null) return null;

            foreach (var configName in GetConfigurationCandidates(configurationName))
            {
                try
                {
                    var dbPath = string.Empty;
                    var materialName = partDoc.GetMaterialPropertyName2(configName, out dbPath);
                    if (!string.IsNullOrWhiteSpace(materialName))
                    {
                        databasePath = dbPath;
                        return materialName.Trim();
                    }
                }
                catch
                {
                    // try next config
                }
            }

            return null;
        }

        private static IEnumerable<string> GetConfigurationCandidates(string referencedConfig)
        {
            if (!string.IsNullOrWhiteSpace(referencedConfig))
            {
                yield return referencedConfig.Trim();
            }

            yield return "默认";
            yield return "Default";
            yield return string.Empty;
        }
    }
}
