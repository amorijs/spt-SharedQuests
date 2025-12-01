# SharedQuests Development Summary

## Project Overview

**SharedQuests** is a mod for SPT Tarkov 4.0 that displays quest status for all player profiles on the server when viewing quest descriptions in the Character → Tasks menu.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        SPT Server                            │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │  SharedQuestsBackend.dll                                │ │
│  │  • Reads profile JSON files directly from disk          │ │
│  │  • Exposes /sharedquests/statuses HTTP endpoint         │ │
│  │  • Returns real-time quest status for all profiles      │ │
│  └─────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
                              │
                              │ HTTP GET /sharedquests/statuses
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                        Game Client                           │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │  SharedQuests.dll (BepInEx Plugin)                      │ │
│  │  • Fetches statuses from server on startup & menu open  │ │
│  │  • Monitors DescriptionLabel in Tasks screen            │ │
│  │  • Injects formatted status text into quest description │ │
│  │  • F12 config menu for profile visibility               │ │
│  └─────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

## Key Features Implemented

### 1. Server-Side (SharedQuestsBackend)

- **Direct disk reading**: Reads `user/profiles/*.json` files directly instead of using cached profile data
- **Real-time data**: Always returns current quest statuses, not stale cached data
- **Profile filtering**: Automatically excludes profiles starting with `headless_`
- **HTTP endpoint**: `/sharedquests/statuses` returns JSON with all profile quest statuses

### 2. Client-Side (SharedQuests)

- **Continuous monitoring**: Uses coroutine to monitor DescriptionLabel every 0.1s while Tasks screen is open
- **Smart quest detection**: Walks up UI hierarchy to find current quest from QuestClass fields
- **Rich text rendering**: Color-coded status display with proper Unity TextMeshPro support
- **Forced visual refresh**: Uses ForceMeshUpdate(), SetAllDirty(), LayoutRebuilder, Canvas.ForceUpdateCanvases()

### 3. F12 Configuration Menu

- **Enable/Disable toggle**: Turn the entire mod on/off
- **Dynamic profile checkboxes**: Created automatically when profiles are fetched from server
- **Exclusion persistence**: Stores excluded profiles in BepInEx config file
- **Read-only exclusion display**: Shows which profiles are currently excluded

## Technical Challenges Solved

### Challenge 1: Stale Profile Data

**Problem**: Server's in-memory profile cache wasn't updated when profiles saved and exited.
**Solution**: Read profile JSON files directly from disk instead of using `profileHelper.GetPmcProfile()`.

### Challenge 2: Text Being Overwritten

**Problem**: Game UI kept overwriting our injected text.
**Solution**: Continuous monitoring with repeated injection attempts over 2 seconds with variable check frequency.

### Challenge 3: Wrong Quest Status Displayed

**Problem**: When switching quests, old quest's status was shown.
**Solution**: Walk up UI hierarchy from DescriptionLabel to find QuestClass field containing current quest.

### Challenge 4: Rich Text Not Rendering

**Problem**: Character → Tasks menu didn't render rich text colors.
**Solution**: Enable `richText = true` on all TextMeshProUGUI components when screen opens.

### Challenge 5: F12 Menu Not Populated Initially

**Problem**: Profile checkboxes only appeared after opening Tasks menu.
**Solution**: Added initial fetch 2 seconds after game start.

## File Structure

```
shared-quests/
├── Client/
│   ├── SharedQuests.cs      # Main client plugin (BepInEx)
│   ├── SharedQuests.csproj  # Client project file (.NET Standard 2.1)
│   └── Settings.cs          # F12 configuration menu settings
├── Server/
│   ├── SharedQuestsBackend.cs    # Server mod (HTTP endpoint)
│   └── SharedQuestsBackend.csproj # Server project file (.NET 9.0)
├── dist/                    # Build output (gitignored)
│   ├── BepInEx/plugins/SharedQuests/
│   └── SPT/user/mods/SharedQuestsBackend/
├── SharedQuests.sln         # Visual Studio solution
├── README.md                # User documentation
├── LICENSE                  # License file
└── .gitignore              # Git ignore rules
```

## Quest Status Display Format

```
--- Shared Quest Status ---
Marklar: Started
JohnGoob: Completed
clinicallylazy: Available
--------------------------

[Original quest description follows...]
```

## Status Colors

| Status         | Color      | Hex     |
| -------------- | ---------- | ------- |
| Locked         | Gray       | #808080 |
| Available      | Gold       | #FFD700 |
| Started        | Orange     | #FFA500 |
| Ready!         | Green      | #00FF00 |
| Completed      | Lime       | #32CD32 |
| Failed         | Red        | #FF4444 |
| Failed (Retry) | Orange-Red | #FF6600 |
| Expired        | Dark Gray  | #666666 |
| Timed          | Sky Blue   | #87CEEB |

## Known Limitations

1. **Character → Tasks only**: Currently only works in the Character → Tasks menu, not Traders → Tasks
2. **Requires both mods**: Server and client mods must both be installed
3. **Profile save timing**: Status updates when profile saves (usually on game exit)

## Dependencies

### Server

- SPTarkov.Common 4.0.0
- SPTarkov.DI 4.0.0
- SPTarkov.Server.Core 4.0.0

### Client

- BepInEx
- 0Harmony
- Assembly-CSharp (EFT)
- Unity.TextMeshPro
- UnityEngine.UI / UIModule
- SPT.Common (RequestHandler)
- Newtonsoft.Json

## Build Commands

```bash
# Build entire solution
dotnet build SharedQuests.sln

# Build server only
dotnet build Server/SharedQuestsBackend.csproj

# Build client only
dotnet build Client/SharedQuests.csproj
```

## Configuration File Location

`BepInEx/config/com.sharedquests.client.cfg`

## Future Improvements (Deferred)

- [ ] Support for Traders → Tasks screen
- [ ] Event-based updates instead of polling
- [ ] In-game refresh button
- [ ] Per-quest profile visibility
