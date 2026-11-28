using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace DeskBox.Services.Timetable;

/// <summary>
/// 最小 .xlsx 读取器：只用 BCL 的 <see cref="ZipArchive"/> + <see cref="XmlReader"/>，
/// 不引第三方库（AOT 友好）。只做一件事——把第一张工作表读成二维字符串表，
/// 其余样式、公式结果、批注一概不管。
///
/// 解析是「一趟线性扫描 + 状态机」，刻意不用嵌套 <c>ReadSubtree</c>：
/// 嵌套子树 + Skip() 会让外层读取器越过相邻单元格，表现为整行值错位。
///
/// 明确不支持 .xls（老 BIFF8 二进制）：解析成本高、边界多，装了也只能半准，
/// 界面统一提示「另存为 .xlsx 或整块复制粘贴」。
/// </summary>
public static class XlsxSheetReader
{
    public sealed record Result(bool Ok, string? Error, IReadOnlyList<IReadOnlyList<string>> Rows);

    private const string Dtd = "http://www.w3.org/2000/xmlns/";

    public static bool LooksLikeXlsx(string path) =>
        path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase);

    public static Result ReadFirstSheet(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new Result(false, "找不到文件：" + path, []);
        }

        if (path.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
        {
            return new Result(
                false,
                "老式 .xls 不支持直接读取。请用 WPS/Excel 另存为 .xlsx，或整块复制后点「从剪贴板导入」。",
                []);
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            using ZipArchive zip = new(stream, ZipArchiveMode.Read);

            List<string> shared = ReadSharedStrings(zip);
            string sheetPath = FindFirstSheetPath(zip);
            if (sheetPath.Length == 0)
            {
                return new Result(false, "这个文件里没有工作表。", []);
            }

            List<List<string>> rows = ReadSheet(zip, sheetPath, shared);
            return new Result(true, null, rows);
        }
        catch (InvalidDataException)
        {
            return new Result(false, "文件不是有效的 .xlsx（可能是被加密或改过扩展名）。", []);
        }
        catch (XmlException)
        {
            return new Result(false, "工作表内容损坏，读不出单元格。", []);
        }
        catch (IOException ex)
        {
            return new Result(false, "读文件失败：" + ex.Message, []);
        }
    }

    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreProcessingInstructions = true,
        IgnoreComments = true
    };

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        List<string> strings = new();
        ZipArchiveEntry? entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return strings;
        }

        using Stream stream = entry.Open();
        using XmlReader reader = XmlReader.Create(stream, Settings());
        StringBuilder current = new();
        bool inItem = false;
        bool inText = false;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.LocalName == "si")
                    {
                        inItem = true;
                        current.Clear();
                    }
                    else if (reader.LocalName == "t" && inItem)
                    {
                        inText = !reader.IsEmptyElement;
                    }

                    break;
                case XmlNodeType.Text:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.CDATA:
                    if (inItem && inText)
                    {
                        current.Append(reader.Value);
                    }

                    break;
                case XmlNodeType.EndElement:
                    if (reader.LocalName == "t")
                    {
                        inText = false;
                    }
                    else if (reader.LocalName == "si" && inItem)
                    {
                        strings.Add(current.ToString());
                        inItem = false;
                        current.Clear();
                    }

                    break;
            }
        }

        return strings;
    }

    private static string FindFirstSheetPath(ZipArchive zip)
    {
        ZipArchiveEntry? workbook = zip.GetEntry("xl/workbook.xml");
        ZipArchiveEntry? rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbook is not null && rels is not null)
        {
            string relationId = string.Empty;
            using (Stream stream = workbook.Open())
            using (XmlReader reader = XmlReader.Create(stream, Settings()))
            {
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet")
                    {
                        relationId = reader.GetAttribute("r:id") ??
                                     reader.GetAttribute("id", Dtd) ??
                                     string.Empty;
                        if (relationId.Length > 0)
                        {
                            break;
                        }
                    }
                }
            }

            if (relationId.Length > 0)
            {
                using Stream stream = rels.Open();
                using XmlReader reader = XmlReader.Create(stream, Settings());
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship")
                    {
                        continue;
                    }

                    string? id = reader.GetAttribute("Id");
                    string? target = reader.GetAttribute("Target");
                    if (id is null || target is null || !string.Equals(id, relationId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string normalized = target.StartsWith("/", StringComparison.Ordinal)
                        ? target.TrimStart('/')
                        : "xl/" + target.TrimStart('.', '/');
                    if (zip.GetEntry(normalized) is not null)
                    {
                        return normalized;
                    }
                }
            }
        }

        return zip.GetEntry("xl/worksheets/sheet1.xml") is not null ? "xl/worksheets/sheet1.xml" : string.Empty;
    }

    private static List<List<string>> ReadSheet(ZipArchive zip, string sheetPath, List<string> shared)
    {
        List<List<string>> rows = new();

        ZipArchiveEntry entry = zip.GetEntry(sheetPath)!;
        using (Stream stream = entry.Open())
        using (XmlReader reader = XmlReader.Create(stream, Settings()))
        {
            int rowIndex = -1;
            int column = -1;
            string cellType = string.Empty;
            StringBuilder value = new();
            StringBuilder inline = new();
            bool inCell = false;
            bool inValue = false;
            bool inInline = false;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        case "row":
                            rowIndex = ParseInt(reader.GetAttribute("r"), rows.Count + 1) - 1;
                            column = -1;
                            EnsureRow(rows, rowIndex);
                            if (reader.IsEmptyElement)
                            {
                                rowIndex = -1;
                            }

                            break;
                        case "c":
                            if (rowIndex < 0)
                            {
                                break;
                            }

                            column = reader.GetAttribute("r") is { } reference
                                ? ColumnIndex(reference)
                                : column + 1;
                            cellType = reader.GetAttribute("t") ?? string.Empty;
                            value.Clear();
                            inline.Clear();
                            inCell = true;
                            if (reader.IsEmptyElement)
                            {
                                Store(rows, rowIndex, column, string.Empty);
                                inCell = false;
                            }

                            break;
                        case "v" when inCell:
                            inValue = !reader.IsEmptyElement;
                            break;
                        case "t" when inCell:
                            inInline = !reader.IsEmptyElement;
                            break;
                        case "mergeCell":
                            // 刻意不展开合并单元格：展开会把纵向合并的班级名补到教师行、
                            // 把表头「时间/班级」补进第一列，解析器就会把教师行当成另一个班
                            // （实测 14 个班被读成 28 个）。星期表头本来就靠列间距推算，
                            // 不需要展开。合并的数据格（跨两节连堂）在 v1 里按左上一格处理。
                            break;
                    }

                    continue;
                }

                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA)
                {
                    if (inCell && inValue)
                    {
                        value.Append(reader.Value);
                    }
                    else if (inCell && inInline)
                    {
                        inline.Append(reader.Value);
                    }

                    continue;
                }

                if (reader.NodeType != XmlNodeType.EndElement)
                {
                    continue;
                }

                if (reader.LocalName == "v")
                {
                    inValue = false;
                }
                else if (reader.LocalName == "t")
                {
                    inInline = false;
                }
                else if (reader.LocalName == "c" && inCell)
                {
                    Store(rows, rowIndex, column, ResolveCellText(cellType, value.ToString(), inline.ToString(), shared));
                    inCell = false;
                }

                if (reader.LocalName == "row")
                {
                    rowIndex = -1;
                }
            }
        }

        while (rows.Count > 0 && rows[^1].TrueForAll(cell => cell.Length == 0))
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return rows;
    }

    private static string ResolveCellText(string type, string value, string inline, List<string> shared)
    {
        if (type == "s")
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) &&
                   index >= 0 && index < shared.Count
                ? shared[index]
                : string.Empty;
        }

        if (type == "inlineStr")
        {
            return inline;
        }

        return value;
    }

    private static void Store(List<List<string>> rows, int row, int column, string text)
    {
        if (row < 0 || column < 0)
        {
            return;
        }

        EnsureRow(rows, row);
        EnsureCell(rows[row], column);
        rows[row][column] = text;
    }

    private static int ColumnIndex(string reference)
    {
        int value = 0;
        foreach (char ch in reference)
        {
            if (ch is >= 'A' and <= 'Z')
            {
                value = (value * 26) + (ch - 'A' + 1);
            }
            else if (ch is >= 'a' and <= 'z')
            {
                value = (value * 26) + (ch - 'a' + 1);
            }
            else
            {
                break;
            }
        }

        return Math.Max(0, value - 1);
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private static void EnsureRow(List<List<string>> rows, int index)
    {
        while (rows.Count <= index)
        {
            rows.Add(new List<string>());
        }
    }

    private static void EnsureCell(List<string> row, int index)
    {
        while (row.Count <= index)
        {
            row.Add(string.Empty);
        }
    }

}
