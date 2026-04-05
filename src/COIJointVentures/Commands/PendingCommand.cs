using System;

namespace COIJointVentures.Commands;

internal sealed class PendingCommand
{
    public PendingCommand(CommandEnvelope envelope, Action<CommandEnvelope> apply)
    {
        Envelope = envelope;
        Apply = apply;
    }

    public CommandEnvelope Envelope { get; }

    public Action<CommandEnvelope> Apply { get; }
}
