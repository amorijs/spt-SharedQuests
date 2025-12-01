# SharedQuests

A mod for **SPT 4.0** that displays quest/task status for all player profiles on your server.

When viewing any quest description in the Character → Tasks menu, you'll see a status summary showing where each profile stands on that quest - perfect for coordinating progress with friends on a shared SPT server.

## Features

- **Real-time Quest Status** - See all profiles' quest status directly in quest descriptions
- **Live Updates** - Status updates without server restart (reads from disk on each request)
- **Color-coded Statuses** - Easy visual identification with rich text colors
- **F12 Configuration Menu** - Enable/disable profiles from showing in the status display
- **Automatic Filtering** - Headless/bot profiles are automatically hidden
- **Persistent Settings** - Your profile visibility preferences are saved

## Screenshot

Quest descriptions show status for all profiles:

<img width="2450" height="627" alt="image" src="https://github.com/user-attachments/assets/c4ccb3a8-86a4-43ef-bc2f-a22126e91bed" />

Config menu:

<img width="612" height="190" alt="image" src="https://github.com/user-attachments/assets/b20753ad-3f7b-4564-8575-cf34d62602a6" />


## Installation

### Requirements

- SPT 4.0.x
- Both server and client mods must be installed

### Server Mod

Copy `SharedQuestsBackend.dll` to:

```
SPT/user/mods/SharedQuestsBackend/
```

### Client Mod

Copy `SharedQuests.dll` to:

```
BepInEx/plugins/SharedQuests/
```

## Configuration

Press **F12** in-game to access the configuration menu:

- **Enable SharedQuests** - Toggle the entire mod on/off
- **Profile Visibility** - Check/uncheck profiles to show/hide them in the status display
- **Excluded Profiles** - Read-only field showing currently excluded profile names

Settings are saved to `BepInEx/config/com.sharedquests.client.cfg`

## Building from Source

### Prerequisites

- .NET 9.0 SDK (for Server)
- .NET SDK with netstandard2.1 support (for Client)
- SPT 4.0 installation (for reference DLLs)

### Configuration

Update `$(SPTPath)` in both `.csproj` files to your SPT installation path.

### Build Commands

```bash
# Build entire solution
dotnet build SharedQuests.sln

# Build server only
dotnet build Server/SharedQuestsBackend.csproj

# Build client only
dotnet build Client/SharedQuests.csproj
```

### Build Output

```
dist/
├── BepInEx/plugins/SharedQuests/
│   └── SharedQuests.dll
│
└── SPT/user/mods/SharedQuestsBackend/
    └── SharedQuestsBackend.dll
```

## How It Works

### Server (`SharedQuestsBackend`)

- Exposes HTTP endpoint `/sharedquests/statuses`
- Reads profile JSON files directly from disk (not cached data)
- Returns real-time quest status for all profiles
- Automatically filters out `headless_*` profiles

### Client (`SharedQuests`)

- BepInEx plugin that runs in-game
- Fetches quest statuses from server on startup and when opening Tasks menu
- Monitors the quest description panel and injects status text
- Detects quest changes by walking the UI hierarchy to find current quest
- Provides F12 configuration menu for profile visibility

## Quest Status Legend

| Status         | Color      | Description                 |
| -------------- | ---------- | --------------------------- |
| Locked         | Gray       | Quest prerequisites not met |
| Available      | Gold       | Ready to start              |
| Started        | Orange     | Quest in progress           |
| Ready!         | Green      | Ready to turn in            |
| Completed      | Lime       | Quest finished successfully |
| Failed         | Red        | Quest failed                |
| Failed (Retry) | Orange-Red | Quest failed, can retry     |
| Expired        | Dark Gray  | Quest expired               |
| Timed          | Sky Blue   | Time-gated quest            |

## Known Limitations

- **Character → Tasks only** - Currently only works in the Character → Tasks menu, not Traders → Tasks
- **Profile save timing** - Status updates when profiles save to disk (usually on game exit)

## License

DO WHATEVER YOU WANT License - see [LICENSE](LICENSE) file.

## Credits

- Inspired by [ExpandedTaskText](https://github.com/c-j-s/ExpandedTaskText) for locale modification concepts
- Thanks to the SPT modding community for reference implementations
