namespace COIJointVentures.Commands;

internal sealed class PendingCommand
{
    public PendingCommand(CommandEnvelope envelope)
    {
        Envelope = envelope;
    }

    public CommandEnvelope Envelope { get; }
}
