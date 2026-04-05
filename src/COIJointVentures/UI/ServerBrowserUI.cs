using System;
using System.Linq;
using COIJointVentures.Runtime;
using Steamworks;
using UnityEngine;
using UnityEngine.UIElements;
using Color = UnityEngine.Color;
using Lobby = Steamworks.Data.Lobby;

namespace COIJointVentures.UI;

internal sealed class ServerBrowserUI
{
    private readonly VisualElement _root;
    private readonly VisualElement _serverListContainer;
    private readonly Label _selectedLabel;
    private readonly TextField _passwordField;
    private readonly VisualElement _passwordRow;
    private readonly Button _connectButton;
    private readonly Label _statusLabel;
    private readonly Button _allTab;
    private readonly Button _friendsTab;

    // lan popup
    private VisualElement _lanOverlay = null!;
    private TextField _lanIpField = null!;
    private TextField _lanPortField = null!;
    private TextField _lanPasswordField = null!;

    // code popup
    private VisualElement _codeOverlay = null!;
    private TextField _codeField = null!;
    private TextField _codePasswordField = null!;

    // drag state
    private bool _dragging;
    private Vector2 _dragOffset;

    private Lobby[] _lobbies = Array.Empty<Lobby>();
    private int _selectedIndex = -1;
    private int _activeTab;
    private bool _searching;

    public event Action? OnClosed;
    public event Action<ulong, string>? OnJoinSteam;
    public event Action<string, int, string>? OnJoinLan;

    public VisualElement Root => _root;
    public bool Visible
    {
        get => _root.style.display == DisplayStyle.Flex;
        set => _root.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
    }

    public ServerBrowserUI()
    {
        _root = new VisualElement();
        _root.style.position = Position.Absolute;
        _root.style.left = 150;
        _root.style.top = 100;
        _root.style.width = 700;
        _root.style.height = 500;
        _root.style.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f);
        SetBorderRadius(_root, 8);
        SetBorder(_root, 1, new Color(0.35f, 0.35f, 0.40f));
        _root.style.flexDirection = FlexDirection.Column;
        _root.style.display = DisplayStyle.None;

        // eat all pointer events so game can't click through
        _root.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
        _root.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
        _root.RegisterCallback<PointerMoveEvent>(evt => evt.StopPropagation());
        _root.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
        _root.RegisterCallback<MouseUpEvent>(evt => evt.StopPropagation());

        // ---- title bar (draggable) ----
        var titleBar = new VisualElement();
        titleBar.style.flexDirection = FlexDirection.Row;
        titleBar.style.backgroundColor = new Color(0.08f, 0.08f, 0.10f, 1f);
        titleBar.style.paddingLeft = 12;
        titleBar.style.paddingRight = 8;
        titleBar.style.paddingTop = 6;
        titleBar.style.paddingBottom = 6;
        titleBar.style.alignItems = Align.Center;
        titleBar.style.borderTopLeftRadius = 8;
        titleBar.style.borderTopRightRadius = 8;

        var title = new Label("Server Browser");
        title.style.fontSize = 15;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.color = Color.white;
        title.style.flexGrow = 1;
        titleBar.Add(title);

        var closeBtn = MakeButton("X", () => { Visible = false; OnClosed?.Invoke(); }, new Color(0.6f, 0.2f, 0.2f));
        closeBtn.style.width = 26;
        closeBtn.style.height = 26;
        titleBar.Add(closeBtn);

        // drag handling on title bar
        titleBar.RegisterCallback<PointerDownEvent>(evt =>
        {
            _dragging = true;
            _dragOffset = new Vector2(evt.position.x - _root.resolvedStyle.left, evt.position.y - _root.resolvedStyle.top);
            titleBar.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        });
        titleBar.RegisterCallback<PointerMoveEvent>(evt =>
        {
            if (!_dragging) return;
            _root.style.left = evt.position.x - _dragOffset.x;
            _root.style.top = evt.position.y - _dragOffset.y;
            evt.StopPropagation();
        });
        titleBar.RegisterCallback<PointerUpEvent>(evt =>
        {
            _dragging = false;
            titleBar.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        });

        _root.Add(titleBar);

        // ---- tab bar ----
        var tabBar = new VisualElement();
        tabBar.style.flexDirection = FlexDirection.Row;
        tabBar.style.paddingLeft = 8;
        tabBar.style.paddingRight = 8;
        tabBar.style.paddingTop = 6;
        tabBar.style.paddingBottom = 6;
        tabBar.style.alignItems = Align.Center;

        _allTab = MakeTabButton("All Servers", true, () => { _activeTab = 0; UpdateTabs(); RefreshList(); });
        _friendsTab = MakeTabButton("Friends", false, () => { _activeTab = 1; UpdateTabs(); RefreshList(); });

        _statusLabel = new Label("");
        _statusLabel.style.flexGrow = 1;
        _statusLabel.style.unityTextAlign = TextAnchor.MiddleRight;
        _statusLabel.style.color = new Color(0.5f, 0.6f, 0.7f);
        _statusLabel.style.fontSize = 12;
        _statusLabel.style.marginRight = 8;

        var refreshBtn = MakeButton("Refresh", () => SearchLobbies(), new Color(0.25f, 0.25f, 0.30f));
        refreshBtn.style.width = 70;
        refreshBtn.style.height = 28;

        var codeBtn = MakeButton("Join by Code", () => ShowCodePopup(), new Color(0.35f, 0.35f, 0.40f));
        codeBtn.style.width = 110;
        codeBtn.style.height = 28;
        codeBtn.style.marginLeft = 6;

        var lanBtn = MakeButton("Connect LAN", () => ShowLanPopup(), new Color(0.35f, 0.35f, 0.40f));
        lanBtn.style.width = 110;
        lanBtn.style.height = 28;
        lanBtn.style.marginLeft = 6;

        tabBar.Add(_allTab);
        tabBar.Add(_friendsTab);
        tabBar.Add(_statusLabel);
        tabBar.Add(refreshBtn);
        tabBar.Add(codeBtn);
        tabBar.Add(lanBtn);
        _root.Add(tabBar);

        // ---- header row ----
        var header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.backgroundColor = new Color(0.08f, 0.08f, 0.10f, 1f);
        header.style.paddingLeft = 12;
        header.style.paddingRight = 12;
        header.style.paddingTop = 5;
        header.style.paddingBottom = 5;

        header.Add(MakeHeaderLabel("Server Name", 0, true));
        header.Add(MakeHeaderLabel("Host", 130, false));
        header.Add(MakeHeaderLabel("Players", 70, false));
        header.Add(MakeHeaderLabel("", 34, false));
        _root.Add(header);

        // ---- server list (scrollable) ----
        var scrollView = new ScrollView(ScrollViewMode.Vertical);
        scrollView.style.flexGrow = 1;
        scrollView.style.backgroundColor = new Color(0.14f, 0.14f, 0.16f, 1f);
        _serverListContainer = scrollView.contentContainer;
        _root.Add(scrollView);

        // ---- bottom bar ----
        var bottomBar = new VisualElement();
        bottomBar.style.backgroundColor = new Color(0.08f, 0.08f, 0.10f, 1f);
        bottomBar.style.paddingLeft = 12;
        bottomBar.style.paddingRight = 12;
        bottomBar.style.paddingTop = 8;
        bottomBar.style.paddingBottom = 8;
        bottomBar.style.borderBottomLeftRadius = 8;
        bottomBar.style.borderBottomRightRadius = 8;

        _selectedLabel = new Label("Select a server to connect.");
        _selectedLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
        _selectedLabel.style.fontSize = 14;
        _selectedLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        bottomBar.Add(_selectedLabel);

        var connectRow = new VisualElement();
        connectRow.style.flexDirection = FlexDirection.Row;
        connectRow.style.alignItems = Align.Center;
        connectRow.style.marginTop = 4;

        _passwordRow = new VisualElement();
        _passwordRow.style.flexDirection = FlexDirection.Row;
        _passwordRow.style.alignItems = Align.Center;
        _passwordRow.style.display = DisplayStyle.None;

        var pwLabel = new Label("Password:");
        pwLabel.style.color = Color.white;
        pwLabel.style.marginRight = 6;
        pwLabel.style.fontSize = 13;
        _passwordRow.Add(pwLabel);

        _passwordField = new TextField();
        _passwordField.isPasswordField = true;
        _passwordField.style.width = 160;
        UIHelpers.StyleTextField(_passwordField);
        _passwordRow.Add(_passwordField);

        connectRow.Add(_passwordRow);

        var spacer = new VisualElement();
        spacer.style.flexGrow = 1;
        connectRow.Add(spacer);

        _connectButton = MakeButton("Connect", OnConnect, new Color(0.22f, 0.45f, 0.65f));
        _connectButton.style.width = 120;
        _connectButton.style.height = 30;
        _connectButton.style.fontSize = 14;
        _connectButton.style.display = DisplayStyle.None;
        connectRow.Add(_connectButton);

        bottomBar.Add(connectRow);
        _root.Add(bottomBar);

        // ---- popups ----
        BuildLanPopup();
        BuildCodePopup();
    }

    private void BuildLanPopup()
    {
        _lanOverlay = new VisualElement();
        _lanOverlay.style.position = Position.Absolute;
        _lanOverlay.style.left = 0;
        _lanOverlay.style.top = 0;
        _lanOverlay.style.right = 0;
        _lanOverlay.style.bottom = 0;
        _lanOverlay.style.backgroundColor = new Color(0, 0, 0, 0.6f);
        _lanOverlay.style.alignItems = Align.Center;
        _lanOverlay.style.justifyContent = Justify.Center;
        _lanOverlay.style.display = DisplayStyle.None;
        _lanOverlay.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());

        var lanBox = new VisualElement();
        lanBox.style.width = 340;
        lanBox.style.backgroundColor = new Color(0.15f, 0.15f, 0.18f, 1f);
        lanBox.style.paddingLeft = 20;
        lanBox.style.paddingRight = 20;
        lanBox.style.paddingTop = 16;
        lanBox.style.paddingBottom = 16;
        SetBorderRadius(lanBox, 8);
        SetBorder(lanBox, 1, new Color(0.35f, 0.35f, 0.40f));

        var lanTitle = new Label("Connect to Server");
        lanTitle.style.fontSize = 16;
        lanTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
        lanTitle.style.color = Color.white;
        lanTitle.style.marginBottom = 12;
        lanBox.Add(lanTitle);

        _lanIpField = MakeLabeledField(lanBox, "IP Address:", "127.0.0.1");
        _lanPortField = MakeLabeledField(lanBox, "Port:", "38455");
        _lanPasswordField = MakeLabeledField(lanBox, "Password:", "");
        _lanPasswordField.isPasswordField = true;

        var lanBtnRow = new VisualElement();
        lanBtnRow.style.flexDirection = FlexDirection.Row;
        lanBtnRow.style.justifyContent = Justify.SpaceBetween;
        lanBtnRow.style.marginTop = 16;

        var cancelBtn = MakeButton("Cancel", () => _lanOverlay.style.display = DisplayStyle.None, new Color(0.3f, 0.3f, 0.3f));
        cancelBtn.style.height = 30;
        cancelBtn.style.width = 90;

        var joinLanBtn = MakeButton("Join Server", OnJoinLanClick, new Color(0.22f, 0.45f, 0.65f));
        joinLanBtn.style.height = 30;
        joinLanBtn.style.width = 110;

        lanBtnRow.Add(cancelBtn);
        lanBtnRow.Add(joinLanBtn);
        lanBox.Add(lanBtnRow);

        _lanOverlay.Add(lanBox);
        _root.Add(_lanOverlay);
    }

    private void BuildCodePopup()
    {
        _codeOverlay = new VisualElement();
        _codeOverlay.style.position = Position.Absolute;
        _codeOverlay.style.left = 0;
        _codeOverlay.style.top = 0;
        _codeOverlay.style.right = 0;
        _codeOverlay.style.bottom = 0;
        _codeOverlay.style.backgroundColor = new Color(0, 0, 0, 0.6f);
        _codeOverlay.style.alignItems = Align.Center;
        _codeOverlay.style.justifyContent = Justify.Center;
        _codeOverlay.style.display = DisplayStyle.None;
        _codeOverlay.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());

        var box = new VisualElement();
        box.style.width = 340;
        box.style.backgroundColor = new Color(0.15f, 0.15f, 0.18f, 1f);
        box.style.paddingLeft = 20;
        box.style.paddingRight = 20;
        box.style.paddingTop = 16;
        box.style.paddingBottom = 16;
        SetBorderRadius(box, 8);
        SetBorder(box, 1, new Color(0.35f, 0.35f, 0.40f));

        var title = new Label("Join by Lobby Code");
        title.style.fontSize = 16;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.color = Color.white;
        title.style.marginBottom = 12;
        box.Add(title);

        _codeField = MakeLabeledField(box, "Lobby Code:", "");
        _codePasswordField = MakeLabeledField(box, "Password:", "");
        _codePasswordField.isPasswordField = true;

        var btnRow = new VisualElement();
        btnRow.style.flexDirection = FlexDirection.Row;
        btnRow.style.justifyContent = Justify.SpaceBetween;
        btnRow.style.marginTop = 16;

        var cancelBtn = MakeButton("Cancel", () => _codeOverlay.style.display = DisplayStyle.None, new Color(0.3f, 0.3f, 0.3f));
        cancelBtn.style.height = 30;
        cancelBtn.style.width = 90;

        var joinBtn = MakeButton("Join", OnJoinCodeClick, new Color(0.22f, 0.45f, 0.65f));
        joinBtn.style.height = 30;
        joinBtn.style.width = 100;

        btnRow.Add(cancelBtn);
        btnRow.Add(joinBtn);
        box.Add(btnRow);

        _codeOverlay.Add(box);
        _root.Add(_codeOverlay);
    }

    private void OnJoinCodeClick()
    {
        var code = _codeField.value?.Trim() ?? "";
        if (!ulong.TryParse(code, out var lobbyId))
        {
            Plugin.LogInstance.LogWarning("Invalid lobby code.");
            return;
        }

        _codeOverlay.style.display = DisplayStyle.None;
        Visible = false;
        OnJoinSteam?.Invoke(lobbyId, _codePasswordField.value ?? "");
    }

    public void Show()
    {
        Visible = true;
        _selectedIndex = -1;
        _passwordField.value = "";
        UpdateBottomBar();
        SearchLobbies();
    }

    private void UpdateTabs()
    {
        StyleTab(_allTab, _activeTab == 0);
        StyleTab(_friendsTab, _activeTab == 1);
    }

    private void StyleTab(Button btn, bool active)
    {
        btn.style.backgroundColor = active ? new Color(0.22f, 0.45f, 0.65f) : new Color(0.18f, 0.18f, 0.20f);
        btn.style.color = active ? Color.white : new Color(0.6f, 0.6f, 0.6f);
        btn.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
    }

    private void RefreshList()
    {
        _serverListContainer.Clear();

        var filtered = _activeTab == 1 ? _lobbies.Where(l => IsHostedByFriend(l)).ToArray() : _lobbies;

        if (filtered.Length == 0)
        {
            var empty = new Label(_searching ? "Searching for servers..." : "No servers found. Hit Refresh or try Connect LAN.");
            empty.style.color = new Color(0.5f, 0.5f, 0.5f);
            empty.style.fontSize = 14;
            empty.style.unityTextAlign = TextAnchor.MiddleCenter;
            empty.style.paddingTop = 40;
            empty.style.paddingBottom = 40;
            _serverListContainer.Add(empty);
            return;
        }

        for (int i = 0; i < filtered.Length; i++)
        {
            var lobby = filtered[i];
            var idx = i;
            var row = MakeServerRow(lobby, i == _selectedIndex, i % 2 == 0);
            row.RegisterCallback<ClickEvent>(evt =>
            {
                _selectedIndex = idx;
                _passwordField.value = "";
                RefreshList();
                UpdateBottomBar();
            });
            _serverListContainer.Add(row);
        }
    }

    private void UpdateBottomBar()
    {
        var filtered = _activeTab == 1 ? _lobbies.Where(l => IsHostedByFriend(l)).ToArray() : _lobbies;

        if (_selectedIndex >= 0 && _selectedIndex < filtered.Length)
        {
            var sel = filtered[_selectedIndex];
            var name = sel.GetData("name");
            if (string.IsNullOrEmpty(name)) name = "Unknown Server";
            var hasPass = sel.GetData("password") == "1";

            _selectedLabel.text = $"Selected: {name}";
            _selectedLabel.style.color = Color.white;
            _passwordRow.style.display = hasPass ? DisplayStyle.Flex : DisplayStyle.None;
            _connectButton.style.display = DisplayStyle.Flex;
        }
        else
        {
            _selectedLabel.text = "Select a server to connect.";
            _selectedLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            _passwordRow.style.display = DisplayStyle.None;
            _connectButton.style.display = DisplayStyle.None;
        }
    }

    private VisualElement MakeServerRow(Lobby lobby, bool selected, bool even)
    {
        var serverName = lobby.GetData("name") ?? "Unknown Server";
        var hostName = lobby.GetData("host_name") ?? lobby.Owner.Name ?? "???";
        var hasPass = lobby.GetData("password") == "1";
        var members = lobby.MemberCount;
        var max = lobby.MaxMembers;

        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.paddingLeft = 12;
        row.style.paddingRight = 12;
        row.style.paddingTop = 7;
        row.style.paddingBottom = 7;
        row.style.backgroundColor = selected
            ? new Color(0.22f, 0.40f, 0.58f, 1f)
            : even ? new Color(0.14f, 0.14f, 0.16f, 1f) : new Color(0.17f, 0.17f, 0.19f, 1f);

        row.Add(MakeRowLabel(serverName, 0, true, selected));
        row.Add(MakeRowLabel(hostName, 130, false, selected));
        row.Add(MakeRowLabel($"{members} / {max}", 70, false, selected));
        row.Add(MakeRowLabel(hasPass ? "[PW]" : "", 34, false, selected));

        AddHoverEffect(row);
        return row;
    }

    private void OnConnect()
    {
        var filtered = _activeTab == 1 ? _lobbies.Where(l => IsHostedByFriend(l)).ToArray() : _lobbies;
        if (_selectedIndex < 0 || _selectedIndex >= filtered.Length) return;

        var lobby = filtered[_selectedIndex];
        Visible = false;
        OnJoinSteam?.Invoke(lobby.Id.Value, _passwordField.value);
    }

    private void ShowLanPopup() => _lanOverlay.style.display = DisplayStyle.Flex;
    private void ShowCodePopup() => _codeOverlay.style.display = DisplayStyle.Flex;

    private void OnJoinLanClick()
    {
        _lanOverlay.style.display = DisplayStyle.None;
        Visible = false;
        int port = 38455;
        int.TryParse(_lanPortField.value, out port);
        OnJoinLan?.Invoke(_lanIpField.value, port, _lanPasswordField.value);
    }

    public async void SearchLobbies()
    {
        if (_searching) return;
        _searching = true;
        _statusLabel.text = "Searching...";
        _lobbies = Array.Empty<Lobby>();
        RefreshList();

        try
        {
            var results = await SteamMatchmaking.LobbyList
                .WithKeyValue("mod", "coi-joint-ventures")
                .FilterDistanceWorldwide()
                .WithMaxResults(50)
                .RequestAsync();

            if (results != null)
            {
                _lobbies = results
                    .OrderByDescending(l => IsHostedByFriend(l))
                    .ThenBy(l => l.GetData("name") ?? "")
                    .ToArray();
            }

            _statusLabel.text = $"{_lobbies.Length} server(s) found";
        }
        catch (Exception ex)
        {
            _statusLabel.text = "Search failed";
            Plugin.LogInstance.LogWarning($"Lobby search failed: {ex.Message}");
        }

        _searching = false;
        _selectedIndex = -1;
        UpdateBottomBar();
        RefreshList();
    }

    // ---- helpers ----

    private Button MakeTabButton(string text, bool active, Action onClick)
    {
        var btn = new Button(onClick) { text = text };
        btn.style.height = 28;
        btn.style.paddingLeft = 16;
        btn.style.paddingRight = 16;
        btn.style.paddingTop = 0;
        btn.style.paddingBottom = 0;
        btn.style.fontSize = 13;
        btn.style.marginRight = 4;
        btn.style.unityTextAlign = TextAnchor.MiddleCenter;
        btn.style.alignItems = Align.Center;
        btn.style.justifyContent = Justify.Center;
        SetBorderRadius(btn, 4);
        btn.style.borderTopWidth = 0;
        btn.style.borderBottomWidth = 0;
        btn.style.borderLeftWidth = 0;
        btn.style.borderRightWidth = 0;
        StyleTab(btn, active);
        // no AddHoverEffect — hover is handled by StyleTab re-reading active state
        btn.RegisterCallback<MouseEnterEvent>(evt =>
        {
            var isActive = (_activeTab == 0 && btn == _allTab) || (_activeTab == 1 && btn == _friendsTab);
            if (!isActive)
                btn.style.backgroundColor = new Color(0.25f, 0.25f, 0.28f);
        });
        btn.RegisterCallback<MouseLeaveEvent>(evt =>
        {
            var isActive = (_activeTab == 0 && btn == _allTab) || (_activeTab == 1 && btn == _friendsTab);
            StyleTab(btn, isActive);
        });
        return btn;
    }

    private static Button MakeButton(string text, Action onClick, Color bg)
    {
        var btn = new Button(onClick) { text = text };
        btn.style.backgroundColor = bg;
        btn.style.color = Color.white;
        btn.style.borderTopWidth = 0;
        btn.style.borderBottomWidth = 0;
        btn.style.borderLeftWidth = 0;
        btn.style.borderRightWidth = 0;
        btn.style.paddingTop = 0;
        btn.style.paddingBottom = 0;
        btn.style.unityTextAlign = TextAnchor.MiddleCenter;
        btn.style.alignItems = Align.Center;
        btn.style.justifyContent = Justify.Center;
        SetBorderRadius(btn, 4);
        btn.style.unityFontStyleAndWeight = FontStyle.Bold;
        btn.style.fontSize = 13;
        AddHoverEffect(btn);
        return btn;
    }

    private static Label MakeHeaderLabel(string text, int width, bool expand)
    {
        var lbl = new Label(text);
        lbl.style.fontSize = 12;
        lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
        lbl.style.color = new Color(0.65f, 0.65f, 0.65f);
        lbl.style.unityTextAlign = TextAnchor.MiddleLeft;
        if (expand) lbl.style.flexGrow = 1;
        else lbl.style.width = width;
        return lbl;
    }

    private static bool IsHostedByFriend(Lobby lobby)
    {
        var hostId = lobby.GetData("host_id");
        if (!string.IsNullOrEmpty(hostId) && ulong.TryParse(hostId, out var id))
        {
            return new Friend(new SteamId { Value = id }).IsFriend;
        }

        return lobby.Owner.IsFriend;
    }

    private static Label MakeRowLabel(string text, int width, bool expand, bool selected)
    {
        var lbl = new Label(text);
        lbl.style.fontSize = 13;
        lbl.style.color = selected ? Color.white : new Color(0.8f, 0.8f, 0.8f);
        lbl.style.unityTextAlign = TextAnchor.MiddleLeft;
        if (expand) lbl.style.flexGrow = 1;
        else lbl.style.width = width;
        return lbl;
    }

    private static TextField MakeLabeledField(VisualElement parent, string label, string defaultValue)
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;
        row.style.marginBottom = 6;

        var lbl = new Label(label);
        lbl.style.color = Color.white;
        lbl.style.width = 90;
        lbl.style.fontSize = 13;
        row.Add(lbl);

        var field = new TextField();
        field.value = defaultValue;
        field.style.flexGrow = 1;
        UIHelpers.StyleTextField(field);
        row.Add(field);

        parent.Add(row);
        return field;
    }

    private static void AddHoverEffect(VisualElement el)
    {
        Color origBg = default;
        bool captured = false;

        el.RegisterCallback<MouseEnterEvent>(evt =>
        {
            if (!captured)
            {
                origBg = el.resolvedStyle.backgroundColor;
                captured = true;
            }

            el.style.backgroundColor = new Color(
                Mathf.Min(origBg.r + 0.08f, 1f),
                Mathf.Min(origBg.g + 0.08f, 1f),
                Mathf.Min(origBg.b + 0.08f, 1f),
                origBg.a);
        });

        el.RegisterCallback<MouseLeaveEvent>(evt =>
        {
            if (captured)
                el.style.backgroundColor = origBg;
        });
    }

    private static void SetBorderRadius(VisualElement el, float r)
    {
        el.style.borderTopLeftRadius = r;
        el.style.borderTopRightRadius = r;
        el.style.borderBottomLeftRadius = r;
        el.style.borderBottomRightRadius = r;
    }

    private static void SetBorder(VisualElement el, float w, Color c)
    {
        el.style.borderTopWidth = w;
        el.style.borderBottomWidth = w;
        el.style.borderLeftWidth = w;
        el.style.borderRightWidth = w;
        el.style.borderTopColor = c;
        el.style.borderBottomColor = c;
        el.style.borderLeftColor = c;
        el.style.borderRightColor = c;
    }
}
