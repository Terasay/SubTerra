using System.Windows;
using System.Windows.Controls;
using SubTerra.ViewModels;

namespace SubTerra.Views;

/// <summary>Shared shell layout, also exercised by the windowless rendering tests.</summary>
public static class WorkspaceLayout
{
    private const double SidebarWidth = 304;
    private const double InspectorWidth = 312;
    private const double CanvasMargins = 26;
    private const double SplitterWidth = 8;
    private const double MinimumSplitPane = 320;

    public static void Apply(FrameworkElement root, MainViewModel model, double width)
    {
        if (root.FindName("LeftPanel") is not Border left || root.FindName("RightPanel") is not Border right) return;
        var sidebar = !model.IsPresentationMode && model.IsSidebarOpen;
        var inspector = !model.IsPresentationMode && model.IsInspectorOpen;
        var leftWidth = sidebar ? SidebarWidth : 0;
        var minimumCanvas = model.IsSplitView ? 2 * MinimumSplitPane + SplitterWidth : 440;
        var floatingRight = inspector && width - leftWidth - InspectorWidth - CanvasMargins < minimumCanvas;
        left.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        right.Visibility = inspector ? Visibility.Visible : Visibility.Collapsed;
        ((ColumnDefinition)root.FindName("LeftColumn")).Width = new(leftWidth);
        ((ColumnDefinition)root.FindName("RightColumn")).Width = new(inspector && !floatingRight ? InspectorWidth : 0);
        Grid.SetColumn(right, floatingRight ? 1 : 2);
        right.Width = floatingRight ? InspectorWidth : double.NaN;
        right.Margin = new Thickness(0);
        right.HorizontalAlignment = floatingRight ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        var scene = (ColumnDefinition)root.FindName("Scene3DColumn");
        var plan = (ColumnDefinition)root.FindName("PlanColumn");
        scene.MinWidth = plan.MinWidth = model.IsSplitView ? MinimumSplitPane : 0;
        if (!model.IsSplitView || !scene.Width.IsStar || !plan.Width.IsStar)
        {
            scene.Width = model.IsPlanView ? new(0) : new(1, GridUnitType.Star);
            plan.Width = model.Is3DView ? new(0) : new(1, GridUnitType.Star);
        }
        ((ColumnDefinition)root.FindName("ViewDividerColumn")).Width = new(model.IsSplitView ? SplitterWidth : 0);
    }
}
