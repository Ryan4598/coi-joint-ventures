using System;

namespace COIJointVentures.Networking;

internal sealed class TransportMessage
{
    public string SenderPeerId { get; set; } = string.Empty;

    public byte[] Payload { get; set; } = Array.Empty<byte>();
}
