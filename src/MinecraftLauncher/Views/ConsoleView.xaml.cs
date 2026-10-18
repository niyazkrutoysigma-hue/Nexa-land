using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using MinecraftLauncher.Common;
using MinecraftLauncher.ViewModels;

namespace MinecraftLauncher.Views;

public partial class ConsoleView : UserControl
{
    public ConsoleView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
    }

    private INotifyCollectionChanged? _observed;

    private void Attach()
    {
        if (DataContext is not ConsoleViewModel vm) return;
        if (ReferenceEquals(_observed, vm.Entries as INotifyCollectionChanged)) return;

        if (_observed is not null) _observed.CollectionChanged -= OnLogAdded;
        _observed = vm.Entries as INotifyCollectionChanged;
        if (_observed is not null) _observed.CollectionChanged += OnLogAdded;
    }

    private void OnLogAdded(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (DataContext is not ConsoleViewModel { AutoScroll: true }) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        }));
    }
}