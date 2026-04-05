using COIJointVentures.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace COIJointVentures.UI;

// full-screen blocking overlay when someone is joining
internal sealed class JoinOverlayUI
{
    private readonly VisualElement _root;
    private readonly Label _textLabel;

    public VisualElement Root => _root;

    public JoinOverlayUI()
    {
        _root = new VisualElement();
        _root.style.position = Position.Absolute;
        _root.style.left = 0;
        _root.style.top = 0;
        _root.style.right = 0;
        _root.style.bottom = 0;
        _root.style.backgroundColor = new Color(0, 0, 0, 0.92f);
        _root.style.alignItems = Align.Center;
        _root.style.justifyContent = Justify.Center;
        _root.style.display = DisplayStyle.None;

        // block all clicks
        _root.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
        _root.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
        _root.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());

        _textLabel = new Label("");
        _textLabel.style.fontSize = 28;
        _textLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        _textLabel.style.color = Color.white;
        _textLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        _textLabel.style.whiteSpace = WhiteSpace.Normal;
        _textLabel.style.maxWidth = 600;
        _root.Add(_textLabel);
    }

    public void Update(MultiplayerBootstrap? bootstrap)
    {
        if (bootstrap == null)
        {
            _root.style.display = DisplayStyle.None;
            return;
        }

        var session = bootstrap.Session;
        string? text = null;

        // joining client — their own receive/load takes priority over IsJoinSyncActive
        if (session.State == Session.ConnectionState.ReceivingSave ||
            session.State == Session.ConnectionState.LoadingSave ||
            session.State == Session.ConnectionState.WaitingForAccept ||
            session.State == Session.ConnectionState.Connecting)
        {
            text = $"Joining server...\n\n{session.StatusMessage ?? "Connecting..."}";
        }
        // host side — coordinating join
        else if (session.JoinCoordinator is { IsBlocking: true } coordinator)
        {
            text = $"{coordinator.JoiningPlayerNames} is joining...\n\n{coordinator.PhaseDescription}";
        }
        // existing client side — another player joining
        else if (session.IsJoinSyncActive)
        {
            text = $"{session.JoinSyncPlayerName} is joining...\n\nSaving and transferring world data.\nPlease wait.";
        }

        if (text != null)
        {
            _textLabel.text = text;
            _root.style.display = DisplayStyle.Flex;

            // block keyboard input too
            Input.ResetInputAxes();
        }
        else
        {
            _root.style.display = DisplayStyle.None;
        }
    }
}
