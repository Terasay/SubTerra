using System.Windows.Controls;

namespace SubTerra.Views;

public partial class PersonTrainingPanel : UserControl
{
    public PersonTrainingPanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) CloseTrainingButton.Focus(); };
    }
}
