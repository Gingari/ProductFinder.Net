using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ProductFinder.Models;
using ProductFinder.Services;
using ProductFinder.ViewModels;

namespace ProductFinder.Views;

public partial class ExcelImportWindow : Window
{
    private readonly ExcelImportViewModel _viewModel;
    public IReadOnlyList<RequestLine>? Result { get; private set; }
    public ExcelImportWindow(string path, IReadOnlyList<string> sheets, IExcelService excel)
    {
        InitializeComponent();
        FileNameText.Text = Path.GetFileName(path);
        _viewModel = new(path, sheets, excel, message => MessageBox.Show(this, message, "Импорт Excel", MessageBoxButton.OK, MessageBoxImage.Warning));
        DataContext = _viewModel;
        ContentRendered += OnContentRendered;
        Closed += (_, _) => _viewModel.Dispose();
    }
    private async void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        await _viewModel.InitializeAsync();
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !RowsGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        try
        {
            var result = _viewModel.GetSelectedLines();
            if (result.Count == 0) { MessageBox.Show(this, "Не выбрана ни одна строка для импорта.", "Импорт Excel", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            Result = result;
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Импорт Excel", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
