using System.Collections.Generic;

namespace COIJointVentures.Chat;

internal sealed class ChatLog
{
    private const int MaxEntries = 200;
    private readonly object _gate = new object();
    private readonly List<ChatEntry> _entries = new();
    private int _version;

    public int Version
    {
        get { lock (_gate) { return _version; } }
    }

    public List<ChatEntry> GetEntries()
    {
        lock (_gate)
        {
            return new List<ChatEntry>(_entries);
        }
    }

    public void AddChat(string senderName, string text)
    {
        Add(new ChatEntry(senderName, text, ChatEntryKind.Chat));
    }

    public void AddAction(string senderName, string description)
    {
        Add(new ChatEntry(senderName, description, ChatEntryKind.Action));
    }

    public void AddSimControl(string senderName, string description)
    {
        Add(new ChatEntry(senderName, description, ChatEntryKind.SimControl));
    }

    public void AddSystem(string text)
    {
        Add(new ChatEntry(string.Empty, text, ChatEntryKind.System));
    }

    private void Add(ChatEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(0);
            }

            _version++;
        }
    }
}
