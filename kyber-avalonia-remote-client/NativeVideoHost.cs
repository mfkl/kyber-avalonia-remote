using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace KyberAvaloniaRemoteClient;

/// <summary>
/// Hosts a native child window for video rendering via KyberSharp.
/// On Windows: creates an HWND child window via CreateWindowEx.
/// On Linux/X11: creates an X11 child window via XCreateSimpleWindow.
/// On macOS: uses the parent NSView directly (Avalonia provides one via NativeControlHost).
/// The native handle is passed to KyberSharp's VideoPlayerConfig.
/// </summary>
public class NativeVideoHost : NativeControlHost
{
    private ulong _nativeHandle;
    private IntPtr _x11Display;

    /// <summary>
    /// The native window handle (HWND on Windows, X11 window ID on Linux, NSView pointer on macOS).
    /// Valid after the control is attached to the visual tree.
    /// </summary>
    public ulong NativeHandle => _nativeHandle;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CreateWindowsChild(parent);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return CreateX11Child(parent);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return CreateMacOSChild(parent);
        }

        // Fallback: let the base create a default
        return base.CreateNativeControlCore(parent);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (_nativeHandle != 0)
            {
                Win32.DestroyWindow((IntPtr)(nint)_nativeHandle);
                _nativeHandle = 0;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (_nativeHandle != 0 && _x11Display != IntPtr.Zero)
            {
                X11.XDestroyWindow(_x11Display, (IntPtr)(nint)_nativeHandle);
                _nativeHandle = 0;
            }
            if (_x11Display != IntPtr.Zero)
            {
                X11.XCloseDisplay(_x11Display);
                _x11Display = IntPtr.Zero;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // NSView lifecycle is managed by Avalonia; we just created a subview.
            if (_nativeHandle != 0)
            {
                AppKit.objc_msgSend(_nativeHandle, AppKit.sel_registerName("removeFromSuperview"));
                AppKit.objc_msgSend_release(_nativeHandle, AppKit.sel_registerName("release"));
                _nativeHandle = 0;
            }
        }

        base.DestroyNativeControlCore(control);
    }

    private IPlatformHandle CreateWindowsChild(IPlatformHandle parent)
    {
        var parentHwnd = parent.Handle;
        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        var width = (int)(Bounds.Width * scaling);
        var height = (int)(Bounds.Height * scaling);

        if (width <= 0) width = 1;
        if (height <= 0) height = 1;

        var hwnd = Win32.CreateWindowEx(
            0, // dwExStyle
            "STATIC", // lpClassName
            "", // lpWindowName
            Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPSIBLINGS,
            0, 0, width, height,
            parentHwnd,
            IntPtr.Zero, // hMenu
            IntPtr.Zero, // hInstance
            IntPtr.Zero  // lpParam
        );

        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create Win32 child window");

        _nativeHandle = (ulong)(nuint)hwnd;
        return new PlatformHandle(hwnd, "HWND");
    }

    private IPlatformHandle CreateX11Child(IPlatformHandle parent)
    {
        _x11Display = X11.XOpenDisplay(IntPtr.Zero);
        if (_x11Display == IntPtr.Zero)
            throw new InvalidOperationException("Failed to open X11 display");

        var parentWindow = parent.Handle;
        var screen = X11.XDefaultScreen(_x11Display);
        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        var width = (uint)(Bounds.Width * scaling);
        var height = (uint)(Bounds.Height * scaling);

        if (width == 0) width = 1;
        if (height == 0) height = 1;

        var window = X11.XCreateSimpleWindow(
            _x11Display,
            parentWindow,
            0, 0,
            width, height,
            0, // border_width
            0, // border color
            0  // background (black)
        );

        X11.XMapWindow(_x11Display, window);
        X11.XFlush(_x11Display);

        _nativeHandle = (ulong)(nuint)window;
        return new PlatformHandle(window, "XID");
    }

    private IPlatformHandle CreateMacOSChild(IPlatformHandle parent)
    {
        // Avalonia on macOS provides an NSView as the parent handle.
        // We create a child NSView and add it as a subview for VLC to render into.
        var parentNsView = parent.Handle;
        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        var width = Bounds.Width * scaling;
        var height = Bounds.Height * scaling;

        if (width <= 0) width = 1;
        if (height <= 0) height = 1;

        // [[NSView alloc] initWithFrame:NSMakeRect(0, 0, width, height)]
        var nsViewClass = AppKit.objc_getClass("NSView");
        var alloc = AppKit.objc_msgSend_IntPtr(nsViewClass, AppKit.sel_registerName("alloc"));
        var frame = new AppKit.NSRect(0, 0, width, height);
        var childView = AppKit.objc_msgSend_initWithFrame(alloc, AppKit.sel_registerName("initWithFrame:"), frame);

        if (childView == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create macOS NSView child");

        // [parentView addSubview:childView]
        AppKit.objc_msgSend_addSubview(parentNsView, AppKit.sel_registerName("addSubview:"), childView);

        _nativeHandle = (ulong)(nuint)childView;
        return new PlatformHandle(childView, "NSView");
    }

    /// <summary>
    /// Resize the native child window to match the current Avalonia bounds.
    /// Call this when the control's bounds change.
    /// </summary>
    public void ResizeNativeWindow()
    {
        if (_nativeHandle == 0) return;

        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        var width = (int)(Bounds.Width * scaling);
        var height = (int)(Bounds.Height * scaling);

        if (width <= 0 || height <= 0) return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Win32.MoveWindow((IntPtr)(nint)_nativeHandle, 0, 0, width, height, true);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && _x11Display != IntPtr.Zero)
        {
            X11.XMoveResizeWindow(_x11Display, (IntPtr)(nint)_nativeHandle, 0, 0, (uint)width, (uint)height);
            X11.XFlush(_x11Display);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var frame = new AppKit.NSRect(0, 0, width, height);
            AppKit.objc_msgSend_setFrame((IntPtr)(nint)_nativeHandle, AppKit.sel_registerName("setFrame:"), frame);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        ResizeNativeWindow();
        return result;
    }
}

/// <summary>Platform handle wrapper for NativeControlHost.</summary>
internal class PlatformHandle : IPlatformHandle
{
    public PlatformHandle(IntPtr handle, string descriptor)
    {
        Handle = handle;
        HandleDescriptor = descriptor;
    }

    public IntPtr Handle { get; }
    public string HandleDescriptor { get; }
}

/// <summary>Win32 P/Invoke declarations for native child window management.</summary>
internal static partial class Win32
{
    internal const uint WS_CHILD = 0x40000000;
    internal const uint WS_VISIBLE = 0x10000000;
    internal const uint WS_CLIPSIBLINGS = 0x04000000;

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);
}

/// <summary>X11/Xlib P/Invoke declarations for native child window management.</summary>
internal static partial class X11
{
    private const string LibX11 = "libX11.so.6";

    [LibraryImport(LibX11)]
    internal static partial IntPtr XOpenDisplay(IntPtr displayName);

    [LibraryImport(LibX11)]
    internal static partial int XDefaultScreen(IntPtr display);

    [LibraryImport(LibX11)]
    internal static partial IntPtr XCreateSimpleWindow(
        IntPtr display, IntPtr parent,
        int x, int y, uint width, uint height,
        uint borderWidth, ulong border, ulong background);

    [LibraryImport(LibX11)]
    internal static partial int XMapWindow(IntPtr display, IntPtr window);

    [LibraryImport(LibX11)]
    internal static partial int XCloseDisplay(IntPtr display);

    [LibraryImport(LibX11)]
    internal static partial int XDestroyWindow(IntPtr display, IntPtr window);

    [LibraryImport(LibX11)]
    internal static partial int XMoveResizeWindow(IntPtr display, IntPtr window, int x, int y, uint width, uint height);

    [LibraryImport(LibX11)]
    internal static partial int XFlush(IntPtr display);
}

/// <summary>macOS Objective-C runtime P/Invoke declarations for NSView management.</summary>
internal static partial class AppKit
{
    private const string LibObjC = "/usr/lib/libobjc.dylib";

    [StructLayout(LayoutKind.Sequential)]
    internal struct NSRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;

        public NSRect(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr objc_getClass(string name);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr sel_registerName(string name);

    // objc_msgSend variants — each C# signature covers one ObjC call convention.
    // The Objective-C runtime dispatches by selector at runtime; we need distinct
    // managed signatures because P/Invoke requires fixed parameter lists.

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void objc_msgSend(ulong receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void objc_msgSend_release(ulong receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr objc_msgSend_initWithFrame(IntPtr receiver, IntPtr selector, NSRect frame);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void objc_msgSend_addSubview(IntPtr receiver, IntPtr selector, IntPtr subview);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void objc_msgSend_setFrame(IntPtr receiver, IntPtr selector, NSRect frame);
}
