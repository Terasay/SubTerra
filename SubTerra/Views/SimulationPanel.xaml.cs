using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SubTerra.Views;

public partial class SimulationPanel : UserControl
{
    public SimulationPanel() => InitializeComponent();
    private void NumericInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox input) return;
        input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
