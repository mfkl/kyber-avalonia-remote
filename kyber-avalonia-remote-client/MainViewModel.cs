using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using KyberSharp.Api;
using KyberSharp.Interop;

namespace KyberAvaloniaRemoteClient;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Streaming,
    Reconnecting,
    Error
}

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private string _host = "";
    private string _username = "demo";
    private string _password = "demo";
    private ConnectionState _connectionState = ConnectionState.Disconnected;
    private string _statusText = "Disconnected";
    private string _bitrateText = "";
    private string _latencyText = "";
    private string _fpsText = "";
    private float _volumeLevel;
    private bool _isMuted;
    private bool _softwareDecode = true;
    private bool _disposed;
    private bool _restartingStreaming;

    // FPS tracking: count FrameRendered metrics per second
    private int _frameCount;
    private DateTime _lastFpsUpdate = DateTime.UtcNow;

    private KyberClient? _client;
    private ConnectConfig? _connectConfig;
    private AuthCredentials? _credentials;
    private StreamingProtocol? _streamingProtocol;
    private StreamingSessionConfig? _sessionConfig;
    private InputPipeline? _inputPipeline;
    private InputSessionConfig? _inputSessionConfig;
    private VideoLayout? _videoLayout;

    private IReadOnlyList<KyClientDisplay>? _displays;
    private NativeVideoHost? _videoHost;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Host
    {
        get => _host;
        set => SetField(ref _host, value);
    }

    public string Username
    {
        get => _username;
        set => SetField(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => SetField(ref _password, value);
    }

    public ConnectionState ConnectionState
    {
        get => _connectionState;
        set
        {
            if (SetField(ref _connectionState, value))
            {
                // Error state: callers set StatusText with a specific message before
                // setting ConnectionState to Error, so don't overwrite it here.
                if (value != ConnectionState.Error)
                {
                    StatusText = value switch
                    {
                        ConnectionState.Disconnected => "Disconnected",
                        ConnectionState.Connecting => $"Connecting to {Host}...",
                        ConnectionState.Connected => $"Connected to {Host}",
                        ConnectionState.Streaming => $"Streaming from {Host}",
                        ConnectionState.Reconnecting => $"Reconnecting to {Host}...",
                        _ => value.ToString()
                    };
                }
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(WindowTitle));
                ((RelayCommand)ConnectCommand).RaiseCanExecuteChanged();
                ((RelayCommand)DisconnectCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string BitrateText
    {
        get => _bitrateText;
        set => SetField(ref _bitrateText, value);
    }

    public string LatencyText
    {
        get => _latencyText;
        set => SetField(ref _latencyText, value);
    }

    public string FpsText
    {
        get => _fpsText;
        set => SetField(ref _fpsText, value);
    }

    public bool SoftwareDecode
    {
        get => _softwareDecode;
        set => SetField(ref _softwareDecode, value);
    }

    public float VolumeLevel
    {
        get => _volumeLevel;
        set => SetField(ref _volumeLevel, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => SetField(ref _isMuted, value);
    }

    public bool IsConnected => _connectionState is ConnectionState.Connected
        or ConnectionState.Streaming
        or ConnectionState.Reconnecting;

    public bool CanConnect => _connectionState == ConnectionState.Disconnected
        || _connectionState == ConnectionState.Error;

    public string WindowTitle => _connectionState == ConnectionState.Disconnected
        ? "Kyber - Disconnected"
        : $"Kyber - {_connectionState} - {Host}";

    public InputPipeline? InputPipeline => _inputPipeline;
    public VideoLayout? VideoLayout => _videoLayout;
    public IReadOnlyList<KyClientDisplay>? Displays => _displays;

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }

    public MainViewModel()
    {
        ConnectCommand = new RelayCommand(_ => Connect(), _ => CanConnect);
        DisconnectCommand = new RelayCommand(_ => Disconnect(), _ => IsConnected);
    }

    /// <summary>
    /// Set the video host control so connection logic can pass its native handle to KyberSharp.
    /// </summary>
    public void SetVideoHost(NativeVideoHost videoHost)
    {
        _videoHost = videoHost;
    }

    private void Connect()
    {
        if (!CanConnect) return;

        try
        {
            ConnectionState = ConnectionState.Connecting;

            // Enable native logging on first connect
            KyberLog.SetLogCallback((level, target, message) =>
            {
                Console.WriteLine($"[Kyber/{level}] {target}: {message}");
            });

            _credentials = AuthCredentials.CreateBasic(Username, Password);
            _connectConfig = ConnectConfig.Create(Host, _credentials)
                .SetAutoReconnection(true);
            _connectConfig.EnableTofuAutoAccept();

            _client = new KyberClient(_connectConfig, enableMetrics: true);
            _client.EventReceived += OnEventReceived;
            _client.SetMetricsCallback(OnMetric);
            _client.Connect();
        }
        catch (Exception ex)
        {
            StatusText = $"Connection failed: {ex.Message}";
            ConnectionState = ConnectionState.Error;
            CleanupConnection();
        }
    }

    private void Disconnect()
    {
        if (_client is not null)
        {
            try
            {
                _client.DisconnectAndStop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Disconnect error: {ex.Message}");
            }
        }
        CleanupConnection();
        ConnectionState = ConnectionState.Disconnected;
        BitrateText = "";
        LatencyText = "";
        FpsText = "";
        _frameCount = 0;
    }

    private void OnEventReceived(object? sender, KyberEventArgs e)
    {
        // All UI updates must be marshalled to the UI thread
        Dispatcher.UIThread.Post(() => HandleEvent(e));
    }

    private void OnMetric(Metric metric)
    {
        if (metric.Type == MetricType.Video && metric.Data.Video.Data == VideoMetricData.FrameDisplayed)
        {
            _frameCount++;
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastFpsUpdate).TotalSeconds;
            if (elapsed >= 1.0)
            {
                var fps = (int)(_frameCount / elapsed);
                _frameCount = 0;
                _lastFpsUpdate = now;
                Dispatcher.UIThread.Post(() => FpsText = $"{fps} FPS");
            }
        }
        else if (metric.Type == MetricType.NetworkPing)
        {
            var latencyMs = metric.Data.NetworkPing.DelayMicros / 1000.0;
            Dispatcher.UIThread.Post(() => LatencyText = $"{latencyMs:F0} ms");
        }
    }

    private void HandleEvent(KyberEventArgs e)
    {
        switch (e.EventType)
        {
            case EventType.ControlPlaneConnected:
                HandleControlPlaneConnected((ControlPlaneConnectedEventArgs)e);
                break;

            case EventType.StreamingStarted:
                ConnectionState = ConnectionState.Streaming;
                break;

            case EventType.StreamingStartFailed:
                StatusText = "Streaming failed to start";
                ConnectionState = ConnectionState.Error;
                CleanupConnection();
                break;

            case EventType.StreamingStopped:
                if (_restartingStreaming)
                {
                    // Suppress state change during intentional restart (e.g. display list update)
                    _restartingStreaming = false;
                }
                else if (ConnectionState == ConnectionState.Streaming)
                {
                    ConnectionState = ConnectionState.Connected;
                }
                break;

            case EventType.Stopped:
                HandleStopped((StoppedEventArgs)e);
                break;

            case EventType.Reconnecting:
                var reconnecting = (ReconnectingEventArgs)e;
                ConnectionState = ConnectionState.Reconnecting;
                StatusText = $"Reconnecting to {Host}... (attempt {reconnecting.Attempt}/{reconnecting.MaxAttempts})";
                break;

            case EventType.Reconnected:
                var reconnected = (ReconnectedEventArgs)e;
                ConnectionState = ConnectionState.Connected;
                if (reconnected.ShouldRestartStreaming)
                {
                    StartStreaming();
                }
                break;

            case EventType.ReconnectionFailed:
                StatusText = "Reconnection failed";
                ConnectionState = ConnectionState.Error;
                CleanupConnection();
                break;

            case EventType.DisplayListUpdated:
                var displayUpdate = (DisplayListUpdatedEventArgs)e;
                _displays = displayUpdate.Displays;
                UpdateVideoLayoutFromDisplays();
                // Restart streaming with updated display info if currently streaming
                if (ConnectionState == ConnectionState.Streaming)
                {
                    Console.WriteLine("Display list updated while streaming — restarting stream");
                    _restartingStreaming = true;
                    _client?.StopStreaming();
                    StartStreaming();
                }
                break;

            case EventType.BitrateChanged:
                var bitrateEvent = (BitrateChangedEventArgs)e;
                BitrateText = FormatBitrate(bitrateEvent.Bitrate);
                break;

            case EventType.AudioVolumeChanged:
                var volumeEvent = (AudioVolumeChangedEventArgs)e;
                VolumeLevel = volumeEvent.Volume;
                break;

            case EventType.AudioMuteChanged:
                var muteEvent = (AudioMuteChangedEventArgs)e;
                IsMuted = muteEvent.Mute;
                break;

            case EventType.ControlPlaneConnectFailed:
                StatusText = "Connection refused";
                ConnectionState = ConnectionState.Error;
                CleanupConnection();
                break;

            case EventType.DataPlaneConnectFailed:
                StatusText = "Data plane connection failed";
                ConnectionState = ConnectionState.Error;
                CleanupConnection();
                break;

            case EventType.StreamerPipelineEvent:
                var pipeline = (StreamerPipelineEventArgs)e;
                Console.WriteLine($"Pipeline: {pipeline.PipelineType}/{pipeline.Stage}/{pipeline.PipelineEventType}");
                break;

            case EventType.InputServiceConnected:
                Console.WriteLine("Input service connected");
                break;

            case EventType.InputServiceDisconnected:
                Console.WriteLine("Input service disconnected");
                break;

            case EventType.DataPlaneClosed:
                Console.WriteLine("Data plane closed");
                if (ConnectionState == ConnectionState.Streaming)
                {
                    StatusText = "Data plane closed";
                    ConnectionState = ConnectionState.Connected;
                }
                break;
        }
    }

    private void HandleControlPlaneConnected(ControlPlaneConnectedEventArgs e)
    {
        ConnectionState = ConnectionState.Connected;
        _displays = e.Displays;

        if (_displays.Count == 0)
        {
            StatusText = "Connected but no displays available";
            return;
        }

        UpdateVideoLayoutFromDisplays();
        StartStreaming();
    }

    private void StartStreaming()
    {
        if (_client is null || _displays is null || _displays.Count == 0)
            return;

        try
        {
            var display = _displays[0];

            // Create input pipeline
            _inputPipeline?.Dispose();
            _inputPipeline = InputPipeline.CreateClient();
            _inputPipeline.Start();
            _inputSessionConfig?.Dispose();
            _inputSessionConfig = InputSessionConfig.Create(_inputPipeline);

            // Create streaming protocol
            _streamingProtocol?.Dispose();
            _streamingProtocol = StreamingProtocol.Kymux(
                KymuxVideoProtocol.GopStream,
                KymuxAudioProtocol.Unreliable);

            // Create session config
            _sessionConfig?.Dispose();
            _sessionConfig = StreamingSessionConfig.Create(_streamingProtocol)
                .SetInput(_inputSessionConfig)
                .SetAudio(AudioSessionConfig.Create());

            // Create video session if we have a native window handle
            if (_videoHost is not null && _videoHost.NativeHandle != 0)
            {
                var windowHandle = CreateWindowHandle(_videoHost.NativeHandle);
                var playerConfig = VideoPlayerConfig.Create(windowHandle)
                    .SetSoftwareDecode(SoftwareDecode);

                var videoConfig = VideoSessionConfig.Create(display.Id, playerConfig);
                _sessionConfig.AddVideo(videoConfig);
            }

            _client.StartStreaming(_sessionConfig);
        }
        catch (Exception ex)
        {
            StatusText = $"Streaming failed: {ex.Message}";
            Console.WriteLine($"StartStreaming error: {ex}");
            ConnectionState = ConnectionState.Error;
        }
    }

    private static WindowHandle CreateWindowHandle(ulong nativeHandle)
    {
        if (OperatingSystem.IsWindows())
            return WindowHandle.FromHwnd(nativeHandle);
        if (OperatingSystem.IsLinux())
            return WindowHandle.FromX11(nativeHandle);
        if (OperatingSystem.IsMacOS())
            return WindowHandle.FromNsView(nativeHandle);

        throw new PlatformNotSupportedException("Unsupported platform for video rendering");
    }

    private void UpdateVideoLayoutFromDisplays()
    {
        if (_displays is null || _displays.Count == 0) return;

        var display = _displays[0];
        var windowWidth = _videoHost?.Bounds.Width ?? 1280;
        var windowHeight = _videoHost?.Bounds.Height ?? 720;

        _videoLayout?.Dispose();
        _videoLayout = new VideoLayout(windowWidth, windowHeight, display.Width, display.Height);
    }

    /// <summary>Update the VideoLayout when the video host control resizes.</summary>
    public void UpdateWindowSize(double width, double height)
    {
        _videoLayout?.SetWindowSize(width, height);
    }

    private void HandleStopped(StoppedEventArgs e)
    {
        var reasonText = e.Reason switch
        {
            StopReason.Requested => "Disconnected by request",
            StopReason.ControlPlaneClosed => "Server closed the connection",
            StopReason.ControlPlaneIoError => "Connection I/O error",
            StopReason.ControlPlaneTimeout => "Connection timed out",
            StopReason.DecodeError => "Video decode error",
            _ => $"Stopped: {e.Reason}"
        };

        StatusText = reasonText;
        ConnectionState = e.Reason == StopReason.Requested
            ? ConnectionState.Disconnected
            : ConnectionState.Error;

        CleanupConnection();
    }

    private static string FormatBitrate(uint bitrate)
    {
        return bitrate switch
        {
            >= 1_000_000 => $"{bitrate / 1_000_000.0:F1} Mbps",
            >= 1_000 => $"{bitrate / 1_000.0:F0} kbps",
            _ => $"{bitrate} bps"
        };
    }

    private void CleanupConnection()
    {
        if (_client is not null)
        {
            _client.EventReceived -= OnEventReceived;
            _client.Dispose();
            _client = null;
        }

        try { _inputPipeline?.Stop(); }
        catch (Exception ex) { Console.WriteLine($"InputPipeline.Stop failed: {ex.Message}"); }
        try { _inputPipeline?.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"InputPipeline.Dispose failed: {ex.Message}"); }
        _inputPipeline = null;

        _inputSessionConfig?.Dispose();
        _inputSessionConfig = null;

        _videoLayout?.Dispose();
        _videoLayout = null;

        _sessionConfig?.Dispose();
        _sessionConfig = null;

        _streamingProtocol?.Dispose();
        _streamingProtocol = null;

        _connectConfig?.Dispose();
        _connectConfig = null;

        _credentials?.Dispose();
        _credentials = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();

        try { KyberLog.SetLogCallback(null); }
        catch (DllNotFoundException) { /* Native lib not loaded */ }

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

    public event System.EventHandler? CanExecuteChanged;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, System.EventArgs.Empty);
}
