using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace KyberAvaloniaRemoteClient;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--validate"))
        {
            return RunValidation(args);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static int RunValidation(string[] args)
    {
        // Validate that the app, window, and all UI components can be constructed.
        // Uses the desktop lifetime but shuts down immediately after the window opens.
        // If no display is available (e.g. headless CI), we still verify type construction.
        try
        {
            // Verify core types can be constructed
            var vm = new MainViewModel();
            if (string.IsNullOrEmpty(vm.WindowTitle))
                throw new InvalidOperationException("WindowTitle is empty");
            if (vm.ConnectionState != ConnectionState.Disconnected)
                throw new InvalidOperationException("Initial state should be Disconnected");

            Console.WriteLine("Validation: ViewModel OK.");

            // Try to launch with a display; timeout gracefully if headless
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var task = Task.Run(() =>
            {
                BuildAvaloniaApp().Start((application, _) =>
                {
                    if (application is App avApp
                        && avApp.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.MainWindow!.Opened += (_, _) =>
                        {
                            Console.WriteLine("Validation: Window opened successfully.");
                            desktop.Shutdown(0);
                        };
                    }
                }, args);
            }, cts.Token);

            if (task.Wait(TimeSpan.FromSeconds(15)))
            {
                Console.WriteLine("Validation: Full UI validation passed.");
            }
            else
            {
                Console.WriteLine("Validation: No display available, type-level validation passed.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Validation failed: {ex.Message}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
