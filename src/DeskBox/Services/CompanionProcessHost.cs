using System.Diagnostics;

namespace DeskBox.Services;

/// <summary>
/// Owns the bundled companion engine (<c>companion\deskbox-smart.exe</c>), which
/// performs AI-assisted desktop classification, 坚果云 WebDAV synchronisation and
/// NAS/inner-network backups. The engine is started hidden together with the app
/// and killed when the app exits, so the user only ever sees one program.
/// </summary>
public sealed class CompanionProcessHost : IDisposable
{
    private const string ExeName = "deskbox-smart.exe";
    private const string Arguments = "watch";
    private const string CompanionFolder = "companion";

    private Process? _process;
    private bool _disposed;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Starts the companion when it ships next to this build. Returns null when
    /// the folder is absent (upstream builds, unit tests, unpacked dev runs).
    /// </summary>
    public static CompanionProcessHost? StartIfPresent()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, CompanionFolder);
        string exePath = Path.Combine(directory, ExeName);
        if (!File.Exists(exePath))
        {
            return null;
        }

        var host = new CompanionProcessHost();
        if (!host.TryStart(exePath, directory))
        {
            host.Dispose();
            return null;
        }

        // Last-resort cleanup for exits that skip the normal shutdown path.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => host.Dispose();
        return host;
    }

    private bool TryStart(string exePath, string workingDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo(exePath, Arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            _process = Process.Start(startInfo);
            if (_process is null)
            {
                return false;
            }

            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    App.Log("[Companion] " + e.Data);
                }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    App.Log("[Companion:stderr] " + e.Data);
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            App.Log($"[Companion] started pid={_process.Id} from '{workingDirectory}'");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[Companion] start failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Companion] stop failed: {ex.Message}");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }
}
