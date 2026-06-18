namespace SecureCryptoClient.Models;

public class SignedPacket
{
    public string Sender { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Type { get; set; } = ""; // "PING" или "MESSAGE"
    public string PayloadCipherBase64 { get; set; } = "";
    public string Signature { get; set; } = "";
}
