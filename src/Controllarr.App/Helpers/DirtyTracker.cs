using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Controllarr.App.Helpers
{
    /// <summary>
    /// Attached behavior that listens for bubbling input events (text changes,
    /// checkbox toggles, combo-box selections) on a container element and invokes
    /// a bound <see cref="ICommand"/> whenever any descendant is edited.
    ///
    /// Usage in XAML:
    ///   xmlns:h="clr-namespace:Controllarr.App.Helpers"
    ///   <StackPanel h:DirtyTracker.Command="{Binding MarkSettingsModifiedCommand}">
    /// </summary>
    public static class DirtyTracker
    {
        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.RegisterAttached(
                "Command",
                typeof(ICommand),
                typeof(DirtyTracker),
                new PropertyMetadata(null, OnCommandChanged));

        public static ICommand? GetCommand(DependencyObject obj) =>
            (ICommand?)obj.GetValue(CommandProperty);

        public static void SetCommand(DependencyObject obj, ICommand? value) =>
            obj.SetValue(CommandProperty, value);

        private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not UIElement element) return;

            // Unsubscribe old handlers
            element.RemoveHandler(TextBoxBase.TextChangedEvent, (RoutedEventHandler)OnInputEvent);
            element.RemoveHandler(ToggleButton.CheckedEvent, (RoutedEventHandler)OnInputEvent);
            element.RemoveHandler(ToggleButton.UncheckedEvent, (RoutedEventHandler)OnInputEvent);
            element.RemoveHandler(Selector.SelectionChangedEvent, (RoutedEventHandler)OnInputEvent);

            if (e.NewValue is ICommand)
            {
                // Subscribe to bubbling routed events from any descendant
                element.AddHandler(TextBoxBase.TextChangedEvent, (RoutedEventHandler)OnInputEvent);
                element.AddHandler(ToggleButton.CheckedEvent, (RoutedEventHandler)OnInputEvent);
                element.AddHandler(ToggleButton.UncheckedEvent, (RoutedEventHandler)OnInputEvent);
                element.AddHandler(Selector.SelectionChangedEvent, (RoutedEventHandler)OnInputEvent);
            }
        }

        private static void OnInputEvent(object sender, RoutedEventArgs e)
        {
            if (sender is DependencyObject d)
            {
                var command = GetCommand(d);
                if (command?.CanExecute(null) == true)
                    command.Execute(null);
            }
        }
    }
}
