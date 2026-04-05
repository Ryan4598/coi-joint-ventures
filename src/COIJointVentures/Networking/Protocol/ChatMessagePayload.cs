using System.Runtime.Serialization;

namespace COIJointVentures.Networking.Protocol;

[DataContract]
internal sealed class ChatMessagePayload
{
    [DataMember(Order = 1)]
    public string SenderName { get; set; } = string.Empty;

    [DataMember(Order = 2)]
    public string SenderPeerId { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public int Kind { get; set; }

    [DataMember(Order = 4)]
    public string Text { get; set; } = string.Empty;
}
