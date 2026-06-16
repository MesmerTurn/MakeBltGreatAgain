# MakeBltGreatAgain

A mod collection for **Mount & Blade II: Bannerlord** built on top of [BannerlordTwitch (BLT) 5.2.4](https://www.nexusmods.com/mountandblade2bannerlord/mods/981).  
Developed by **MesmerTurn** — Bannerlord streamer on Twitch.

## What this mod adds

| Feature | Description |
|---|---|
| **Resurrection** | Resurrects a permanently dead BLT hero by spending Twitch channel points (battle deaths are handled by summon/attack redeems) |
| **Formations** | Control hero troop formations via chat commands |
| **Follow / Defollow** | `!follow` — BLT hero runs after the streamer to protect them; `!defollow` — stops following |
| **Guard** | `!guard` — the BLT hero's retinue follows and protects that hero |
| **Upgrade** | Spend in-game gold to upgrade everything available in the player's clan, fiefs and kingdom (`upgradeclan`, `upgradefief`, `upgradekingdom`) |
| **Duel** | On the battlefield, BLT hero A can command their hero to immediately charge and fight BLT hero B on the opposing side |
| **Clan Gold** | Automatically distributes gold equally among all clan members |
| **Grail** | Chance to trigger a quest for a legendary weapon or armor by meeting a specific condition (e.g. win 5 battles in a row) |
| **Auras** | Special passive abilities added to the game — healing, poison, and many more |

## Requirements

- Mount & Blade II: Bannerlord **1.3.15**
- [BannerlordTwitch (BLT) 5.2.4](https://github.com/Randomchair22/Bannerlord-Twitch)
- BLT Adopt a Hero (included in the BLT repo above)

## Installation

1. Download `MakeBltGreatAgain_v1.1.zip` from [Releases](../../releases)
2. Extract to your Bannerlord `Modules/` folder
3. Enable the mod in the Bannerlord launcher (load after BLT and BLTAdoptAHero)

## Power System

Heroes can be assigned custom active and passive powers. Each power can be configured with duration, stacks, particle effects and contour color. Available powers:

### Strike Powers (active)
| Power | Effect |
|---|---|
| **Poison Strike** | Hit poisons the target, dealing damage over time |
| **Burning Strike** | Hit ignites the target |
| **Frost Strike** | Hit slows and chills the target |
| **Vampirism Strike** | Hit heals the attacker for a portion of damage dealt |
| **Chain Lightning** | Hit sends electricity jumping to nearby enemies |
| **Bleed Strike** | Hit causes bleeding damage over time |
| **Jump Attack** | Hero leaps toward a target and strikes |
| **Kick** | Knocks back the target on hit |

### Aura Powers (passive, affect nearby units)
| Power | Effect |
|---|---|
| **Heal Aura** | Heals nearby allies over time |
| **Damage Aura** | Deals damage to nearby enemies |
| **Curse Aura** | Weakens nearby enemies |
| **Buff Aura** | Strengthens nearby allies |
| **Fear Aura** | Causes nearby enemies to flee |
| **Slow Aura** | Slows movement of nearby enemies |
| **Weakness Aura** | Reduces armor/damage of nearby enemies |
| **Battle Cry Aura** | Boosts morale of nearby allies |
| **Commanding Aura** | Powerful aura reward — redeemable via Twitch channel points |

### Survival Powers (passive)
| Power | Effect |
|---|---|
| **Berserk** | Increases damage as HP drops lower |
| **Last Stand** | Grants a powerful bonus when near death |
| **Blood Rage** | Stacks bonus damage with each kill |
| **Vengeance** | Returns a portion of received damage back to the attacker |
| **Absorb Health** | Absorbs incoming damage and converts it to health |
| **Taunt** | Forces nearby enemies to focus attacks on this hero |

### Support Powers
| Power | Effect |
|---|---|
| **War Banner** | Allies within radius get a speed boost while hero is alive |
| **Mark Target** | Enemies near hero are marked — all allies deal bonus damage to them |
| **Rallying Cry** | One-time burst heal for all nearby allies when hero HP drops below threshold |

### Special Powers
| Power | Effect |
|---|---|
| **Shadowstep** | Periodically teleports hero behind the nearest enemy |
| **Teleport (Passive)** | Passive teleportation ability |
| **Stealth** | Periodically makes hero untargetable — enemies lose their target |
| **Clone on Kill** | Each kill spawns a weak clone of the hero nearby (max clones configurable) |

All powers support: **duration**, **stack count**, **particle effects** (visual FX) and **contour color** (hero glow color in battle).

## Releases

| Version | Changes |
|---|---|
| v1.1 | Particle effects on Demon Lord, all power names in English |

## License

MIT — free to use, modify and share.
