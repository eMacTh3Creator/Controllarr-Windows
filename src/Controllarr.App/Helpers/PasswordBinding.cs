using System.Windows;
using System.Windows.Controls;

namespace Controllarr.App.Helpers;

public static class PasswordBinding
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password", typeof(string), typeof(PasswordBinding), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));
    private static readonly DependencyProperty UpdatingProperty = DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBinding), new PropertyMetadata(false));
    public static string GetPassword(DependencyObject value) => (string)value.GetValue(PasswordProperty);
    public static void SetPassword(DependencyObject value, string password) => value.SetValue(PasswordProperty, password);
    private static void OnPasswordChanged(DependencyObject value, DependencyPropertyChangedEventArgs e)
    {
        if (value is not PasswordBox box) return;
        box.PasswordChanged -= OnUserChange;
        if (!(bool)box.GetValue(UpdatingProperty)) box.Password = (string?)e.NewValue ?? "";
        box.PasswordChanged += OnUserChange;
    }
    private static void OnUserChange(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(UpdatingProperty, true);
        box.SetCurrentValue(PasswordProperty, box.Password);
        box.SetValue(UpdatingProperty, false);
        if (box.IsKeyboardFocusWithin && box.DataContext is ViewModels.MainViewModel vm)
            vm.MarkSettingsModifiedCommand.Execute(null);
    }
}
