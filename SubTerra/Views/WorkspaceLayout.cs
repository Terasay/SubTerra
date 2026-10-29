using System.Windows;
using System.Windows.Controls;
using SubTerra.ViewModels;

namespace SubTerra.Views;

/// <summary>Shared shell layout, also exercised by the windowless rendering tests.</summary>
public static class WorkspaceLayout
{
    public static void Apply(FrameworkElement root, MainViewModel model, double width)
    {
        if (root.FindName("LeftPanel") is not Border left || root.FindName("RightPanel") is not Border right) return;
        var sidebar = !model.IsPresentationMode && model.IsSidebarOpen;
        var inspector = !model.IsPresentationMode && model.IsInspectorOpen;
        var floatingLeft = width < 1040;
        var floatingRight = width < 1040;
        left.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        right.Visibility = inspector ? Visibility.Visible : Visibility.Collapsed;
        ((ColumnDefinition)root.FindName("LeftColumn")).Width = new(sidebar && !floatingLeft ? 284 : 0);
        ((ColumnDefinition)root.FindName("RightColumn")).Width = new(inspector && !floatingRight ? 312 : 0);
        Grid.SetColumn(left, floatingLeft ? 1 : 0); Grid.SetColumn(right, floatingRight ? 1 : 2);
        left.Width = floatingLeft ? 284 : double.NaN; right.Width = floatingRight ? 312 : double.NaN;
        left.HorizontalAlignment = floatingLeft ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        right.HorizontalAlignment = floatingRight ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        ((ColumnDefinition)root.FindName("Scene3DColumn")).Width = model.IsPlanView ? new(0) : new(1, GridUnitType.Star);
        ((ColumnDefinition)root.FindName("PlanColumn")).Width = model.Is3DView ? new(0) : new(1, GridUnitType.Star);
        ((ColumnDefinition)root.FindName("ViewDividerColumn")).Width = new(model.IsSplitView ? 6 : 0);
    }
}
