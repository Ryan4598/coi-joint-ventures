using System;
using System.Runtime.Serialization;

namespace COIJointVentures.Commands;

[DataContract]
public sealed class CommandEnvelope
{
    [DataMember(Order = 1)]
    public Guid CommandId { get; set; } = Guid.NewGuid();

    [DataMember(Order = 2)]
    public string CommandType { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string IssuerPlayerId { get; set; } = string.Empty;

    [DataMember(Order = 4)]
    public long Sequence { get; set; }

    [DataMember(Order = 5)]
    public long Tick { get; set; }

    [DataMember(Order = 6)]
    public string PayloadJson { get; set; } = string.Empty;

    [DataMember(Order = 7)]
    public string? Checksum { get; set; }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(CommandType) &&
        !string.IsNullOrWhiteSpace(IssuerPlayerId) &&
        !string.IsNullOrWhiteSpace(PayloadJson);
}
