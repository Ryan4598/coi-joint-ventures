using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace COIJointVentures.Commands;

internal sealed class JsonCommandCodec : ICommandCodec
{
    public byte[] Encode(CommandEnvelope envelope)
    {
        using (var stream = new MemoryStream())
        {
            var serializer = new DataContractJsonSerializer(typeof(CommandEnvelope));
            serializer.WriteObject(stream, envelope);
            return stream.ToArray();
        }
    }

    public CommandEnvelope Decode(byte[] payload)
    {
        using (var stream = new MemoryStream(payload))
        {
            var serializer = new DataContractJsonSerializer(typeof(CommandEnvelope));
            var envelope = serializer.ReadObject(stream) as CommandEnvelope;
            return envelope ?? throw new InvalidOperationException("Command payload could not be deserialized.");
        }
    }
}
