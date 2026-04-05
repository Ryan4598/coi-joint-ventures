using System.Runtime.Serialization;

namespace COIJointVentures.Networking.Protocol;

[DataContract]
internal sealed class JoinResponse
{
    [DataMember(Order = 1)]
    public bool Accepted { get; set; }

    [DataMember(Order = 2)]
    public string ServerName { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string Reason { get; set; } = string.Empty;

    [DataMember(Order = 4)]
    public string AssignedPeerId { get; set; } = string.Empty;
}
