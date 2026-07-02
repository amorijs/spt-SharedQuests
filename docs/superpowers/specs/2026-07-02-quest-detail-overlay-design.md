# Quest Detail Overlay — Design

Date: 2026-07-02
Status: Approved (brainstormed with user; approach and all sections approved)

## Summary

Clicking any quest row in the quest planner overlay opens a second, stacked
overlay showing that quest's details: description, objectives (with live
per-player progress where the profile data supports it), prerequisites with
per-player status, and rewards. Data comes from a new on-demand server
endpoint; the native EFT tasks UI is explicitly not reused (reflection into
`TasksScreen` is fragile across SPT versions and bound to one profile).

## Decisions made during brainstorming

- **Content**: description + objectives + per-profile status breakdown +
  prerequisites + rewards. All four.
- **Live objective progress**: yes, because the profile data supports it
  reliably — `TaskConditionCounters` entries carry `sourceId` (quest id),
  `id` (condition id), `value`; quest entries carry `completedConditions`
  (condition ids). Direct key matching only; no heuristic cross-referencing.
  Where neither a counter nor a completed-condition entry exists, show "–".
- **Delivery**: on-demand endpoint per quest (approach 1). Rejected: fat
  overview payload (ships locale text for ~300 quests per planner open);
  native EFT quest view (fragile, profile-bound).
- **Profile filtering stays client-side**: the endpoint returns data for all
  profiles; the client renders only profiles visible per `Settings.IsProfileVisible`,
  matching the overview endpoint's contract. No filter parameters.

## Server

### Route

`/sharedquests/quest/<questId>` — dynamic (prefix-match) route, quest id
parsed from the URL tail. The existing `SharedQuestsRouter` is a
`StaticRouter` (exact match), so this needs a `DynamicRouter` registration.
**Fallback** if SPT 4.0's `DynamicRouter` doesn't fit: static route
`/sharedquests/quest` with the quest id in a POST body
(`RequestHandler.PostJson` client-side). Executor verifies against the
`SPTarkov.Server.Core` reference assemblies.

Unknown quest id → `HttpResponseUtil.NullResponse()` (existing error pattern).

### Response DTO

```jsonc
// QuestDetailResponse
{
  "Name": "Debut",
  "Trader": "Prapor",
  "Maps": ["bigmap"],               // canonical ids, client maps to display names
  "Description": "…locale text…",   // "" if locale key missing
  "Objectives": [
    {
      "Text": "Eliminate 5 Scavs",  // locale key = condition id; raw id fallback
      "Target": 5,                   // nullable — null when condition has no count
      "Progress": {                  // nickname -> progress; entry per profile
        "JohnGoob": { "Count": 3, "Done": false },   // Count nullable
        "clinicallylazy": { "Count": null, "Done": true }
      }
    }
  ],
  "Prereqs": [
    { "Name": "Shooting Cans", "Statuses": { "JohnGoob": 4, "clinicallylazy": 2 } }
  ],
  "Rewards": [ "+1,700 EXP", "Prapor rep +0.02", "MP-133 12ga shotgun", "3× RGD-5" ]
}
```

- **Objectives** come from the quest template's `AvailableForFinish`
  conditions, in template order. Text from locale key `"{conditionId}"`;
  target from the condition's `value`. Per-profile: `Done` = condition id in
  that profile's `completedConditions` for this quest; `Count` = value of the
  `TaskConditionCounters` entry with matching condition id and
  `sourceId == questId`; both may be absent (client shows "–").
- **Rewards** are pre-formatted strings built server-side from the template's
  `Rewards.Success`: EXP, trader rep (trader name from the existing
  `TraderNames` map), money and items (names via locale `"{tpl} Name"`,
  `N×` prefix when count > 1). Unknown reward types are skipped. Client just
  prints lines.
- **Prereqs** reuse `QuestMeta.PrereqQuestIds` (already extracted at load);
  names via the existing name cache; per-profile status ints from
  `ParsedProfile.QuestStatusByQid` (0/Locked when absent).

### Structure (pure/impure split, matching OverviewBuilder)

- `QuestDetailBuilder` (new file, pure, SPT-free): takes an SPT-free detail
  meta (conditions, reward primitives, resolved locale strings), parsed
  profiles, and returns `QuestDetailResponse`. Unit-testable.
- `SharedQuestsServer` (impure): on request, finds the quest template by id
  via `questHelper.GetQuestsFromDb()`, resolves locale strings via
  `localeService.GetLocaleDb()` (keys: `"{questId} description"`,
  `"{conditionId}"`, `"{tpl} Name"` — pattern proven by ExpandedTaskText),
  re-parses profiles fresh from disk (same as overview), calls the builder.
  No new startup cache; extraction happens per request, on demand only.
- `ProfileParser` gains, in the same single JSON pass:
  - per-quest `completedConditions` (list of condition-id strings), and
  - `TaskConditionCounters` parsed to condition-id → (sourceId, value).
  Missing/malformed sections parse to empty collections (profiles from older
  saves have no `TaskConditionCounters`).

## Client

### Interaction

- Each quest row in `QuestPlannerPanel.BuildQuestRow` gets a `Button`
  (transition `None`, like all other panel buttons) opening the detail
  overlay for `quest.Id`.
- The planner stays visible behind the detail panel; closing the detail
  returns to the planner with scroll position intact and no overview re-fetch.

### QuestDetailPanel

New `Client/QuestDetailPanel.cs`, code-built UI in the LootNet style (no
asset bundles), as a sibling GameObject after `PlannerRoot` in the existing
planner canvas so it draws on top. Same visual language: dark panel
(`#0F0F12`-ish, same as planner), 3px accent top bar, `QuestPlannerPanel.Accent`
color, ✕ close button. Narrower than the planner: ~900px, same 8%–92%
vertical anchors.

Layout, top to bottom:

1. **Header**: quest name (title style), subtitle line with trader and map
   display names (reuse the planner's `MapNames` mapping — make it accessible
   to the detail panel rather than duplicating it).
2. **Scrollable content** (same ScrollRect/viewport/VerticalLayoutGroup
   pattern as the planner, including the invisible viewport Image so scroll
   events register):
   - Description paragraph, gray body text.
   - `OBJECTIVES` section: one row per objective — objective text, then per
     visible profile a colored fragment: green `✓` when `Done`, `3/5` when
     `Count` present (Target null → just the count), gray `–` otherwise.
   - `PREREQUISITES` section: quest name + per visible profile
     `Plugin.GetStatusName`/`GetStatusColor` colored status.
   - `REWARDS` section: one line per pre-formatted string.
   - Sections with no content are omitted entirely (no empty headers).

### Close behavior & focus

- ESC closes only the detail panel when it is open; `QuestPlannerPanel.Update`
  must check the detail panel first so ESC doesn't fall through and close the
  planner in the same frame.
- Click outside the detail panel (its own dim backdrop) closes only the
  detail panel. ✕ button ditto.

### Loading / error states

- On click, the panel opens immediately showing the planner's centered
  "Loading..." message pattern.
- Fetch failure or null/unparseable response → "Couldn't load quest details"
  + RETRY button (same style as the planner's), retrying the same quest id.

## Error handling summary

- Server: unknown quest id → null response; profile read/parse errors →
  log + skip that profile (existing pattern); missing locale keys →
  description "" / objective falls back to raw condition id; reward
  formatting failures → skip that reward line, never fail the request.
- Client: any exception during fetch/deserialize → error state with retry;
  a null field anywhere in the payload must not throw (defensive null checks,
  as the planner does for `Statuses`).

## Testing

- `Server.Tests`:
  - `ProfileParserTests`: `completedConditions` and `TaskConditionCounters`
    parsing — present, absent, malformed, numeric vs string values.
  - New `QuestDetailBuilderTests`: objective progress matrix (done / counted /
    no data), counter with wrong `sourceId` ignored, prereq status mapping
    (absent profile → 0), reward formatting (EXP, rep, money, item with and
    without count, unknown type skipped), missing locale fallbacks.
- Client: manual in-game verification (no Unity test infra) — open planner,
  click quest, verify content/close/ESC/retry behavior.

## Out of scope

- Native EFT tasks-view rendering.
- Server-side profile filtering.
- Caching profile parses (revisit with mtime-based cache only if measured).
- Live progress for objective types with no counter and no completed entry.
