using System;
using System.IO;
using DeskBox.Platform;

namespace DeskBox.Tests;

/// <summary>
/// 托管附件跨机搬运的纯路径/复制语义。换机失效的根因是附件路径写死了本机
/// 绝对路径，这里钉住「镜像写逻辑路径、本机解析回绝对路径」的往返。
/// </summary>
public sealed class AttachmentMirrorTests
{
    [Fact]
    public void LogicalPathsRoundTripThroughLibraryRoot()
    {
        string library = Path.Combine(Path.GetTempPath(), "deskbox-lib");
        string logical = AttachmentMirror.LibraryToken + "\\_deskbox\\attachments\\abc\\a.jpg";

        Assert.True(AttachmentMirror.IsLogical(logical));

        string resolved = AttachmentMirror.Resolve(logical, library);
        Assert.Equal(
            Path.Combine(library, "_deskbox", "attachments", "abc", "a.jpg"),
            resolved);
        Assert.False(AttachmentMirror.IsLogical(resolved));
    }

    [Fact]
    public void ResolveLeavesPlainPathsUntouched()
    {
        string absolute = Path.Combine(Path.GetTempPath(), "plain.jpg");
        Assert.Equal(absolute, AttachmentMirror.Resolve(absolute, Path.GetTempPath()));
        Assert.Equal(absolute, AttachmentMirror.Resolve(absolute, string.Empty));
    }

    [Fact]
    public void TryMirrorCopiesFileIntoLibraryAndReturnsLogicalPath()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "deskbox-att-" + Guid.NewGuid().ToString("N"));
        string sourceDir = Path.Combine(root, "widgets", "w1");
        string library = Path.Combine(root, "library");
        Directory.CreateDirectory(sourceDir);
        string source = Path.Combine(sourceDir, "照片.jpg");
        File.WriteAllText(source, "payload");

        try
        {
            string? logical = AttachmentMirror.TryMirror(source, library, "att1", "照片.jpg");

            Assert.NotNull(logical);
            Assert.True(AttachmentMirror.IsLogical(logical));

            string mirrored = AttachmentMirror.Resolve(logical!, library);
            Assert.True(File.Exists(mirrored));
            Assert.Equal("payload", File.ReadAllText(mirrored));

            // 只复制，不搬走原件：本机仍在用原路径。
            Assert.True(File.Exists(source));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TryMirrorIsIdempotent()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "deskbox-att-" + Guid.NewGuid().ToString("N"));
        string library = Path.Combine(root, "library");
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "a.jpg");
        File.WriteAllText(source, "one");

        try
        {
            string? first = AttachmentMirror.TryMirror(source, library, "att1", "a.jpg");
            string? second = AttachmentMirror.TryMirror(source, library, "att1", "a.jpg");

            Assert.NotNull(first);
            Assert.Equal(first, second);

            // 第二次不能再复制覆盖：库里仍是第一次的内容。
            File.WriteAllText(source, "two");
            AttachmentMirror.TryMirror(source, library, "att1", "a.jpg");
            string mirrored = AttachmentMirror.Resolve(first!, library);
            Assert.Equal("one", File.ReadAllText(mirrored));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TryMirrorRefusesWithoutSourceOrLibrary()
    {
        Assert.Null(AttachmentMirror.TryMirror("/nope/x.jpg", string.Empty, "id", "x.jpg"));

        string root = Path.Combine(
            Path.GetTempPath(),
            "deskbox-att-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(AttachmentMirror.TryMirror(
                Path.Combine(root, "missing.jpg"),
                root,
                "id",
                null));
            Assert.Null(AttachmentMirror.TryMirror(Path.Combine(root, "missing.jpg"), root, string.Empty, null));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TryMirrorReusesFileAlreadyInsideLibraryWithoutCopying()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "deskbox-att-" + Guid.NewGuid().ToString("N"));
        string library = Path.Combine(root, "library");
        Directory.CreateDirectory(library);
        string inside = Path.Combine(library, "已有附件.jpg");
        File.WriteAllText(inside, "x");

        try
        {
            string? logical = AttachmentMirror.TryMirror(inside, library, "att1", "已有附件.jpg");

            Assert.Equal(
                AttachmentMirror.LibraryToken + "\\已有附件.jpg",
                logical);
            // 不该在 attachments\ 下再复制一份。
            Assert.False(Directory.Exists(Path.Combine(library, "_deskbox", "attachments", "att1")));
            Assert.Equal(inside, AttachmentMirror.Resolve(logical!, library));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>资料库换盘：旧绝对路径不在了，按附件 Id 回新库认领。</summary>
    [Fact]
    public void RepairIfMissingRewritesToNewLibraryRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "deskbox-repair-" + Guid.NewGuid().ToString("N"));
        string sourceDir = Path.Combine(root, "widgets", "w1");
        string oldLibrary = Path.Combine(root, "old-library");
        string newLibrary = Path.Combine(root, "new-library");
        Directory.CreateDirectory(sourceDir);
        string source = Path.Combine(sourceDir, "卷子.jpg");
        File.WriteAllText(source, "x");

        try
        {
            // 先进旧库，再把库搬到别处（同步目录换盘）。
            string? logical = AttachmentMirror.TryMirror(source, oldLibrary, "att9", "卷子.jpg");
            Assert.NotNull(logical);
            string oldPath = AttachmentMirror.Resolve(logical!, oldLibrary);
            Directory.CreateDirectory(Path.GetDirectoryName(newLibrary)!);
            Directory.Move(oldLibrary, newLibrary);
            Assert.False(File.Exists(oldPath));

            string? repaired = AttachmentMirror.RepairIfMissing(oldPath, newLibrary, "att9", "卷子.jpg");
            Assert.NotNull(repaired);
            Assert.True(File.Exists(repaired));
            Assert.Equal(
                Path.Combine(newLibrary, "_deskbox", "attachments", "att9", "卷子.jpg"),
                repaired);

            // 文件还在原处时不该乱改。
            Assert.Null(AttachmentMirror.RepairIfMissing(source, newLibrary, "att9", "卷子.jpg"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
