using AutoOpeningHoleBase;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.models;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ISheet = NPOI.SS.UserModel.ISheet;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 从总装配体导出铜排清单到 Excel（基于「铜排标准清单模板.xlsx」）。
    /// </summary>
    public class BusbarBomExportDriver
    {
        private const string TemplateFileName = "铜排标准清单模板.xlsx";

        private const string DrawingNumberPropertyName = "图号";
        private const string SpecificationPropertyName = "铜排规格";
        private const string PartTypePropertyName = "零件类型";
        private const string UnfoldedLengthPropertyName = "铜排展开长度";
        private const string UnfoldedLengthAltPropertyName = "展开长度";

        private const int DataStartRowIndex = 1;
        private const int ColIndex = 0;
        private const int ColBusbarName = 1;
        private const int ColDrawingNumber = 2;
        private const int ColQuantity = 3;
        private const int ColSpecification = 4;
        private const int ColPartType = 5;
        private const int ColMaterial = 6;
        private const int ColNetWeight = 7;
        private const int ColUnfoldedLength = 8;
        private const int ColUnfoldedWidth = 9;
        private const int ColThickness = 10;
        private const int ColRemarks = 11;

        private static readonly Regex ExtensionBusbarSourcePartNamePattern = new Regex(
            @"^(?:[AB]柜内伸出排-[ABC]-[AB]柜|柜内伸出排-[ABC]-柜)(前|后)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ExtensionBusbarAssemblyNamePattern = new Regex(
            @"^(?:[AB]柜内伸出排-[AB]柜|柜内伸出排-柜)(前|后)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ISldWorks _swApp;

        public BusbarBomExportDriver(ISldWorks swApp)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
        }

        /// <summary>
        /// 基于当前激活装配体导出铜排清单。
        /// </summary>
        public BusbarBomExportResult ExportFromCurrentAssembly(string outputPath)
        {
            var assemblyModel = _swApp.ActiveDoc as ModelDoc2;
            if (assemblyModel == null)
            {
                throw new InvalidOperationException("当前没有活动文档，无法导出铜排清单。");
            }

            if (assemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException("当前活动文档不是装配体，无法导出铜排清单。");
            }

            var assemblyPath = assemblyModel.GetPathName();
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new InvalidOperationException("请先保存当前装配体，再执行导出铜排清单。");
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("输出路径不能为空。", nameof(outputPath));
            }

            ResolveLightweightComponents(assemblyModel);
            try { assemblyModel.EditRebuild3(); } catch { /* ignore */ }

            var result = new BusbarBomExportResult { OutputPath = outputPath };
            var items = CollectAndAggregateItems(assemblyModel as AssemblyDoc, result);
            result.RowCount = items.Count;

            var templatePath = ResolveTemplatePath();
            FillTemplate(templatePath, outputPath, items);

            return result;
        }

        private static void ResolveLightweightComponents(ModelDoc2 assemblyModel)
        {
            var asm = assemblyModel as AssemblyDoc;
            if (asm == null) return;

            try { asm.ResolveAllLightWeightComponents(false); }
            catch { /* ignore */ }
        }

        private List<BusbarBomItem> CollectAndAggregateItems(AssemblyDoc asm, BusbarBomExportResult result)
        {
            var items = new List<BusbarBomItem>();
            if (asm == null)
            {
                return items;
            }

            var allBusbarComponents = asm.GetAllBusbarComponents(false);
            if (allBusbarComponents == null || allBusbarComponents.Count == 0)
            {
                result.Warnings.Add("未在总装配体中找到铜排零件。");
                return items;
            }

            var aggregateByKey = new Dictionary<string, BusbarBomAggregate>(StringComparer.OrdinalIgnoreCase);

            foreach (var component in allBusbarComponents)
            {
                if (component == null) continue;
                if (IsSuppressed(component)) continue;

                var partModel = component.IGetModelDoc() as ModelDoc2;
                if (partModel == null || partModel.GetType() != (int)swDocumentTypes_e.swDocPART)
                {
                    continue;
                }

                string referencedConfig = null;
                try { referencedConfig = component.ReferencedConfiguration; } catch { }
                if (referencedConfig == null) referencedConfig = string.Empty;

                if (IsExtensionBusbarComponent(component, partModel, referencedConfig))
                {
                    continue;
                }

                result.InstanceCount++;

                var snapshot = ReadComponentSnapshot(component, partModel, referencedConfig, result);
                var aggregateKey = BuildAggregateKey(
                    NormalizeDrawingNumber(snapshot.DrawingNumber),
                    TryGetComponentModelPath(component, partModel));

                if (string.IsNullOrWhiteSpace(aggregateKey))
                {
                    var componentLabel = SafeGetComponentLabel(component);
                    result.Warnings.Add($"无法聚合，已跳过：{componentLabel}");
                    continue;
                }

                if (!aggregateByKey.TryGetValue(aggregateKey, out var aggregate))
                {
                    aggregate = new BusbarBomAggregate { Item = snapshot };
                    aggregateByKey[aggregateKey] = aggregate;
                    continue;
                }

                aggregate.Quantity++;
                if (!aggregate.Item.IsCompatibleWith(snapshot, out var conflictMessage))
                {
                    result.Warnings.Add($"图号 {snapshot.DrawingNumber} 的属性不一致：{conflictMessage}");
                }
            }

            items.AddRange(aggregateByKey.Values.Select(aggregate =>
            {
                aggregate.Item.Quantity = aggregate.Quantity;
                return aggregate.Item;
            }));

            for (var i = 0; i < items.Count; i++)
            {
                items[i].Index = i + 1;
            }

            return items;
        }

        /// <summary>
        /// 构建聚合键：优先图号；图号为空时退化为模型文件路径。
        /// </summary>
        private static string BuildAggregateKey(string normalizedDrawingNumber, string modelFullPath)
        {
            if (!string.IsNullOrWhiteSpace(normalizedDrawingNumber))
            {
                return normalizedDrawingNumber;
            }

            if (string.IsNullOrWhiteSpace(modelFullPath))
            {
                return null;
            }

            try
            {
                return "__PATH__:" + Path.GetFullPath(modelFullPath);
            }
            catch
            {
                return "__PATH__:" + modelFullPath.Trim();
            }
        }

        private static string TryGetComponentModelPath(Component2 component, ModelDoc2 partModel)
        {
            string path = null;
            try { path = component?.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(path))
            {
                try { path = partModel?.GetPathName(); } catch { }
            }

            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        /// <summary>
        /// 判断铜排零件是否属于伸出排（柜内伸出排源零件或其子层级）。
        /// </summary>
        private static bool IsExtensionBusbarComponent(
            Component2 component,
            ModelDoc2 partModel,
            string referencedConfig)
        {
            if (component == null) return false;

            var partType = ReadCustomProperty(partModel, referencedConfig, PartTypePropertyName);
            if (!string.IsNullOrWhiteSpace(partType) &&
                partType.IndexOf("伸出排", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            if (IsExtensionBusbarName(NormalizeBusbarComponentName(component.Name2)))
            {
                return true;
            }

            try
            {
                var pathName = component.GetPathName();
                if (IsExtensionBusbarName(NormalizeBusbarComponentName(Path.GetFileNameWithoutExtension(pathName))))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(pathName) &&
                    pathName.IndexOf("伸出排", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch
            {
                // ignore
            }

            for (var current = component; current != null; current = SafeGetParentComponent(current))
            {
                if (IsExtensionBusbarAssemblyName(NormalizeBusbarComponentName(current.Name2)))
                {
                    return true;
                }

                try
                {
                    var parentPath = current.GetPathName();
                    if (IsExtensionBusbarAssemblyName(
                            NormalizeBusbarComponentName(Path.GetFileNameWithoutExtension(parentPath))))
                    {
                        return true;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            return false;
        }

        private static bool IsExtensionBusbarName(string normalizedName)
        {
            return !string.IsNullOrWhiteSpace(normalizedName) &&
                   ExtensionBusbarSourcePartNamePattern.IsMatch(normalizedName);
        }

        private static bool IsExtensionBusbarAssemblyName(string normalizedName)
        {
            return !string.IsNullOrWhiteSpace(normalizedName) &&
                   ExtensionBusbarAssemblyNamePattern.IsMatch(normalizedName);
        }

        private static Component2 SafeGetParentComponent(Component2 component)
        {
            try { return component.GetParent() as Component2; }
            catch { return null; }
        }

        private static string NormalizeBusbarComponentName(string componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName))
            {
                return string.Empty;
            }

            var normalizedName = componentName.Trim();

            var atIndex = normalizedName.IndexOf('@');
            if (atIndex >= 0)
            {
                normalizedName = normalizedName.Substring(0, atIndex);
            }

            var slashIndex = normalizedName.LastIndexOf('/');
            if (slashIndex >= 0)
            {
                normalizedName = normalizedName.Substring(slashIndex + 1);
            }

            normalizedName = Regex.Replace(normalizedName, @"<\d+>$", string.Empty);
            normalizedName = Regex.Replace(normalizedName, @"-\d+$", string.Empty);

            return normalizedName.Trim();
        }

        private BusbarBomItem ReadComponentSnapshot(
            Component2 component,
            ModelDoc2 partModel,
            string referencedConfig,
            BusbarBomExportResult result)
        {
            var drawingNumber = ReadCustomProperty(partModel, referencedConfig, DrawingNumberPropertyName);
            var busbarName = TryGetPartModelName(partModel, component);
            var specification = ReadCustomProperty(partModel, referencedConfig, SpecificationPropertyName);
            var partType = ReadCustomProperty(partModel, referencedConfig, PartTypePropertyName);
            var unfoldedLength = ReadCustomProperty(partModel, referencedConfig, UnfoldedLengthPropertyName)
                ?? ReadCustomProperty(partModel, referencedConfig, UnfoldedLengthAltPropertyName);

            var materialName = TryGetMaterialName(partModel as PartDoc, referencedConfig);
            var netWeightKg = TryCalculateNetWeightKg(partModel, referencedConfig, materialName, result, SafeGetComponentLabel(component));

            double? unfoldedWidth = null;
            double? thickness = null;
            if (!TryParseSpecification(specification, out var width, out var parsedThickness))
            {
                if (!string.IsNullOrWhiteSpace(specification))
                {
                    result.Warnings.Add($"{SafeGetComponentLabel(component)}：无法解析铜排规格「{specification}」。");
                }
            }
            else
            {
                unfoldedWidth = width;
                thickness = parsedThickness;
            }

            return new BusbarBomItem
            {
                DrawingNumber = drawingNumber?.Trim(),
                BusbarName = busbarName,
                Specification = specification?.Trim(),
                PartType = partType?.Trim(),
                MaterialName = materialName,
                NetWeightKg = netWeightKg,
                UnfoldedLength = unfoldedLength?.Trim(),
                UnfoldedWidth = unfoldedWidth,
                Thickness = thickness,
                Quantity = 1,
                Remarks = string.Empty
            };
        }

        private double? TryCalculateNetWeightKg(
            ModelDoc2 partModel,
            string referencedConfig,
            string materialName,
            BusbarBomExportResult result,
            string componentLabel)
        {
            var volumeM3 = TryGetPartVolumeM3(partModel);
            if (!volumeM3.HasValue || volumeM3.Value <= 0)
            {
                result.Warnings.Add($"{componentLabel}：无法获取零件体积，净重留空。");
                return null;
            }

            if (!SolidWorksMaterialHelper.TryGetPartMaterialDensityKgPerM3(
                    partModel as PartDoc,
                    partModel,
                    referencedConfig,
                    _swApp,
                    out var density,
                    out var densityError))
            {
                result.Warnings.Add($"{componentLabel}：{densityError}");
                return null;
            }

            var netWeightKg = density * volumeM3.Value;
            return Math.Floor(netWeightKg * 100000) / 100000;
        }

        private static double? TryGetPartVolumeM3(ModelDoc2 partModel)
        {
            if (partModel?.Extension == null) return null;

            try
            {
                var massProp = partModel.Extension.CreateMassProperty() as IMassProperty;
                if (massProp == null) return null;

                var volume = massProp.Volume;
                return volume > 0 ? volume : (double?)null;
            }
            catch
            {
                return null;
            }
        }

        private static string TryGetMaterialName(PartDoc partDoc, string referencedConfig)
        {
            if (partDoc == null) return null;

            foreach (var configName in GetConfigurationCandidates(referencedConfig))
            {
                try
                {
                    var databasePath = string.Empty;
                    var materialName = partDoc.GetMaterialPropertyName2(configName, out databasePath);
                    if (!string.IsNullOrWhiteSpace(materialName))
                    {
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

        private static string ReadCustomProperty(ModelDoc2 model, string config, string propertyName)
        {
            if (model == null || string.IsNullOrWhiteSpace(propertyName)) return null;

            try
            {
                var value = model.GetCustomInfoValue(config ?? string.Empty, propertyName);
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryParseSpecification(string specification, out double width, out double thickness)
        {
            width = 0;
            thickness = 0;

            if (string.IsNullOrWhiteSpace(specification)) return false;

            var parts = specification.Trim()
                .Replace('×', 'x')
                .Replace('*', 'x')
                .Split(new[] { 'x', 'X' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2) return false;

            if (!TryParseDouble(parts[0], out width) || !TryParseDouble(parts[1], out thickness))
            {
                return false;
            }

            return width > 0 && thickness > 0;
        }

        private static bool TryParseDouble(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private static string NormalizeDrawingNumber(string drawingNumber)
        {
            return string.IsNullOrWhiteSpace(drawingNumber)
                ? null
                : drawingNumber.Trim().ToUpperInvariant();
        }

        private static bool IsSuppressed(Component2 component)
        {
            try { return component.IsSuppressed(); }
            catch { return true; }
        }

        /// <summary>
        /// 铜排名称：取铜排零件模型文件名（不含扩展名）。
        /// </summary>
        private static string TryGetPartModelName(ModelDoc2 partModel, Component2 component)
        {
            string path = null;
            try { path = component?.GetPathName(); } catch { /* ignore */ }
            if (string.IsNullOrWhiteSpace(path))
            {
                try { path = partModel?.GetPathName(); } catch { /* ignore */ }
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                return Path.GetFileNameWithoutExtension(path);
            }

            try
            {
                var title = partModel?.GetTitle();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    return Path.GetFileNameWithoutExtension(title.Trim());
                }
            }
            catch { /* ignore */ }

            return SafeGetComponentLabel(component);
        }

        private static string SafeGetComponentLabel(Component2 component)
        {
            try
            {
                var name = component.Name2;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { /* ignore */ }

            try
            {
                var path = component.GetPathName();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return Path.GetFileNameWithoutExtension(path);
                }
            }
            catch { /* ignore */ }

            return "未知铜排";
        }

        private static string ResolveTemplatePath()
        {
            var addinDirectory = SolidWorksDriver.RegDllPath();
            if (string.IsNullOrWhiteSpace(addinDirectory))
            {
                throw new FileNotFoundException($"无法定位插件目录，无法找到铜排清单模板：{TemplateFileName}");
            }

            var templatePath = Path.Combine(addinDirectory, "Settings", TemplateFileName);
            if (!File.Exists(templatePath))
            {
                throw new FileNotFoundException(
                    $"未找到铜排清单模板：{templatePath}。请将模板放置到该路径后重试。",
                    templatePath);
            }

            return templatePath;
        }

        private static void FillTemplate(string templatePath, string outputPath, List<BusbarBomItem> items)
        {
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory) && !Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            IWorkbook workbook = LoadWorkbook(templatePath);
            try
            {
                var sheet = workbook.NumberOfSheets > 0 ? workbook.GetSheetAt(0) : workbook.CreateSheet("铜排清单");
                ClearExistingDataRows(sheet, DataStartRowIndex);

                for (var i = 0; i < items.Count; i++)
                {
                    WriteItemRow(sheet, DataStartRowIndex + i, items[i]);
                }

                using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    workbook.Write(fs);
                }
            }
            finally
            {
                (workbook as IDisposable)?.Dispose();
            }
        }

        private static IWorkbook LoadWorkbook(string templatePath)
        {
            using (var fs = new FileStream(templatePath, FileMode.Open, FileAccess.Read))
            {
                if (templatePath.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
                {
                    return new HSSFWorkbook(fs);
                }

                return new XSSFWorkbook(fs);
            }
        }

        private static void ClearExistingDataRows(ISheet sheet, int startRowIndex)
        {
            if (sheet == null) return;

            var lastRow = sheet.LastRowNum;
            for (var rowIndex = startRowIndex; rowIndex <= lastRow; rowIndex++)
            {
                var row = sheet.GetRow(rowIndex);
                if (row == null) continue;

                for (var col = ColIndex; col <= ColRemarks; col++)
                {
                    var cell = row.GetCell(col);
                    cell?.SetCellValue(string.Empty);
                }
            }
        }

        private static void WriteItemRow(ISheet sheet, int rowIndex, BusbarBomItem item)
        {
            var row = sheet.GetRow(rowIndex) ?? sheet.CreateRow(rowIndex);

            WriteCell(row, ColIndex, item.Index);
            WriteCell(row, ColBusbarName, item.BusbarName);
            WriteCell(row, ColDrawingNumber, item.DrawingNumber);
            WriteCell(row, ColQuantity, item.Quantity);
            WriteCell(row, ColSpecification, item.Specification);
            WriteCell(row, ColPartType, item.PartType);
            WriteCell(row, ColMaterial, item.MaterialName);
            WriteCell(row, ColNetWeight, item.NetWeightKg);
            WriteCell(row, ColUnfoldedLength, item.UnfoldedLength);
            WriteCell(row, ColUnfoldedWidth, item.UnfoldedWidth);
            WriteCell(row, ColThickness, item.Thickness);
            WriteCell(row, ColRemarks, item.Remarks);
        }

        private static void WriteCell(IRow row, int columnIndex, object value)
        {
            var cell = row.GetCell(columnIndex) ?? row.CreateCell(columnIndex);
            if (value == null)
            {
                cell.SetCellValue(string.Empty);
                return;
            }

            switch (value)
            {
                case int intValue:
                    cell.SetCellValue(intValue);
                    break;
                case double doubleValue:
                    cell.SetCellValue(doubleValue);
                    break;
                case float floatValue:
                    cell.SetCellValue(floatValue);
                    break;
                case decimal decimalValue:
                    cell.SetCellValue((double)decimalValue);
                    break;
                default:
                    cell.SetCellValue(value.ToString());
                    break;
            }
        }

        private sealed class BusbarBomAggregate
        {
            public BusbarBomItem Item { get; set; }
            public int Quantity { get; set; } = 1;
        }
    }

    internal static class BusbarBomItemExtensions
    {
        public static bool IsCompatibleWith(this BusbarBomItem left, BusbarBomItem right, out string conflictMessage)
        {
            conflictMessage = null;
            if (left == null || right == null) return true;

            var conflicts = new List<string>();
            CompareField("铜排模型名称", left.BusbarName, right.BusbarName, conflicts);
            CompareField("铜排规格", left.Specification, right.Specification, conflicts);
            CompareField("零件类型", left.PartType, right.PartType, conflicts);
            CompareField("材质", left.MaterialName, right.MaterialName, conflicts);

            if (conflicts.Count == 0) return true;

            conflictMessage = string.Join("；", conflicts);
            return false;
        }

        private static void CompareField(string fieldName, string left, string right, List<string> conflicts)
        {
            var normalizedLeft = left?.Trim() ?? string.Empty;
            var normalizedRight = right?.Trim() ?? string.Empty;
            if (!string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal))
            {
                conflicts.Add($"{fieldName}「{normalizedLeft}」/「{normalizedRight}」");
            }
        }
    }
}

