using System.IO;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;

namespace TakosToolkit;

public partial class MainWindow
{
    // Keep one instance of each page so results (like a disk scan) survive switching tools.
    private readonly CachedPageProvider _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        RootNavigation.SetPageProviderService(_pages);

        Loaded += (_, _) =>
        {
            SystemThemeWatcher.Watch(this);
            HandleStartupArguments(Environment.GetCommandLineArgs().Skip(1).ToArray());
        };
    }

    /// <summary>
    /// Supported switches:
    ///   --disk [path]   open Disk Space and scan the path (default: system drive)
    /// </summary>
    private void HandleStartupArguments(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--disk", StringComparison.OrdinalIgnoreCase))
        {
            RootNavigation.Navigate(typeof(Pages.DiskSpacePage));
            string path = args.Length > 1 ? args[1] : Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            if (_pages.GetPage(typeof(Pages.DiskSpacePage)) is Pages.DiskSpacePage page)
                page.Scan(path);
            return;
        }

        RootNavigation.Navigate(typeof(Pages.SnapshotPage));
    }

    private sealed class CachedPageProvider : INavigationViewPageProvider
    {
        private readonly Dictionary<Type, object> _pages = [];

        public object? GetPage(Type pageType)
        {
            if (!_pages.TryGetValue(pageType, out var page))
            {
                page = Activator.CreateInstance(pageType)!;
                _pages[pageType] = page;
            }
            return page;
        }
    }
}
