using System;
using System.Collections;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace AstroHybrid.Linux.Views;

/// <summary>
/// A ComboBox for upstream's page view models. Their label lists are built anew on every read, and they
/// refresh all properties after each change. A plain Avalonia ComboBox drops its selection when it gets a
/// new list and writes -1 back to the setting (WPF does not). This one keeps its list when the new one has
/// the same entries, and only passes selections the user made (or valid ones) back to the view model.
/// Bind <see cref="Items"/> instead of ItemsSource and <see cref="Index"/> (or <see cref="Value"/>) instead of
/// SelectedIndex (SelectedItem).
/// </summary>
public class StableComboBox : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    public static readonly StyledProperty<IEnumerable> ItemsProperty =
        AvaloniaProperty.Register<StableComboBox, IEnumerable>(nameof(Items));
    public static readonly StyledProperty<int> IndexProperty =
        AvaloniaProperty.Register<StableComboBox, int>(nameof(Index), -1, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<object> ValueProperty =
        AvaloniaProperty.Register<StableComboBox, object>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public IEnumerable Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public int Index { get => GetValue(IndexProperty); set => SetValue(IndexProperty, value); }
    public object Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private bool _syncing;
    private bool _usesValue;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            var items = change.GetNewValue<IEnumerable>();
            if (ItemsSource != null && items != null && ItemsSource.Cast<object>().SequenceEqual(items.Cast<object>())) return;
            Sync(() => ItemsSource = items);
            ApplyFromViewModel();
        }
        else if (change.Property == IndexProperty) { ApplyFromViewModel(); }
        else if (change.Property == ValueProperty) { _usesValue = true; ApplyFromViewModel(); }
        else if (change.Property == SelectedIndexProperty && !_syncing && SelectedIndex >= 0)
        {
            Sync(() =>
            {
                if (_usesValue) Value = SelectedItem;
                else Index = SelectedIndex;
            });
        }
    }

    private void ApplyFromViewModel() => Sync(() =>
    {
        if (_usesValue) SelectedItem = Value;
        else SelectedIndex = Index;
    });

    private void Sync(Action action)
    {
        var was = _syncing;
        _syncing = true;
        try { action(); } finally { _syncing = was; }
    }
}
