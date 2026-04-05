namespace COIJointVentures.Integration;

internal sealed class NativeCommandInfo
{
    public string CommandType { get; set; } = string.Empty;

    public bool AffectsSaveState { get; set; }

    public bool IsVerificationCmd { get; set; }

    public bool IsProcessed { get; set; }

    public string Summary { get; set; } = string.Empty;
}

