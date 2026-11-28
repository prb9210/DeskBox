using System;
using System.IO;

namespace DeskBox.Platform;

/// <summary>
/// 待办 / 随手记「托管附件」的跨机搬运。
///
/// 托管副本原来只活在 <c>%LOCALAPPDATA%\DeskBox\widgets\&lt;id&gt;\</c>，另一台机器
/// 上的同一个绝对路径不存在，附件就是断链。这里把文件复制进资料库的
/// <c>_deskbox\attachments\&lt;附件Id&gt;\&lt;文件名&gt;</c>，共享镜像里只写逻辑路径
/// <c>@{Library}\…</c>，另一台按自己的资料库根解析回去——路径不再跟机器绑定。
///
/// 放在 Platform 是因为模块边界契约
/// （DestructiveFileOperations_StayInsideOwnedDomains）要求 File.Copy 只能出现在
/// FileSafety / Core.Persistence / Platform 三个域里。
/// </summary>
public static class AttachmentMirror
{
    /// <summary>逻辑路径前缀；与 engine 配置里的 @{Library} 同一套写法。</summary>
    public const string LibraryToken = "@{Library}";

    private const string SharedDirName = "_deskbox";
    private const string AttachmentsDirName = "attachments";

    public static bool IsLogical(string? path)
    {
        return !string.IsNullOrEmpty(path) &&
               path.StartsWith(LibraryToken, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把逻辑路径换成本机绝对路径；不是逻辑路径就原样返回。</summary>
    public static string Resolve(string path, string libraryRoot)
    {
        if (!IsLogical(path) || string.IsNullOrWhiteSpace(libraryRoot))
        {
            return path;
        }

        string tail = path.Substring(LibraryToken.Length).TrimStart('\\', '/');
        return Path.Combine(libraryRoot, tail.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// 把本机托管附件复制进资料库，返回逻辑路径。
    ///
    /// 返回 null 表示「别改这个路径」：库为空、源文件不存在、源与目标是同一个文件。
    /// 已有同名目标时不再复制（附件是只读资料，后到的同名文件不覆盖先到的）。
    /// </summary>
    public static string? TryMirror(
        string filePath,
        string libraryRoot,
        string attachmentId,
        string? displayName)
    {
        if (IsLogical(filePath))
        {
            return filePath;
        }

        if (string.IsNullOrWhiteSpace(filePath) ||
            string.IsNullOrWhiteSpace(libraryRoot) ||
            string.IsNullOrWhiteSpace(attachmentId))
        {
            return null;
        }

        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            string? inside = RelativeToLibrary(filePath, libraryRoot);
            if (inside is not null)
            {
                // 已经在资料库里（用户本来就放库里的 linked 附件）：只转逻辑路径，
                // 不再复制一份到 attachments\ 下。
                return LibraryToken + "\\" + inside.Replace('/', '\\');
            }

            string relative = Path.Combine(
                SharedDirName,
                AttachmentsDirName,
                SafeSegment(attachmentId),
                SafeFileName(displayName, filePath));
            string destination = Path.Combine(libraryRoot, relative);

            bool sameFile = string.Equals(
                Path.GetFullPath(filePath),
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase);

            if (!sameFile)
            {
                string? directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (!File.Exists(destination))
                {
                    File.Copy(filePath, destination, overwrite: false);
                }
            }

            return LibraryToken + "\\" + relative.Replace('/', '\\');
        }
        catch (Exception ex)
        {
            DeskBox.App.Log("[AttachmentMirror] 搬运失败 " + filePath + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 文件已在资料库内时返回相对路径（正斜杠）；否则 null。
    /// 用带分隔符的前缀比较，避免 C:\库 命中 C:\库2。
    /// </summary>
    private static string? RelativeToLibrary(string filePath, string libraryRoot)
    {
        string root = Path.GetFullPath(libraryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(filePath);

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return full.Substring(root.Length).Replace('\\', '/');
    }

    private static string SafeSegment(string value)
    {
        string result = value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalid, '_');
        }

        return result.Length == 0 ? "item" : result;
    }

    private static string SafeFileName(string? displayName, string filePath)
    {
        string name = Path.GetFileName(displayName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(filePath);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "attachment";
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }
}
