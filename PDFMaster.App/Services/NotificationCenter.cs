using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PDFMaster.App.Services;

public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed class NotificationAction
{
    public NotificationAction(string label, Action execute)
    {
        Label = label;
        Command = new RelayCommand(execute);
    }

    public string Label { get; }
    public ICommand Command { get; }
}

public sealed class NotificationItem
{
    public NotificationItem(NotificationKind kind, string title, string message, IReadOnlyList<NotificationAction> actions)
    {
        Kind = kind;
        Title = title;
        Message = message;
        Actions = actions;
        Time = DateTime.Now;
    }

    public NotificationKind Kind { get; }
    public string Title { get; }
    public string Message { get; }
    public IReadOnlyList<NotificationAction> Actions { get; }
    public DateTime Time { get; }
    public string TimeText => Time.ToString("HH:mm");

    public string Glyph => Kind switch
    {
        NotificationKind.Success => "",
        NotificationKind.Warning => "",
        NotificationKind.Error => "",
        _ => "",
    };
}

/// <summary>Notificações internas do programa (nada é enviado ao sistema ou à rede).</summary>
public sealed partial class NotificationCenter : ObservableObject
{
    private readonly DispatcherTimer _toastTimer;

    public NotificationCenter()
    {
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = null;
        };
    }

    public ObservableCollection<NotificationItem> Items { get; } = [];

    [ObservableProperty]
    private NotificationItem? _toast;

    [ObservableProperty]
    private int _unreadCount;

    [ObservableProperty]
    private bool _isPanelOpen;

    public bool HasItems => Items.Count > 0;

    partial void OnIsPanelOpenChanged(bool value)
    {
        if (!value) return;
        UnreadCount = 0;
        Toast = null;
    }

    public void Show(NotificationKind kind, string title, string message, params NotificationAction[] actions)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Show(kind, title, message, actions));
            return;
        }

        var item = new NotificationItem(kind, title, message, actions);
        Items.Insert(0, item);
        while (Items.Count > 100) Items.RemoveAt(Items.Count - 1);
        OnPropertyChanged(nameof(HasItems));

        if (!IsPanelOpen)
        {
            UnreadCount++;
            Toast = item;
            _toastTimer.Stop();
            _toastTimer.Start();
        }
    }

    [RelayCommand]
    private void DismissToast()
    {
        _toastTimer.Stop();
        Toast = null;
    }

    [RelayCommand]
    private void Clear()
    {
        Items.Clear();
        UnreadCount = 0;
        Toast = null;
        OnPropertyChanged(nameof(HasItems));
    }
}
