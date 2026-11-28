using System;
using System.Collections.Generic;
using System.Text;

namespace DeskBox.Services.Timetable;

/// <summary>
/// 从 Excel / WPS 复制出来的制表符文本。这是课表导入的主通道：
/// 不管原文是 .xls 还是 .xlsx、横排还是竖排，复制出来的都是这张二维表。
/// </summary>
public static class TimetableTsv
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string? text)
    {
        List<IReadOnlyList<string>> rows = new();
        if (string.IsNullOrEmpty(text))
        {
            return rows;
        }

        List<string> current = new();
        StringBuilder cell = new();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }

                    continue;
                }

                cell.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case '\t':
                    current.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    current.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(current);
                    current = new List<string>();
                    break;
                default:
                    cell.Append(ch);
                    break;
            }
        }

        if (cell.Length > 0 || current.Count > 0)
        {
            current.Add(cell.ToString());
            rows.Add(current);
        }

        // 去掉尾部整行空白（Excel 复制时常带一行空行）。
        while (rows.Count > 0 && IsBlank(rows[^1]))
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return rows;
    }

    private static bool IsBlank(IReadOnlyList<string> row)
    {
        foreach (string cell in row)
        {
            if (!string.IsNullOrWhiteSpace(cell))
            {
                return false;
            }
        }

        return true;
    }
}
