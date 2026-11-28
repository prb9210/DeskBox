using System;
using System.Collections.Generic;
using System.Text;

namespace DeskBox.Services.Timetable;

/// <summary>
/// 课表格子的文字清洗：全角空格、换行、科目别名、以及「科目 + 老师」合成格的拆分。
/// 所有方法都必须是幂等的——导入预览与最终落盘走同一套清洗，否则体检数字和
/// 实际入库内容会对不上。
/// </summary>
public static class TimetableText
{
    /// <summary>科目别名 → 规范名。只收敛同义写法，不做「政治/法治」这类合并。</summary>
    private static readonly Dictionary<string, string> s_subjectAliases = new(StringComparer.Ordinal)
    {
        ["电脑"] = "信息技术",
        ["微机"] = "信息技术",
        ["计算机"] = "信息技术",
        ["信息技术课"] = "信息技术",
        ["道法"] = "法治",
        ["品德"] = "政治",
        ["劳动"] = "劳动教育"
    };

    /// <summary>出现这些词就说明这一格写的是科目而不是人名。</summary>
    private static readonly string[] s_subjectHints =
    [
        "语文", "数学", "英语", "物理", "化学", "生物", "历史", "地理", "政治", "法治",
        "体育", "音乐", "美术", "心理", "信息", "电脑", "微机", "班会", "周会", "活动",
        "劳动", "自习", "实验", "综合", "健康", "安全", "书法", "阅读"
    ];

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder builder = new(text.Length);
        bool lastSpace = false;
        foreach (char ch in text)
        {
            char current = ch;
            if (current is '\u00A0' or '\u3000' or '\r' or '\n' or '\t')
            {
                current = ' ';
            }

            if (current == ' ')
            {
                if (lastSpace || builder.Length == 0)
                {
                    continue;
                }

                lastSpace = true;
                builder.Append(' ');
                continue;
            }

            lastSpace = false;
            builder.Append(current);
        }

        return builder.ToString().TrimEnd();
    }

    public static string CanonicalSubject(string? subject)
    {
        string normalized = Normalize(subject);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return s_subjectAliases.TryGetValue(normalized, out string? canonical) ? canonical : normalized;
    }

    public static bool LooksLikeSubject(string? text)
    {
        string normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return false;
        }

        foreach (string hint in s_subjectHints)
        {
            if (normalized.Contains(hint, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 人名判定：2 至 4 个汉字，且不像科目/班级/说明文字。总课程表里教师单元格
    /// 偶尔会写成「地理 黎科」这种科目带人名的形态，靠这条判断把两半拆开。
    /// </summary>
    public static bool LooksLikePersonName(string? text)
    {
        string normalized = Normalize(text);
        if (normalized.Length is < 2 or > 4)
        {
            return false;
        }

        if (LooksLikeSubject(normalized) || IsClassLabel(normalized))
        {
            return false;
        }

        foreach (char ch in normalized)
        {
            if (ch < 0x4E00 || ch > 0x9FFF)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>班级标签：初三2、初二5、七年级(3)班、九（2）班 等。</summary>
    public static bool IsClassLabel(string? text)
    {
        string normalized = Normalize(text);
        if (normalized.Length is < 2 or > 10)
        {
            return false;
        }

        string[] prefixes =
        [
            "初一", "初二", "初三", "高一", "高二", "高三",
            "七年级", "八年级", "九年级", "一年级", "二年级", "三年级"
        ];

        foreach (string prefix in prefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return normalized.EndsWith("班", StringComparison.Ordinal) &&
               normalized.Length <= 8 &&
               !LooksLikeSubject(normalized);
    }

    /// <summary>
    /// 拆分「科目 老师」合成格。返回 true 表示这一格确实写了两样东西。
    /// 拆不出来就原样返回 false，调用方按「未识别」记一条可疑项，绝不猜。
    /// </summary>
    public static bool TrySplitSubjectAndTeacher(string? text, out string subject, out string teacher)
    {
        subject = string.Empty;
        teacher = string.Empty;
        string normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return false;
        }

        string[] parts = normalized.Split([' ', '/', '／', '、', '，', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        if (!LooksLikeSubject(parts[0]))
        {
            return false;
        }

        // 「电脑 白少锋」「地理 黎科」：首段是科目，末段是人名。
        string last = parts[^1];
        if (!LooksLikePersonName(last) && !LooksLikeTeacherToken(last))
        {
            return false;
        }

        subject = CanonicalSubject(parts[0]);
        teacher = last;
        return true;
    }

    /// <summary>教师名可以是 2 至 4 个汉字，也允许「李老师」这类不带姓的写法（3 字内）。</summary>
    private static bool LooksLikeTeacherToken(string text)
    {
        if (text.Length is < 2 or > 4)
        {
            return false;
        }

        foreach (char ch in text)
        {
            if (ch < 0x4E00 || ch > 0x9FFF)
            {
                return false;
            }
        }

        return !LooksLikeSubject(text);
    }

    /// <summary>从班级名推年级，用于分组显示（「初三2」→「九年级」）。</summary>
    public static string? GradeOf(string? className)
    {
        string normalized = Normalize(className);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.StartsWith("初一", StringComparison.Ordinal) ||
            normalized.StartsWith("七年级", StringComparison.Ordinal))
        {
            return "七年级";
        }

        if (normalized.StartsWith("初二", StringComparison.Ordinal) ||
            normalized.StartsWith("八年级", StringComparison.Ordinal))
        {
            return "八年级";
        }

        if (normalized.StartsWith("初三", StringComparison.Ordinal) ||
            normalized.StartsWith("九年级", StringComparison.Ordinal))
        {
            return "九年级";
        }

        if (normalized.StartsWith("高一", StringComparison.Ordinal))
        {
            return "高一";
        }

        if (normalized.StartsWith("高二", StringComparison.Ordinal))
        {
            return "高二";
        }

        if (normalized.StartsWith("高三", StringComparison.Ordinal))
        {
            return "高三";
        }

        return null;
    }

    /// <summary>班级稳定 id：只保留字母数字和汉字，避免中文标点进文件名。</summary>
    public static string ClassId(string? className)
    {
        string normalized = Normalize(className);
        StringBuilder builder = new(normalized.Length);
        foreach (char ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.Length == 0 ? "class" : builder.ToString();
    }
}
