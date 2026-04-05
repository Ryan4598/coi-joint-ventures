namespace COIJointVentures.Session;

internal enum ConnectionState
{
    Idle,
    Hosting,
    Connecting,
    WaitingForAccept,
    ReceivingSave,
    LoadingSave,
    Connected
}
