using System.ComponentModel;
using System.Windows;
using SubTerra.Services;

namespace SubTerra.Views;

public partial class AdminLoginWindow : Window
{
    private readonly AdminAccessService _access;
    private readonly bool _setup;
    private bool _submitting;
    public AdminLoginWindow(AdminAccessService access)
    {
        _access = access;
        _setup = !access.IsConfigured;
        InitializeComponent();
        if (_setup)
        {
            Heading.Text = "Создать администратора";
            Explanation.Text = "Первый запуск: задайте пароль для локального администратора admin. От 10 до 128 символов. Этот пароль понадобится при следующих входах.";
            LoginBox.IsReadOnly = true;
            ConfirmationPanel.Visibility = Visibility.Visible;
            SubmitButton.Content = "Создать и войти";
        }
        Loaded += (_, _) => PasswordBox.Focus();
    }
    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_submitting) return;
        if (_setup && PasswordBox.Password != ConfirmationBox.Password)
        { ErrorText.Text = "Пароли не совпадают."; return; }
        _submitting = true;
        SubmitButton.IsEnabled = CancelButton.IsEnabled = false;
        ErrorText.Text = "Проверка учётной записи…";
        try
        {
            var result = _setup ? await _access.CreateAdministratorAsync(PasswordBox.Password) :
                await _access.LoginAsync(LoginBox.Text, PasswordBox.Password);
            _submitting = false;
            ErrorText.Text = result.Message;
            if (result.Success) DialogResult = true;
        }
        catch { ErrorText.Text = "Не удалось выполнить вход. Попробуйте ещё раз."; }
        finally
        {
            _submitting = false;
            PasswordBox.Clear(); ConfirmationBox.Clear();
            SubmitButton.IsEnabled = CancelButton.IsEnabled = true;
        }
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (_submitting) e.Cancel = true; }
}
