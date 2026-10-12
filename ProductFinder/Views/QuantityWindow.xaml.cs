using System.Globalization;
using System.Windows;

namespace ProductFinder.Views;

public partial class QuantityWindow : Window
{
    public int Quantity { get; private set; }
    public QuantityWindow(string productName)
    {
        InitializeComponent();
        ProductNameText.Text = productName;
        Loaded += (_, _) => { QuantityTextBox.Focus(); QuantityTextBox.SelectAll(); };
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(QuantityTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) || quantity is < 1 or > 100000)
        {
            ValidationText.Text = "Введите целое количество от 1 до 100000.";
            ValidationText.Visibility = Visibility.Visible;
            QuantityTextBox.Focus();
            return;
        }
        Quantity = quantity;
        DialogResult = true;
    }
}
