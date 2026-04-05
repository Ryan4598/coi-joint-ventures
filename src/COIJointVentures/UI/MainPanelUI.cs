using System;
using System.Collections.Generic;
using COIJointVentures.Integration;
using COIJointVentures.Runtime;
using COIJointVentures.Session;
using Steamworks;
using UnityEngine;
using UnityEngine.UIElements;
using Color = UnityEngine.Color;
using ConnectionState = COIJointVentures.Session.ConnectionState;

namespace COIJointVentures.UI;

internal sealed class MainPanelUI
{
    private readonly VisualElement _root;

    // state containers — show/hide based on session state
    private readonly VisualElement _idleContainer;
    private readonly VisualElement _hostSetupContainer;
    private readonly VisualElement _hostingContainer;
    private readonly VisualElement _connectingContainer;
    private readonly VisualElement _connectedContainer;

    // status
    private readonly Label _statusMode;
    private readonly Label _statusMessage;
    private readonly Label _steamName;

    // host setup fields
    private TextField _hostNameField = null!;
    private TextField _hostPasswordField = null!;
    private TextField _hostPortField = null!;
    private VisualElement _hostPortRow = null!;

    // hosting display
    private Label _lobbyCodeLabel = null!;
    private VisualElement _steamInfoContainer = null!;
    private VisualElement _lanInfoContainer = null!;
    private Label _lanInfoLabel = null!;
    private Label _playerListLabel = null!;
    private bool _hostingIsSteam;

    // connecting/connected
    private Label _connectStatusLabel = null!;
    private Label _connectedAsLabel = null!;

    // transport toggle
    private bool _useSteam = true;

    public event Action? OnOpenServerBrowser;
    public event Action<ServerConfig>? OnHostSteam;
    public event Action<ServerConfig>? OnHostLan;
    public event Action? OnStopHosting;
    public event Action? OnDisconnect;
    public event Action? OnCancelConnect;

    public VisualElement Root => _root;

    public bool Visible
    {
        get => _root.style.display == DisplayStyle.Flex;
        set => _root.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
    }

    public MainPanelUI()
    {
        _root = new VisualElement();
        _root.style.position = Position.Absolute;
        _root.style.left = 20;
        _root.style.top = 20;
        _root.style.width = 380;
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
        var titleBar = UIHelpers.MakeTitleBar("COI: Joint Ventures", () => Visible = false);
        UIHelpers.MakeDraggable(titleBar, _root);
        _root.Add(titleBar);

        // status area
        var statusArea = new VisualElement();
        statusArea.style.paddingLeft = 12;
        statusArea.style.paddingRight = 12;
        statusArea.style.paddingTop = 6;
        statusArea.style.paddingBottom = 6;

        _statusMode = new Label("[OFFLINE] Idle");
        _statusMode.style.fontSize = 14;
        _statusMode.style.unityFontStyleAndWeight = FontStyle.Bold;
        _statusMode.style.color = Color.white;
        statusArea.Add(_statusMode);

        _steamName = new Label("");
        _steamName.style.fontSize = 10;
        _steamName.style.color = new Color(0.5f, 0.6f, 0.7f);
        statusArea.Add(_steamName);

        _statusMessage = new Label("");
        _statusMessage.style.fontSize = 12;
        _statusMessage.style.color = new Color(0.7f, 0.7f, 0.7f);
        _statusMessage.style.whiteSpace = WhiteSpace.Normal;
        statusArea.Add(_statusMessage);

        _root.Add(statusArea);

        // content area
        var content = new VisualElement();
        content.style.paddingLeft = 12;
        content.style.paddingRight = 12;
        content.style.paddingBottom = 10;

        _idleContainer = BuildIdlePanel();
        _hostSetupContainer = BuildHostSetupPanel();
        _hostingContainer = BuildHostingPanel();
        _connectingContainer = BuildConnectingPanel();
        _connectedContainer = BuildConnectedPanel();

        content.Add(_idleContainer);
        content.Add(_hostSetupContainer);
        content.Add(_hostingContainer);
        content.Add(_connectingContainer);
        content.Add(_connectedContainer);
        _root.Add(content);

        // footer
        var footer = new Label("F8 toggles this panel. F9 opens chat.");
        footer.style.fontSize = 10;
        footer.style.color = new Color(0.4f, 0.4f, 0.4f);
        footer.style.paddingLeft = 12;
        footer.style.paddingBottom = 6;
        _root.Add(footer);
    }

    public void Update()
    {
        if (!Visible) return;

        var session = PluginRuntime.Session;
        if (session == null) return;

        // update status bar
        var modeLabel = session.Mode switch
        {
            MultiplayerMode.Host => "HOST",
            MultiplayerMode.Client => "CLIENT",
            _ => "OFFLINE"
        };
        var stateLabel = session.State switch
        {
            ConnectionState.Idle => "Idle",
            ConnectionState.Hosting => "Hosting",
            ConnectionState.Connecting => "Connecting...",
            ConnectionState.WaitingForAccept => "Authenticating...",
            ConnectionState.ReceivingSave => "Receiving save...",
            ConnectionState.LoadingSave => "Load save to continue",
            ConnectionState.Connected => "Connected",
            _ => session.State.ToString()
        };
        _statusMode.text = $"[{modeLabel}] {stateLabel}";

        try
        {
            _steamName.text = SteamClient.IsValid ? $"Steam: {SteamClient.Name}" : "";
        }
        catch { _steamName.text = ""; }

        _statusMessage.text = session.StatusMessage ?? "";

        // show/hide panels based on state
        var inGame = MainCapture.IsInGame;
        _idleContainer.style.display = session.State == ConnectionState.Idle && !_inHostSetup ? DisplayStyle.Flex : DisplayStyle.None;
        _hostSetupContainer.style.display = session.State == ConnectionState.Idle && _inHostSetup ? DisplayStyle.Flex : DisplayStyle.None;
        _hostingContainer.style.display = session.State == ConnectionState.Hosting ? DisplayStyle.Flex : DisplayStyle.None;
        _connectingContainer.style.display =
            (session.State == ConnectionState.Connecting ||
             session.State == ConnectionState.WaitingForAccept ||
             session.State == ConnectionState.ReceivingSave ||
             session.State == ConnectionState.LoadingSave) ? DisplayStyle.Flex : DisplayStyle.None;
        _connectedContainer.style.display = session.State == ConnectionState.Connected ? DisplayStyle.Flex : DisplayStyle.None;

        // update dynamic content
        if (session.State == ConnectionState.Hosting)
            UpdateHostingPanel(session);
        if (session.State == ConnectionState.Connected)
            UpdateConnectedPanel(session);
        if (session.State >= ConnectionState.Connecting && session.State <= ConnectionState.LoadingSave)
            _connectStatusLabel.text = session.StatusMessage ?? "Connecting...";
    }

    private bool _inHostSetup;

    private bool _defaultNameSet;

    public void ShowHostSetup()
    {
        _inHostSetup = true;
        _idleContainer.style.display = DisplayStyle.None;
        _hostSetupContainer.style.display = DisplayStyle.Flex;

        // set the default name on first open — steam isn't ready during constructor
        if (!_defaultNameSet)
        {
            _defaultNameSet = true;
            try
            {
                if (SteamClient.IsValid)
                    _hostNameField.value = $"{SteamClient.Name}'s COI Server";
            }
            catch { }
        }
    }

    // ---- panel builders ----

    private VisualElement BuildIdlePanel()
    {
        var panel = new VisualElement();
        panel.style.display = DisplayStyle.None;

        var label = new Label("Start or join a multiplayer game:");
        label.style.color = new Color(0.8f, 0.8f, 0.8f);
        label.style.fontSize = 13;
        label.style.marginBottom = 8;
        panel.Add(label);

        var activeColor = new Color(0.22f, 0.45f, 0.65f);
        var disabledColor = new Color(0.2f, 0.2f, 0.2f);
        var disabledText = new Color(0.4f, 0.4f, 0.4f);

        // host button — no SetEnabled, no AddHoverEffect. pure manual control.
        var hostBtn = new Button(() =>
        {
            Plugin.LogInstance.LogInfo($"[UI] Host button clicked. IsInGame={MainCapture.IsInGame}, IsSchedulerActive={PluginRuntime.IsSchedulerActive}, Scheduler={PluginRuntime.Scheduler != null}");
            if (MainCapture.IsInGame) ShowHostSetup();
            else Plugin.LogInstance.LogInfo("[UI] Host button blocked — not in game.");
        }) { text = "Host Game" };
        hostBtn.style.height = 32;
        hostBtn.style.marginBottom = 6;
        hostBtn.style.fontSize = 13;
        hostBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
        hostBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        hostBtn.style.borderTopWidth = 0;
        hostBtn.style.borderBottomWidth = 0;
        hostBtn.style.borderLeftWidth = 0;
        hostBtn.style.borderRightWidth = 0;
        UIHelpers.SetBorderRadius(hostBtn, 4);
        panel.Add(hostBtn);

        var joinBtn = new Button(() => { if (!MainCapture.IsInGame) OnOpenServerBrowser?.Invoke(); }) { text = "Join Game" };
        joinBtn.style.height = 32;
        joinBtn.style.fontSize = 13;
        joinBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
        joinBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        joinBtn.style.borderTopWidth = 0;
        joinBtn.style.borderBottomWidth = 0;
        joinBtn.style.borderLeftWidth = 0;
        joinBtn.style.borderRightWidth = 0;
        UIHelpers.SetBorderRadius(joinBtn, 4);
        panel.Add(joinBtn);

        // simple poll — just set the colors, nothing else
        bool lastHostState = false;
        bool lastJoinState = false;
        hostBtn.schedule.Execute(() =>
        {
            var canHost = MainCapture.IsInGame;
            if (canHost == lastHostState) return;
            lastHostState = canHost;
            hostBtn.text = canHost ? "Host Game (current world)" : "Host Game (load a save first)";
            hostBtn.style.backgroundColor = canHost ? activeColor : disabledColor;
            hostBtn.style.color = canHost ? Color.white : disabledText;
        }).Every(300);

        joinBtn.schedule.Execute(() =>
        {
            var canJoin = !MainCapture.IsInGame;
            if (canJoin == lastJoinState) return;
            lastJoinState = canJoin;
            joinBtn.text = canJoin ? "Join Game" : "Join Game (exit to menu first)";
            joinBtn.style.backgroundColor = canJoin ? activeColor : disabledColor;
            joinBtn.style.color = canJoin ? Color.white : disabledText;
        }).Every(300);

        // initial state
        hostBtn.style.backgroundColor = disabledColor;
        hostBtn.style.color = disabledText;
        hostBtn.text = "Host Game (load a save first)";
        joinBtn.style.backgroundColor = activeColor;
        joinBtn.style.color = Color.white;

        return panel;
    }

    private VisualElement BuildHostSetupPanel()
    {
        var panel = new VisualElement();
        panel.style.display = DisplayStyle.None;

        var title = new Label("Host Setup — current world");
        title.style.fontSize = 14;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.color = Color.white;
        title.style.marginBottom = 8;
        panel.Add(title);

        _hostNameField = UIHelpers.MakeLabeledField(panel, "Server Name:", "COI Server", 100);
        _hostPasswordField = UIHelpers.MakeLabeledField(panel, "Password:", "", 100);
        _hostPasswordField.isPasswordField = true;

        // port row (only for LAN)
        _hostPortRow = new VisualElement();
        _hostPortRow.style.display = DisplayStyle.None;
        _hostPortField = UIHelpers.MakeLabeledField(_hostPortRow, "Port:", "38455", 100);
        panel.Add(_hostPortRow);

        // transport toggle — two buttons
        var toggleRow = new VisualElement();
        toggleRow.style.flexDirection = FlexDirection.Row;
        toggleRow.style.alignItems = Align.Center;
        toggleRow.style.marginBottom = 8;

        var netLabel = new Label("Network:");
        netLabel.style.color = Color.white;
        netLabel.style.width = 70;
        netLabel.style.fontSize = 13;
        toggleRow.Add(netLabel);

        var activeTabColor = new Color(0.22f, 0.45f, 0.65f);
        var inactiveTabColor = new Color(0.22f, 0.22f, 0.25f);
        var hoverColor = new Color(0.28f, 0.28f, 0.32f);

        // plain buttons — no MakeButton, no AddHoverEffect, manual control
        var steamBtn = new Button() { text = "Steam" };
        steamBtn.style.width = 80;
        steamBtn.style.height = 26;
        steamBtn.style.marginRight = 4;
        steamBtn.style.fontSize = 13;
        steamBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
        steamBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        steamBtn.style.color = Color.white;
        steamBtn.style.backgroundColor = activeTabColor;
        steamBtn.style.borderTopWidth = 0;
        steamBtn.style.borderBottomWidth = 0;
        steamBtn.style.borderLeftWidth = 0;
        steamBtn.style.borderRightWidth = 0;
        UIHelpers.SetBorderRadius(steamBtn, 4);

        var lanBtn = new Button() { text = "LAN" };
        lanBtn.style.width = 60;
        lanBtn.style.height = 26;
        lanBtn.style.fontSize = 13;
        lanBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
        lanBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        lanBtn.style.color = new Color(0.6f, 0.6f, 0.6f);
        lanBtn.style.backgroundColor = inactiveTabColor;
        lanBtn.style.borderTopWidth = 0;
        lanBtn.style.borderBottomWidth = 0;
        lanBtn.style.borderLeftWidth = 0;
        lanBtn.style.borderRightWidth = 0;
        UIHelpers.SetBorderRadius(lanBtn, 4);

        void StyleTransportTabs()
        {
            steamBtn.style.backgroundColor = _useSteam ? activeTabColor : inactiveTabColor;
            steamBtn.style.color = _useSteam ? Color.white : new Color(0.6f, 0.6f, 0.6f);
            lanBtn.style.backgroundColor = _useSteam ? inactiveTabColor : activeTabColor;
            lanBtn.style.color = _useSteam ? new Color(0.6f, 0.6f, 0.6f) : Color.white;
        }

        steamBtn.clicked += () => { _useSteam = true; StyleTransportTabs(); _hostPortRow.style.display = DisplayStyle.None; };
        lanBtn.clicked += () => { _useSteam = false; StyleTransportTabs(); _hostPortRow.style.display = DisplayStyle.Flex; };

        // hover that respects active state
        steamBtn.RegisterCallback<MouseEnterEvent>(evt => { if (!_useSteam) steamBtn.style.backgroundColor = hoverColor; });
        steamBtn.RegisterCallback<MouseLeaveEvent>(evt => StyleTransportTabs());
        lanBtn.RegisterCallback<MouseEnterEvent>(evt => { if (_useSteam) lanBtn.style.backgroundColor = hoverColor; });
        lanBtn.RegisterCallback<MouseLeaveEvent>(evt => StyleTransportTabs());

        toggleRow.Add(steamBtn);
        toggleRow.Add(lanBtn);
        panel.Add(toggleRow);

        // buttons
        var btnRow = new VisualElement();
        btnRow.style.flexDirection = FlexDirection.Row;
        btnRow.style.justifyContent = Justify.SpaceBetween;
        btnRow.style.marginTop = 4;

        var backBtn = UIHelpers.MakeButton("Back", () =>
        {
            _inHostSetup = false;
        }, new Color(0.3f, 0.3f, 0.3f));
        backBtn.style.height = 30;
        backBtn.style.width = 80;
        btnRow.Add(backBtn);

        var startBtn = UIHelpers.MakeButton("Start Hosting", () =>
        {
            var config = new ServerConfig
            {
                ServerName = _hostNameField.value,
                Password = _hostPasswordField.value,
                Port = int.TryParse(_hostPortField.value, out var p) ? p : 38455
            };

            _inHostSetup = false;
            _hostingIsSteam = _useSteam;
            HostPort = config.Port;
            if (_useSteam) OnHostSteam?.Invoke(config);
            else OnHostLan?.Invoke(config);
        }, new Color(0.22f, 0.45f, 0.65f));
        startBtn.style.height = 30;
        startBtn.style.width = 130;
        btnRow.Add(startBtn);

        panel.Add(btnRow);
        return panel;
    }

    private VisualElement BuildHostingPanel()
    {
        var panel = new VisualElement();
        panel.style.display = DisplayStyle.None;

        var hostTitle = new Label("Server is running.");
        hostTitle.style.fontSize = 14;
        hostTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
        hostTitle.style.color = Color.white;
        hostTitle.style.marginBottom = 4;
        panel.Add(hostTitle);

        // steam info — lobby code
        _steamInfoContainer = new VisualElement();

        var codeTitle = new Label("Lobby Code (click to copy):");
        codeTitle.style.fontSize = 12;
        codeTitle.style.color = new Color(0.7f, 0.7f, 0.7f);
        _steamInfoContainer.Add(codeTitle);

        _lobbyCodeLabel = new Label("");
        _lobbyCodeLabel.style.fontSize = 13;
        _lobbyCodeLabel.style.color = new Color(0.5f, 0.8f, 1f);
        _lobbyCodeLabel.style.marginBottom = 8;
        _lobbyCodeLabel.style.backgroundColor = new Color(0.08f, 0.08f, 0.10f, 1f);
        _lobbyCodeLabel.style.paddingLeft = 8;
        _lobbyCodeLabel.style.paddingRight = 8;
        _lobbyCodeLabel.style.paddingTop = 4;
        _lobbyCodeLabel.style.paddingBottom = 4;
        UIHelpers.SetBorderRadius(_lobbyCodeLabel, 4);
        _lobbyCodeLabel.RegisterCallback<ClickEvent>(evt =>
        {
            var code = LobbyCode;
            if (!string.IsNullOrEmpty(code))
            {
                GUIUtility.systemCopyBuffer = code;
                _lobbyCodeLabel.text = "Copied!";
                _lobbyCodeLabel.schedule.Execute(() =>
                {
                    _lobbyCodeLabel.text = code;
                }).ExecuteLater(1000);
            }
        });
        UIHelpers.AddHoverEffect(_lobbyCodeLabel);
        _steamInfoContainer.Add(_lobbyCodeLabel);
        panel.Add(_steamInfoContainer);

        // lan info — IP and port
        _lanInfoContainer = new VisualElement();
        _lanInfoContainer.style.display = DisplayStyle.None;

        _lanInfoLabel = new Label("");
        _lanInfoLabel.style.fontSize = 13;
        _lanInfoLabel.style.color = new Color(0.5f, 0.8f, 1f);
        _lanInfoLabel.style.marginBottom = 8;
        _lanInfoLabel.style.whiteSpace = WhiteSpace.Normal;
        _lanInfoContainer.Add(_lanInfoLabel);
        panel.Add(_lanInfoContainer);

        _playerListLabel = new Label("");
        _playerListLabel.style.fontSize = 12;
        _playerListLabel.style.color = new Color(0.8f, 0.8f, 0.8f);
        _playerListLabel.style.whiteSpace = WhiteSpace.Normal;
        _playerListLabel.style.marginBottom = 8;
        panel.Add(_playerListLabel);

        var stopBtn = UIHelpers.MakeButton("Stop Hosting", () => OnStopHosting?.Invoke(), new Color(0.5f, 0.2f, 0.2f));
        stopBtn.style.height = 30;
        panel.Add(stopBtn);

        return panel;
    }

    private VisualElement BuildConnectingPanel()
    {
        var panel = new VisualElement();
        panel.style.display = DisplayStyle.None;

        var title = new Label("Connecting...");
        title.style.fontSize = 14;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.color = Color.white;
        title.style.marginBottom = 4;
        panel.Add(title);

        _connectStatusLabel = new Label("");
        _connectStatusLabel.style.fontSize = 12;
        _connectStatusLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
        _connectStatusLabel.style.whiteSpace = WhiteSpace.Normal;
        _connectStatusLabel.style.marginBottom = 8;
        panel.Add(_connectStatusLabel);

        var cancelBtn = UIHelpers.MakeButton("Cancel", () => OnCancelConnect?.Invoke(), new Color(0.5f, 0.2f, 0.2f));
        cancelBtn.style.height = 30;
        panel.Add(cancelBtn);

        return panel;
    }

    private VisualElement BuildConnectedPanel()
    {
        var panel = new VisualElement();
        panel.style.display = DisplayStyle.None;

        var title = new Label("Connected to server.");
        title.style.fontSize = 14;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.color = Color.white;
        title.style.marginBottom = 4;
        panel.Add(title);

        _connectedAsLabel = new Label("");
        _connectedAsLabel.style.fontSize = 12;
        _connectedAsLabel.style.color = new Color(0.8f, 0.8f, 0.8f);
        _connectedAsLabel.style.marginBottom = 8;
        panel.Add(_connectedAsLabel);

        var disconnectBtn = UIHelpers.MakeButton("Disconnect", () => OnDisconnect?.Invoke(), new Color(0.5f, 0.2f, 0.2f));
        disconnectBtn.style.height = 30;
        panel.Add(disconnectBtn);

        return panel;
    }

    // set from Plugin.cs so we don't need to reach into the bootstrap
    public string? LobbyCode { get; set; }

    // stored from host setup so we know the port for LAN display
    public int HostPort { get; set; } = 38455;

    private void UpdateHostingPanel(MultiplayerSession session)
    {
        _steamInfoContainer.style.display = _hostingIsSteam ? DisplayStyle.Flex : DisplayStyle.None;
        _lanInfoContainer.style.display = _hostingIsSteam ? DisplayStyle.None : DisplayStyle.Flex;

        if (_hostingIsSteam)
        {
            _lobbyCodeLabel.text = LobbyCode ?? "Creating lobby...";
        }
        else
        {
            try
            {
                var localIp = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                var ip = "127.0.0.1";
                foreach (var addr in localIp.AddressList)
                {
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        ip = addr.ToString();
                        break;
                    }
                }
                _lanInfoLabel.text = $"IP: {ip}\nPort: {HostPort}\nShare these with your friends to connect.";
            }
            catch
            {
                _lanInfoLabel.text = $"Port: {HostPort}";
            }
        }

        // player list
        var peers = session.ActivePeers;
        var pending = session.PendingPeers;
        var lines = $"Connected players: {peers.Count}";
        foreach (var peer in peers)
            lines += $"\n  - {GetSteamName(peer)}";
        if (pending.Count > 0)
        {
            lines += $"\nJoining: {pending.Count}";
            foreach (var peer in pending)
                lines += $"\n  - {GetSteamName(peer)} (syncing...)";
        }
        _playerListLabel.text = lines;
    }

    private void UpdateConnectedPanel(MultiplayerSession session)
    {
        _connectedAsLabel.text = $"Playing as: {GetSteamName(session.LocalPeerId)}";
    }

    private static string GetSteamName(string peerId)
    {
        try
        {
            if (ulong.TryParse(peerId, out var steamIdValue))
            {
                var friend = new Friend(new SteamId { Value = steamIdValue });
                if (!string.IsNullOrEmpty(friend.Name))
                    return $"{friend.Name} ({peerId})";
            }
        }
        catch { }
        return peerId;
    }
}
