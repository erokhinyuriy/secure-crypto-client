using System;

namespace SecureCryptoClient.Models;

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ChatPartner { get; set; } = ""; // С кем общаемся
    public string Sender { get; set; } = "";      // Кто конкретно автор (вы или он)
    public string Text { get; set; } = "";        // Текст сообщения
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
