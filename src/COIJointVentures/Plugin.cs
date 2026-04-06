using BepInEx;
using BepInEx.Logging;
using COIJointVentures.Integration;
using COIJointVentures.Logging;
using COIJointVentures.Patches;
using COIJointVentures.Runtime;
using COIJointVentures.Session;
using COIJointVentures.UI;
using COIJointVentures.Waypoints;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using Mafi.Core.Input;
using UnityEngine;

namespace COIJointVentures;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[DefaultExecutionOrder(-10000)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "coi.multiplayer";
    public const string PluginName = "COI: Joint Ventures";
    // PluginVersion is generated at build time — see VersionConst in COIJointVentures.csproj
    public const string PluginVersion = VersionConst.Value;

    internal static ManualLogSource LogInstance { get; private set; } = null!;

    private Harmony? _harmony;
    private MultiplayerBootstrap? _bootstrap;
    private ModUIManager? _uiManager;
    private ServerBrowserUI? _serverBrowser;
    private MainPanelUI? _mainPanel;
    private ChatPanelUI? _chatPanel;
    private JoinOverlayUI? _joinOverlay;



    private bool _wasInGame;
    private WaypointManager? _waypoints;
    private MultiplayerSession? _hookedSession;
    private float _lastWaypointTime;
    private bool _consumeNextClick;

    private void Awake()
    {
        try
        {
            LogInstance = Logger;

            _bootstrap = new MultiplayerBootstrap(Logger);
            _bootstrap.Initialize();
            var pluginDirectory = Path.Combine(Paths.PluginPath, "COIJointVentures");
            var observedLogPath = Path.Combine(pluginDirectory, "observed-commands.log");
            var replicatedLogPath = Path.Combine(pluginDirectory, "replicated-commands.log");
            PluginRuntime.Initialize(
                Logger,
                _bootstrap.Session,
                new RuntimeCommandLog(Logger, observedLogPath),
                new RuntimeReplicationLog(Logger, replicatedLogPath),
                new NativeCommandCodec(),
                _bootstrap.SaveManager);

            _harmony = new Harmony(PluginGuid);
            MainCapture.TryApplyPatch(_harmony, Logger);

            var processCommandsMethod = CoiReflection.FindInputSchedulerProcessCommandsMethod();
            if (processCommandsMethod != null)
            {
                var prefix = typeof(CommandInterceptionPatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
                if (prefix != null)
                {
                    _harmony.Patch(processCommandsMethod, prefix: new HarmonyMethod(prefix));
                    Logger.LogInfo("Applied Harmony prefix patch to InputScheduler.ProcessCommands.");
                }
            }

            InitUI();

            Logger.LogInfo("COI: Joint Ventures bootstrap complete.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"COI: Joint Ventures failed during Awake: {ex}");
            throw;
        }
    }

    private void InitUI()
    {
        _uiManager = new ModUIManager();
        _uiManager.Initialize();

        // server browser
        _serverBrowser = new ServerBrowserUI();
        _serverBrowser.OnJoinSteam += (lobbyId, pw) =>
            RunAction(() => _bootstrap!.JoinSteamLobby(lobbyId, pw), "join steam server");
        _serverBrowser.OnJoinLan += (ip, port, pw) =>
        {
            var name = _bootstrap!.IsSteamAvailable ? _bootstrap.SteamPlayerName : "Player";
            RunAction(() => _bootstrap!.JoinGame(name, ip, port, pw), "join LAN server");
        };
        // main panel (F8) — added before server browser so browser renders on top
        _mainPanel = new MainPanelUI();
        _mainPanel.OnOpenServerBrowser += () => _serverBrowser.Show();
        _mainPanel.OnHostSteam += config =>
            RunAction(() => _bootstrap!.HostSteamGame(config), "host steam game");
        _mainPanel.OnHostLan += config =>
            RunAction(() => _bootstrap!.HostGame(config), "host LAN game");
        _mainPanel.OnStopHosting += () =>
        {
            RunAction(() =>
            {
                _waypoints?.Clear();
                _bootstrap!.Disconnect();
            }, "stop hosting");
        };
        _mainPanel.OnDisconnect += () =>
        {
            RunAction(() =>
            {
                _waypoints?.Clear();
                _bootstrap!.Disconnect();
                MainCapture.TryGoToMainMenu();
            }, "disconnect");
        };
        _mainPanel.OnCancelConnect += () =>
        {
            RunAction(() => _bootstrap!.Disconnect(), "cancel connection");
        };
        _uiManager.AddElement(_mainPanel.Root);

        // server browser — added after main panel so it renders on top
        _uiManager.AddElement(_serverBrowser.Root);

        // chat panel (F9)
        _chatPanel = new ChatPanelUI();
        _uiManager.AddElement(_chatPanel.Root);

        // join overlay (full screen blocking)
        _joinOverlay = new JoinOverlayUI();
        _uiManager.AddElement(_joinOverlay.Root);
    }

    private void OnDestroy()
    {
        _bootstrap?.Dispose();
        _harmony?.UnpatchSelf();
        _uiManager?.Dispose();
    }

    private void Update()
    {
        // waypoint check FIRST — if Ctrl+Click, ResetInputAxes before
        // the game's input controllers run later this frame
        HandleWaypointInput();
        _waypoints?.Update();

        // auto-show/hide chat HUD based on session state
        if (_chatPanel != null)
        {
            var inSession = _bootstrap != null &&
                (_bootstrap.Session.Mode == MultiplayerMode.Host || _bootstrap.Session.Mode == MultiplayerMode.Client);
            _chatPanel.Visible = inSession;
        }

        // Enter/F9 opens chat input, Escape closes it
        if (_chatPanel != null && _chatPanel.Visible)
        {
            if ((Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.F9))
                && !_chatPanel.InputOpen)
            {
                _chatPanel.OpenInput();
            }
            else if (Input.GetKeyDown(KeyCode.Escape) && _chatPanel.InputOpen)
            {
                _chatPanel.CloseInput();
                Input.ResetInputAxes();
            }
        }

        if (Input.GetKeyDown(KeyCode.F8) && _mainPanel != null)
        {
            _mainPanel.Visible = !_mainPanel.Visible;
        }

        _bootstrap?.PollTransport();

        if (_bootstrap != null)
        {
            var session = _bootstrap.Session;
            var inGame = MainCapture.IsInGame;

            // host dropped us — always try to go back to menu
            if (session.HostDisconnected)
            {
                Logger.LogInfo("Host disconnected — returning to main menu.");
                PluginRuntime.Chat.AddSystem("Host disconnected. Session ended.");
                _waypoints?.Clear();
                _bootstrap.Disconnect();
                MainCapture.TryGoToMainMenu();
            }

            // left the game while in a session
            if (_wasInGame && !inGame && (session.Mode == MultiplayerMode.Host || session.Mode == MultiplayerMode.Client))
            {
                Logger.LogInfo("Left game while in multiplayer session — disconnecting.");
                _waypoints?.Clear();
                _bootstrap.Disconnect();
            }

            _wasInGame = inGame;
        }

        // update panels
        _mainPanel?.Update();
        _chatPanel?.Update();
        _joinOverlay?.Update(_bootstrap);

        if (_mainPanel != null)
        {
            _mainPanel.LobbyCode = _bootstrap?.LobbyCode;
        }
    }

    // main menu hint — keep this as OnGUI since it's trivial
    private void OnGUI()
    {
        // consume the click event that placed a waypoint so the game
        // doesn't also open the entity interaction menu
        if (_consumeNextClick && Event.current != null)
        {
            if (Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseUp)
            {
                Event.current.Use();
            }

            // clear after we've consumed both down and up
            if (Event.current.type == EventType.Repaint || Event.current.type == EventType.Layout)
            {
                _consumeNextClick = false;
            }
        }

        // off-screen waypoint arrows
        _waypoints?.DrawOffScreenIndicators();

        if (_bootstrap == null) return;

        if (_mainPanel != null && !_mainPanel.Visible && !MainCapture.IsInGame && _bootstrap.Session.Mode == MultiplayerMode.None)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 1f, 1f, 0.7f) }
            };
            GUI.Label(new Rect(10, Screen.height - 35, 350, 30), "Press F8 for Joint Ventures (Multiplayer)", style);
        }
    }

    private void HandleWaypointInput()
    {
        if (_bootstrap == null) return;
        var session = _bootstrap.Session;
        if (session.Mode == MultiplayerMode.None) return;

        // hook the session event if it changed
        if (session != _hookedSession)
        {
            if (_hookedSession != null) _hookedSession.WaypointReceived -= OnWaypointReceived;
            session.WaypointReceived += OnWaypointReceived;
            _hookedSession = session;
            _waypoints ??= new WaypointManager();
        }

        // ctrl+click while in game — 1 second cooldown
        if (!Input.GetMouseButtonDown(0) || !Input.GetKey(KeyCode.LeftControl)) return;
        if (!MainCapture.IsInGame) return;
        if (Time.time - _lastWaypointTime < 1f) return;

        var cam = Camera.main;
        if (cam == null) return;

        var ray = cam.ScreenPointToRay(Input.mousePosition);
        Vector3? worldPos = null;

        if (Physics.Raycast(ray, out var hit, 2000f))
        {
            worldPos = hit.point;
        }
        else
        {
            // terrain has no physics collider — intersect a plane to get
            // approximate XZ, query terrain height, then re-intersect at the
            // real height so the XZ lines up with where the cursor points
            var plane = new Plane(Vector3.up, Vector3.zero);
            if (plane.Raycast(ray, out var dist))
            {
                var approx = ray.GetPoint(dist);
                var terrainY = Waypoints.TerrainHeightQuery.GetHeightAtWorldXZ(approx.x, approx.z, Logger);
                if (terrainY.HasValue)
                {
                    // re-intersect the ray with a plane at the actual terrain height
                    var correctedPlane = new Plane(Vector3.up, new Vector3(0f, terrainY.Value, 0f));
                    if (correctedPlane.Raycast(ray, out var correctedDist))
                    {
                        var corrected = ray.GetPoint(correctedDist);
                        // query height again at the corrected XZ for precision
                        var finalY = Waypoints.TerrainHeightQuery.GetHeightAtWorldXZ(corrected.x, corrected.z, Logger);
                        worldPos = new Vector3(corrected.x, finalY ?? terrainY.Value, corrected.z);
                    }
                    else
                    {
                        worldPos = new Vector3(approx.x, terrainY.Value, approx.z);
                    }
                }
                else
                {
                    worldPos = approx;
                }
            }
        }

        if (worldPos.HasValue)
        {
            _lastWaypointTime = Time.time;
            _consumeNextClick = true;
            session.SendWaypoint(worldPos.Value.x, worldPos.Value.y, worldPos.Value.z);

            // nuke all input state so later Update() calls in this frame
            // don't see the mouse click
            Input.ResetInputAxes();
        }
    }

    private void OnWaypointReceived(Networking.Protocol.WaypointPayload wp)
    {
        _waypoints?.Spawn(wp);
        PluginRuntime.Chat.AddAction(wp.SenderName, "placed a waypoint");
    }

    private void RunAction(Action action, string description)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Failed to {description}: {ex.Message}");
        }
    }
}
