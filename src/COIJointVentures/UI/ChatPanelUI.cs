using System.Collections.Generic;
using COIJointVentures.Chat;
using COIJointVentures.Runtime;
using UnityEngine;
using UnityEngine.UIElements;

namespace COIJointVentures.UI;

internal sealed class ChatPanelUI
{
    private readonly VisualElement _root;
    private readonly ScrollView _scrollView;
    private readonly VisualElement _messageContainer;
    private readonly TextField _inputField;
    private int _lastVersion;

    public VisualElement Root => _root;

    public bool Visible
    {
        get => _root.style.display == DisplayStyle.Flex;
        set => _root.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
    }

    public ChatPanelUI()
    {
        _root = new VisualElement();
        _root.style.position = Position.Absolute;
        _root.style.left = 440;
        _root.style.top = 20;
        _root.style.width = 360;
        _root.style.height = 350;
        _root.style.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f);
        UIHelpers.SetBorderRadius(_root, 8);
        UIHelpers.SetBorder(_root, 1, new Color(0.35f, 0.35f, 0.40f));
        _root.style.flexDirection = FlexDirection.Column;
        _root.style.display = DisplayStyle.None;

        _root.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
        _root.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
        _root.RegisterCallback<PointerMoveEvent>(evt => evt.StopPropagation());
        _root.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());

        // title bar
        var titleBar = UIHelpers.MakeTitleBar("Chat & Activity (F9)", () => Visible = false);
        UIHelpers.MakeDraggable(titleBar, _root);
        _root.Add(titleBar);

        // message area
        _scrollView = new ScrollView(ScrollViewMode.Vertical);
        _scrollView.style.flexGrow = 1;
        _scrollView.style.backgroundColor = new Color(0.10f, 0.10f, 0.12f, 1f);
        _scrollView.style.paddingLeft = 6;
        _scrollView.style.paddingRight = 6;
        _scrollView.style.paddingTop = 4;
        _scrollView.style.paddingBottom = 4;
        _messageContainer = _scrollView.contentContainer;
        _root.Add(_scrollView);

        // input row
        var inputRow = new VisualElement();
        inputRow.style.flexDirection = FlexDirection.Row;
        inputRow.style.paddingLeft = 6;
        inputRow.style.paddingRight = 6;
        inputRow.style.paddingTop = 4;
        inputRow.style.paddingBottom = 6;
        inputRow.style.backgroundColor = new Color(0.08f, 0.08f, 0.10f, 1f);
        inputRow.style.borderBottomLeftRadius = 8;
        inputRow.style.borderBottomRightRadius = 8;

        _inputField = new TextField();
        _inputField.style.flexGrow = 1;
        _inputField.style.marginRight = 4;
        UIHelpers.StyleTextField(_inputField);
        _inputField.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                SendMessage();
                evt.StopPropagation();
            }
        });
        inputRow.Add(_inputField);

        var sendBtn = UIHelpers.MakeButton("Send", SendMessage, new Color(0.22f, 0.45f, 0.65f));
        sendBtn.style.width = 50;
        sendBtn.style.height = 24;
        inputRow.Add(sendBtn);

        _root.Add(inputRow);
    }

    public void Update()
    {
        if (!Visible) return;

        var chat = PluginRuntime.Chat;
        var version = chat.Version;
        if (version == _lastVersion) return;
        _lastVersion = version;

        _messageContainer.Clear();
        var entries = chat.GetEntries();
        foreach (var entry in entries)
        {
            var color = entry.Kind switch
            {
                ChatEntryKind.Chat => new Color(1f, 1f, 1f),
                ChatEntryKind.Action => new Color(0.65f, 0.65f, 0.65f),
                ChatEntryKind.System => new Color(1f, 0.85f, 0.3f),
                _ => Color.white
            };

            var lbl = new Label(entry.FormatForDisplay());
            lbl.style.fontSize = 12;
            lbl.style.color = color;
            lbl.style.whiteSpace = WhiteSpace.Normal;
            lbl.style.marginBottom = 1;
            _messageContainer.Add(lbl);
        }

        // auto-scroll to bottom
        _scrollView.schedule.Execute(() => _scrollView.scrollOffset = new Vector2(0, float.MaxValue));
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
