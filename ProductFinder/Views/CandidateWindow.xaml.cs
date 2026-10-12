using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProductFinder.Models;

namespace ProductFinder.Views;

public partial class CandidateWindow : Window
{
    public SearchResult? SelectedResult { get; private set; }
    public CandidateWindow(string sourceText, IReadOnlyList<SearchResult> candidates)
    {
        InitializeComponent();
        SourceText.Text = "Строка из заявки: " + sourceText;
        CandidatesGrid.ItemsSource = candidates;
        if (candidates.Count > 0) CandidatesGrid.SelectedIndex = 0;
    }
    private void CandidatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConfirmButton is not null) ConfirmButton.IsEnabled = CandidatesGrid.SelectedItem is SearchResult;
    }
    private void Confirm_Click(object sender, RoutedEventArgs e) => ConfirmSelection();
    private void CandidatesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(CandidatesGrid, source) is not DataGridRow { Item: SearchResult }) return;
        e.Handled = true;
        ConfirmSelection();
    }
    private void ConfirmSelection()
    {
        if (CandidatesGrid.SelectedItem is not SearchResult result) return;
        SelectedResult = result;
        DialogResult = true;
    }
}
