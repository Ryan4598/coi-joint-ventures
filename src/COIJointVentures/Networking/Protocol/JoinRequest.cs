using System.Runtime.Serialization;

namespace COIJointVentures.Networking.Protocol;

[DataContract]
internal sealed class JoinRequest
{
    [DataMember(Order = 1)]
    public string PlayerName { get; set; } = string.Empty;

    [DataMember(Order = 2)]
    public string Password { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string ModVersion { get; set; } = string.Empty;
}
