using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KyberAvaloniaRemoteServer;

/// <summary>
/// Manages the lifecycle of the native kycontroller process.
/// The controller is a standalone Rust executable that exposes an HTTP/WebSocket API
/// for desktop capture, encoding, and input injection.
/// </summary>
public sealed class ControllerProcess : IDisposable
{
    private Process? _process;
    private readonly List<string> _outputLog = new();
    private readonly object _logLock = new();
    private bool _disposed;

    public bool IsRunning => _process is not null && !_process.HasExited;
    public int? ExitCode => _process is { HasExited: true } p ? p.ExitCode : null;

    public event Action<string>? OutputReceived;
    public event Action<string>? ErrorReceived;
    public event Action<int>? ProcessExited;

    /// <summary>
    /// Resolves the path to the kycontroller executable.
    /// Looks for it adjacent to the current executable, or in the kyber-desktop build output.
    /// </summary>
    public static string? FindControllerPath()
    {
        var candidates = new List<string>();

        // Adjacent to this executable
        var appDir = AppContext.BaseDirectory;
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "controller.exe"
            : "kycontroller";

        candidates.Add(Path.Combine(appDir, exeName));
        candidates.Add(Path.Combine(appDir, "kyber", exeName));

        // Common kyber-desktop build output locations relative to repo
        var repoRoot = FindRepoRoot(appDir);
        if (repoRoot is not null)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                candidates.Add(Path.Combine(repoRoot, "kyber-desktop", "kyber-windows-x86_64", exeName));
                candidates.Add(Path.Combine(repoRoot, "kyber-desktop", "rootfs-x86_64-w64-mingw32", "bin", exeName));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                candidates.Add(Path.Combine(repoRoot, "kyber-desktop", "rootfs-x86_64-linux-gnu", "bin", exeName));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = startDir;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "kyber-desktop")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public void Start(string controllerPath, ControllerConfig config)
    {
        if (IsRunning)
            throw new InvalidOperationException("Controller is already running");

        var args = BuildArgs(config);

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = controllerPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_logLock) _outputLog.Add(e.Data);
            OutputReceived?.Invoke(e.Data);
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_logLock) _outputLog.Add($"[ERR] {e.Data}");
            ErrorReceived?.Invoke(e.Data);
        };

        _process.Exited += (_, _) =>
        {
            ProcessExited?.Invoke(_process.ExitCode);
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public void Stop()
    {
        if (_process is null || _process.HasExited) return;

        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
    }

    public IReadOnlyList<string> GetLog()
    {
        lock (_logLock)
            return _outputLog.ToList();
    }

    private static string BuildArgs(ControllerConfig config)
    {
        var args = new List<string>();

        if (config.Port != 0)
        {
            args.Add($"--port {config.Port}");
        }

        if (!string.IsNullOrEmpty(config.Password))
        {
            args.Add($"--password {config.Password}");
        }

        if (config.SoftwareEncode)
        {
            args.Add("--software-encode");
        }

        return string.Join(" ", args);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _process?.Dispose();
    }
}

public class ControllerConfig
{
    public int Port { get; set; } = 9090;
    public string Password { get; set; } = "demo";
    public bool SoftwareEncode { get; set; }
}
