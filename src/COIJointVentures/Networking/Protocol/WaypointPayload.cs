using System.Runtime.Serialization;

namespace COIJointVentures.Networking.Protocol;

[DataContract]
internal sealed class WaypointPayload
{
    [DataMember(Order = 1)]
    public string SenderPeerId { get; set; } = string.Empty;

    [DataMember(Order = 2)]
    public string SenderName { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public float X { get; set; }

    [DataMember(Order = 4)]
    public float Y { get; set; }

    [DataMember(Order = 5)]
    public float Z { get; set; }

    [DataMember(Order = 6)]
    public int ColorIndex { get; set; }
}
