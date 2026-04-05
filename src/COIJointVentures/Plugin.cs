using BepInEx;
using BepInEx.Logging;
using COIJointVentures.Integration;
using COIJointVentures.Logging;
using COIJointVentures.Patches;
using COIJointVentures.Runtime;
using COIJointVentures.Session;
using COIJointVentures.UI;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using Mafi.Core.Input;
using UnityEngine;

namespace COIJointVentures;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
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

    private bool _showMainPanel;
    private bool _showChatPanel;
    private bool _wasInGame;

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
            RunAction(() => _bootstrap!.Disconnect(), "stop hosting");
        };
        _mainPanel.OnDisconnect += () =>
        {
            RunAction(() => _bootstrap!.Disconnect(), "disconnect");
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
        if (Input.GetKeyDown(KeyCode.F9))
        {
            var inSession = _bootstrap != null &&
                (_bootstrap.Session.Mode == MultiplayerMode.Host || _bootstrap.Session.Mode == MultiplayerMode.Client);
            if (inSession)
            {
                _showChatPanel = !_showChatPanel;
                if (_chatPanel != null) _chatPanel.Visible = _showChatPanel;
            }
        }

        // hide chat if session ends
        if (_chatPanel != null && _chatPanel.Visible && _bootstrap != null &&
            _bootstrap.Session.Mode == MultiplayerMode.None)
        {
            _showChatPanel = false;
            _chatPanel.Visible = false;
        }

        if (Input.GetKeyDown(KeyCode.F8))
        {
            _showMainPanel = !_showMainPanel;
            if (_mainPanel != null) _mainPanel.Visible = _showMainPanel;
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
                _bootstrap.Disconnect();

                // always try GoToMainMenu — don't check IsInGame because the
                // scheduler might not be ticking (game paused) but we're still
                // in a save that needs to be exited
                if (MainCapture.HasMain)
                {
                    try
                    {
                        var mainInst = MainCapture.MainInstance;
                        var goToMenu = mainInst?.GetType().GetMethod("GoToMainMenu",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (goToMenu != null)
                        {
                            var argsType = goToMenu.GetParameters()[0].ParameterType;
                            var defaultArgs = Activator.CreateInstance(argsType);
                            goToMenu.Invoke(mainInst, new[] { defaultArgs });
                            Logger.LogInfo("GoToMainMenu invoked.");
                        }
                        else
                        {
                            Logger.LogWarning("GoToMainMenu method not found.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"Failed to return to menu: {ex.Message}");
                    }
                }
                else
                {
                    Logger.LogWarning("MainCapture.HasMain is false — can't navigate to menu.");
                }
            }

            // left the game while in a session
            if (_wasInGame && !inGame && (session.Mode == MultiplayerMode.Host || session.Mode == MultiplayerMode.Client))
            {
                Logger.LogInfo("Left game while in multiplayer session — disconnecting.");
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
        if (_bootstrap == null) return;

        if (!_showMainPanel && !MainCapture.IsInGame && _bootstrap.Session.Mode == MultiplayerMode.None)
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
