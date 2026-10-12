using System;
using System.Windows;
using ProductFinder.Models;
using ProductFinder.ViewModels;

namespace ProductFinder.Views;

public partial class SmartInsertWindow : Window
{
    private readonly SmartInsertViewModel _viewModel = new();
    public SmartInsertRequest? Request { get; private set; }
    public SmartInsertWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += (_, _) => RequestTextBox.Focus();
    }
    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.ContainsText()) _viewModel.Text = Clipboard.GetText(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Буфер обмена", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanAccept) return;
        Request = _viewModel.CreateRequest();
        DialogResult = true;
    }
}
