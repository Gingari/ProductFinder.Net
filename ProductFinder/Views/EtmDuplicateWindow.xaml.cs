using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ProductFinder.Models;

namespace ProductFinder.Views;

public partial class EtmDuplicateWindow : Window
{
    public EtmDuplicateDecision? Decision { get; private set; }
    public EtmDuplicateWindow(EtmProduct product, IReadOnlyList<CatalogEntry> existing)
    {
        InitializeComponent(); DataContext = product;
        DescriptionText.Text = existing.Count == 1
            ? "Можно использовать имеющуюся запись без изменений или заменить её название и производителя данными ЭТМ."
            : $"Найдено записей: {existing.Count}. Выберите одну. Замена затронет только выбранную строку; остальные останутся без изменений.";
        ExistingGrid.ItemsSource = existing; ExistingGrid.SelectedIndex = 0;
    }
    private void ExistingGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (KeepButton is not null) KeepButton.IsEnabled = ReplaceButton.IsEnabled = ExistingGrid.SelectedItem is CatalogEntry; }
    private void Keep_Click(object sender, RoutedEventArgs e) => Finish(EtmDuplicateAction.Keep);
    private void Replace_Click(object sender, RoutedEventArgs e) => Finish(EtmDuplicateAction.Replace);
    private void Finish(EtmDuplicateAction action)
    {
        if (ExistingGrid.SelectedItem is not CatalogEntry entry) return;
        Decision = new(action, entry); DialogResult = true;
    }
}
