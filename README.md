# SharedQuests

A mod for **SPT 4.0** that displays quest/task status for all player profiles on your server.

When viewing any quest description, you'll see a status summary showing where each profile stands on that quest - perfect for coordinating progress with friends on a shared SPT server.

## Features

- **Shared Quest Status** - See all profiles' quest status directly in quest descriptions
- **Color-coded statuses** - Easy visual identification (works in Trader menu)
- **Automatic filtering** - Headless/bot profiles are automatically hidden
- **Rich text support** - Client mod enables colored text in Character → Tasks menu

## Screenshots

Quest descriptions show status for all profiles:

```
--- Shared Quest Status ---
Marklar: Locked
JohnGoob: Started
--------------------------

[Original quest description...]
```

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

- Runs on SPT server startup
- Collects quest status for all player profiles
- Prepends status information to quest descriptions in the locale database
- Updates apply to all languages automatically

### Client (`SharedQuests`)

- BepInEx plugin that runs in-game
- Patches the Character → Tasks UI to enable rich text rendering
- Allows color tags to display properly in that menu

## Quest Status Legend

| Status    | Color  | Description                 |
| --------- | ------ | --------------------------- |
| Locked    | Gray   | Quest prerequisites not met |
| Available | Gold   | Ready to start              |
| Started   | Orange | Quest in progress           |
| Ready!    | Green  | Ready to turn in            |
| Completed | Green  | Quest finished              |
| Failed    | Red    | Quest failed                |

## Known Limitations

- Quest status is captured at server startup. If a player completes a quest, the status won't update until the server restarts.
- The Trader menu supports rich text natively; the Character → Tasks menu requires the client mod for colors.

## License

MIT License - see [LICENSE](LICENSE) file.

## Credits

- Inspired by [ExpandedTaskText](https://github.com/c-j-s/ExpandedTaskText) for the locale modification approach
- Thanks to the SPT modding community for reference implementations
