using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using SubTerra.Models;
using SubTerra.Services;
using SubTerra.ViewModels;
using SubTerra.Views;
using Xunit;

namespace SubTerra.Tests;

public sealed class ThemeRenderingTests
{
    private static ResourceDictionary Theme() => new() { Source = new Uri("/SubTerra;component/Themes/Dark.xaml", UriKind.Relative) };

    [Fact]
    public Task ExpanderHeaderBodyAndDisabledControlsHaveReadableText() => ScanControlTests.OnSta(async () =>
    {
        var body = new StackPanel(); var label = new TextBlock { Text = "Чувствительность мыши" }; body.Children.Add(label);
        body.Children.Add(new TextBox { Text = "0,025" }); body.Children.Add(new CheckBox { Content = "Адаптивное качество", IsChecked = true });
        body.Children.Add(new CheckBox { Content = "Недоступная настройка", IsEnabled = false });
        body.Children.Add(new Button { Content = "Подтвердить класс", IsEnabled = false });
        var expander = new Expander { Header = "Измерения и метки", IsExpanded = true, Content = body };
        var root = new Border { Background = new SolidColorBrush(Color.FromRgb(11, 18, 25)), Child = expander, Padding = new Thickness(12) };
        root.Resources.MergedDictionaries.Add(Theme()); TextElement.SetForeground(root, Brushes.White);
        Layout(root, 328, 600); await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.True(label.ActualWidth > 0, "The expanded body must be laid out.");
        Assert.Contains(Descendants(expander), e => e is AccessText a && a.Text == "Измерения и метки" || e is TextBlock t && t.Text == "Измерения и метки");
        CheckReadableText(root);
        var toggle = (ToggleButton)expander.Template.FindName("HeaderToggle", expander);
        toggle.IsChecked = false; Assert.False(expander.IsExpanded);
        toggle.IsChecked = true; Assert.True(expander.IsExpanded);
    });

    [Fact]
    public Task RealWorkspacePanelsResolveBindingsAndRenderWithoutOpeningAWindow() => ScanControlTests.OnSta(async () =>
    {
        using var demo = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(), access: new TestAccess(false));
        await demo.LoadDemoAsync();
        using var project = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(), access: new TestAccess(), interaction: new TestInteraction());
        await project.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters, LocalOrigin: false));
        await project.AnalyzeStructureAsync();
        var sourcePanel = new ScanProjectPanel { DataContext = project };
        Layout(sourcePanel, 268, 900);
        foreach (var expander in Descendants(sourcePanel).OfType<Expander>().ToArray()) expander.IsExpanded = true;
        Layout(sourcePanel, 268, 900);
        var sourceInputs = Descendants(sourcePanel).OfType<TextBox>().ToArray(); Assert.NotEmpty(sourceInputs);
        foreach (var input in sourceInputs)
            Assert.True(input.Foreground is SolidColorBrush color && Contrast(color.Color, Background(input)) >= 4.5, "Source coordinate fields must use the dark theme too.");
        var root = new Grid { Background = new SolidColorBrush(Color.FromRgb(11, 18, 25)), Margin = new Thickness(0) };
        root.Resources.MergedDictionaries.Add(Theme()); TextElement.SetForeground(root, Brushes.White);
        root.RowDefinitions.Add(new() { Height = new GridLength(88) }); root.RowDefinitions.Add(new());
        var heading = new StackPanel { Margin = new Thickness(24, 18, 0, 0) };
        heading.Children.Add(new TextBlock { Text = "SUBTERRA / рабочие панели", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Настройки · инструменты карты · сканы и анализ", Foreground = new SolidColorBrush(Color.FromRgb(172, 191, 205)), Margin = new Thickness(0, 6, 0, 0) });
        root.Children.Add(heading);
        var columns = new Grid { Margin = new Thickness(16, 0, 16, 16) }; Grid.SetRow(columns, 1); root.Children.Add(columns);
        foreach (var width in new[] { 312, 336, 312 }) columns.ColumnDefinitions.Add(new() { Width = new GridLength(width) });
        UserControl[] panels = [new ViewSettingsPanel { DataContext = demo }, new SessionInspectorPanel { DataContext = demo }, new StructureAnalysisPanel { DataContext = project }];
        for (var i = 0; i < panels.Length; i++)
        {
            var card = new Border { Child = panels[i], Margin = new Thickness(6, 0, 6, 0), Padding = new Thickness(i == 1 ? 0 : 16), Style = (Style)root.FindResource("Card") };
            Grid.SetColumn(card, i); columns.Children.Add(card);
        }
        Layout(root, 992, 1120); await Dispatcher.Yield(DispatcherPriority.DataBind); root.UpdateLayout();
        var classPicker = Descendants(root).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Класс поверхности");
        Assert.Contains(Descendants(classPicker).OfType<TextBlock>(), text => text.Text == project.SelectedStructureClass!.Label);
        foreach (var element in Descendants(root))
        {
            var values = element.GetLocalValueEnumerator();
            while (values.MoveNext())
            {
                var binding = BindingOperations.GetBindingExpressionBase(element, values.Current.Property);
                if (binding is not null) Assert.False(binding.HasError, $"{element.GetType().Name}.{values.Current.Property.Name}");
            }
        }
        var folder = Environment.GetEnvironmentVariable("SUBTERRA_UI_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
            var bitmap = new RenderTargetBitmap(1488, 1680, 144, 144, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(folder, "workspace-panels.png")); encoder.Save(stream);
        }
        CheckReadableText(root);
        // Exercise the real shell XAML as a two-dimensional layout. No Window, Helix device or desktop automation.
        var shell = ShellPreview(demo); Layout(shell, 1440, 900);
        await Dispatcher.Yield(DispatcherPriority.DataBind); shell.UpdateLayout(); CheckReadableText(shell);
        var importedShell = ShellPreview(project); Layout(importedShell, 1440, 900);
        await Dispatcher.Yield(DispatcherPriority.DataBind); importedShell.UpdateLayout(); CheckReadableText(importedShell);
        var scenarioPicker = (ComboBox)importedShell.FindName("ScenarioPicker");
        Assert.Equal(project.SelectedScenario, scenarioPicker.SelectedItem);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var bitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(shell);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(folder, "workspace-layout.png")); encoder.Save(stream);
            var importedBitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32); importedBitmap.Render(importedShell);
            var importedEncoder = new PngBitmapEncoder(); importedEncoder.Frames.Add(BitmapFrame.Create(importedBitmap));
            using var importedStream = File.Create(Path.Combine(folder, "workspace-import-layout.png")); importedEncoder.Save(importedStream);
        }
    });

    [Fact]
    public Task AnalysisTabSurvivesReviewsRoomChangesAndHistory() => ScanControlTests.OnSta(async () =>
    {
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(),
            access: new TestAccess(), interaction: new TestInteraction());
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        Assert.Equal(1, vm.SidebarTabIndex);
        var shell = ShellPreview(vm); Layout(shell, 1440, 900);
        var tabs = (TabControl)shell.FindName("SidebarTabs");
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.Equal(2, tabs.SelectedIndex);
        await vm.AnalyzeStructureAsync();
        foreach (var surface in vm.StructureSurfaces.Where(s => s.Kind != StructureSurfaceKind.Unknown).ToArray())
        { vm.SelectedStructureSurface = surface; vm.ConfirmStructureSurfaceCommand.Execute(null); }
        await vm.BuildRoomAsync(); Assert.True(vm.HasRoom);
        vm.RemoveRoomCommand.Execute(null); Assert.False(vm.HasRoom);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.True(vm.HasRoom);
        await vm.RedoProjectCommand.ExecuteAsync(); Assert.False(vm.HasRoom);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.True(vm.HasRoom);
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.Equal(2, tabs.SelectedIndex); Assert.Equal(2, vm.SidebarTabIndex);
        tabs.SelectedIndex = 4;
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.Equal(4, vm.SidebarTabIndex);
        vm.ClearStructureCommand.Execute(null); await vm.UndoProjectCommand.ExecuteAsync();
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.Equal(4, tabs.SelectedIndex);
        await vm.LoadDemoAsync();
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.Equal(0, tabs.SelectedIndex); Assert.Equal(0, vm.SidebarTabIndex);
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.Equal(1, tabs.SelectedIndex);
        Assert.False(vm.HasNotification, vm.Notification);
    });

    private static FrameworkElement ShellPreview(MainViewModel model)
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ThemeExamples", "MainWindow.xaml"));
        var root = document.Root!; root.Name = p + "Border";
        var events = new HashSet<string> { "Loaded", "Closing", "Closed", "PreviewKeyDown", "StateChanged", "SizeChanged", "MouseLeftButtonDown", "Click", "SelectionChanged", "KeyDown", "SaveAsRequested", "ExportRequested" };
        foreach (var attribute in root.DescendantsAndSelf().Attributes().ToArray())
            if (attribute.Name == x + "Class" || events.Contains(attribute.Name.LocalName)) attribute.Remove();
        foreach (var attribute in root.Attributes().Where(a => !a.IsNamespaceDeclaration).ToArray())
            if (attribute.Name.LocalName is "Foreground" or "FontFamily")
            { root.SetAttributeValue("TextElement." + attribute.Name.LocalName, attribute.Value); attribute.Remove(); }
            else if (attribute.Name.LocalName is not ("Width" or "Height" or "MinWidth" or "MinHeight" or "Background" or "UseLayoutRounding" or "SnapsToDevicePixels")) attribute.Remove();
        root.SetAttributeValue(XNamespace.Xmlns + "views", "clr-namespace:SubTerra.Views;assembly=SubTerra");
        foreach (var element in root.Descendants().Where(e => e.Name.NamespaceName == "clr-namespace:SubTerra.Views"))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:SubTerra.Views;assembly=SubTerra");
        root.Elements().Where(e => e.Name.LocalName == "WindowChrome.WindowChrome").Remove();
        var resources = root.Element(p + "Window.Resources")!; resources.Name = p + "Border.Resources";
        var styles = resources.Elements().ToArray(); resources.RemoveNodes();
        resources.Add(new XElement(p + "ResourceDictionary",
            new XElement(p + "ResourceDictionary.MergedDictionaries", new XElement(p + "ResourceDictionary", new XAttribute("Source", "/SubTerra;component/Themes/Dark.xaml"))), styles));
        var viewport = root.Descendants().Single(e => e.Name.LocalName == "Viewport3DX");
        viewport.ReplaceWith(new XElement(p + "Grid", new XAttribute("Background", "#0B131B"),
            new XElement(p + "StackPanel", new XAttribute("VerticalAlignment", "Center"), new XAttribute("HorizontalAlignment", "Center"),
                new XElement(p + "TextBlock", new XAttribute("Text", "3D-сцена"), new XAttribute("FontSize", "26"), new XAttribute("Foreground", "#ACBFCD"), new XAttribute("HorizontalAlignment", "Center")),
                new XElement(p + "TextBlock", new XAttribute("Text", "Предпросмотр вёрстки · без GPU"), new XAttribute("Foreground", "#ACBFCD"), new XAttribute("Margin", "0,10,0,0")))));
        root.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "RightPanel").SetAttributeValue("Visibility", "Visible");
        root.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "RightColumn").SetAttributeValue("Width", "336");
        if (model.IsImported) model.SidebarTabIndex = 2;
        model.IsInspectorOpen = true;
        var control = (FrameworkElement)XamlReader.Parse(root.ToString()); control.DataContext = model;
        return control;
    }

    private static void Layout(FrameworkElement root, double width, double height)
    { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    internal static void CheckReadableText(DependencyObject root)
    {
        var inspected = 0;
        foreach (var element in Descendants(root).OfType<FrameworkElement>())
        {
            var text = element switch { TextBlock t => t.Text, AccessText a => a.Text, _ => null };
            if (string.IsNullOrWhiteSpace(text) || element.ActualWidth == 0 || element.ActualHeight == 0) continue;
            if (TextElement.GetForeground(element) is not SolidColorBrush foreground) continue;
            var opacity = foreground.Color.A / 255d; var enabled = true; var visible = true;
            for (DependencyObject? ancestor = element; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is UIElement ui) { opacity *= ui.Opacity; enabled &= ui.IsEnabled; visible &= ui.Visibility == Visibility.Visible; }
            if (!visible) continue;
            var background = Background(element);
            var effective = Color.FromRgb((byte)(foreground.Color.R * opacity + background.R * (1 - opacity)),
                (byte)(foreground.Color.G * opacity + background.G * (1 - opacity)), (byte)(foreground.Color.B * opacity + background.B * (1 - opacity)));
            var ratio = Contrast(effective, background);
            Assert.True(ratio >= (enabled ? 4.5 : 3), $"Unreadable '{text}': {ratio:F2}:1, text {foreground.Color}, background {background}, opacity {opacity:F2}");
            inspected++;
        }
        Assert.True(inspected >= 5, "The template's visible text must actually be measured.");
    }
    private static Color Background(DependencyObject element)
    {
        for (DependencyObject? ancestor = element; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            // A control's Background can be a system default that its custom template never draws.
            var brush = ancestor switch { Border b => b.Background, Panel p => p.Background, _ => null };
            if (brush is SolidColorBrush solid && solid.Color.A == 255) return solid.Color;
        }
        return Color.FromRgb(11, 18, 25);
    }
    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Linear(byte v) { var s = v / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        var x = L(a); var y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
