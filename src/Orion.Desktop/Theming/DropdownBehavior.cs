using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Orion.Desktop.Theming;

/// <summary>Keep overlay selectors scoped to the visible page and contain focus-driven scrolling.</summary>
public sealed class DropdownBehavior : AvaloniaObject
{
    public static readonly AttachedProperty<bool> EnabledProperty = AvaloniaProperty.RegisterAttached<DropdownBehavior, ComboBox, bool>("Enabled");
    public static bool GetEnabled(ComboBox combo) => combo.GetValue(EnabledProperty);
    public static void SetEnabled(ComboBox combo, bool value) => combo.SetValue(EnabledProperty, value);

    static DropdownBehavior()
    {
        EnabledProperty.Changed.AddClassHandler<ComboBox>((combo, args) =>
        {
            if (args.NewValue is not true) return;
            ScrollViewer[] scrolls = [];
            Control[] ancestors = [];
            Window? window = null;
            void Close(object? sender, EventArgs e) => combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
            void PageScrolled(object? sender, ScrollChangedEventArgs e)
            {
                // Scrolling a long dropdown must not count as scrolling the page beneath it.
                if (ReferenceEquals(e.Source, sender) && e.OffsetDelta != default) Close(sender, e);
            }
            void VisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property == Visual.IsVisibleProperty && (sender is Control { IsVisible: false } || !combo.IsEffectivelyVisible)) Close(null, EventArgs.Empty);
            }
            void Unsubscribe()
            {
                foreach (var scroll in scrolls) scroll.ScrollChanged -= PageScrolled;
                foreach (var ancestor in ancestors) ancestor.PropertyChanged -= VisibilityChanged;
                if (window is not null) window.Deactivated -= Close;
                scrolls = []; ancestors = []; window = null;
            }
            combo.DropDownOpened += (_, _) =>
            {
                Unsubscribe();
                scrolls = combo.GetVisualAncestors().OfType<ScrollViewer>().ToArray();
                ancestors = combo.GetVisualAncestors().OfType<Control>().ToArray();
                foreach (var ancestor in ancestors) ancestor.PropertyChanged += VisibilityChanged;
                foreach (var scroll in scrolls) scroll.ScrollChanged += PageScrolled;
                window = TopLevel.GetTopLevel(combo) as Window;
                if (window is not null) window.Deactivated += Close;
            };
            combo.DropDownClosed += (_, _) => Unsubscribe();
            combo.DetachedFromVisualTree += (_, _) => { Close(null, EventArgs.Empty); Unsubscribe(); };
            combo.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty && !combo.IsEffectivelyVisible) Close(null, EventArgs.Empty);
            };
            combo.TemplateApplied += (_, e) =>
            {
                if (e.NameScope.Find<Popup>("PART_Popup")?.Child is { } child)
                    child.AddHandler(Control.RequestBringIntoViewEvent, (_, request) => request.Handled = true);
            };
        });
    }
}
