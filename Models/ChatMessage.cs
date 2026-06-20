using System;

namespace SecureCryptoClient.Models;

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ChatPartner { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // --- НОВЫЕ СВОЙСТВА ДЛЯ СТИЛИЗАЦИИ ТЕЛЕГРАМА ---

    // Свойство для привязки к HorizontalAlignment в XAML. 
    // Если имя отправителя совпадает с именем текущего профиля (Вы) — бабл улетает вправо, иначе — влево.
    public string Alignment => IsMe ? "Right" : "Left";

    // Фирменный цвет Telegram: зеленый для ваших сообщений (#2B5278 или #3D6A97), темный для чужих
    public string BubbleColor => IsMe ? "#2B5278" : "#182533";

    // Цвет рамки вокруг сообщения
    public string BorderColor => IsMe ? "#3D6A97" : "#212F3D";

    // Свойство-индикатор автора (задается во ViewModel при загрузке/получении)
    public bool IsMe { get; set; } = false;

    // Первая буква имени для круглой аватарки
    public string Initials => string.IsNullOrEmpty(Sender) ? "?" : Sender[..1].ToUpper();
}
