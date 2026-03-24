using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace KyberAvaloniaRemoteServer;

public partial class MainWindow : Window
{
    private ServerViewModel ViewModel => (ServerViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = new ServerViewModel();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Wire up file picker for Browse button
        BrowseButton.Click += async (_, _) => await BrowseForController();

        // Auto-scroll log to bottom
        if (ViewModel.LogEntries is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged += (_, _) =>
            {
                LogList.ScrollIntoView(ViewModel.LogEntries.Count - 1);
            };
        }
    }

    private async Task BrowseForController()
    {
        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Kyber Controller Executable",
            AllowMultiple = false
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (path is not null)
                ViewModel.ControllerPath = path;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ViewModel.Dispose();
        base.OnClosing(e);
    }
}
