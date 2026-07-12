# Wanderer Self-Progression Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give wanderers their own kill/battle-driven Tier (1-8) that grows equipment and power magnitude independently of the owner's tier, plus a two-stage death-chance system (Battle Death Chance, then Permanent Death Chance) so most "killing blows" are survivable and even a real death has a chance to be reversible.

**Architecture:** All changes live in the single existing file `MakeBltGreatAgain/source/MakeBltGreatAgain.cs` (this codebase has no test framework and no multi-file split for this feature area - `WandererRecord`, `WandererGlobalConfig`, and `WandererSpawnMissionBehavior` already live together there). No new files.

**Tech Stack:** C# / .NET Framework 4.8, Bannerlord modding API (TaleWorlds.*), Harmony (already referenced, not newly needed here), Newtonsoft.Json (existing serialization of `WandererRecord`).

**Verification:** This codebase has no automated test suite - "tests" below mean (a) `dotnet build` succeeds with 0 errors, and (b) specific manual in-game checks. Every task ends with a build-verification step; the final task is a full manual verification pass.

---

### Task 1: Add persisted progression fields to `WandererRecord`

**Files:**
- Modify: `MakeBltGreatAgain/source/MakeBltGreatAgain.cs:6460-6465`

- [ ] **Step 1: Add the three new fields**

Find this exact block:

```csharp
    public class WandererRecord
    {
        public string OwnerName { get; set; }     // owner viewer name (hero.FirstName.Raw())
        public string HeroStringId { get; set; }  // real Hero backing this wanderer - resolved live, never serialized as an object
        public string Power { get; set; }         // WandererPowerCatalog.Def.Id - rolled once at hire time, kept for the wanderer's lifetime
    }
```

Replace with:

```csharp
    public class WandererRecord
    {
        public string OwnerName { get; set; }     // owner viewer name (hero.FirstName.Raw())
        public string HeroStringId { get; set; }  // real Hero backing this wanderer - resolved live, never serialized as an object
        public string Power { get; set; }         // WandererPowerCatalog.Def.Id - rolled once at hire time, kept for the wanderer's lifetime
        public int Kills { get; set; }            // lifetime kills by this wanderer (self-progression, independent of owner)
        public int Battles { get; set; }          // lifetime battles this wanderer has fought in
        public int Tier { get; set; } = 1;        // 1-8, recomputed from Kills/Battles after each battle; drives own equipment + power scaling
    }
```

Old records deserialize fine with `Kills=0, Battles=0, Tier=1` (Newtonsoft.Json fills missing JSON properties with the C# default/initializer value) - no migration code needed.

- [ ] **Step 2: Build to verify it compiles**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain/MakeBltGreatAgain"
dotnet build source/MakeBltGreatAgain.csproj -c Release -p:DefineConstants=BLT_1315 --nologo -v:minimal
```

Expected: `Liczba błędów: 0`

- [ ] **Step 3: Commit**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain"
git add MakeBltGreatAgain/source/MakeBltGreatAgain.cs
git commit -m "feat(wanderer): add Kills/Battles/Tier fields to WandererRecord"
```

---

### Task 2: Add tier-threshold table and config fields to `WandererGlobalConfig`

**Files:**
- Modify: `MakeBltGreatAgain/source/MakeBltGreatAgain.cs:7517` (`WandererGlobalConfig` class)

- [ ] **Step 1: Read the current class to get its exact current contents**

```bash
grep -n -A20 "public class WandererGlobalConfig" "E:/BLT/source_codes/MakeBltGreatAgain/MakeBltGreatAgain/source/MakeBltGreatAgain.cs"
```

- [ ] **Step 2: Add the new fields**

Inside `WandererGlobalConfig` (after the existing `GoldPerKill` property, before the closing `}` of the class), add:

```csharp
        [DisplayName("Tier Kill Thresholds"), Description("Comma-separated kills required to reach tier 2,3,4,5,6,7,8 (7 numbers). A wanderer reaches a tier when its kills OR battles meet that tier's threshold."), UsedImplicitly]
        public string TierKillThresholds { get; set; } = "5,12,22,35,50,70,95";

        [DisplayName("Tier Battle Thresholds"), Description("Comma-separated battles required to reach tier 2,3,4,5,6,7,8 (7 numbers)."), UsedImplicitly]
        public string TierBattleThresholds { get; set; } = "3,7,12,18,25,33,42";

        [DisplayName("Battle Death Chance (%)"), Description("Chance a killing blow actually kills the wanderer in this battle. Low by default - most 'fatal' hits are survived."), UsedImplicitly]
        public float BattleDeathChancePercent { get; set; } = 3f;

        [DisplayName("Permanent Death Chance (%)"), Description("Only rolled if Battle Death Chance already resulted in death. 100% = any real death is permanent (today's behavior). Lower this to let a wanderer sometimes come back with its Kills/Battles/Tier intact."), UsedImplicitly]
        public float PermanentDeathChancePercent { get; set; } = 100f;
```

- [ ] **Step 3: Add a static tier-computation helper**

Immediately above the `WandererGlobalConfig` class declaration, add:

```csharp
    // Computes a wanderer's Tier (1-8) from its lifetime Kills/Battles against the configured
    // per-tier thresholds. A tier is reached when EITHER the kill OR the battle threshold for it
    // is met (same kills-OR-battles pattern as PowerProgression for hero powers).
    public static class WandererTierCalculator
    {
        public static int ComputeTier(int kills, int battles, WandererGlobalConfig cfg)
        {
            var killThresholds = ParseCsv(cfg?.TierKillThresholds);
            var battleThresholds = ParseCsv(cfg?.TierBattleThresholds);
            int tier = 1;
            for (int i = 0; i < killThresholds.Count && i < battleThresholds.Count; i++)
            {
                if (kills >= killThresholds[i] || battles >= battleThresholds[i])
                    tier = i + 2; // thresholds[0] is the requirement for tier 2, etc.
                else
                    break;
            }
            return Math.Min(8, Math.Max(1, tier));
        }

        private static List<int> ParseCsv(string csv)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(csv)) return result;
            foreach (var part in csv.Split(','))
                if (int.TryParse(part.Trim(), out int v)) result.Add(v);
            return result;
        }
    }

```

- [ ] **Step 4: Build to verify it compiles**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain/MakeBltGreatAgain"
dotnet build source/MakeBltGreatAgain.csproj -c Release -p:DefineConstants=BLT_1315 --nologo -v:minimal
```

Expected: `Liczba błędów: 0`

- [ ] **Step 5: Commit**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain"
git add MakeBltGreatAgain/source/MakeBltGreatAgain.cs
git commit -m "feat(wanderer): add tier thresholds + death-chance config, tier calculator"
```

---

### Task 3: Replace owner-tier equipment sync with own-Tier equipment upgrade

**Files:**
- Modify: `MakeBltGreatAgain/source/MakeBltGreatAgain.cs:7157` (`SyncWandererEquipmentToOwner`)
- Modify: call site in `SpawnWanderer` (~line 7186-7230, wherever `SyncWandererEquipmentToOwner` is currently called)

- [ ] **Step 1: Find the current method and its call site**

```bash
grep -n "SyncWandererEquipmentToOwner" "E:/BLT/source_codes/MakeBltGreatAgain/MakeBltGreatAgain/source/MakeBltGreatAgain.cs"
```

- [ ] **Step 2: Replace the method**

Replace the entire `SyncWandererEquipmentToOwner` method body with a tier-driven version:

```csharp
        // Upgrades the wanderer's own equipment to match its own Tier (1-8), independent of the
        // owner's gear. For each slot the wanderer already has something equipped in, looks up
        // another item of the SAME ItemType at the wanderer's OWN Tier and equips that instead
        // (never downgrades - if the wanderer's current item is already at or above the target
        // tier, that slot is left alone).
        private static void UpgradeWandererEquipment(Hero wanderer, int tier)
        {
            try
            {
                for (var idx = EquipmentIndex.Weapon0; idx < EquipmentIndex.NumEquipmentSetSlots; idx++)
                {
                    var current = wanderer.BattleEquipment[idx];
                    if (current.IsEmpty || current.Item == null) continue;
                    if (current.Item.Tier >= tier) continue; // already at or above target

                    var upgraded = TaleWorlds.ObjectSystem.MBObjectManager.Instance
                        .GetObjectTypeList<ItemObject>()
                        .Where(i => i.ItemType == current.Item.ItemType && i.Tier == tier)
                        .ToList()
                        .SelectRandom();
                    if (upgraded == null) continue;

                    var element = new EquipmentElement(upgraded);
                    wanderer.BattleEquipment[idx] = element;
                    wanderer.CivilianEquipment[idx] = element;
                }
            }
            catch (Exception ex) { Log.Exception("UpgradeWandererEquipment", ex); }
        }
```

- [ ] **Step 3: Update the call site in `SpawnWanderer`**

Find the line that calls `SyncWandererEquipmentToOwner(hero, wandererHero);` (in `OnAgentCreated`, right before `pending.Add(...)`) and replace it with:

```csharp
                    UpgradeWandererEquipment(wandererHero, record?.Tier ?? 1);
```

- [ ] **Step 4: Build to verify it compiles**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain/MakeBltGreatAgain"
dotnet build source/MakeBltGreatAgain.csproj -c Release -p:DefineConstants=BLT_1315 --nologo -v:minimal
```

Expected: `Liczba błędów: 0` (there must be no remaining references to `SyncWandererEquipmentToOwner` - if the build reports an unresolved reference, grep for `SyncWandererEquipmentToOwner` again and update any missed call site)

- [ ] **Step 5: Commit**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain"
git add MakeBltGreatAgain/source/MakeBltGreatAgain.cs
git commit -m "feat(wanderer): equipment now scales from own Tier, not owner's tier"
```

---

### Task 4: Track kills/battles per mission and recompute Tier at mission end

**Files:**
- Modify: `MakeBltGreatAgain/source/MakeBltGreatAgain.cs` - `WandererSpawnMissionBehavior`

- [ ] **Step 1: Add an `OnEndMission` (Necromancy already gives us the pattern) or equivalent end-of-mission hook**

`WandererSpawnMissionBehavior` does not currently override `OnEndMission`. Add it. Find the constructor:

```csharp
        public WandererSpawnMissionBehavior() { BLTWandererKillTracker.Reset(); }
```

Immediately after the class's field declarations (after `private readonly List<PendingSpawn> pending = new List<PendingSpawn>();`), add a new tracking set and the override:

```csharp
        // Wanderers that actually spawned into THIS mission, tracked so we can credit a Battle
        // (and roll Tier) for them at mission end, keyed by (ownerKey, wanderer StringId).
        private readonly HashSet<(string ownerKey, string wandererStringId)> spawnedThisMission = new HashSet<(string, string)>();

        protected override void OnEndMission()
        {
            try
            {
                var cfg = WandererGlobalConfig.Get();
                var behavior = BLTWandererBehavior.Current;
                if (cfg == null || behavior == null) return;

                foreach (var (ownerKey, wandererStringId) in spawnedThisMission)
                {
                    var entries = behavior.GetWanderers(ownerKey);
                    var match = entries.FirstOrDefault(e => e.Hero?.StringId == wandererStringId);
                    if (match.Record == null || match.Hero == null || match.Hero.IsDead) continue; // dead ones are handled by the death-chance path instead

                    var kills = BLTWandererKillTracker.Get(match.Hero) >= 0
                        ? 0 // placeholder overwritten below - BLTWandererKillTracker is keyed by OWNER hero, not wanderer, see next line
                        : 0;
                    // BLTWandererKillTracker.Add(ownerHero) is called once per kill in OnAgentRemoved
                    // and is keyed by the OWNER's Hero, not per-wanderer, so it can't tell us THIS
                    // wanderer's kill count when an owner has multiple wanderers. Track per-wanderer
                    // kills directly instead, in wandererAgentKillsThisMission (added in Task 6's
                    // OnAgentRemoved edit) keyed by the wanderer's own Agent - resolve via the
                    // agent-to-record map built at spawn time.
                    int wandererKillsThisMission = wandererKillCounts.TryGetValue(match.Hero.StringId, out int k) ? k : 0;

                    match.Record.Kills += wandererKillsThisMission;
                    match.Record.Battles += 1;
                    match.Record.Tier = WandererTierCalculator.ComputeTier(match.Record.Kills, match.Record.Battles, cfg);
                }
            }
            catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.OnEndMission", ex); }
        }
```

- [ ] **Step 2: Add the per-wanderer kill counter dictionary and populate it**

Add this field next to `spawnedThisMission`:

```csharp
        // Kills THIS mission per wanderer, keyed by the wanderer Hero's StringId (survives even if
        // the wanderer's agent dies, unlike keying off Agent). Reset per mission via the constructor.
        private readonly Dictionary<string, int> wandererKillCounts = new Dictionary<string, int>();
```

In `OnAgentCreated`, right after `spawnedFor.Add(ownerKey)` succeeds and inside the `foreach (var (record, wandererHero) in behavior.GetWanderers(ownerKey))` loop, add:

```csharp
                    spawnedThisMission.Add((ownerKey, wandererHero.StringId));
```

(add this line right before the existing `UpgradeWandererEquipment(...)` call from Task 3, inside the same loop iteration)

- [ ] **Step 3: Increment `wandererKillCounts` in `OnAgentRemoved`**

Find the existing `OnAgentRemoved` override in `WandererSpawnMissionBehavior` (around line 6961). Inside it, where `wandererAgentToOwner.TryGetValue(affectorAgent, out var ownerHero)` succeeds and a kill is credited (`BLTWandererKillTracker.Add(ownerHero);`), add directly after that line:

```csharp
                if (wandererAgentToHero.TryGetValue(affectorAgent, out var killerWandererHero) && killerWandererHero != null)
                {
                    wandererKillCounts.TryGetValue(killerWandererHero.StringId, out int cur);
                    wandererKillCounts[killerWandererHero.StringId] = cur + 1;
                }
```

This requires a new map from wanderer Agent to wanderer Hero (separate from `wandererAgentToOwner`, which maps to the OWNER's Hero). Add the field next to `wandererAgentToOwner`:

```csharp
        // Maps a spawned wanderer's own agent to the WANDERER's own Hero (as opposed to
        // wandererAgentToOwner, which maps to the OWNER's Hero) - needed to credit kills to the
        // correct wanderer's own Kills/Battles/Tier when an owner has more than one wanderer.
        private readonly Dictionary<Agent, Hero> wandererAgentToHero = new Dictionary<Agent, Hero>();
```

And populate it in `SpawnWanderer`, right next to the existing `wandererAgentToOwner[agent] = owner;` line:

```csharp
                wandererAgentToHero[agent] = wanderer;
```

- [ ] **Step 4: Remove the placeholder dead code from Step 1**

Go back into the `OnEndMission` method written in Step 1 and delete this now-unused placeholder block (it was never meant to ship - the real per-wanderer count comes from `wandererKillCounts` added in Step 2/3):

```csharp
                    var kills = BLTWandererKillTracker.Get(match.Hero) >= 0
                        ? 0 // placeholder overwritten below - BLTWandererKillTracker is keyed by OWNER hero, not wanderer, see next line
                        : 0;
```

The method should read `wandererKillCounts` directly as already shown in the comment/next line in Step 1 - after deleting the placeholder, `OnEndMission`'s loop body is just:

```csharp
                foreach (var (ownerKey, wandererStringId) in spawnedThisMission)
                {
                    var entries = behavior.GetWanderers(ownerKey);
                    var match = entries.FirstOrDefault(e => e.Hero?.StringId == wandererStringId);
                    if (match.Record == null || match.Hero == null || match.Hero.IsDead) continue;

                    int wandererKillsThisMission = wandererKillCounts.TryGetValue(match.Hero.StringId, out int k) ? k : 0;
                    match.Record.Kills += wandererKillsThisMission;
                    match.Record.Battles += 1;
                    match.Record.Tier = WandererTierCalculator.ComputeTier(match.Record.Kills, match.Record.Battles, cfg);
                }
```

- [ ] **Step 5: Build to verify it compiles**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain/MakeBltGreatAgain"
dotnet build source/MakeBltGreatAgain.csproj -c Release -p:DefineConstants=BLT_1315 --nologo -v:minimal
```

Expected: `Liczba błędów: 0`

- [ ] **Step 6: Commit**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain"
git add MakeBltGreatAgain/source/MakeBltGreatAgain.cs
git commit -m "feat(wanderer): track per-wanderer kills/battles, recompute Tier at mission end"
```

---

### Task 5: Scale wanderer power magnitude by Tier

**Files:**
- Modify: `MakeBltGreatAgain/source/MakeBltGreatAgain.cs` - the power-application code in `WandererSpawnMissionBehavior` (`ApplyOnSpawnPower` and the `OnAgentRemoved`/`OnAgentHit`/`OnMissionTick` power-effect switch blocks added for the 8 wanderer powers)

- [ ] **Step 1: Add a scale helper**

Next to `WandererTierCalculator` (Task 2), add:

```csharp
    public static class WandererPowerScaling
    {
        // +15% per tier above 1 (Tier 1 = 1.0x, Tier 8 = 2.05x)
        public static float MagnitudeScale(int tier) => 1f + Math.Max(0, tier - 1) * 0.15f;
    }
```

- [ ] **Step 2: Find every hardcoded magnitude constant in the wanderer power effects**

```bash
grep -n "0.20f\|130f\|0.6f\|140f\|125f\|0.5f \* dt" "E:/BLT/source_codes/MakeBltGreatAgain/MakeBltGreatAgain/source/MakeBltGreatAgain.cs" | grep -i "vamp\|bloodrage\|secondwind\|berserk\|battlecry\|juggernaut\|ironskin\|swifthunter" 
```

(This grep is a starting point - since the constants aren't uniquely named, cross-reference against the known 8 power `case` blocks added earlier this session: `"Vampirism"`, `"Bloodrage"`, `"SecondWind"`, `"Berserk"`, `"BattleCry"`, `"Juggernaut"` inside `WandererSpawnMissionBehavior`'s `OnAgentRemoved`, `OnAgentHit`, and `OnMissionTick` overrides.)

- [ ] **Step 3: Thread a `tier` scale factor into each power's magnitude at the point it's read**

For each of the 8 power cases, look up the wanderer's own `Tier` via `wandererAgentToHero` (Task 4) + the corresponding `WandererRecord` (via `BLTWandererBehavior.Current`), then multiply the effect magnitude by `WandererPowerScaling.MagnitudeScale(tier)`. Concretely, in the `"Vampirism"` case inside `OnAgentRemoved` (heal-on-kill), change:

```csharp
                            case "Vampirism":
                                if (affectorAgent.HealthLimit > 0f)
                                    affectorAgent.Health = Math.Min(affectorAgent.HealthLimit, affectorAgent.Health + affectorAgent.HealthLimit * 0.20f);
                                break;
```

to:

```csharp
                            case "Vampirism":
                            {
                                float scale = GetWandererPowerScale(affectorAgent);
                                if (affectorAgent.HealthLimit > 0f)
                                    affectorAgent.Health = Math.Min(affectorAgent.HealthLimit, affectorAgent.Health + affectorAgent.HealthLimit * 0.20f * scale);
                                break;
                            }
```

Add the shared lookup helper as a private method on `WandererSpawnMissionBehavior`:

```csharp
        // Resolves the current Tier-based magnitude scale for a wanderer's own spawned agent.
        // Returns 1.0 (no scaling) if the agent/record can't be resolved, so this never throws
        // and never blocks a power from firing at its base strength.
        private float GetWandererPowerScale(Agent wandererAgent)
        {
            try
            {
                if (wandererAgent == null) return 1f;
                if (!wandererAgentToHero.TryGetValue(wandererAgent, out var wandererHero) || wandererHero == null) return 1f;
                var behavior = BLTWandererBehavior.Current;
                if (behavior == null) return 1f;
                foreach (var ownerList in AllOwnerKeysCache())
                {
                    var match = behavior.GetWanderers(ownerList).FirstOrDefault(e => e.Hero?.StringId == wandererHero.StringId);
                    if (match.Record != null) return WandererPowerScaling.MagnitudeScale(match.Record.Tier);
                }
                return 1f;
            }
            catch { return 1f; }
        }
```

This needs a way to enumerate owner keys without a dedicated lookup-by-wanderer-StringId method on `BLTWandererBehavior`. Rather than add that indirection, add the lookup directly to `BLTWandererBehavior` instead - **replace** the `GetWandererPowerScale` body above with this simpler version, and add the small helper method to `BLTWandererBehavior`:

In `BLTWandererBehavior` (search `public class BLTWandererBehavior : CampaignBehaviorBase`), add a new public method:

```csharp
        public WandererRecord FindRecordByHeroStringId(string heroStringId)
        {
            if (string.IsNullOrEmpty(heroStringId)) return null;
            foreach (var list in wanderers.Values)
            {
                var match = list?.FirstOrDefault(r => r.HeroStringId == heroStringId);
                if (match != null) return match;
            }
            return null;
        }
```

Then simplify `GetWandererPowerScale` in `WandererSpawnMissionBehavior` to:

```csharp
        private float GetWandererPowerScale(Agent wandererAgent)
        {
            try
            {
                if (wandererAgent == null) return 1f;
                if (!wandererAgentToHero.TryGetValue(wandererAgent, out var wandererHero) || wandererHero == null) return 1f;
                var record = BLTWandererBehavior.Current?.FindRecordByHeroStringId(wandererHero.StringId);
                return record != null ? WandererPowerScaling.MagnitudeScale(record.Tier) : 1f;
            }
            catch { return 1f; }
        }
```

- [ ] **Step 4: Apply the same `* GetWandererPowerScale(agent)` multiplier to the other 5 scalable powers**

Repeat the Step 3 pattern for: `"Bloodrage"` (attack-speed buff percent), `"SecondWind"` (heal-to-% and speed burst - scale only the heal portion), `"BattleCry"` (speed/attack pulse percent), `"Juggernaut"` (HP regen per second). `"IronSkin"` and `"SwiftHunter"` are permanent on-spawn buffs applied once in `SpawnWanderer` - scale their `ModifierPercent` the same way, using `record.Tier` directly there (the `WandererRecord` is already in scope in `SpawnWanderer` from Task 3's edits) instead of `GetWandererPowerScale`.

- [ ] **Step 5: Build to verify it compiles**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain/MakeBltGreatAgain"
dotnet build source/MakeBltGreatAgain.csproj -c Release -p:DefineConstants=BLT_1315 --nologo -v:minimal
```

Expected: `Liczba błędów: 0`

- [ ] **Step 6: Commit**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain"
git add MakeBltGreatAgain/source/MakeBltGreatAgain.cs
git commit -m "feat(wanderer): scale power magnitude by own Tier"
```

---

### Task 6: Battle Death Chance (survive a killing blow)

**Files:**
- Modify: `MakeBltGreatAgain/source/MakeBltGreatAgain.cs` - `WandererSpawnMissionBehavior.OnAgentHit`

`WandererSpawnMissionBehavior` does not currently override `OnAgentHit`. Add it.

- [ ] **Step 1: Add the override**

Add this method to `WandererSpawnMissionBehavior` (near the other overrides):

```csharp
        // Battle Death Chance: when a hit would drop a tracked wanderer's HP to/below 0, roll
        // BattleDeathChancePercent. On failure (the common case at the low default), clamp HP to a
        // small positive value instead of letting the hit be lethal - same "survive at 1% HP"
        // pattern already used elsewhere in this file for near-death saves.
        public override void OnAgentHit(Agent affectedAgent, Agent affectorAgent, in MissionWeapon affectorWeapon,
            in Blow blow, in AttackCollisionData attackCollisionData)
        {
            try
            {
                if (affectedAgent == null || !affectedAgent.IsActive()) return;
                if (!wandererAgentToHero.ContainsKey(affectedAgent)) return; // not a tracked wanderer
                if (affectedAgent.Health > 0f) return; // hit wasn't lethal, nothing to do

                var cfg = WandererGlobalConfig.Get();
                float battleDeathChance = cfg?.BattleDeathChancePercent ?? 3f;
                if (MBRandom.RandomFloat * 100f < battleDeathChance) return; // rolled real death - let it proceed normally

                affectedAgent.Health = Math.Max(1f, affectedAgent.HealthLimit * 0.01f);
            }
            catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.OnAgentHit", ex); }
        }
```

- [ ] **Step 2: Build to verify it compiles**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain/MakeBltGreatAgain"
dotnet build source/MakeBltGreatAgain.csproj -c Release -p:DefineConstants=BLT_1315 --nologo -v:minimal
```

Expected: `Liczba błędów: 0`

- [ ] **Step 3: Commit**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain"
git add MakeBltGreatAgain/source/MakeBltGreatAgain.cs
git commit -m "feat(wanderer): Battle Death Chance - most killing blows are now survivable"
```

---

### Task 7: Permanent Death Chance + reborn-style revival

**Files:**
- Modify: `MakeBltGreatAgain/source/MakeBltGreatAgain.cs` - `WandererSpawnMissionBehavior.OnAgentRemoved` and `OnMissionTick`

- [ ] **Step 1: Add a deferred-revival queue (spawning a fresh Hero must not happen mid-dispatch, same lesson as the Necromancy crash fix)**

Add this field next to `pending` (the existing `PendingSpawn` queue):

```csharp
        private class PendingRevive
        {
            public string OwnerKey;
            public WandererRecord Record;
            public Hero DeadHero;
        }
        private readonly List<PendingRevive> pendingRevives = new List<PendingRevive>();
```

- [ ] **Step 2: Extend `OnAgentRemoved` to roll Permanent Death Chance for tracked wanderers**

In the existing `OnAgentRemoved` override, after the existing kill-crediting logic (Task 4, Step 3), add a check for when the REMOVED agent (not the killer) is a tracked wanderer that died:

```csharp
                if (affectedAgent != null && agentState == AgentState.Killed
                    && wandererAgentToHero.TryGetValue(affectedAgent, out var deadWandererHero) && deadWandererHero != null)
                {
                    var record = BLTWandererBehavior.Current?.FindRecordByHeroStringId(deadWandererHero.StringId);
                    if (record != null)
                    {
                        var cfg = WandererGlobalConfig.Get();
                        float permanentChance = cfg?.PermanentDeathChancePercent ?? 100f;
                        if (MBRandom.RandomFloat * 100f < permanentChance)
                        {
                            // Permanent: progress resets. The Hero itself still dies normally through
                            // the campaign's own death resolution - we only reset OUR tracked progress.
                            record.Kills = 0;
                            record.Battles = 0;
                            record.Tier = 1;
                        }
                        else
                        {
                            // Revive: queue a fresh-Hero clone for next tick (never spawn/clone synchronously
                            // from inside OnAgentRemoved - see the Necromancy crash fix for why).
                            var ownerKey = wandererAgentToOwner.TryGetValue(affectedAgent, out var ownerHero) ? ownerHero?.FirstName?.Raw() : null;
                            if (!string.IsNullOrEmpty(ownerKey))
                                pendingRevives.Add(new PendingRevive { OwnerKey = ownerKey, Record = record, DeadHero = deadWandererHero });
                        }
                    }
                }
```

- [ ] **Step 3: Process `pendingRevives` in `OnMissionTick`**

In the existing `OnMissionTick` override, after the existing `pending` (spawn queue) processing block, add:

```csharp
            if (pendingRevives.Count > 0)
            {
                try
                {
                    foreach (var r in pendingRevives)
                    {
                        try
                        {
                            var template = CampaignHelpers.GetWandererTemplates(r.DeadHero.Culture)?.SelectRandom()
                                        ?? CampaignHelpers.AllWandererTemplates.SelectRandom();
                            if (template == null) continue;

                            var revived = HeroCreator.CreateSpecialHero(template);
                            revived.ChangeState(Hero.CharacterStates.Active);
                            var targetSettlement = TaleWorlds.CampaignSystem.Settlements.Settlement.All.Where(s => s.IsTown).SelectRandom();
                            if (targetSettlement != null) EnterSettlementAction.ApplyForCharacterOnly(revived, targetSettlement);
                            if (revived.GetSkillValue(CampaignHelpers.AllSkillObjects.First()) == 0)
                                revived.HeroDeveloper.SetInitialSkillLevel(CampaignHelpers.AllSkillObjects.First(), 1);

                            // Copy equipment directly (Kills/Battles/Tier already live on the Record,
                            // which we keep unchanged - only HeroStringId needs to point at the new Hero).
                            for (var idx = EquipmentIndex.Weapon0; idx < EquipmentIndex.NumEquipmentSetSlots; idx++)
                            {
                                revived.BattleEquipment[idx] = r.DeadHero.BattleEquipment[idx];
                                revived.CivilianEquipment[idx] = r.DeadHero.CivilianEquipment[idx];
                            }

                            r.Record.HeroStringId = revived.StringId;
                            Log.ShowInformation($"A wanderer cheats death and returns, remembering everything!", revived.CharacterObject);
                        }
                        catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.ProcessPendingRevive", ex); }
                    }
                    pendingRevives.Clear();
                }
                catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.OnMissionTick.Revives", ex); }
            }
```

- [ ] **Step 4: Build to verify it compiles**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain/MakeBltGreatAgain"
dotnet build source/MakeBltGreatAgain.csproj -c Release -p:DefineConstants=BLT_1315 --nologo -v:minimal
```

Expected: `Liczba błędów: 0`

- [ ] **Step 5: Commit**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain"
git add MakeBltGreatAgain/source/MakeBltGreatAgain.cs
git commit -m "feat(wanderer): Permanent Death Chance - reborn-style revival keeps progress"
```

---

### Task 8: Surface Tier/Kills/Battles in `!wanderer info` and `!wanderer list`

**Files:**
- Modify: `MakeBltGreatAgain/source/MakeBltGreatAgain.cs` - `WandererCommand.Execute`, `"list"` and `"info"` cases

- [ ] **Step 1: Update the `"list"` case**

Find (added earlier this session, in the `"list"` case):

```csharp
                        return $"{i + 1}:{e.Hero.Name}{(e.Hero.IsDead ? " (dead)" : "")}{(pd.HasValue ? $" [{pd.Value.Display}]" : "")}";
```

Replace with:

```csharp
                        return $"{i + 1}:{e.Hero.Name}{(e.Hero.IsDead ? " (dead)" : "")} T{e.Record.Tier}{(pd.HasValue ? $" [{pd.Value.Display}]" : "")}";
```

- [ ] **Step 2: Update the `"info"` case**

Find the `powerStr` construction in the `"info"` case and the final `ActionManager.SendReply` call. Add a `tierStr` alongside it:

```csharp
                    var powerDef = WandererPowerCatalog.Get(entries[idx].Record.Power);
                    string powerStr = powerDef.HasValue ? $" | Power: {powerDef.Value.Display} ({powerDef.Value.Desc})" : "";
                    string tierStr = $" | Tier {entries[idx].Record.Tier} ({entries[idx].Record.Kills} kills, {entries[idx].Record.Battles} battles)";
```

Then append `tierStr` to both branches of the existing `ActionManager.SendReply(context, string.IsNullOrEmpty(list) ? ... : ...)` call, e.g.:

```csharp
                    ActionManager.SendReply(context, string.IsNullOrEmpty(list)
                        ? $"Wanderer {idx + 1} ({wandererHero.Name}) has no equipment.{powerStr}{tierStr}"
                        : $"Wanderer {idx + 1} ({wandererHero.Name}): {list}{powerStr}{tierStr}");
```

- [ ] **Step 3: Build to verify it compiles**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain/MakeBltGreatAgain"
dotnet build source/MakeBltGreatAgain.csproj -c Release -p:DefineConstants=BLT_1315 --nologo -v:minimal
```

Expected: `Liczba błędów: 0`

- [ ] **Step 4: Commit**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain"
git add MakeBltGreatAgain/source/MakeBltGreatAgain.cs
git commit -m "feat(wanderer): show Tier/Kills/Battles in !wanderer list and info"
```

---

### Task 9: Deploy and manual in-game verification

**Files:** none (deployment + testing only)

- [ ] **Step 1: Confirm the game is closed**

```bash
tasklist 2>/dev/null | grep -i "bannerlord\|blse" && echo "GAME RUNNING - close it first" || echo "OK - safe to deploy"
```

- [ ] **Step 2: Deploy the built DLL**

```bash
MODULES="C:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/Modules"
cp "E:/BLT/source_codes/MakeBltGreatAgain/MakeBltGreatAgain/bin/Win64_Shipping_Client/MakeBltGreatAgain.dll" "$MODULES/BLTAdoptAHero/bin/Win64_Shipping_Client/MakeBltGreatAgain.dll"
cp "E:/BLT/source_codes/MakeBltGreatAgain/MakeBltGreatAgain/bin/Win64_Shipping_Client/MakeBltGreatAgain.dll" "E:/BLT/dll_backups/BLT_5.4.0_for_1.3.15_20260708/MakeBltGreatAgain.dll"
```

- [ ] **Step 3: Manual verification checklist (launch the game, in this order)**

1. `!wanderer hire` → confirm the reply still shows a rolled power name (Task 1-2 didn't break hiring).
2. `!wanderer info 1` → confirm it now shows `Tier 1 (0 kills, 0 battles)`.
3. Enter a battle, get several kills with the wanderer alive and visible, end the battle.
4. `!wanderer info 1` again → confirm `Kills`/`Battles` increased and, if thresholds were crossed, `Tier` increased.
5. If `Tier` increased, check the wanderer's gear in a follow-up battle looks visibly better (higher-tier item icons) - compare against `!wanderer info 1`'s equipment listing before/after.
6. Temporarily set `Battle Death Chance (%)` to `100` in BLTConfigure (MBGA - Wanderers section), redeploy config only (no rebuild needed), fight until the wanderer takes what should be a killing blow, confirm it dies as before (baseline regression check).
7. Set `Battle Death Chance (%)` back to `3`, `Permanent Death Chance (%)` to `0`, fight until the wanderer would die, confirm a message about "cheats death and returns" appears and `!wanderer info` afterward shows the SAME `Kills`/`Battles`/`Tier` as before it "died".
8. Restore `Battle Death Chance` to `3` and `Permanent Death Chance` to `100` (the shipped defaults) before finishing testing.

- [ ] **Step 4: Push final state to GitHub**

```bash
cd "E:/BLT/source_codes/MakeBltGreatAgain"
git status --short
git push origin main
```
