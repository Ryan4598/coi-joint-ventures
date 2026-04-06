using System.Collections.Generic;
using COIJointVentures.Chat;
using COIJointVentures.Runtime;
using UnityEngine;
using UnityEngine.UIElements;

namespace COIJointVentures.UI;

internal sealed class ChatPanelUI
{
    private const int MaxVisibleMessages = 12;
    private const float ShowDuration = 5f;
    private const float FadeDuration = 1f;

    private readonly VisualElement _root;
    private readonly ScrollView _scrollView;
    private readonly VisualElement _messageContainer;
    private readonly VisualElement _inputRow;
    private readonly TextField _inputField;
    private int _lastVersion;
    private float _lastMessageTime;
    private bool _inputOpen;

    public VisualElement Root => _root;
    public bool InputOpen => _inputOpen;

    /// <summary>
    /// Controls whether the HUD is active at all (set true when in session).
    /// </summary>
    public bool Visible
    {
        get => _root.style.display == DisplayStyle.Flex;
        set
        {
            _root.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
            if (!value) CloseInput();
        }
    }

    public ChatPanelUI()
    {
        _root = new VisualElement();
        _root.style.position = Position.Absolute;
        _root.style.right = 16;
        _root.style.bottom = 16;
        _root.style.width = 380;
        _root.style.flexDirection = FlexDirection.Column;
        _root.style.justifyContent = Justify.FlexEnd;
        _root.style.display = DisplayStyle.None;

        // don't let clicks on the chat fall through to the game
        _root.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
        _root.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
        _root.RegisterCallback<PointerMoveEvent>(evt => evt.StopPropagation());
        _root.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());

        // message area — scrollable when input is open, unbounded when passive
        _scrollView = new ScrollView(ScrollViewMode.Vertical);
        _scrollView.style.paddingLeft = 6;
        _scrollView.style.paddingRight = 6;
        _scrollView.style.paddingTop = 4;
        _scrollView.style.paddingBottom = 4;
        _messageContainer = _scrollView.contentContainer;
        _root.Add(_scrollView);

        // input row — hidden until opened
        _inputRow = new VisualElement();
        _inputRow.style.flexShrink = 0;
        _inputRow.style.flexDirection = FlexDirection.Row;
        _inputRow.style.paddingLeft = 4;
        _inputRow.style.paddingRight = 4;
        _inputRow.style.paddingTop = 4;
        _inputRow.style.paddingBottom = 4;
        _inputRow.style.backgroundColor = new Color(0.08f, 0.08f, 0.10f, 0.7f);
        UIHelpers.SetBorderRadius(_inputRow, 4);
        _inputRow.style.display = DisplayStyle.None;

        _inputField = new TextField();
        _inputField.style.flexGrow = 1;
        _inputField.style.marginRight = 4;
        UIHelpers.StyleTextField(_inputField);
        _inputField.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                evt.StopImmediatePropagation();
                _inputField.schedule.Execute(SendMessage);
            }
            else if (evt.keyCode == KeyCode.Escape)
            {
                evt.StopImmediatePropagation();
                _inputField.schedule.Execute(CloseInput);
            }
        }, TrickleDown.TrickleDown);
        _inputRow.Add(_inputField);

        var sendBtn = UIHelpers.MakeButton("Send", SendMessage, new Color(0.22f, 0.45f, 0.65f));
        sendBtn.style.width = 50;
        sendBtn.style.height = 24;
        _inputRow.Add(sendBtn);

        _root.Add(_inputRow);
    }

    public void ToggleInput()
    {
        if (_inputOpen)
            CloseInput();
        else
            OpenInput();
    }

    public void OpenInput()
    {
        _inputOpen = true;
        _inputRow.style.display = DisplayStyle.Flex;
        _root.style.backgroundColor = new Color(0.05f, 0.05f, 0.07f, 0.5f);
        UIHelpers.SetBorderRadius(_root, 6);

        // constrain scroll area so it's scrollable
        _scrollView.style.maxHeight = 280;

        _inputField.Focus();
        _lastMessageTime = Time.unscaledTime;
        // force a rebuild to show all messages, then scroll to bottom
        _lastVersion = -1;
    }

    public void CloseInput()
    {
        _inputOpen = false;
        _inputRow.style.display = DisplayStyle.None;
        _root.style.backgroundColor = Color.clear;

        // unconstrain — let it just show the last few messages
        _scrollView.style.maxHeight = StyleKeyword.None;

        _inputField.value = "";
        // force rebuild to go back to limited messages
        _lastVersion = -1;
    }

    public void Update()
    {
        if (!Visible) return;

        var chat = PluginRuntime.Chat;
        var version = chat.Version;

        // rebuild message labels when log changes
        if (version != _lastVersion)
        {
            _lastVersion = version;
            _lastMessageTime = Time.unscaledTime;

            _messageContainer.Clear();
            var entries = chat.GetEntries();

            // when input is open show all messages (scrollable), otherwise just the last N
            int start = _inputOpen ? 0 : (entries.Count > MaxVisibleMessages ? entries.Count - MaxVisibleMessages : 0);
            for (int i = start; i < entries.Count; i++)
            {
                var entry = entries[i];
                var color = entry.Kind switch
                {
                    ChatEntryKind.Chat => new Color(1f, 1f, 1f),
                    ChatEntryKind.Action => new Color(0.65f, 0.65f, 0.65f),
                    ChatEntryKind.SimControl => new Color(0.55f, 0.8f, 1f),
                    ChatEntryKind.System => new Color(1f, 0.85f, 0.3f),
                    _ => Color.white
                };

                var lbl = new Label(entry.FormatForDisplay());
                lbl.style.fontSize = 12;
                lbl.style.color = color;
                lbl.style.whiteSpace = WhiteSpace.Normal;
                lbl.style.marginBottom = 1;
                lbl.style.textShadow = new TextShadow
                {
                    offset = new Vector2(1, 1),
                    blurRadius = 2,
                    color = new Color(0, 0, 0, 0.8f)
                };
                _messageContainer.Add(lbl);
            }

            // auto-scroll to bottom
            _scrollView.schedule.Execute(() => _scrollView.scrollOffset = new Vector2(0, float.MaxValue));
        }

        // fade logic — always visible while input is open
        if (_inputOpen)
        {
            _scrollView.style.opacity = 1f;
        }
        else
        {
            float elapsed = Time.unscaledTime - _lastMessageTime;
            if (elapsed < ShowDuration)
            {
                _scrollView.style.opacity = 1f;
            }
            else if (elapsed < ShowDuration + FadeDuration)
            {
                float t = (elapsed - ShowDuration) / FadeDuration;
                _scrollView.style.opacity = 1f - t;
            }
            else
            {
                _scrollView.style.opacity = 0f;
            }
        }
    }

    private void SendMessage()
    {
        var text = _inputField.value?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        PluginRuntime.Session?.SendChatMessage(text);
        _inputField.value = "";
        _inputField.Focus();
    }
}
