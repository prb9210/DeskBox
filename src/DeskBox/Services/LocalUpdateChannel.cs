using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeskBox.Services;

/// <summary>
/// The nutstore update channel: the release package is dropped into a synced
/// folder (manifest.json + zip), so checking is a local file read — no network,
/// no GitHub, no 403 behind the school firewall.
///
/// The boot-time half is a per-user scheduled task running the shipped worker
/// script. The settings checkbox registers or removes it; the user never sees
/// schtasks or a .bat.
///
/// Note: this is separate from <see cref="AppUpdateService"/>, which speaks to
/// the upstream deskbox.fun manifest and hands an installer .exe to a detached
/// helper. That pipeline cannot update this build (different package layout),
/// so the two channels stay explicit instead of sharing state.
/// </summary>
public static class LocalUpdateChannel
{
    public const string TaskNameLogon = "DeskBox自动更新";
    public const string TaskNameTimer = "DeskBox自动更新-定时";

    private const string WorkerFileName = "自动更新.ps1";
    private const string VersionFileName = "version.txt";
    private const string ManifestFileName = "manifest.json";
    private const string SourceOverrideFileName = "update-source.txt";
    private const string LogFileName = "update.log";

    public sealed record UpdateStatus(
        string CurrentVersion,
        string? RemoteVersion,
        string? SourceDirectory,
        bool UpdateAvailable,
        string Message);

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBox");

    public static string WorkerPath
    {
        get
        {
            string root = Path.Combine(AppContext.BaseDirectory, WorkerFileName);
            if (File.Exists(root))
            {
                return root;
            }

            // Older packages copied the worker under Assets\ because the csproj
            // content item kept its source folder; keep that layout working.
            string legacy = Path.Combine(AppContext.BaseDirectory, "Assets", WorkerFileName);
            return File.Exists(legacy) ? legacy : root;
        }
    }

    public static string CurrentVersion
    {
        get
        {
            try
            {
                string file = Path.Combine(AppContext.BaseDirectory, VersionFileName);
                if (File.Exists(file))
                {
                    string text = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }
            }
            catch (Exception)
            {
                // Fall back to the assembly version below.
            }

            return Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                .Split('+')[0] ?? "0.0.0";
        }
    }

    /// <summary>Where the synced update folder may land on this machine.</summary>
    public static IReadOnlyList<string> SourceCandidates()
    {
        var list = new List<string>();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            string overrideFile = Path.Combine(AppContext.BaseDirectory, SourceOverrideFileName);
            if (File.Exists(overrideFile))
            {
                string custom = File.ReadAllText(overrideFile).Trim();
                if (!string.IsNullOrEmpty(custom))
                {
                    list.Add(custom);
                }
            }
        }
        catch (Exception)
        {
            // The override is optional.
        }

        list.Add(Path.Combine(@"C:\同步", "DeskBox更新"));
        list.Add(Path.Combine(profile, "我的坚果云", "DeskBox更新"));
        list.Add(Path.Combine(profile, "Nutstore", "我的坚果云", "DeskBox更新"));
        list.Add(Path.Combine(profile, "坚果云", "我的坚果云", "DeskBox更新"));
        list.Add(Path.Combine(profile, "Nutstore", "DeskBox更新"));
        list.Add(Path.Combine(profile, "Desktop", "DeskBox更新"));

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                list.Add(Path.Combine(drive.RootDirectory.FullName, "我的坚果云", "DeskBox更新"));
                list.Add(Path.Combine(drive.RootDirectory.FullName, "坚果云", "DeskBox更新"));
            }
            catch (Exception)
            {
                // A sleeping drive must not break the scan.
            }
        }

        return list;
    }

    private static string? FindSource(out string? remoteVersion, out string manifestVersionText)
    {
        remoteVersion = null;
        manifestVersionText = string.Empty;

        foreach (string dir in SourceCandidates())
        {
            try
            {
                string manifest = Path.Combine(dir, ManifestFileName);
                if (!File.Exists(manifest))
                {
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (document.RootElement.TryGetProperty("version", out JsonElement version) &&
                    version.ValueKind == JsonValueKind.String)
                {
                    remoteVersion = version.GetString();
                    manifestVersionText = directorySafe(dir);
                }

                if (!string.IsNullOrEmpty(remoteVersion))
                {
                    return dir;
                }
            }
            catch (Exception)
            {
                // Half-synced manifest: keep looking.
            }
        }

        return null;
    }

    private static string directorySafe(string dir) => dir;

    public static string? LastLogLine()
    {
        try
        {
            string log = Path.Combine(AppContext.BaseDirectory, LogFileName);
            if (!File.Exists(log))
            {
                return null;
            }

            string[] lines = File.ReadAllLines(log);
            return lines.Length == 0 ? null : lines[^1];
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Compares the synced manifest against the installed version.</summary>
    public static UpdateStatus Check()
    {
        string current = CurrentVersion;
        string? source = FindSource(out string? remote, out _);

        if (source is null || string.IsNullOrEmpty(remote))
        {
            return new UpdateStatus(
                current,
                null,
                null,
                false,
                "没找到更新文件夹（坚果云里应有一个「DeskBox更新」目录，里面有 manifest.json）");
        }

        bool newer;
        try
        {
            newer = Version.Parse(remote) > Version.Parse(current);
        }
        catch (Exception)
        {
            return new UpdateStatus(current, remote, source, false, "版本号无法比较：" + remote);
        }

        return newer
            ? new UpdateStatus(current, remote, source, true, "发现新版本 " + remote + "，点「立即检查并更新」安装")
            : new UpdateStatus(current, remote, source, false, "已是最新（" + remote + "）");
    }

    /// <summary>Runs the worker now; returns whatever it printed or logged.</summary>
    public static async Task<string> RunWorkerAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(WorkerPath))
        {
            return "安装目录里缺少 " + WorkerFileName + "，无法更新。请用完整安装包覆盖一次。";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + WorkerPath + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("powershell 启动失败");
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            string text = (output + error).Trim();
            return string.IsNullOrEmpty(text) ? LastLogLine() ?? "已执行更新检查" : text;
        }
        catch (Exception ex)
        {
            return "执行更新脚本失败：" + ex.Message;
        }
    }

    public static bool IsAutoUpdateEnabled() => TaskExists(TaskNameLogon);

    private static bool TaskExists(string taskName)
    {
        (int code, _) = RunSchtasks("/Query /TN \"" + taskName + "\"");
        return code == 0;
    }

    /// <summary>Registers or removes the logon + every-2-hours check.</summary>
    public static (bool Ok, string Message) SetAutoUpdateEnabled(bool enabled)
    {
        if (!File.Exists(WorkerPath))
        {
            return (false, "安装目录里缺少 " + WorkerFileName + "，请用完整安装包覆盖一次。");
        }

        string command = "powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"" + WorkerPath + "\"";

        if (enabled)
        {
            (int code1, string out1) = RunSchtasks(
                "/Create /TN \"" + TaskNameLogon + "\" /TR \"" + command + "\" /SC ONLOGON /RL LIMITED /F");
            (int code2, string out2) = RunSchtasks(
                "/Create /TN \"" + TaskNameTimer + "\" /TR \"" + command + "\" /SC HOURLY /MO 2 /RL LIMITED /F");

            if (code1 == 0 && code2 == 0)
            {
                return (true, "已开启：登录时检查一次，之后每 2 小时检查一次。");
            }

            return (false, ("注册计划任务失败：" + out1 + " " + out2).Trim());
        }

        RunSchtasks("/Delete /TN \"" + TaskNameLogon + "\" /F");
        RunSchtasks("/Delete /TN \"" + TaskNameTimer + "\" /F");
        return (true, "已关闭开机自动检查（「立即检查并更新」仍可用）。");
    }

    private static (int Code, string Output) RunSchtasks(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, "schtasks 启动失败");
            }

            string text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(10000);
            return (process.ExitCode, text.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
