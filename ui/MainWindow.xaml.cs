using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SheetNumberingRevit.Commands.Models;
using SheetNumberingRevit.Commands.ViewModels;

namespace SheetNumberingRevit.Views;

public partial class MainWindow : Window
{
    private readonly bool _isDesignMode;
    private bool _isUpdatingSelection;
    private bool _forceClose = false;

    public MainWindow()
    {
        _isDesignMode = System.ComponentModel.DesignerProperties.GetIsInDesignMode(this);
        InitializeComponent();

        if (_isDesignMode)
            DataContext = new MainViewModel();
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        if (!_isDesignMode && viewModel != null)
        {
            DataContext = viewModel;
            // FIX MODELESS TRANSACTION: Handle RequestClose to close window from ViewModel
            viewModel.RequestClose += (s, e) =>
            {
                _forceClose = true;
                Close();
            };
            // FIX MODELESS TRANSACTION: Handle Completed to close window on OK click
            viewModel.Completed += (s, e) =>
            {
                LoggingService.LogInfo($"Apply completed: {e.SheetsUpdated} sheets updated");
            };
            // FIX MODELESS TRANSACTION: Handle Cancelled to close window
            viewModel.Cancelled += (s, e) =>
            {
                _forceClose = true;
                Close();
            };
        }
    }

    // FIX MODELESS TRANSACTION: Override Close to handle window closing
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceClose && !_isDesignMode)
        {
            // If not forced close and not in design mode, force close for Cancel behavior
            _forceClose = true;
        }
        base.OnClosing(e);
    }

    private void SheetCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (DataContext is not MainViewModel viewModel) return;
        if (sender is not CheckBox cb) return;

        var clicked = cb.DataContext as SheetInfo;
        if (clicked == null) return;

        var newState = cb.IsChecked == true;
        bool hasHighlight = SheetsGrid != null && SheetsGrid.SelectedItems.Count > 1
            && SheetsGrid.SelectedItems.Contains(clicked);

        if (!hasHighlight) { viewModel.UpdateSelectedCount(); return; }

        _isUpdatingSelection = true;
        try
        {
            foreach (var sheet in SheetsGrid.SelectedItems.Cast<SheetInfo>())
                if (sheet.IsSelected != newState) sheet.IsSelected = newState;
        }
        finally { _isUpdatingSelection = false; }

        viewModel.UpdateSelectedCount();
        viewModel.RefreshPreview();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            // FIX MODELESS TRANSACTION: Escape closes window (like Cancel)
            if (DataContext is MainViewModel vm)
            {
                _forceClose = true;
                vm.CancelCommand.Execute(null);
            }
            e.Handled = true;
        }
    }
}
