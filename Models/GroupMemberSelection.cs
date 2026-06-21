using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SecureCryptoClient.Models;

public class GroupMemberSelection : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Username { get; set; } = "";
    public string Initials => string.IsNullOrEmpty(Username) ? "?" : Username[..1].ToUpper();

    // Свойство для привязки к синей галочке
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
