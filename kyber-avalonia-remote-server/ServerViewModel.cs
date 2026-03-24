using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;

namespace KyberAvaloniaRemoteServer;

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Error
}

public class ServerViewModel : INotifyPropertyChanged, IDisposable
{
    private ServerState _serverState = ServerState.Stopped;
    private string _statusText = "Stopped";
    private string _controllerPath = "";
    private int _port = 9090;
    private string _password = "demo";
    private bool _softwareEncode;
    private bool _disposed;
    private string _connectionInfo = "";
    private string _lanIp = "";

    private readonly ControllerProcess _controller = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public ServerState ServerState
    {
        get => _serverState;
        set
        {
            if (SetField(ref _serverState, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(WindowTitle));
                ((RelayCommand)StartCommand).RaiseCanExecuteChanged();
                ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string ControllerPath
    {
        get => _controllerPath;
        set => SetField(ref _controllerPath, value);
    }

    public int Port
    {
        get => _port;
        set => SetField(ref _port, value);
    }

    public string Password
    {
        get => _password;
        set => SetField(ref _password, value);
    }

    public bool SoftwareEncode
    {
        get => _softwareEncode;
        set => SetField(ref _softwareEncode, value);
    }

    public string ConnectionInfo
    {
        get => _connectionInfo;
        set => SetField(ref _connectionInfo, value);
    }

    public string LanIp
    {
        get => _lanIp;
        set => SetField(ref _lanIp, value);
    }

    public bool IsRunning => _serverState == ServerState.Running;
    public bool CanStart => _serverState is ServerState.Stopped or ServerState.Error;

    public string WindowTitle => _serverState == ServerState.Running
        ? $"Kyber Server - Running on port {Port}"
        : "Kyber Server";

    public ObservableCollection<string> LogEntries { get; } = new();

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand ClearLogCommand { get; }

    public ServerViewModel()
    {
        StartCommand = new RelayCommand(_ => StartServer(), _ => CanStart);
        StopCommand = new RelayCommand(_ => StopServer(), _ => IsRunning);
        BrowseCommand = new RelayCommand(_ => { /* File picker handled in code-behind */ });
        ClearLogCommand = new RelayCommand(_ => LogEntries.Clear());

        // Detect LAN IP addresses
        LanIp = DetectLanIp();
        AddLog($"LAN IP: {LanIp}");

        // Auto-detect controller path
        var detected = ControllerProcess.FindControllerPath();
        if (detected is not null)
        {
            ControllerPath = detected;
            AddLog($"Auto-detected controller: {detected}");
        }
        else
        {
            AddLog("Controller not found. Set the path to controller.exe / kycontroller.");
        }

        _controller.OutputReceived += line =>
            Dispatcher.UIThread.Post(() => AddLog(line));
        _controller.ErrorReceived += line =>
            Dispatcher.UIThread.Post(() => AddLog($"[ERR] {line}"));
        _controller.ProcessExited += code =>
            Dispatcher.UIThread.Post(() => HandleProcessExited(code));
    }

    private void StartServer()
    {
        if (!CanStart) return;

        if (string.IsNullOrWhiteSpace(ControllerPath) || !File.Exists(ControllerPath))
        {
            StatusText = "Controller executable not found";
            ServerState = ServerState.Error;
            return;
        }

        try
        {
            ServerState = ServerState.Starting;
            StatusText = "Starting controller...";
            AddLog($"Starting: {ControllerPath}");

            var config = new ControllerConfig
            {
                Port = Port,
                Password = Password,
                SoftwareEncode = SoftwareEncode
            };

            _controller.Start(ControllerPath, config);

            ServerState = ServerState.Running;
            StatusText = $"Running on port {Port}";
            ConnectionInfo = $"{LanIp}:{Port}";
            AddLog($"Controller started on port {Port}");
            AddLog($"Client connection info:  Host: {LanIp}  Port: {Port}  Password: {Password}");
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to start: {ex.Message}";
            ServerState = ServerState.Error;
            AddLog($"Start failed: {ex.Message}");
        }
    }

    private void StopServer()
    {
        if (!IsRunning) return;

        try
        {
            AddLog("Stopping controller...");
            _controller.Stop();
            StatusText = "Stopped";
            ConnectionInfo = "";
            ServerState = ServerState.Stopped;
            AddLog("Controller stopped");
        }
        catch (Exception ex)
        {
            StatusText = $"Stop failed: {ex.Message}";
            AddLog($"Stop error: {ex.Message}");
        }
    }

    private void HandleProcessExited(int exitCode)
    {
        AddLog($"Controller exited with code {exitCode}");
        if (ServerState == ServerState.Running)
        {
            StatusText = $"Controller exited unexpectedly (code {exitCode})";
            ServerState = ServerState.Error;
        }
    }

    private void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogEntries.Add($"[{timestamp}] {message}");

        // Keep log manageable
        while (LogEntries.Count > 1000)
            LogEntries.RemoveAt(0);
    }

    private static string DetectLanIp()
    {
        try
        {
            // Find the best LAN IP by looking at active network interfaces
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel) continue;

                var props = iface.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.ToString();
                    // Skip link-local and loopback
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                    return ip;
                }
            }
        }
        catch
        {
            // Fallback below
        }

        // Fallback: use DNS
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    return ip.ToString();
            }
        }
        catch
        {
            // Give up
        }

        return "unknown";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.Dispose();
        GC.SuppressFinalize(this);
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
