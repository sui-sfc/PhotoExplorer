using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PhotoExplorer.Presentation.ViewModels;

/// <summary>
/// 大量追加・全件置換時に CollectionChanged を 1 回だけ発行する ObservableCollection。
/// 数万件の結果を表示するときの UI フリーズを防ぐ。
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification) base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotification) base.OnPropertyChanged(e);
    }

    public void AddRange(IEnumerable<T> items)
    {
        _suppressNotification = true;
        bool added = false;
        try
        {
            foreach (var item in items)
            {
                Items.Add(item);
                added = true;
            }
        }
        finally
        {
            _suppressNotification = false;
        }
        if (added) RaiseReset();
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppressNotification = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
        }
        finally
        {
            _suppressNotification = false;
        }
        RaiseReset();
    }

    private void RaiseReset()
    {
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        base.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
