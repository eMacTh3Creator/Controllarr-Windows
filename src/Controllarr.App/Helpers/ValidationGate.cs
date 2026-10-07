using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace Controllarr.App.Helpers;

/// <summary>Do not silently save the old model value when a numeric text box contains invalid input.</summary>
public static class ValidationGate
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ValidationGate), new PropertyMetadata(false, OnEnabled));
    public static readonly DependencyProperty IsValidProperty = DependencyProperty.RegisterAttached("IsValid", typeof(bool), typeof(ValidationGate), new PropertyMetadata(true));
    private static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached("Pending", typeof(bool), typeof(ValidationGate), new PropertyMetadata(false));
    public static void SetEnabled(DependencyObject root, bool value) => root.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject root) => (bool)root.GetValue(EnabledProperty);
    public static bool GetIsValid(DependencyObject root) => (bool)root.GetValue(IsValidProperty);
    public static void SetIsValid(DependencyObject root, bool value) => root.SetValue(IsValidProperty, value);
    private static void OnEnabled(DependencyObject value, DependencyPropertyChangedEventArgs e)
    {
        if (value is not FrameworkElement root) return;
        root.RemoveHandler(TextBoxBase.TextChangedEvent, new RoutedEventHandler(OnInput));
        root.RemoveHandler(Selector.SelectionChangedEvent, new RoutedEventHandler(OnInput));
        root.Loaded -= OnInput;
        if ((bool)e.NewValue)
        {
            root.AddHandler(TextBoxBase.TextChangedEvent, new RoutedEventHandler(OnInput), true);
            root.AddHandler(Selector.SelectionChangedEvent, new RoutedEventHandler(OnInput), true);
            root.Loaded += OnInput;
        }
    }
    private static void OnInput(object sender, RoutedEventArgs e)
    {
        var root = (FrameworkElement)sender;
        if ((bool)root.GetValue(PendingProperty)) return;
        root.SetValue(PendingProperty, true);
        root.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            root.SetValue(PendingProperty, false);
            SetIsValid(root, !HasErrors(root));
        }));
    }
    private static bool HasErrors(DependencyObject node)
    {
        if (Validation.GetHasError(node)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (HasErrors(VisualTreeHelper.GetChild(node, i))) return true;
        return false;
    }
}
