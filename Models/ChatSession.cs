using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SecureCryptoClient.Models;

public class ChatSession : INotifyPropertyChanged
{
    private string _lastMessage = "";
    private int _unreadCount = 0;
    private bool _hasUnread = false;

    public string PartnerName { get; set; } = "";

    // Первой буква имени друга для круглой аватарки в списке чатов
    public string Initials => string.IsNullOrEmpty(PartnerName) ? "?" : PartnerName[..1].ToUpper();


    public string LastMessage
    {
        get => _lastMessage;
        set { _lastMessage = value; OnPropertyChanged(); }
    }

    // Количество непрочитанных сообщений
    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            _unreadCount = value;
            OnPropertyChanged();
            HasUnread = _unreadCount > 0; // Флаг для видимости кружка в XAML
        }
    }

    // Виден ли кружок со счетчиком на экране
    public bool HasUnread
    {
        get => _hasUnread;
        set { _hasUnread = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
