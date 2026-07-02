# Quest Planner — Design

**Date:** 2026-07-02
**Status:** Approved (Approach A)

## Problem

SharedQuests currently shows all profiles' status for one quest at a time, inside that quest's description in Character → Tasks. To answer "what map should our group of 4 play tonight?" a player must click through every quest manually. The group's real questions are:

- What maps do I have active quests on?
- Which other players also have quests on that map?
- Which quests do we share?
- Who is blocked on a quest I have, and by what?

## Solution overview

A new in-game overlay panel, opened from a button injected into the main-menu taskbar (`MenuTaskBar`), that shows all visible profiles' quest statuses **grouped by map**, sorted so the highest-overlap map is on top. Blocked players show what quest is blocking them.

Architecture follows the existing mod split (Approach A): the server pre-computes everything and the client only renders.

- **Server** (`SharedQuestsBackend`): new endpoint `/sharedquests/overview` returning quest metadata + per-profile statuses in one payload.
- **Client** (`SharedQuests` BepInEx plugin): taskbar button + overlay panel, borrowing the injection and code-built-UI patterns from the LootNet reference (`references/LootNet/UI/RaidHistoryMenuButton.cs`, `RaidHistoryDisplay.cs`).

The existing quest-description injection and `/sharedquests/statuses` endpoint remain untouched; this feature is additive.

## Server: `/sharedquests/overview`

New route registered in `SharedQuestsRouter` alongside `/sharedquests/statuses`. Reads profiles fresh from disk per request (same as today, via `ProfileParser`).

### Response shape

```json
{
  "profiles": ["Alice", "Bob", "Carl", "Dana"],
  "quests": [
    {
      "id": "5936d90786f7742b1420ba5b",
      "name": "Gunsmith - Part 3",
      "trader": "Mechanic",
      "maps": ["bigmap"],
      "statuses": {
        "Alice": { "status": 2, "lockedReason": null },
        "Carl":  { "status": 0, "lockedReason": "Gunsmith - Part 2 (Started)" }
      }
    }
  ]
}
```

- `status` uses the same `QuestStatusEnum` int codes as the existing endpoint.
- `lockedReason` extends today's prerequisite-name logic: for each prerequisite quest, append that profile's own status on it — e.g. `"Gunsmith - Part 2 (Started)"` — so the group can see how close the blocked player is. One level deep only; no full-chain analysis.
- Headless profiles are excluded server-side (existing behavior).

### Relevance filter

A quest is included when **at least one profile** has status in {AvailableForStart (1), Started (2), AvailableForFinish (3)}. For included quests, every profile's status is returned — including Locked (with reason) and Success — because "who's blocked / who's already done" is the point. Quests active for nobody (all Locked, all Success, etc.) are omitted. Included fields per quest: id, name, trader, maps, per-profile statuses (no level requirements — nothing in the UI renders them).

### Map derivation

For each quest template (from `questHelper.GetQuestsFromDb()`):

1. If the template's `Location` field is a specific map id, that is the quest's map.
2. If `Location` is the "any" marker (templates use either the literal string `any` or the placeholder id `5af5e9f286f7746c3d532f18` — treat both as "any") or absent, scan `Conditions.AvailableForFinish` for condition-level location data (counter conditions carrying location id lists). Union all map ids found; a quest may belong to several maps.
3. If nothing derivable, the quest goes in the "Any map" group (`maps: []`).

Map ids are returned raw (e.g. `bigmap`); the client maps them to display names ("Customs") via a small static table — display naming is presentation.

### Code placement

Derivation and payload assembly live in a new pure, SPT-free-where-possible class next to `ProfileParser` (input: parsed profiles + minimal quest-template data extracted by the router layer; output: the response DTO). This keeps the logic unit-testable in `Server.Tests`, same pattern as `ProfileParser`.

## Client: taskbar button + overlay panel

### Entry point

A "QUESTS" nav button injected into `MenuTaskBar`'s nav `HorizontalLayoutGroup`, cloning sibling label font/size/color — the exact `RaidHistoryMenuButton` technique. Click toggles the panel. ESC or the panel's ✕ closes it.

### Panel

Code-built Unity UI (no asset bundles), centered panel sized for 4+ player columns, on its own high-sort-order canvas with a dim backdrop. Fetches `/sharedquests/overview` via `RequestHandler.GetJson` each time it opens (data is small; no caching).

Layout:

```
┌─ SHARED QUESTS ─────────────────────────────── ✕ ─┐
│                    Alice   Bob   Carl   Dana       │
│ ▼ CUSTOMS (4 players · 6 quests)                   │
│   Capturing Outposts   Start   Start  Start  Ready │
│   Gunsmith Pt.3        Start   Start  Lock   Done  │
│     └ Carl needs: Gunsmith - Part 2 (Started)      │
│ ▼ WOODS (2 players · 3 quests)                     │
│ ▶ ANY MAP (12 quests)                              │
└────────────────────────────────────────────────────┘
```

- **Grouping/sorting:** one collapsible section per map, sorted descending by number of profiles with an active (1/2/3) quest there, then by active-quest count. "Any map" section last, collapsed by default. A quest with multiple maps appears in each of its map sections.
- **Status cells:** short status word per profile, colored with the existing `GetStatusColor` palette. Profiles without the quest relevant show a dim "–" (Locked with no reason) so rows stay aligned.
- **Blocked detail:** a Locked cell with a `lockedReason` renders an indented sub-line under the quest row ("Carl needs: …"). Inline, not hover — controllers/simplicity.
- **Profile visibility:** reuses the existing F12 exclusion config; excluded profiles get no column and don't count toward relevance or sorting (filtering applied client-side from the full payload). After exclusion, the client re-applies the relevance rule: a quest active only for excluded profiles is hidden entirely.
- **Scrolling:** vertical `ScrollRect` over the section list (LootNet pattern).

## Error handling

- Fetch fails or returns invalid JSON → panel body shows "Couldn't reach SharedQuests server" and a Retry button.
- Empty quest list → "No active quests found."
- Map ids without a display-name entry → shown under their raw id (uppercased); only quests with **no** derivable map land in "Any map".
- Corrupt profile files are already skipped server-side.

## Testing

- **Unit (Server.Tests):** map derivation (specific location, any + condition locations, multi-map, none), relevance filter, locked-reason-with-status formatting, payload assembly. Pure-function style, no SPT deps.
- **Manual (in-game):** button injection, panel open/close, rendering with 2+ profiles, F12 exclusion respected, server-down error state.

## Out of scope (deliberately)

- Objective-level progress counters (kill counts, items) — later add-on if wanted.
- Full prerequisite-chain analysis — one level only.
- Browser-served planner page — the overview endpoint would already feed one if ever wanted.
- Changes to the existing quest-description injection.
