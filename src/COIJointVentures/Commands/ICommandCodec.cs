namespace COIJointVentures.Commands;

internal interface ICommandCodec
{
    byte[] Encode(CommandEnvelope envelope);

    CommandEnvelope Decode(byte[] payload);
}
