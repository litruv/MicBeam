using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CrossPlatformMicStreamer.ViewModels;

namespace CrossPlatformMicStreamer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Opened += (_, _) => _viewModel.CompleteUiInitialization();
        Closed += OnClosed;
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        _viewModel.RefreshDiscovery();
    }

    private void OnAddPeerClicked(object? sender, RoutedEventArgs e)
    {
        _viewModel.AddManualPeer(ManualIpTextBox.Text ?? string.Empty);
        ManualIpTextBox.Text = string.Empty;
    }

    private void OnManualIpKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.AddManualPeer(ManualIpTextBox.Text ?? string.Empty);
            ManualIpTextBox.Text = string.Empty;
        }
    }

    private void OnBufferEditClicked(object? sender, RoutedEventArgs e)
    {
        _viewModel.ToggleBufferEdit();

        if (_viewModel.IsEditingBuffer)
        {
            BufferEditTextBox.Focus();
            BufferEditTextBox.SelectAll();
        }
    }

    private void OnBufferEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.ApplyBufferEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.CancelBufferEdit();
            e.Handled = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
    }
}
