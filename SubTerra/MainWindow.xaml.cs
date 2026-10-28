using System.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Utilities;
using Microsoft.Win32;
using SubTerra.Services;
using SubTerra.Navigation;
using HelixToolkit.Wpf.SharpDX;
using SubTerra.Simulation;
using SubTerra.ViewModels;
using SubTerra.Views;
using SubTerra.Rendering;

namespace SubTerra;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AdminAccessService _access;
    private readonly WpfSessionInteraction _interaction;
    private bool _changingScenario;
    private bool _closeAllowed;
    private bool _checkingClose;
    private readonly WalkController _walk;
    private readonly DispatcherTimer _diagnosticsTimer;
    private readonly DispatcherTimer _interactionTimer;
    private readonly DispatcherTimer _autosaveTimer;
    private readonly FrameTiming _frameTiming = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private bool _ready;
    private WindowState _previousWindowState;
    public IEffectsManager EffectsManager { get; }

    public MainWindow() : this(new AdminAccessService()) { }

    public MainWindow(AdminAccessService access)
    {
        _access = access;
        EffectsManager = new DefaultEffectsManager();
        _interaction = new WpfSessionInteraction(() => this);
        _viewModel = new MainViewModel(new SimulatedScanSource(), new SessionStorage(), access: _access, interaction: _interaction,
            workspaceStore: new WorkspaceStateStore());
        InitializeComponent();
        DataContext = _viewModel;
        _walk = new WalkController(this, Viewport, _viewModel);
        _diagnosticsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            Diagnostics_Tick, Dispatcher);
        _diagnosticsTimer.Stop();
        _interactionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => _viewModel.SettleCameraInteraction(), Dispatcher);
        _autosaveTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background,
            async (_, _) => await _viewModel.AutoSaveAsync(), Dispatcher);
        _interactionTimer.Stop(); _autosaveTimer.Stop();
        Viewport.OnRendered += Viewport_OnRendered;
        Viewport.PreviewMouseLeftButtonDown += InspectPoint_Click;
        _viewModel.Camera.Changed += Camera_Changed;
        Viewport.InputBindings.Clear();
        Viewport.InputBindings.Add(new MouseBinding(ViewportCommands.Rotate, new MouseGesture(MouseAction.LeftClick)));
        Viewport.InputBindings.Add(new MouseBinding(ViewportCommands.Pan, new MouseGesture(MouseAction.RightClick)));
        Viewport.InputBindings.Add(new MouseBinding(ViewportCommands.Pan, new MouseGesture(MouseAction.LeftClick, ModifierKeys.Shift)));
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_ready) return;
        _ready = true;
        _diagnosticsTimer.Start();
        _interactionTimer.Start(); _autosaveTimer.Start();
        await _viewModel.LoadDemoAsync();
    }
    private void Diagnostics_Tick(object? sender, EventArgs e)
    {
        _process.Refresh();
        _viewModel.UpdateMemory(_process.PrivateMemorySize64);
        _viewModel.UpdateFrameTiming(_frameTiming.Snapshot(Stopwatch.GetTimestamp()));
    }
    private void Viewport_OnRendered(object? sender, EventArgs e) => _frameTiming.Frame(Stopwatch.GetTimestamp());
    private void Camera_Changed(object? sender, EventArgs e) => _viewModel.NotifyCameraInteraction();
    private void InspectPoint_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsPicking) return;
        var hit = Viewport.FindHits(e.GetPosition(Viewport)).FirstOrDefault(result => result.IsValid);
        if (hit is not null) _viewModel.SelectInspectionPoint(hit.PointHit);
        e.Handled = true;
    }
    private async void Start_Click(object sender, RoutedEventArgs e) => await _viewModel.StartAsync();
    private async void Scenario_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _changingScenario || _viewModel.IsBusy || ScenarioPicker.SelectedItem is not Models.ScenarioDefinition scenario ||
            scenario == _viewModel.SelectedScenario) return;
        _changingScenario = true;
        try { await _viewModel.LoadDemoAsync(scenario); }
        finally
        {
            ScenarioPicker.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, _viewModel.SelectedScenario);
            _changingScenario = false;
        }
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanInteract) return;
        var dialog = new OpenFileDialog { Title = "Открыть сеанс Subterra", Filter = "Сеанс Subterra (*.subterra)|*.subterra", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await _viewModel.OpenAsync(dialog.FileName);
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveCurrentAsync();
    }
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await _viewModel.SaveCurrentAsync(saveAs: true);
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanSave) return;
        _viewModel.Pause();
        var dialog = new SaveFileDialog { Title = "Экспорт облака точек", Filter = "Облако точек PLY (*.ply)|*.ply", DefaultExt = ".ply", FileName = "Subterra_scan" };
        if (dialog.ShowDialog(this) == true) await _viewModel.ExportAsync(dialog.FileName);
    }
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel.CanCancelOperation)
        {
            if (!e.IsRepeat) _viewModel.CancelOperation();
            e.Handled = true; return;
        }
        if (e.Key == Key.Escape && _viewModel.IsPicking)
        {
            _viewModel.EndInspection(); e.Handled = true; return;
        }
        // These shortcuts remain global, including while a slider or numeric field has focus.
        if (e.Key == Key.F11)
        {
            if (!e.IsRepeat) _viewModel.IsPresentationMode = !_viewModel.IsPresentationMode;
            e.Handled = true; return;
        }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
        {
            if (!e.IsRepeat) SaveAs_Click(this, new());
            e.Handled = true; return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.S or Key.O)
        {
            if (!e.IsRepeat) { if (e.Key == Key.S) Save_Click(this, new()); else Open_Click(this, new()); }
            e.Handled = true; return;
        }
        if (e.Key == Key.Escape && _viewModel.IsPresentationMode && !_viewModel.IsNavigating)
        {
            if (!e.IsRepeat) _viewModel.IsPresentationMode = false;
            e.Handled = true; return;
        }
        if (e.Key == Key.Escape && _viewModel.IsNavigating && Keyboard.FocusedElement is not ComboBox { IsDropDownOpen: true })
        {
            if (!e.IsRepeat) _viewModel.ReturnToOverview();
            e.Handled = true; return;
        }
        if (!_viewModel.MouseCaptured && (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
            or PasswordBox or ComboBox or System.Windows.Controls.Primitives.RangeBase)) return;
        if (_walk.HandleKeyDown(e)) { e.Handled = true; return; }
        if (e.Key == Key.Home) { if (!e.IsRepeat) _viewModel.Home(); e.Handled = true; }
        else if (e.Key == Key.Space && _viewModel.IsAdministrator && _viewModel.CanInteract)
        {
            e.Handled = true;
            if (!e.IsRepeat) { if (_viewModel.IsRunning) _viewModel.Pause(); else await _viewModel.StartAsync(); }
        }
    }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.WalkTargetProgress))
        {
            _walk.PlaceAt(_viewModel.WalkTargetProgress);
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.IsNavigating))
        {
            try { if (_viewModel.IsNavigating) _walk.Start(); else _walk.Stop(); }
            catch (Exception ex) { _viewModel.Home(); _viewModel.ReportError("Не удалось включить навигацию", ex); }
            return;
        }
        if (e.PropertyName is not (nameof(MainViewModel.IsPresentationMode) or nameof(MainViewModel.IsInspectorOpen) or nameof(MainViewModel.IsSidebarOpen))) return;
        if (ActualWidth < 1200)
        {
            if (e.PropertyName == nameof(MainViewModel.IsInspectorOpen) && _viewModel.IsInspectorOpen) _viewModel.IsSidebarOpen = false;
            if (e.PropertyName == nameof(MainViewModel.IsSidebarOpen) && _viewModel.IsSidebarOpen) _viewModel.IsInspectorOpen = false;
        }
        UpdatePanelLayout();
        if (e.PropertyName != nameof(MainViewModel.IsPresentationMode)) return;
        if (_viewModel.IsPresentationMode) { _previousWindowState = WindowState; WindowState = WindowState.Maximized; }
        else WindowState = _previousWindowState;
    }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePanelLayout();
    private void UpdatePanelLayout()
    {
        if (LeftPanel is null || RightPanel is null) return;
        var presentation = _viewModel.IsPresentationMode;
        var inspector = !presentation && _viewModel.IsInspectorOpen;
        var sidebar = !presentation && _viewModel.IsSidebarOpen;
        var floatingLeft = ActualWidth < 1200;
        var floatingRight = ActualWidth < 1440;
        LeftPanel.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        RightPanel.Visibility = inspector ? Visibility.Visible : Visibility.Collapsed;
        LeftColumn.Width = new GridLength(sidebar && !floatingLeft ? 248 : 0);
        RightColumn.Width = new GridLength(inspector && !floatingRight ? 288 : 0);
        Grid.SetColumn(LeftPanel, floatingLeft ? 1 : 0);
        Grid.SetColumn(RightPanel, floatingRight ? 1 : 2);
        LeftPanel.Width = floatingLeft ? 232 : double.NaN;
        RightPanel.Width = floatingRight ? 280 : double.NaN;
        LeftPanel.HorizontalAlignment = floatingLeft ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        RightPanel.HorizontalAlignment = floatingRight ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
    }
    private void CloseInspector_Click(object sender, RoutedEventArgs e) => _viewModel.IsInspectorOpen = false;
    private void CloseSidebar_Click(object sender, RoutedEventArgs e) => _viewModel.IsSidebarOpen = false;
    private void NumericInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox input) return;
        input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
    private void Viewport_RenderExceptionOccurred(object? sender, RelayExceptionEventArgs e)
    {
        _viewModel.ReportError("Не удалось отобразить 3D-сцену", e.Exception);
        e.Handled = true;
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Maximize_Click(sender, e); else DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Access_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanInteract) return;
        _viewModel.Pause();
        if (_viewModel.IsAdministrator) await _viewModel.LogoutAsync();
        else new AdminLoginWindow(_access) { Owner = this }.ShowDialog();
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeAllowed) return;
        e.Cancel = true;
        if (_checkingClose) return;
        _checkingClose = true;
        try
        {
            if (await _viewModel.RequestCloseAsync())
            {
                _closeAllowed = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
        finally { _checkingClose = false; }
    }
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (ShellFrame is not null)
            ShellFrame.Padding = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }
    private void Window_Closed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _diagnosticsTimer.Stop();
        _interactionTimer.Stop(); _autosaveTimer.Stop();
        Viewport.OnRendered -= Viewport_OnRendered;
        Viewport.PreviewMouseLeftButtonDown -= InspectPoint_Click;
        _viewModel.Camera.Changed -= Camera_Changed;
        _process.Dispose();
        _walk.Dispose();
        _viewModel.Dispose();
        Viewport.Dispose();
        EffectsManager.Dispose();
    }
}
