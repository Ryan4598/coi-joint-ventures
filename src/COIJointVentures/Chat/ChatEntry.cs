using System;

namespace COIJointVentures.Chat;

internal readonly struct ChatEntry
{
    public ChatEntry(string senderName, string text, ChatEntryKind kind)
    {
        Timestamp = DateTime.Now;
        SenderName = senderName;
        Text = text;
        Kind = kind;
    }

    public DateTime Timestamp { get; }
    public string SenderName { get; }
    public string Text { get; }
    public ChatEntryKind Kind { get; }

    public string FormatForDisplay()
    {
        var time = Timestamp.ToString("HH:mm");
        return Kind switch
        {
            ChatEntryKind.Chat => $"[{time}] {SenderName}: {Text}",
            ChatEntryKind.Action => $"[{time}] * {SenderName} {Text}",
            ChatEntryKind.System => $"[{time}] {Text}",
            _ => $"[{time}] {Text}"
        };
    }
}
