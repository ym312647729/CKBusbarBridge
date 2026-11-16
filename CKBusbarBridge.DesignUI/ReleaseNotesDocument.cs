using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace CKBusbarBridge.views
{
    /// <summary>将本地说明的标题、列表、正文和行内标记转换为可选择的原生文档。</summary>
    internal static class ReleaseNotesDocument
    {
        internal static FlowDocument Create(string markdown)
        {
            var document = new FlowDocument
            {
                FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14,
                Foreground = Brush("#334155"), Background = Brushes.White,
                PagePadding = new Thickness(28, 20, 28, 24), ColumnWidth = 10000
            };
            foreach (var raw in Regex.Split(markdown ?? string.Empty, @"\r\n|\n|\r"))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var heading = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
                var list = Regex.Match(line, @"^(?:[-*]|\d+[.)、])\s+(.+)$");
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12), LineHeight = 25 };
                var text = line;
                if (heading.Success)
                {
                    var level = heading.Groups[1].Length;
                    text = heading.Groups[2].Value;
                    paragraph.FontSize = level == 1 ? 23 : level == 2 ? 18 : 15;
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.Foreground = Brush("#163A59");
                    paragraph.Margin = new Thickness(0, document.Blocks.Count == 0 ? 0 : 16, 0, 12);
                }
                else if (list.Success)
                {
                    text = list.Groups[1].Value;
                    paragraph.Margin = new Thickness(22, 0, 0, 10);
                    paragraph.TextIndent = -22;
                    var marker = char.IsDigit(line[0]) ? line.Substring(0, list.Groups[1].Index) : "• ";
                    paragraph.Inlines.Add(new Run(marker)
                        { Foreground = Brush("#237CB4") });
                }
                AddInlines(paragraph, text);
                document.Blocks.Add(paragraph);
            }
            return document;
        }

        private static void AddInlines(Paragraph paragraph, string text)
        {
            var offset = 0;
            foreach (Match match in Regex.Matches(text, @"`([^`]+)`|\*\*([^*]+)\*\*"))
            {
                paragraph.Inlines.Add(new Run(text.Substring(offset, match.Index - offset)));
                if (match.Groups[1].Success)
                    paragraph.Inlines.Add(new Run(match.Groups[1].Value)
                    {
                        FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                        Background = Brush("#EDF4F9"), Foreground = Brush("#17577F")
                    });
                else paragraph.Inlines.Add(new Bold(new Run(match.Groups[2].Value)));
                offset = match.Index + match.Length;
            }
            paragraph.Inlines.Add(new Run(text.Substring(offset)));
        }

        private static SolidColorBrush Brush(string color) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
}
