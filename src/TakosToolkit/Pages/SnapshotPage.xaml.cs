using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TakosToolkit.Core.SystemInfo;

namespace TakosToolkit.Pages;

public partial class SnapshotPage : Page
{
    private IReadOnlyList<InfoSection> _sections = [];
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    public SnapshotPage()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => { StatusText.Text = ""; _statusTimer.Stop(); };
        Loaded += async (_, _) =>
        {
            if (_sections.Count == 0)
                await RefreshAsync();
        };
    }

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        StatusText.Text = "Collecting…";
        try
        {
            _sections = await SystemSnapshot.CollectAsync();
            SectionsList.ItemsSource = _sections;
            StatusText.Text = "";
            CopyButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't collect system info: " + ex.Message;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(SystemSnapshot.ToTicketText(_sections));
        ShowStatus("Copied to clipboard.");
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InfoItem item })
        {
            Clipboard.SetText(item.Value);
            ShowStatus($"Copied {item.Label}.");
        }
    }
}
