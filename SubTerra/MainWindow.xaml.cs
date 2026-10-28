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
        _viewModel = new MainViewModel(new SimulatedScanSource(), new SessionStorage(), access: _access, interaction: _interaction);
        InitializeComponent();
        DataContext = _viewModel;
        _walk = new WalkController(this, Viewport, _viewModel);
        _diagnosticsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            Diagnostics_Tick, Dispatcher);
        _diagnosticsTimer.Stop();
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
        await _viewModel.LoadDemoAsync();
    }
    private void Diagnostics_Tick(object? sender, EventArgs e)
    {
        _process.Refresh();
        _viewModel.UpdateMemory(_process.PrivateMemorySize64);
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
        if (!_viewModel.CanSave) return;
        _viewModel.Pause();
        var path = await _interaction.ChooseSavePathAsync();
        if (path is not null) await _viewModel.SaveAsync(path);
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanSave) return;
        _viewModel.Pause();
        var dialog = new SaveFileDialog { Title = "Экспорт облака точек", Filter = "Облако точек PLY (*.ply)|*.ply", DefaultExt = ".ply", FileName = "Subterra_scan" };
        if (dialog.ShowDialog(this) == true) await _viewModel.ExportAsync(dialog.FileName);
    }
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.MouseCaptured && (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
            or PasswordBox or ComboBox or System.Windows.Controls.Primitives.RangeBase)) return;
        if (_walk.HandleKeyDown(e)) { e.Handled = true; return; }
        if (e.Key == Key.F11) { if (!e.IsRepeat) _viewModel.IsPresentationMode = !_viewModel.IsPresentationMode; e.Handled = true; }
        else if (e.Key == Key.Escape && _viewModel.IsPresentationMode) { _viewModel.IsPresentationMode = false; e.Handled = true; }
        else if (e.Key == Key.Home) { _viewModel.Home(); e.Handled = true; }
        else if (e.Key == Key.Space && _viewModel.IsAdministrator && _viewModel.CanInteract)
        {
            e.Handled = true;
            if (!e.IsRepeat) { if (_viewModel.IsRunning) _viewModel.Pause(); else await _viewModel.StartAsync(); }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { if (!e.IsRepeat) Save_Click(this, new()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { if (!e.IsRepeat) Open_Click(this, new()); e.Handled = true; }
    }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.WalkTargetProgress))
        {
            _walk.PlaceAt(_viewModel.WalkTargetProgress);
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.IsWalking))
        {
            try { if (_viewModel.IsWalking) _walk.Start(); else _walk.Stop(); }
            catch (Exception ex) { _viewModel.Home(); _viewModel.ReportError("Не удалось начать прогулку", ex); }
            return;
        }
        if (e.PropertyName is not (nameof(MainViewModel.IsPresentationMode) or nameof(MainViewModel.IsInspectorOpen))) return;
        var presentation = _viewModel.IsPresentationMode;
        var inspector = !presentation && _viewModel.IsInspectorOpen;
        LeftPanel.Visibility = presentation ? Visibility.Collapsed : Visibility.Visible;
        RightPanel.Visibility = inspector ? Visibility.Visible : Visibility.Collapsed;
        LeftColumn.Width = new GridLength(presentation ? 0 : 248);
        RightColumn.Width = new GridLength(inspector ? 264 : 0);
        if (e.PropertyName != nameof(MainViewModel.IsPresentationMode)) return;
        if (presentation) { _previousWindowState = WindowState; WindowState = WindowState.Maximized; }
        else WindowState = _previousWindowState;
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
        _process.Dispose();
        _walk.Dispose();
        _viewModel.Dispose();
        Viewport.Dispose();
        EffectsManager.Dispose();
    }
}
