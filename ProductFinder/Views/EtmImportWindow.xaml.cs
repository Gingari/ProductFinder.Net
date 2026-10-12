using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ProductFinder.Models;
using ProductFinder.Services;
using ProductFinder.Services.Etm;
using ProductFinder.ViewModels;

namespace ProductFinder.Views;

public partial class EtmImportWindow : Window
{
    private readonly EtmImportViewModel _viewModel;
    private bool _allowClose;
    private bool _closing;
    public EtmSaveResult? Result => _viewModel.Result;
    public EtmImportWindow(string sourceText, string databasePath)
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        var height = Math.Max(400, area.Height - 16);
        var width = Math.Max(640, area.Width - 16);
        MinHeight = Math.Min(MinHeight, height);
        MinWidth = Math.Min(MinWidth, width);
        MaxHeight = height;
        MaxWidth = width;
        Height = Math.Min(Height, height);
        Width = Math.Min(Width, width);
        _viewModel = new(sourceText, new EtmBrowserService(approveDownload: ConfirmChromiumDownload), new CatalogWriteService(databasePath), ChooseDuplicate);
        DataContext = _viewModel; Closing += OnClosing; _viewModel.Saved += OnSaved;
    }
    private System.Threading.Tasks.Task<bool> ConfirmChromiumDownload() => System.Threading.Tasks.Task.FromResult(
        MessageBox.Show(this, "Не найден Chromium, Chrome или Edge. Скачать Chromium один раз для работы ЭТМ?\n\nНужен интернет и свободное место. Повторно скачивать при каждом поиске не требуется.",
            "Компонент браузера", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
    private EtmDuplicateDecision? ChooseDuplicate(EtmProduct product, IReadOnlyList<CatalogEntry> existing)
    {
        var dialog = new EtmDuplicateWindow(product, existing) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Decision : null;
    }
    private async void OnSaved(object? sender, EventArgs e)
    {
        _closing = true;
        try { await _viewModel.DisposeAsync(); }
        catch (Exception) { }
        _allowClose = true;
        DialogResult = true;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _viewModel.Cancel();
        if (_viewModel.IsSaving || _closing) return;
        _closing = true;
        try { await _viewModel.DisposeAsync(); }
        catch (Exception) { }
        _allowClose = true; Close();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void CandidateLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EtmCandidate candidate }) return;
        _viewModel.Selected = candidate;
        await _viewModel.ParseAsync();
    }
}
