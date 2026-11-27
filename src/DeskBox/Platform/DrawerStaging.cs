using System;
using System.Collections.Generic;
using System.IO;

namespace DeskBox.Platform;

/// <summary>
/// 悬浮球「拖文件到球上暂存」的文件移动。放在 Platform 是因为模块边界契约
/// (DestructiveFileOperations_StayInsideOwnedDomains) 要求 File.Move 只出现在
/// FileSafety / Core.Persistence / Platform 三个域里；App 层只负责决定“放到哪”。
/// </summary>
public static class DrawerStaging
{
    /// <summary>同名不覆盖：追加 _2、_3…，极端情况下用 6 位随机后缀。</summary>
    public static string UniqueDestination(string folder, string fileName)
    {
        string dest = Path.Combine(folder, fileName);
        if (!File.Exists(dest))
        {
            return dest;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            dest = Path.Combine(folder, stem + "_" + i + ext);
            if (!File.Exists(dest))
            {
                return dest;
            }
        }

        return Path.Combine(folder, stem + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
    }

    /// <summary>把一批文件移进目标目录；返回成功数与逐条失败原因。文件夹只记账不递归。</summary>
    public static (int Moved, List<string> Failures) MoveFiles(
        string destinationFolder,
        IReadOnlyList<string> paths)
    {
        int moved = 0;
        var failures = new List<string>();
        foreach (string path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    failures.Add(Path.GetFileName(path) + "（是文件夹）");
                    continue;
                }

                if (!File.Exists(path))
                {
                    continue;
                }

                File.Move(path, UniqueDestination(destinationFolder, Path.GetFileName(path)));
                moved++;
            }
            catch (Exception ex)
            {
                failures.Add(Path.GetFileName(path) + "（" + ex.Message + "）");
            }
        }

        return (moved, failures);
    }
}
