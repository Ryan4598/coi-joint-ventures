namespace COIJointVentures.Session;

internal sealed class ServerConfig
{
    public string ServerName { get; set; } = "COI Server";

    public string Password { get; set; } = string.Empty;

    public int Port { get; set; } = 38455;

    public bool HasPassword => !string.IsNullOrEmpty(Password);
}
