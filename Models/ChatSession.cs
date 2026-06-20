using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SecureCryptoClient.Models;

public class ChatSession : INotifyPropertyChanged
{
    private string _lastMessage = "";

    public string PartnerName { get; set; } = "";

    public string LastMessage
    {
        get => _lastMessage;
        set { _lastMessage = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
