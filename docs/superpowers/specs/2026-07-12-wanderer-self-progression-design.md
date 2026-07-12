# Wanderer Self-Progression Design

## Purpose

Wanderers currently only grow by syncing their equipment tier to their owner's
BLT hero tier (`SyncWandererEquipmentToOwner`, in `WandererSpawnMissionBehavior`).
Their power (from the random-power system added earlier this session) never
scales. This spec adds a self-contained progression system so a wanderer gets
stronger purely from its own kills/battles, independent of the owner, and adds
a two-stage death-chance system so wanderers aren't guaranteed permadeath the
instant they take a killing blow.

## Data model changes

`WandererRecord` (in `MakeBltGreatAgain.cs`) gains three new persisted fields:

```csharp
public int Kills { get; set; }      // lifetime kills by this wanderer
public int Battles { get; set; }    // lifetime battles this wanderer fought in
public int Tier { get; set; } = 1;  // 1-8, recomputed from Kills/Battles after each battle
```

These are plain ints on the existing JSON-serialized record (`BLTWandererBehavior`
already persists `WandererRecord` via `Newtonsoft.Json`), so no new save-format
migration is needed - old records simply default to `Kills=0, Battles=0, Tier=1`
the first time they're loaded.

## Tier thresholds

A tier is reached when EITHER the kill threshold OR the battle threshold for
that tier is met (kills-OR-battles, matching the existing `PowerProgression`
pattern used for hero powers). Defaults (configurable via `WandererGlobalConfig`
as a comma-separated string per tier, same style as `PowerProgressionSection`):

| Tier | Kills | Battles |
|------|-------|---------|
| 2    | 5     | 3       |
| 3    | 12    | 7       |
| 4    | 22    | 12      |
| 5    | 35    | 18      |
| 6    | 50    | 25      |
| 7    | 70    | 33      |
| 8    | 95    | 42      |

Recomputed once per mission, in `WandererSpawnMissionBehavior`'s existing
`OnMissionOver`-equivalent path (currently there's a kill tracker reset on
`BLTWandererKillTracker.Reset()` at mission start - add a companion step that,
right before that reset, reads `BLTWandererKillTracker.Get(owner)` kills-this-mission
per surviving wanderer, adds to their record's `Kills`, increments `Battles` by 1,
and recomputes `Tier` from the table).

## Equipment: own Tier replaces owner-sync

`SyncWandererEquipmentToOwner` is removed. In its place, a new
`UpgradeWandererEquipment(Hero wanderer, int tier)` picks, per equipped slot,
a random item of that `ItemObject.Tier` and the same `ItemType` as whatever
is currently in the slot - this reuses the exact per-slot "find same type at
new tier" lookup `SyncWandererEquipmentToOwner` already does, just keyed off
the wanderer's own `Tier` field instead of the owner's. Runs once at hire
(tier 1 baseline, using the wanderer's starting gear) and again every time
`Tier` increases after a battle.

## Power scaling

The 8 hand-rolled wanderer powers (Iron Skin, Swift Hunter, Vampirism,
Bloodrage, Second Wind, Berserk, Battle Cry, Juggernaut) get their magnitude
values scaled by `1 + (Tier - 1) * 0.15` (flat +15% per tier above 1, so Tier 8
is +105% over baseline). Applied at the point each power's effect values are
read in `WandererSpawnMissionBehavior` (e.g. Vampirism's 20% heal-on-kill
becomes `20 * scale`).

## Death chance (two independent stages)

New `WandererGlobalConfig` fields:

```csharp
[DisplayName("Battle Death Chance (%)")]
public float BattleDeathChancePercent { get; set; } = 3f;

[DisplayName("Permanent Death Chance (%)")]
public float PermanentDeathChancePercent { get; set; } = 100f;
```

When a wanderer's Hero would be marked dead in a mission (killing blow lands):

1. **Stage 1 - Battle Death Chance.** Roll `BattleDeathChancePercent`. On
   failure (the common case at the 3% default), the wanderer survives the hit
   entirely - death is prevented/undone, no progress lost, mission continues
   as if the blow hadn't been fatal. *(Implementation note: exact interception
   point - a Harmony prefix on whatever finalizes `AgentState.Killed` for the
   wanderer's agent, or hooking earlier in the same place `OnAgentRemoved` is
   already observed in `WandererSpawnMissionBehavior` - needs to be nailed
   down during implementation; Bannerlord's native troops already have an
   Unconscious-vs-Killed split we may be able to piggyback on.)*
2. **Stage 2 - Permanent Death Chance.** Only rolled if stage 1 resulted in
   death. Roll `PermanentDeathChancePercent` (default 100%, so by default any
   real death is permanent). On success, permanent death: `Kills`/`Battles`
   reset to 0 and `Tier` resets to 1 on the record (mirrors normal wanderer
   death today). On failure, the wanderer is revived using the same
   clone-to-a-fresh-Hero technique built for `!reborn` this session (avoids
   the "revived hero keeps getting executed" bug from reusing the same Hero
   object) - `Kills`/`Battles`/`Tier` and equipment are preserved unchanged
   on the new Hero, and `WandererRecord.HeroStringId` is updated to point at it.

## Error handling

All new logic wrapped in the same `try/catch` + `Log.Exception(...)` pattern
already used throughout `WandererSpawnMissionBehavior` and the wanderer power
system - a failure in progression/death-chance logic must never crash the
mission, at worst it silently falls back to today's behavior (permadeath,
no tier growth).

## Testing

Manual, in-game (no automated test harness exists for this mod):
- Fight several battles with a wanderer, confirm `Kills`/`Battles` accumulate
  and `Tier` advances at the documented thresholds via `!wanderer info`.
- Confirm equipment actually changes (better tier items) after a tier-up.
- Confirm power magnitude increases are observable (e.g. Vampirism heal amount
  scales) - can eyeball via combat log / HP changes.
- With `BattleDeathChancePercent` temporarily set to 100 for testing, confirm
  a wanderer dies as before.
- With `PermanentDeathChancePercent` temporarily set to 0, confirm a "died"
  wanderer comes back with `Kills`/`Battles`/`Tier` intact under a new
  `HeroStringId`.
- Restart the game with an existing pre-update save/config to confirm old
  `WandererRecord` entries default `Kills=0, Battles=0, Tier=1` without error.
