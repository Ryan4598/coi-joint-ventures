# COI: Joint Ventures

A work-in-progress multiplayer mod for [Captain of Industry](https://store.steampowered.com/app/1594320/Captain_of_Industry/) that lets you play co-op on a shared island with friends.

> **⚠️ Important disclaimer:** This mod hooks into core game systems to intercept and replicate gameplay commands between players. This means it can cause crashes, unexpected behavior, or save corruption - that's the nature of modding internal game systems that were never designed for multiplayer. **If you experience any issues while this mod is installed, please remove it before reporting bugs to the COI developers.** Always test without mods first. Don't send MaFi Games bug reports for issues caused by mods - it wastes their time and makes the modding community look bad.

## What it does

One player hosts their current save, and friends connect via Steam (or LAN). The host's game is authoritative - all actions (building placement, speed changes, research, vehicle assignments, etc.) are intercepted, serialized, and replicated to connected clients in real-time.

### What works

- **Hosting & joining via Steam** - no port forwarding needed, uses Valve's relay network
- **Server browser** - browse all servers or filter to friends, join by clicking a server in the list
- **Join by lobby code** - share a code with friends who can paste it to connect
- **Save sync** - when someone joins, the game pauses for everyone, the host creates a fresh save, and transfers it to the joining player automatically
- **Command replication** - most gameplay commands are synced between players (building, research, speed controls, vehicle assignments, fleet management, terrain, etc.)
- **In-game chat** - F7 opens a chat panel with player messages and an activity feed showing what everyone is doing (e.g. "Ryan placed Storage Fluid T2")
- **Session management** - if the host exits to menu, all clients are disconnected and sent back to menu. Clients can't load saves while connected.

### What hasn't really been tested

- **LAN support** - direct connect via IP/port is implemented but barely tested. Steam is the primary path.

### What doesn't work (yet)

- Some commands don't replicate correctly across all players
- No desync detection or recovery yet - if the game states diverge, you'll need to rejoin
- Camera and UI-only state aren't synced (each player has their own view)
- This is very much alpha - expect rough edges

## Installation

### Option 1: Launcher (recommended)

The launcher is a single EXE that handles everything for you. It temporarily injects the mod when you run it and cleans up when the game closes, so the mod is only active when you actually want to play multiplayer. Your vanilla game is never permanently modified.

1. Download `JointVentures.exe` from the [latest release](https://github.com/Ryan4598/coi-joint-ventures/releases/latest)
2. Put it anywhere you like
3. Run it instead of launching COI directly

On first run it'll download BepInEx automatically. After that it just launches straight into the game with the mod loaded. When you close the game, everything gets cleaned up.

If you want to play vanilla, just launch COI normally through Steam - the mod won't be there.

### Option 2: Manual install (permanent)

If you'd rather have the mod always loaded, you can install it the traditional BepInEx way. Note that the mod will be active every time you launch the game until you manually remove it.

**Requirements:**
- **Captain of Industry** on Steam
- **BepInEx 5.x** (the .NET Framework/Mono build - **not** BepInEx 6.x)
  - Download from [BepInEx releases](https://github.com/BepInEx/BepInEx/releases) - grab `BepInEx_win_x64_5.x.x.zip`
  - Extract into your COI game folder (where `Captain of Industry.exe` lives)
  - Run the game once and close it so BepInEx generates its folder structure

**Install the mod:**

1. Download `COIJointVentures.dll` from the [latest release](https://github.com/Ryan4598/coi-joint-ventures/releases/latest)
2. Put it in: `<your COI install>/BepInEx/plugins/COIJointVentures/COIJointVentures.dll`
3. Launch the game

### Verify it's working

- You should see `[COI: Joint Ventures]` messages in `BepInEx/LogOutput.log`
- On the main menu you'll see "Press F8 for Joint Ventures (Multiplayer)" at the bottom left
- Press **F8** to open the multiplayer panel

## How to play

### Hosting

1. Load a save game normally
2. Press **F8** to open the multiplayer panel
3. Click **Host Game (current world)** (only available while in a save)
4. Set a server name and optional password
5. Choose **Steam** or **LAN**
6. Click **Start Hosting**
7. For Steam: share the **lobby code** (click it to copy) with friends, or they can find you in the server browser
8. For LAN: share the **IP and port** shown in the panel

### Joining

1. From the **main menu** (not in a save), press **F8**
2. Click **Join Game** to open the server browser
3. Three ways to connect:
   - **Server browser** - your friends' servers show up in the list. Click one, then click Connect
   - **Join by Code** - paste a lobby code from the host
   - **Connect LAN** - enter IP, port, and password for direct connection
4. The host's game will pause, create a fresh save, and transfer it to you automatically

### Controls

- **F9** - Chat & activity log (only available while in a session)
- **F8** - Multiplayer panel (host/join/status)

### Notes

- The host can stop hosting from the F8 panel
- If the host exits to menu or quits, everyone gets disconnected and sent back to menu
- Clients can't load a different save while connected - disconnect first
- Rejoining after a disconnect works but you get a fresh save from the host
- When someone joins mid-game, the world pauses for everyone until the new player is synced

## Building from source

If you want to build from source:

```
dotnet build src\COIJointVentures\COIJointVentures.csproj
```

You'll need COI installed. Update `CaptainOfIndustryDir` in the `.csproj` if your game is installed somewhere other than the default path. The build auto-deploys the DLL to `BepInEx/plugins/COIJointVentures/`.

## Troubleshooting

- **Mod doesn't load** - make sure you have BepInEx 5.x (not 6.x) installed correctly. Check `BepInEx/LogOutput.log` for errors.
- **Can't connect** - both players need the mod installed. Make sure Steam is running for Steam connections.
- **Game crashes** - check the log, remove the mod, and see if the crash reproduces without it. If it only happens with the mod, that's on us, not COI.
- **Things look out of sync** - rejoin. Desync recovery isn't implemented yet.
- **Host button is grayed out** - you need to be in a save to host. Load a save first.
- **Join button is grayed out** - you need to be on the main menu to join. Exit your current save first.
