using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using KyberSharp.Api;
using KyberSharp.Interop;

namespace KyberAvaloniaRemoteClient;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext!;
    private bool _isVideoFocused;
    private readonly HashSet<ushort> _pressedScancodes = new();

    public MainWindow()
    {
        InitializeComponent();

        DataContext = new MainViewModel();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.SetVideoHost(VideoHost);

        // Wire up input events on the video host
        VideoHost.PointerPressed += OnVideoPointerPressed;
        VideoHost.PointerReleased += OnVideoPointerReleased;
        VideoHost.PointerMoved += OnVideoPointerMoved;
        VideoHost.PointerWheelChanged += OnVideoPointerWheelChanged;
        VideoHost.KeyDown += OnVideoKeyDown;
        VideoHost.KeyUp += OnVideoKeyUp;
        VideoHost.TextInput += OnVideoTextInput;

        // Focus tracking for input suppression
        VideoHost.GotFocus += OnVideoHostGotFocus;
        VideoHost.LostFocus += OnVideoHostLostFocus;

        // Release mouse capture when window deactivates (Alt+Tab, etc.)
        Deactivated += OnWindowDeactivated;

        // Resize tracking for VideoLayout
        VideoHost.PropertyChanged += OnVideoHostPropertyChanged;

        // Volume display update
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.VolumeLevel) or nameof(MainViewModel.IsMuted)
            or nameof(MainViewModel.IsConnected))
        {
            UpdateVolumeDisplay();
        }
    }

    private void UpdateVolumeDisplay()
    {
        var vm = ViewModel;
        if (vm.IsMuted)
            VolumeText.Text = "Muted";
        else if (vm.IsConnected)
            VolumeText.Text = $"Vol: {vm.VolumeLevel * 100:F0}%";
        else
            VolumeText.Text = "";
    }

    private void OnVideoHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty)
        {
            var bounds = VideoHost.Bounds;
            ViewModel.UpdateWindowSize(bounds.Width, bounds.Height);
        }
    }

    private void OnVideoHostGotFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _isVideoFocused = true;
    }

    private void OnVideoHostLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _isVideoFocused = false;
        ReleaseAllKeys();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // Release pointer capture and all held keys when window loses activation (Alt+Tab, etc.)
        _isVideoFocused = false;
        ReleaseAllKeys();
    }

    /// <summary>
    /// Release all currently held keyboard keys to prevent stuck modifiers on the remote.
    /// </summary>
    private void ReleaseAllKeys()
    {
        var pipeline = ViewModel.InputPipeline;
        if (pipeline is null || _pressedScancodes.Count == 0) return;

        foreach (var scancode in _pressedScancodes)
        {
            using var packet = KeyboardKey.ToPacket(InputTarget.Host, scancode, false);
            pipeline.SendPacket(packet);
        }
        _pressedScancodes.Clear();
    }

    #region Input Forwarding

    private void OnVideoPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Grab focus and capture pointer to the video area
        VideoHost.Focus();
        e.Pointer.Capture(VideoHost);

        var pipeline = ViewModel.InputPipeline;
        if (pipeline is null) return;

        var point = e.GetCurrentPoint(VideoHost);
        var buttonType = MapMouseButton(point.Properties.PointerUpdateKind);
        if (buttonType is null) return;

        using var packet = KyberSharp.Api.MouseButton.ToPacket(InputTarget.Host, buttonType.Value, true);
        pipeline.SendPacket(packet);

        // Also send position
        SendMousePosition(e.GetPosition(VideoHost));
    }

    private void OnVideoPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Per spec 03: mouse capture is held until window deactivation or focus loss,
        // not released on button-up. This keeps the cursor captured in the video area.

        var pipeline = ViewModel.InputPipeline;
        if (pipeline is null) return;

        var buttonType = MapMouseButton(e.InitialPressMouseButton);
        if (buttonType is null) return;

        using var packet = KyberSharp.Api.MouseButton.ToPacket(InputTarget.Host, buttonType.Value, false);
        pipeline.SendPacket(packet);
    }

    private void OnVideoPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isVideoFocused || ViewModel.InputPipeline is null) return;
        SendMousePosition(e.GetPosition(VideoHost));
    }

    private void SendMousePosition(Point localPos)
    {
        var pipeline = ViewModel.InputPipeline;
        var layout = ViewModel.VideoLayout;
        var displays = ViewModel.Displays;
        if (pipeline is null || layout is null || displays is null || displays.Count == 0) return;

        if (layout.LocalToHost(localPos.X, localPos.Y, false, out var hostX, out var hostY))
        {
            using var packet = MousePosition.ToPacket(
                InputTarget.Host,
                displays[0].Id,
                (short)hostX,
                (short)hostY);
            pipeline.SendPacket(packet);
        }
    }

    private void OnVideoPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var pipeline = ViewModel.InputPipeline;
        if (!_isVideoFocused || pipeline is null) return;

        var dx = (float)Math.Clamp(e.Delta.X, -1.0, 1.0);
        var dy = (float)Math.Clamp(e.Delta.Y, -1.0, 1.0);

        using var packet = MouseWheel.ToPacket(InputTarget.Host, dx, dy);
        pipeline.SendPacket(packet);
    }

    private void OnVideoKeyDown(object? sender, KeyEventArgs e)
    {
        SendKeyEvent(e, true);
        e.Handled = true;
    }

    private void OnVideoKeyUp(object? sender, KeyEventArgs e)
    {
        SendKeyEvent(e, false);
        e.Handled = true;
    }

    private void SendKeyEvent(KeyEventArgs e, bool pressed)
    {
        var pipeline = ViewModel.InputPipeline;
        if (!_isVideoFocused || pipeline is null) return;

        // Use the physical key's native scancode when available
        var keycode = GetPlatformKeycode(e);
        if (keycode == 0) return;

        if (KeyboardKey.MapKeycode(keycode, out var scancode))
        {
            // Track pressed keys so we can release them all on focus loss
            if (pressed)
                _pressedScancodes.Add(scancode);
            else
                _pressedScancodes.Remove(scancode);

            using var packet = KeyboardKey.ToPacket(InputTarget.Host, scancode, pressed);
            pipeline.SendPacket(packet);
        }
    }

    private static ushort GetPlatformKeycode(KeyEventArgs e)
    {
        // Avalonia's PhysicalKey enum uses sequential internal IDs that do NOT
        // correspond to any platform's keycode space. KeycodeMapper translates
        // to evdev codes (Linux) or Win32 VK codes (Windows) for MapKeycode().
        return KeycodeMapper.GetPlatformKeycode(e.PhysicalKey);
    }

    private void OnVideoTextInput(object? sender, TextInputEventArgs e)
    {
        var pipeline = ViewModel.InputPipeline;
        if (!_isVideoFocused || pipeline is null || string.IsNullOrEmpty(e.Text)) return;

        foreach (var rune in e.Text.EnumerateRunes())
        {
            using var packet = UnicodeInput.ToPacket(InputTarget.Host, (uint)rune.Value);
            pipeline.SendPacket(packet);
        }
    }

    private static MouseButtonType? MapMouseButton(PointerUpdateKind kind)
    {
        return kind switch
        {
            PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => MouseButtonType.Left,
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => MouseButtonType.Middle,
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => MouseButtonType.Right,
            PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => MouseButtonType.Side,
            PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => MouseButtonType.Extra,
            _ => null
        };
    }

    private static MouseButtonType? MapMouseButton(Avalonia.Input.MouseButton button)
    {
        return button switch
        {
            Avalonia.Input.MouseButton.Left => MouseButtonType.Left,
            Avalonia.Input.MouseButton.Middle => MouseButtonType.Middle,
            Avalonia.Input.MouseButton.Right => MouseButtonType.Right,
            Avalonia.Input.MouseButton.XButton1 => MouseButtonType.Side,
            Avalonia.Input.MouseButton.XButton2 => MouseButtonType.Extra,
            _ => null
        };
    }

    #endregion

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Graceful disconnect before window close
        ViewModel.Dispose();
        base.OnClosing(e);
    }
}
