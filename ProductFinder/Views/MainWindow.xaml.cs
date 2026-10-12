using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProductFinder.Models;
using ProductFinder.Services;
using ProductFinder.ViewModels;

namespace ProductFinder.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    public MainWindow()
    {
        InitializeComponent();
        var excel = new ExcelService();
        _viewModel = new(new CatalogLoader(), new ProductSearchService(), new WindowDialogService(this, excel), excel: excel);
        DataContext = _viewModel;
        ContentRendered += OnContentRendered;
        Closed += OnClosed;
    }
    private async void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        await _viewModel.InitializeAsync();
    }
    private bool CommitExportEdits()
    {
        if (ExportItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true) && ExportItemsGrid.CommitEdit(DataGridEditingUnit.Row, true)) return true;
        MessageBox.Show(this, "Исправьте количество в редактируемой строке.", "Количество", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
    private void ExportExcelButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommitExportEdits()) _viewModel.ExportExcelCommand.Execute(null);
    }
    private void ImportExcelButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommitExportEdits()) _viewModel.ImportExcelCommand.Execute(null);
    }
    private void SmartInsertButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommitExportEdits()) _viewModel.SmartInsertCommand.Execute(null);
    }
    private async void ParseEtmButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ExportItem item } && CommitExportEdits())
            await _viewModel.ParseEtmAsync(item);
    }
    private void OnClosed(object? sender, EventArgs e) => _viewModel.Dispose();
    private void SearchResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(SearchResultsGrid, source) is not DataGridRow { Item: SearchResult result }) return;
        e.Handled = true;
        _viewModel.AddSearchResult(result);
    }
    private async void ExportItemsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        if (FindParent<Button>(source) is not null) return;
        var cell = FindParent<DataGridCell>(source);
        if (cell?.Column.DisplayIndex == 1) return;
        if (ItemsControl.ContainerFromElement(ExportItemsGrid, source) is not DataGridRow { Item: ExportItem item }) return;
        if (!ExportItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !ExportItemsGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        e.Handled = true;
        await _viewModel.ChooseAlternativeAsync(item);
    }
    private void ExportItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SetSelectedExportItems(ExportItemsGrid.SelectedItems.OfType<ExportItem>().ToArray());
    private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = element is Visual || element is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
}
