// ══════════════════════════════════════════════════════════════════════════════
// Conquest of Doravaro — scalony mod BLT
// Zawiera: BLTResurrect, BLTFormation, BLTGuard, BLTRally,
//          BLTUpgrade, BLTDuel, BLTClanGold, BLTGrail, BLTAuras
// ══════════════════════════════════════════════════════════════════════════════

// ── Usings ──
using BLTAdoptAHero.Actions.Upgrades;
using BLTAdoptAHero.Powers;
using BLTAdoptAHero;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Rewards;
using BannerlordTwitch.UI;
using BannerlordTwitch.Util;
using BannerlordTwitch;
using HarmonyLib;
using JetBrains.Annotations;
using System.Collections.Generic;
using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace MakeBltGreatAgain
{

    // ======================================================================
    // BLTResurrect
    // ======================================================================

public class BLTResurrectModule : MBSubModuleBase
    {
        public BLTResurrectModule()
        {
            ActionManager.RegisterAll(typeof(BLTResurrectModule).Assembly);
        }

        private static Harmony harmony;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            if (harmony != null) return;
            harmony = new Harmony("mod.bannerlord.bltresurrect");

            // Patchuj KillCharacterAction ręcznie — PatchAll nie aplikuje tych patchy
            PatchKillMethod("ApplyByExecution", typeof(BLTResurrect_ExecutionPatch));
            PatchKillMethod("ApplyByExecutionAfterMapEvent", typeof(BLTResurrect_ExecutionAfterMapEventPatch));

            // Reszta przez PatchAll (ChangeState, FugitivePatch itp.)
            harmony.PatchAll();
            Log.Info("[BLTResurrect] Loaded.");
        }

        private static void PatchKillMethod(string methodName, Type patchClass)
        {
            try
            {
                var target = typeof(KillCharacterAction).GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (target == null) { Log.Info($"[BLTResurrect] WARN: {methodName} not found"); return; }

                var prefix = patchClass.GetMethod("Prefix",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (prefix == null) { Log.Info($"[BLTResurrect] WARN: Prefix not found in {patchClass.Name}"); return; }

                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                Log.Info($"[BLTResurrect] Patched {methodName} OK");
            }
            catch (Exception ex)
            {
                Log.Exception($"[BLTResurrect] Failed to patch {methodName}", ex);
            }
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            if (gameStarterObject is CampaignGameStarter campaignStarter)
                campaignStarter.AddBehavior(new ResurrectProtectionBehavior());
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // PROTECTION BEHAVIOR — prevents BLT heroes from dying as fugitives
    // ════════════════════════════════════════════════════════════════════════════

    public class ResurrectProtectionBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, OnHeroKilled);
        }

        public override void SyncData(IDataStore dataStore) { }

        // BLTAdoptAHero fires HeroKilledEvent even when ChangeState is blocked.
        // We listen here (registered AFTER BLTAdoptAHero) and undo their state changes.
        private static void OnHeroKilled(Hero victim, Hero killer,
            KillCharacterAction.KillCharacterActionDetail detail, bool showNotification)
        {
            if (!ResurrectCooldown.IsProtected(victim)) return;
            Log.Info($"[BLTResurrect] HeroKilledEvent for protected {victim?.Name} — undoing BLT death state");
            // Przywróć IsRetiredOrDead=false (BLTAdoptAHero ustawia true w swoim handlerze)
            SetIsRetiredOrDead(victim, false);
            // Przywróć stan Active jeśli jakoś wpadł w Dead
            try { if (victim?.IsDead == true) victim.ChangeState(Hero.CharacterStates.Active); } catch { }
            // Wyczyść stan jeńca bezpośrednio
            try
            {
                var field = victim?.GetType().GetField("_partyBelongedToAsPrisoner",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field?.GetValue(victim) is PartyBase pp && pp != null)
                {
                    try { pp.PrisonRoster?.RemoveTroop(victim.CharacterObject); } catch { }
                    field.SetValue(victim, null);
                }
            }
            catch { }
            // Teleportuj do bezpiecznej osady
            try
            {
                if (victim?.CurrentSettlement == null && victim?.PartyBelongedTo == null)
                {
                    var sett = FindSafeSettlement(victim);
                    if (sett != null) TeleportHeroToSettlement(victim, sett);
                }
            }
            catch { }
        }

        internal static void SetIsRetiredOrDead(Hero hero, bool value)
        {
            try
            {
                var behavior = BLTAdoptAHeroCampaignBehavior.Current;
                if (behavior == null) return;
                var field = behavior.GetType().GetField("heroData", BindingFlags.Instance | BindingFlags.NonPublic);
                var dict = field?.GetValue(behavior) as System.Collections.IDictionary;
                if (dict == null || !dict.Contains(hero)) return;
                var heroData = dict[hero];
                var prop = heroData?.GetType().GetProperty("IsRetiredOrDead",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                prop?.SetValue(heroData, value);
            }
            catch (Exception ex) { Log.Exception("[BLTResurrect] SetIsRetiredOrDead failed", ex); }
        }

        private static void OnDailyTick()
        {
            // Codziennie zwalniaj wszystkich BLT heroes z niewoli (u KAŻDEGO captora)
            foreach (var hero in Hero.AllAliveHeroes.Where(h => h.IsPrisoner && IsBLTHero(h)).ToList())
            {
                try
                {
                    ForceReleaseFromCaptivity(hero);
                    Log.Info($"[BLTResurrect] Daily tick: freed {hero.Name} from captivity");
                }
                catch (Exception ex)
                {
                    Log.Exception($"[BLTResurrect] Daily tick release failed for {hero.Name}", ex);
                }
            }
        }

        internal static bool IsBLTHero(Hero hero)
        {
            if (hero == null) return false;
            // Sprawdź tag w nazwie (normalny stan)
            if (hero.Name?.Contains("[BLT]") == true || hero.Name?.Contains("[DEV]") == true) return true;
            // Po resurrection BLT usuwa [BLT] z nazwy — sprawdź heroData dictionary
            try
            {
                var behavior = BLTAdoptAHeroCampaignBehavior.Current;
                if (behavior == null) return false;
                var field = behavior.GetType().GetField("heroData", BindingFlags.Instance | BindingFlags.NonPublic);
                var dict = field?.GetValue(behavior) as System.Collections.IDictionary;
                return dict?.Contains(hero) == true;
            }
            catch { return false; }
        }

        internal static Settlement FindSafeSettlement(Hero hero)
        {
            // 1. Własne osady klanu (zamek/miasto)
            var clanSett = hero.Clan?.Settlements?.FirstOrDefault(s => s.IsTown || s.IsCastle);
            if (clanSett != null) return clanSett;

            // 2. Dowolne miasto tej samej frakcji
            var factionTown = Settlement.All
                .FirstOrDefault(s => s.IsTown && s.MapFaction == hero.MapFaction);
            if (factionTown != null) return factionTown;

            // 3. Jakiekolwiek miasto
            return Settlement.All.FirstOrDefault(s => s.IsTown);
        }

        internal static void ForceReleaseFromCaptivity(Hero hero)
        {
            try
            {
                // Wyczyść pole _partyBelongedToAsPrisoner bezpośrednio
                var field = hero.GetType().GetField("_partyBelongedToAsPrisoner",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field?.GetValue(hero) is PartyBase prisonerParty && prisonerParty != null)
                {
                    try { prisonerParty.PrisonRoster?.RemoveTroop(hero.CharacterObject); } catch { }
                    field.SetValue(hero, null);
                }
                // Oficjalnie zwolnij jeśli gra wciąż uważa za jeńca
                if (hero.IsPrisoner)
                    EndCaptivityAction.ApplyByEscape(hero);
                // Teleportuj do bezpiecznej osady
                var settlement = FindSafeSettlement(hero);
                if (settlement != null) TeleportHeroToSettlement(hero, settlement);
                Log.Info($"[BLTResurrect] Force-released {hero.Name} from captivity");
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTResurrect] ForceReleaseFromCaptivity failed", ex);
            }
        }

        internal static void TeleportHeroToSettlement(Hero hero, Settlement settlement)
        {
            try
            {
                EnterSettlementAction.ApplyForCharacterOnly(hero, settlement);
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTResurrect] TeleportHeroToSettlement failed", ex);
            }
        }
    }

    // ── Cooldown tracker po resurrection ─────────────────────────────────────
    internal static class ResurrectCooldown
    {
        private static readonly Dictionary<string, DateTime> _resurrectedAt = new Dictionary<string, DateTime>();
        private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

        public static void Grant(Hero hero)
        {
            _resurrectedAt[hero.StringId] = DateTime.UtcNow;
            Log.Info($"[BLTResurrect] {hero.Name} protected for 5 min after resurrection");
        }

        public static bool IsProtected(Hero hero)
        {
            if (hero == null) return false;
            if (!_resurrectedAt.TryGetValue(hero.StringId, out var t)) return false;
            if (DateTime.UtcNow - t > Cooldown) { _resurrectedAt.Remove(hero.StringId); return false; }
            return true;
        }
    }

    // ── Harmony patch — blokuje egzekucję BLT heroes ─────────────────────────
    // Zasady:
    //   1. NPC lord próbuje egzekutować BLT hero → zawsze zablokuj
    //   2. BLT hero próbuje egzekutować wskrzeszonego BLT hero (5 min cooldown) → zablokuj
    //   3. BLT hero egzekutuje normalnego BLT hero po wygranej → dozwolone
    // ── Patch na Hero.ChangeState — blokuje śmierć jeśli hero ma aktywny cooldown ──────────
    // Każda śmierć (battle, execution, wounds, murder...) w końcu woła ChangeState(Dead).
    [HarmonyPatch(typeof(Hero), "ChangeState")]
    internal static class BLTResurrect_ChangeStatePatch
    {
        // Guard against re-entrant calls from EnterSettlementAction
        private static bool _inProtection = false;

        static bool Prefix(Hero __instance, Hero.CharacterStates newState)
        {
            try
            {
                if (newState != Hero.CharacterStates.Dead) return true;
                Log.Info($"[BLTResurrect] ChangeState→Dead: {__instance?.Name} inCooldown={ResurrectCooldown.IsProtected(__instance)} isBLT={ResurrectProtectionBehavior.IsBLTHero(__instance)}");
                if (ResurrectCooldown.IsProtected(__instance))
                {
                    Log.Info($"[BLTResurrect] Blocked death of recently resurrected {__instance?.Name}");
                    // Wyczyść stan jeńca bezpośrednio (BEZ EndCaptivityAction — wywołuje eventy tworzące pętlę)
                    try
                    {
                        var field = __instance.GetType().GetField("_partyBelongedToAsPrisoner",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        if (field?.GetValue(__instance) is PartyBase prisonerParty && prisonerParty != null)
                        {
                            try { prisonerParty.PrisonRoster?.RemoveTroop(__instance.CharacterObject); } catch { }
                            field.SetValue(__instance, null);
                        }
                    }
                    catch { }
                    // Teleportuj do bezpiecznej osady żeby hero nie wisiał w powietrzu (guard re-entrant)
                    if (!_inProtection)
                    {
                        _inProtection = true;
                        try
                        {
                            if (__instance.CurrentSettlement == null && __instance.PartyBelongedTo == null)
                            {
                                var sett = ResurrectProtectionBehavior.FindSafeSettlement(__instance);
                                if (sett != null)
                                {
                                    EnterSettlementAction.ApplyForCharacterOnly(__instance, sett);
                                    Log.Info($"[BLTResurrect] Teleported {__instance.Name} to {sett.Name} after blocked death");
                                }
                            }
                        }
                        catch { }
                        finally { _inProtection = false; }
                    }
                    return false;
                }
                return true;
            }
            catch { return true; }
        }
    }

    // ── Patche na egzekucje (tylko manualne — bez [HarmonyPatch] żeby uniknąć double-patch przez PatchAll) ─
    internal static class BLTResurrect_ExecutionPatch
    {
        internal static bool Prefix(Hero victim, Hero executer)
            => ExecutionGuard.Apply(victim, executer);
    }

    internal static class BLTResurrect_ExecutionAfterMapEventPatch
    {
        internal static bool Prefix(Hero victim, Hero executer)
            => ExecutionGuard.Apply(victim, executer);
    }

    internal static class ExecutionGuard
    {
        internal static bool Apply(Hero victim, Hero executer)
        {
            try
            {
                Log.Info($"[BLTResurrect] ExecutionGuard: victim={victim?.Name} executer={executer?.Name} isBLT={ResurrectProtectionBehavior.IsBLTHero(victim)} inCooldown={ResurrectCooldown.IsProtected(victim)}");

                // Blokuj jeśli hero wskrzeszony w ciągu ostatnich 5 min
                if (ResurrectCooldown.IsProtected(victim))
                {
                    Log.Info($"[BLTResurrect] Blocked execution of recently resurrected {victim?.Name}");
                    return false;
                }

                if (!ResurrectProtectionBehavior.IsBLTHero(victim)) return true;

                // NPC lord egzekutuje BLT hero → zawsze blokuj
                if (executer != Hero.MainHero && !ResurrectProtectionBehavior.IsBLTHero(executer))
                {
                    Log.Info($"[BLTResurrect] Blocked NPC execution of {victim?.Name} by {executer?.Name}");
                    return false;
                }

                Log.Info($"[BLTResurrect] Allowing execution of {victim?.Name} by {executer?.Name}");
                return true;
            }
            catch { return true; }
        }
    }

    // ── Harmony patch — blokuje MakeHeroFugitive dla BLT heroes ─────────────
    [HarmonyPatch(typeof(MakeHeroFugitiveAction), nameof(MakeHeroFugitiveAction.Apply))]
    internal static class BLTResurrect_FugitivePatch
    {
        static bool Prefix(Hero fugitive, bool showNotification)
        {
            try
            {
                if (!ResurrectProtectionBehavior.IsBLTHero(fugitive)) return true;

                var settlement = ResurrectProtectionBehavior.FindSafeSettlement(fugitive);
                if (settlement == null) return true;

                ResurrectProtectionBehavior.TeleportHeroToSettlement(fugitive, settlement);
                Log.Info($"[BLTResurrect] Blocked fugitive for {fugitive.Name} → teleported to {settlement.Name}");
                return false;
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTResurrect] FugitivePatch failed", ex);
                return true;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // REWARD HANDLER — channel points redemption
    // ════════════════════════════════════════════════════════════════════════════

    [DisplayName("Resurrect BLT Hero")]
    public class BLTResurrectHandler : IRewardHandler
    {
        public class ResurrectSettings
        {
            [DisplayName("Gold Cost (BLT gold)")]
            [Description("How much BLT hero gold the resurrection costs. 0 = free.")]
            public int GoldCost { get; set; } = 5000;

            [DisplayName("HP After Resurrection (%)")]
            [Description("Hero HP percentage after resurrection (1-100).")]
            public int ResurrectedHPPercent { get; set; } = 30;

            [DisplayName("Allow Resurrect If Alive")]
            [Description("If false, redemption is cancelled when the hero is already alive.")]
            public bool AllowIfAlive { get; set; } = false;
        }

        public Type RewardConfigType => typeof(ResurrectSettings);

        public void Enqueue(ReplyContext context, object config)
        {
            var settings = config as ResurrectSettings ?? new ResurrectSettings();

            var behavior = BLTAdoptAHeroCampaignBehavior.Current;
            if (behavior == null)
            {
                ActionManager.NotifyCancelled(context, "BLT is not active right now.");
                return;
            }

            // Check if hero is already alive
            var livingHero = behavior.GetAdoptedHero(context.UserName);
            if (livingHero != null)
            {
                if (!settings.AllowIfAlive)
                {
                    ActionManager.NotifyCancelled(context, $"{livingHero.FirstName} is already alive!");
                    return;
                }
            }

            // Find dead / retired hero
            var deadHero = behavior.GetRetiredHero(context.UserName);
            if (deadHero == null)
            {
                ActionManager.NotifyCancelled(context, "No dead hero found for your account.");
                return;
            }

            // Check BLT gold (use reflection to access HeroData from dead hero)
            int heroGold = GetDeadHeroGold(deadHero);
            if (settings.GoldCost > 0 && heroGold < settings.GoldCost)
            {
                ActionManager.NotifyCancelled(context,
                    $"Not enough gold: need {settings.GoldCost}, have {heroGold}.");
                return;
            }

            // Ustaw cooldown PRZED wskrzeszeniem — EndCaptivityAction odpala eventy kampanii
            // które mogą wywołać egzekucję ZANIM wrócimy z TryReviveBannerlordHero
            ResurrectCooldown.Grant(deadHero);

            // Mark as alive in BLT data PRZED wskrzeszeniem żeby BLTAdoptAHero nie rejestrował śmierci
            ResurrectProtectionBehavior.SetIsRetiredOrDead(deadHero, false);

            // Attempt resurrection
            bool revived = TryReviveBannerlordHero(deadHero);
            if (!revived)
            {
                ActionManager.NotifyCancelled(context,
                    "Resurrection failed — hero state could not be restored.");
                return;
            }

            // Restore hero name (remove Roman numeral + deceased/retired suffix)
            string cleanName = StripRetirementSuffix(deadHero.FirstName?.Raw() ?? context.UserName);
            SetHeroName(deadHero, cleanName);

            // Restore some HP
            int maxHp = deadHero.CharacterObject?.MaxHitPoints() ?? 100;
            int pct = settings.ResurrectedHPPercent < 1 ? 1 : settings.ResurrectedHPPercent > 100 ? 100 : settings.ResurrectedHPPercent;
            int hp = Math.Max(1, (int)(maxHp * pct / 100f));
            deadHero.HitPoints = hp;

            // Deduct gold
            if (settings.GoldCost > 0)
                behavior.ChangeHeroGold(deadHero, -settings.GoldCost);

            ActionManager.NotifyComplete(context,
                $"⚔ {deadHero.FirstName} has risen from the dead! ({hp}/{maxHp} HP)");

            Log.Info($"[BLTResurrect] {context.UserName} resurrected hero {deadHero.Name}");
        }

        // ── Bannerlord hero revival ───────────────────────────────────────────

        private static bool TryReviveBannerlordHero(Hero hero)
        {
            try
            {
                if (!hero.IsDead)
                {
                    ResurrectProtectionBehavior.ForceReleaseFromCaptivity(hero);
                    return true;
                }

                hero.ChangeState(Hero.CharacterStates.Active);

                // Wyczyść stan jeńca po ChangeState — silnik może przywrócić state
                ResurrectProtectionBehavior.ForceReleaseFromCaptivity(hero);

                return !hero.IsDead;
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTResurrect] TryReviveBannerlordHero failed", ex);
                return false;
            }
        }

        private static void TryClearPrisonerState(Hero hero)
        {
            try
            {
                if (hero.IsPrisoner)
                    EndCaptivityAction.ApplyByEscape(hero);

                // Wyczyść bezpośrednio przez reflection (IsPrisoner może być false po egzekucji
                // ale wewnętrzne pole _partyBelongedToAsPrisoner może być wciąż ustawione)
                var field = hero.GetType().GetField("_partyBelongedToAsPrisoner",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field?.GetValue(hero) is PartyBase prisonerParty && prisonerParty != null)
                {
                    try
                    {
                        prisonerParty.PrisonRoster?.RemoveTroop(hero.CharacterObject);
                    }
                    catch { }
                    field.SetValue(hero, null);
                    Log.Info($"[BLTResurrect] Cleared prisoner state for {hero.Name} from {prisonerParty.Name}");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTResurrect] TryClearPrisonerState failed", ex);
            }
        }

        private static void EnsureHeroHasLocation(Hero hero)
        {
            try
            {
                if (hero.CurrentSettlement != null || hero.PartyBelongedTo != null) return;

                // Teleport do bezpiecznej osady — NIE MakeHeroFugitiveAction (fugitive = śmierć)
                var settlement = ResurrectProtectionBehavior.FindSafeSettlement(hero);
                if (settlement != null)
                    ResurrectProtectionBehavior.TeleportHeroToSettlement(hero, settlement);
                else
                    MakeHeroFugitiveAction.Apply(hero); // ostateczny fallback
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTResurrect] EnsureHeroHasLocation failed", ex);
            }
        }

        // ── BLT HeroData reflection helpers ──────────────────────────────────

        private static void SetIsRetiredOrDead(Hero hero, bool value)
        {
            try
            {
                var behavior = BLTAdoptAHeroCampaignBehavior.Current;
                if (behavior == null) return;

                var behaviorType = behavior.GetType();

                // Get private heroData dictionary
                var heroDataField = behaviorType.GetField("heroData",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (heroDataField == null) return;

                var dict = heroDataField.GetValue(behavior) as System.Collections.IDictionary;
                if (dict == null || !dict.Contains(hero)) return;

                var heroData = dict[hero];
                if (heroData == null) return;

                var prop = heroData.GetType().GetProperty("IsRetiredOrDead",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                prop?.SetValue(heroData, value);
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTResurrect] SetIsRetiredOrDead failed", ex);
            }
        }

        private static int GetDeadHeroGold(Hero hero)
        {
            try
            {
                var behavior = BLTAdoptAHeroCampaignBehavior.Current;
                if (behavior == null) return 0;

                var behaviorType = behavior.GetType();
                var heroDataField = behaviorType.GetField("heroData",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (heroDataField == null) return 0;

                var dict = heroDataField.GetValue(behavior) as System.Collections.IDictionary;
                if (dict == null || !dict.Contains(hero)) return 0;

                var heroData = dict[hero];
                if (heroData == null) return 0;

                var prop = heroData.GetType().GetProperty("Gold",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return prop != null ? (int)(prop.GetValue(heroData) ?? 0) : 0;
            }
            catch { return 0; }
        }

        // ── Name helpers ──────────────────────────────────────────────────────

        // Removes " III deceased" / " II retired" suffix added by BLT's RetireHero()
        private static string StripRetirementSuffix(string name)
        {
            // Pattern: "<BaseName> <Roman> <deceased|retired>"
            var match = Regex.Match(name,
                @"^(.+?)\s+[IVXLCDM]+\s+(deceased|retired)$",
                RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : name;
        }

        private static void SetHeroName(Hero hero, string newFirstName)
        {
            try
            {
                // BLT adopted heroes use FirstName as the viewer name
                // We restore it (without [BLT] tag — BLT re-adds it internally)
                var firstName = new TaleWorlds.Localization.TextObject(newFirstName);
                hero.SetName(firstName, firstName);
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTResurrect] SetHeroName failed", ex);
            }
        }
    }


    // ======================================================================
    // BLTFormation
    // ======================================================================

public class BLTFormationModule : MBSubModuleBase
    {
        private static Harmony harmony;

        public BLTFormationModule()
        {
            ActionManager.RegisterAll(typeof(BLTFormationModule).Assembly);
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            if (harmony != null) return;
            try
            {
                harmony = new Harmony("mod.bannerlord.bltformation");
                harmony.PatchAll();
                Log.Info("[BLTFormation] Loaded.");
            }
            catch (Exception ex) { Log.Exception("[BLTFormation] Load failed", ex); }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            mission.AddMissionBehavior(new FormationBehavior());
        }
    }

    // ── !follow — włącz podążanie za streamerem ───────────────────────────────
    [DisplayName("FormationStrimer")]
    public class FormationStreamerCommand : ICommandHandler
    {
        public Type HandlerConfigType => typeof(FormationSettings);

        public void Execute(ReplyContext context, object config)
        {
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            { ActionManager.SendReply(context, "Formation command can only be used during an active mission."); return; }
            var behavior = Mission.Current.GetMissionBehavior<FormationBehavior>();
            if (behavior == null) { ActionManager.SendReply(context, "Formation behavior not ready."); return; }
            if (!SummonAccess.IsHeroSummoned(hero)) { ActionManager.SendReply(context, "Your hero must be summoned in this battle."); return; }

            behavior.Activate(hero);
            ActionManager.SendReply(context, $"✓ {hero.FirstName} is now following the streamer!");
        }

        [DisplayName("Formation Settings")]
        public class FormationSettings
        {
            [DisplayName("Follow Distance (meters)")]
            [Description("Odległość od strimera — hero zatrzymuje się i walczy gdy jest bliżej niż ta wartość.")]
            public float FollowDistance { get; set; } = 4f;
        }
    }

    // ── !followhero — podążaj za innym BLT hero podczas bitwy ────────────────
    [DisplayName("FormationFollowHero")]
    public class FormationFollowHeroCommand : ICommandHandler
    {
        public Type HandlerConfigType => typeof(FollowHeroSettings);

        public void Execute(ReplyContext context, object config)
        {
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            { ActionManager.SendReply(context, "Command can only be used during an active battle."); return; }
            var behavior = Mission.Current.GetMissionBehavior<FormationBehavior>();
            if (behavior == null) { ActionManager.SendReply(context, "Formation behavior not ready."); return; }
            if (!SummonAccess.IsHeroSummoned(hero)) { ActionManager.SendReply(context, "Your hero must be summoned in this battle."); return; }

            // Nazwa targetu z args (usuń @ jeśli jest)
            string targetName = context.Args != null && context.Args.Length > 0
                ? string.Join("", context.Args).TrimStart('@')
                : null;

            if (string.IsNullOrWhiteSpace(targetName))
            { ActionManager.SendReply(context, "Usage: !followhero @username"); return; }

            var targetHero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(targetName);
            if (targetHero == null)
            { ActionManager.SendReply(context, $"BLT hero '{targetName}' not found."); return; }

            if (targetHero == hero)
            { ActionManager.SendReply(context, "You cannot follow yourself."); return; }

            if (!SummonAccess.IsHeroSummoned(targetHero))
            { ActionManager.SendReply(context, $"{targetHero.FirstName} is not summoned in this battle."); return; }

            var s = config as FollowHeroSettings ?? new FollowHeroSettings();
            behavior.ActivateFollowHero(hero, targetHero, s.FollowDistance);
            ActionManager.SendReply(context, $"✓ {hero.FirstName} is now following {targetHero.FirstName}!");
        }

        [DisplayName("Follow Hero Settings")]
        public class FollowHeroSettings
        {
            [DisplayName("Follow Distance (meters)")]
            [Description("Hero zatrzymuje się i walczy gdy jest bliżej niż ta wartość od celu.")]
            public float FollowDistance { get; set; } = 4f;
        }
    }

    // ── !follow off — wyłącz podążanie ───────────────────────────────────────
    [DisplayName("FormationStrimerOff")]
    public class FormationOffCommand : ICommandHandler
    {
        public class EmptyConfig { }
        public Type HandlerConfigType => typeof(EmptyConfig);

        public void Execute(ReplyContext context, object config)
        {
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            var behavior = Mission.Current?.GetMissionBehavior<FormationBehavior>();
            if (behavior == null) { ActionManager.SendReply(context, "Not in a mission."); return; }

            behavior.Deactivate(hero);
            ActionManager.SendReply(context, $"✓ {hero.FirstName} returned to normal AI.");
        }
    }

    // ── Mission behavior ──────────────────────────────────────────────────────
    public class FormationBehavior : MissionBehavior
    {
        private readonly Dictionary<Hero, float> activeHeroes = new Dictionary<Hero, float>();
        private readonly Dictionary<Hero, float> lastUpdateTime = new Dictionary<Hero, float>();
        // Mapa: follower → target hero (gdy hero śledzi innego BLT hero zamiast strimera)
        private readonly Dictionary<Hero, (Hero target, float dist)> heroFollowTargets
            = new Dictionary<Hero, (Hero, float)>();
        private const float UpdateInterval = 0.5f;
        private const float FollowDist    = 4f;
        private const float RetinueDist   = 3f;

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public void Activate(Hero hero)
        {
            if (hero == null) return;
            heroFollowTargets.Remove(hero); // wyczyść poprzedni follow hero jeśli był
            activeHeroes[hero] = Mission.Current?.CurrentTime ?? 0f;
        }

        public void ActivateFollowHero(Hero hero, Hero target, float followDist = 4f)
        {
            if (hero == null || target == null) return;
            heroFollowTargets[hero] = (target, followDist);
            activeHeroes[hero] = Mission.Current?.CurrentTime ?? 0f;
        }

        public void Deactivate(Hero hero)
        {
            if (hero == null) return;
            activeHeroes.Remove(hero);
            lastUpdateTime.Remove(hero);
            heroFollowTargets.Remove(hero);

            // Wyczyść scripted position — bez tego agent stoi w miejscu po dezaktywacji
            var heroAgent = SummonAccess.GetHeroAgent(hero);
            if (heroAgent != null && heroAgent.IsActive())
            {
                ResetAgentToNormalAI(heroAgent);
                foreach (var ra in SummonAccess.GetRetinueAgents(hero))
                    ResetAgentToNormalAI(ra);
            }
        }

        private static void ResetAgentToNormalAI(Agent agent)
        {
            try
            {
                // Jeśli agent ma formation → wróć do rozkazu formacji (charge/advance)
                if (agent.Formation != null)
                {
                    agent.Formation.SetMovementOrder(
                        MovementOrder.MovementOrderCharge);
                    return;
                }
                // Fallback: ustaw scripted position na aktualne miejsce ale bez żadnych flag
                // żeby agent mógł swobodnie walczyć (nie blokuje ataku)
                var cur = agent.GetWorldPosition();
                agent.SetScriptedPosition(ref cur, false, Agent.AIScriptedFrameFlags.None);
            }
            catch { }
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (Mission.Current == null) return;

            var streamerAgent = Mission.Current.MainAgent;
            var now = Mission.Current.CurrentTime;
            var toRemove = new List<Hero>();

            foreach (var kvp in activeHeroes)
            {
                var hero = kvp.Key;
                if (lastUpdateTime.TryGetValue(hero, out var lastTime) && now - lastTime < UpdateInterval)
                    continue;
                lastUpdateTime[hero] = now;

                var heroAgent = SummonAccess.GetHeroAgent(hero);
                if (heroAgent == null || !heroAgent.IsActive()) { toRemove.Add(hero); continue; }

                // Ustal cel: inny BLT hero lub streamer
                Agent targetAgent;
                float followDist;
                if (heroFollowTargets.TryGetValue(hero, out var followInfo))
                {
                    targetAgent = SummonAccess.GetHeroAgent(followInfo.target);
                    followDist  = followInfo.dist;
                    // Jeśli target zginął — usuń follow i wróć do normalnego AI
                    if (targetAgent == null || !targetAgent.IsActive())
                    {
                        heroFollowTargets.Remove(hero);
                        toRemove.Add(hero);
                        continue;
                    }
                }
                else
                {
                    // Brak streamera → pomiń (nie wymagaj streamera dla follow-hero mode)
                    if (streamerAgent == null || !streamerAgent.IsActive()) { toRemove.Add(hero); continue; }
                    targetAgent = streamerAgent;
                    followDist  = FollowDist;
                }

                var targetPos = targetAgent.GetWorldPosition();
                float heroDist = (heroAgent.Position - targetAgent.Position).Length;

                if (heroDist > followDist)
                    heroAgent.SetScriptedPosition(ref targetPos, false, Agent.AIScriptedFrameFlags.None);

                // Retinue → hero
                var heroPos = heroAgent.GetWorldPosition();
                foreach (var agent in SummonAccess.GetRetinueAgents(hero))
                {
                    try
                    {
                        float rd = (agent.Position - heroAgent.Position).Length;
                        if (rd > RetinueDist)
                            agent.SetScriptedPosition(ref heroPos, false, Agent.AIScriptedFrameFlags.None);
                    }
                    catch { }
                }
            }

            foreach (var h in toRemove)
            {
                activeHeroes.Remove(h);
                lastUpdateTime.Remove(h);
                heroFollowTargets.Remove(h);
                var ha = SummonAccess.GetHeroAgent(h);
                if (ha != null && ha.IsActive()) ResetAgentToNormalAI(ha);
            }
        }

        public override void OnAgentDeleted(Agent affectedAgent)
        {
            if (affectedAgent.IsMainAgent) { activeHeroes.Clear(); lastUpdateTime.Clear(); heroFollowTargets.Clear(); return; }
            // Jeśli zginął follower
            var dead = activeHeroes.Keys.Where(h => SummonAccess.GetHeroAgent(h) == affectedAgent).ToList();
            foreach (var h in dead) { activeHeroes.Remove(h); lastUpdateTime.Remove(h); heroFollowTargets.Remove(h); }
            // Jeśli zginął target — usuń wszystkich którzy go śledzili
            var orphaned = heroFollowTargets
                .Where(kv => SummonAccess.GetHeroAgent(kv.Value.target) == affectedAgent)
                .Select(kv => kv.Key).ToList();
            foreach (var h in orphaned) heroFollowTargets.Remove(h);
        }
    }

    // ── Shared summon access helpers ──────────────────────────────────────────
    internal static class SummonAccess
    {
        private static readonly Type SummonBehaviorType =
            typeof(BLTAdoptAHeroCampaignBehavior).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "BLTSummonBehavior");

        public static bool IsHeroSummoned(Hero hero)
        {
            var agent = GetHeroAgent(hero);
            return agent != null && agent.IsActive();
        }

        public static Agent GetHeroAgent(Hero hero)
        {
            var state = GetSummonState(hero);
            // CurrentAgent jest Property (nie Field) w HeroSummonState
            return GetProperty<Agent>(state, "CurrentAgent")
                ?? Mission.Current?.Agents?.FirstOrDefault(a => BLTAdoptAHero.AgentExtensions.GetHero(a) == hero);
        }

        public static IEnumerable<Agent> GetRetinueAgents(Hero hero)
        {
            var state = GetSummonState(hero);
            if (state == null) yield break;

            // Retinue = List<RetinueState>, każdy element ma POLE Agent (nie retinueAgent)
            var retinue = GetProperty<System.Collections.IEnumerable>(state, "Retinue");
            if (retinue != null)
                foreach (var entry in retinue)
                {
                    var a = GetField<Agent>(entry, "Agent");
                    if (a != null && a.IsActive()) yield return a;
                }

            // Retinue2 = List<RetinueState>, pole Agent (tak samo)
            var retinue2 = GetProperty<System.Collections.IEnumerable>(state, "Retinue2");
            if (retinue2 != null)
                foreach (var entry in retinue2)
                {
                    var a = GetField<Agent>(entry, "Agent");
                    if (a != null && a.IsActive()) yield return a;
                }
        }

        private static object GetSummonState(Hero hero)
        {
            if (SummonBehaviorType == null || Mission.Current == null) return null;
            var getMB = typeof(Mission).GetMethods()
                .FirstOrDefault(m => m.Name == "GetMissionBehavior" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var summonBehavior = getMB?.MakeGenericMethod(SummonBehaviorType).Invoke(Mission.Current, null);
            return SummonBehaviorType.GetMethod("GetHeroSummonState")?.Invoke(summonBehavior, new object[] { hero });
        }

        private static T GetField<T>(object instance, string name)
        {
            if (instance == null) return default;
            var f = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return f == null ? default : (T)f.GetValue(instance);
        }

        private static T GetProperty<T>(object instance, string name)
        {
            if (instance == null) return default;
            var p = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) return default;
            try { return (T)p.GetValue(instance); } catch { return default; }
        }
    }


    // ======================================================================
    // BLTGuard
    // ======================================================================

public class BLTGuardModule : MBSubModuleBase
    {
        private static Harmony harmony;

        public BLTGuardModule()
        {
            ActionManager.RegisterAll(typeof(BLTGuardModule).Assembly);
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            if (harmony != null) return;
            try
            {
                harmony = new Harmony("mod.bannerlord.bltguard");
                harmony.PatchAll();
                Log.Info("BLTGuard loaded.");
            }
            catch (Exception ex) { Log.Exception("BLTGuard patch failed", ex); }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            mission.AddMissionBehavior(new GuardMissionBehavior());
        }
    }

    [DisplayName("Guard")]
    public class GuardCommand : ICommandHandler
    {
        public class GuardConfig { }
        public Type HandlerConfigType => typeof(GuardConfig);

        public void Execute(ReplyContext context, object config)
        {
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            { ActionManager.SendReply(context, "Guard can only be used during an active battle."); return; }
            var behavior = Mission.Current.GetMissionBehavior<GuardMissionBehavior>();
            if (behavior == null) { ActionManager.SendReply(context, "Guard is not ready in this mission."); return; }
            if (!GuardSummonAccess.IsHeroSummoned(hero))
            { ActionManager.SendReply(context, "Your hero must be summoned in this battle."); return; }

            behavior.ActivateGuard(hero);
            ActionManager.SendReply(context, $"✓ {hero.FirstName}'s retinue is now guarding them!");
        }
    }

    public class GuardMissionBehavior : MissionBehavior
    {
        private readonly HashSet<Hero> activeGuards = new HashSet<Hero>();
        private float _lastTickTime = 0f;
        private const float TickInterval = 0.5f;
        private const float GuardRadius  = 3f;

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public void ActivateGuard(Hero hero)
        {
            if (hero == null) return;
            activeGuards.Add(hero);
            var retinue = GuardSummonAccess.GetRetinueAgents(hero);
            Log.Info($"[BLTGuard v0.6] ActivateGuard: {hero.FirstName}, retinueCount={retinue.Count}");
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (Mission.Current == null) return;

            var now = Mission.Current.CurrentTime;
            if (now - _lastTickTime < TickInterval) return;
            _lastTickTime = now;

            var toRemove = new List<Hero>();

            foreach (var hero in activeGuards)
            {
                var heroAgent = GuardSummonAccess.GetHeroAgent(hero);
                if (heroAgent == null || !heroAgent.IsActive()) { toRemove.Add(hero); continue; }

                var retinue = GuardSummonAccess.GetRetinueAgents(hero).ToList();
                var heroPos = heroAgent.GetWorldPosition();

                foreach (var agent in retinue)
                {
                    try
                    {
                        float dist = (agent.Position - heroAgent.Position).Length;
                        if (dist > GuardRadius)
                            agent.SetScriptedPosition(ref heroPos, false, Agent.AIScriptedFrameFlags.None);
                    }
                    catch { }
                }
            }

            foreach (var h in toRemove) activeGuards.Remove(h);
        }

        public override void OnAgentDeleted(Agent affectedAgent)
        {
            var heroOfAgent = activeGuards.FirstOrDefault(h => GuardSummonAccess.GetHeroAgent(h) == affectedAgent);
            if (heroOfAgent != null) activeGuards.Remove(heroOfAgent);
        }
    }

    // ── Reflection helpers ────────────────────────────────────────────────────
    internal static class GuardSummonAccess
    {
        private static readonly Type SummonBehaviorType =
            typeof(BLTAdoptAHeroCampaignBehavior).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "BLTSummonBehavior");

        public static bool IsHeroSummoned(Hero hero)
        {
            var agent = GetHeroAgent(hero);
            return agent != null && agent.IsActive();
        }

        public static Agent GetHeroAgent(Hero hero)
        {
            var state = GetSummonState(hero);
            // CurrentAgent jest Property (nie Field) w HeroSummonState
            return GetProperty<Agent>(state, "CurrentAgent")
                ?? Mission.Current?.Agents?.FirstOrDefault(a => BLTAdoptAHero.AgentExtensions.GetHero(a) == hero);
        }

        public static List<Agent> GetRetinueAgents(Hero hero)
        {
            var result = new List<Agent>();
            var state = GetSummonState(hero);
            if (state == null) return result;

            // Retinue = List<RetinueState>, każdy element ma POLE Agent (nie retinueAgent)
            var retinue = GetProperty<System.Collections.IEnumerable>(state, "Retinue");
            if (retinue != null)
                foreach (var entry in retinue)
                {
                    var a = GetField<Agent>(entry, "Agent");
                    if (a != null && a.IsActive()) result.Add(a);
                }

            // Retinue2 = List<RetinueState>, pole Agent (tak samo)
            var retinue2 = GetProperty<System.Collections.IEnumerable>(state, "Retinue2");
            if (retinue2 != null)
                foreach (var entry in retinue2)
                {
                    var a = GetField<Agent>(entry, "Agent");
                    if (a != null && a.IsActive()) result.Add(a);
                }

            return result;
        }

        private static object GetSummonState(Hero hero)
        {
            if (SummonBehaviorType == null || Mission.Current == null) return null;
            var getMB = typeof(Mission).GetMethods()
                .FirstOrDefault(m => m.Name == "GetMissionBehavior" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var summonBehavior = getMB?.MakeGenericMethod(SummonBehaviorType).Invoke(Mission.Current, null);
            return SummonBehaviorType.GetMethod("GetHeroSummonState")?.Invoke(summonBehavior, new object[] { hero });
        }

        private static T GetField<T>(object instance, string name)
        {
            if (instance == null) return default;
            var f = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return f == null ? default : (T)f.GetValue(instance);
        }

        private static T GetProperty<T>(object instance, string name)
        {
            if (instance == null) return default;
            var p = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) return default;
            try { return (T)p.GetValue(instance); } catch { return default; }
        }
    }


    // ======================================================================
    // BLTRally
    // ======================================================================

public class BLTRallyModule : MBSubModuleBase
    {
        private static Harmony harmony;

        public BLTRallyModule()
        {
            ActionManager.RegisterAll(typeof(BLTRallyModule).Assembly);
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            if (harmony != null) return;

            try
            {
                harmony = new Harmony("mod.bannerlord.bltrally");
                harmony.PatchAll();
            }
            catch (Exception ex)
            {
                Log.Exception("BLTRally patch failed", ex);
            }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            mission.AddMissionBehavior(new RallyMissionBehavior());
        }
    }

    [DisplayName("Buff")]
    public class Rally : ICommandHandler
    {
        public Type HandlerConfigType => typeof(Settings);

        public void Execute(ReplyContext context, object config)
        {
            var settings = config as Settings ?? new Settings();
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null)
            {
                ActionManager.SendReply(context, "You do not have an adopted hero.");
                return;
            }

            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            {
                ActionManager.SendReply(context, "Rally can only be used during an active mission.");
                return;
            }

            var behavior = Mission.Current.GetMissionBehavior<RallyMissionBehavior>();
            if (behavior == null)
            {
                ActionManager.SendReply(context, "Rally is not ready in this mission.");
                return;
            }

            if (behavior.TryActivate(hero, settings, out var affected, out var error))
            {
                ActionManager.SendReply(context, $"Buff active for {hero.FirstName} and {Math.Max(0, affected - 1)} retinue troops.");
            }
            else
            {
                ActionManager.SendReply(context, error);
            }
        }

        public class Settings
        {
            [DisplayName("Duration Seconds")]
            public float DurationSeconds { get; set; } = 30f;

            [DisplayName("Cooldown Seconds")]
            public float CooldownSeconds { get; set; } = 180f;

            [DisplayName("Speed Multiplier")]
            public float SpeedMultiplier { get; set; } = 1.20f;

            [DisplayName("Attack Speed Multiplier")]
            public float AttackSpeedMultiplier { get; set; } = 1.20f;

            [DisplayName("Damage Multiplier")]
            public float DamageMultiplier { get; set; } = 1.25f;

            [DisplayName("Heal Percent")]
            public float HealPercent { get; set; } = 10f;
        }
    }

    public class RallyMissionBehavior : MissionBehavior
    {
        private static readonly DrivenProperty[] SpeedProperties =
        {
            DrivenProperty.MaxSpeedMultiplier,
            DrivenProperty.CombatMaxSpeedMultiplier
        };

        private static readonly DrivenProperty[] AttackProperties =
        {
            DrivenProperty.SwingSpeedMultiplier,
            DrivenProperty.HandlingMultiplier
        };

        private readonly Dictionary<Hero, float> cooldownUntil = new Dictionary<Hero, float>();
        private readonly List<RallyEffect> activeEffects = new List<RallyEffect>();

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public bool TryActivate(Hero hero, Rally.Settings settings, out int affectedCount, out string error)
        {
            affectedCount = 0;
            error = null;

            var now = Mission.Current.CurrentTime;
            if (cooldownUntil.TryGetValue(hero, out var cooldownEnd) && cooldownEnd > now)
            {
                error = $"Rally cooldown: {(cooldownEnd - now):0}s.";
                return false;
            }

            var agents = RallySummonAccess.GetHeroAndRetinueAgents(hero).Where(IsValidTarget).Distinct().ToList();
            if (agents.Count == 0)
            {
                error = "Your hero must be summoned and alive to use Rally.";
                return false;
            }

            foreach (var agent in agents)
            {
                ApplyAgentBuff(agent, settings);
                if (settings.HealPercent > 0)
                {
                    agent.Health = Math.Min(agent.HealthLimit, agent.Health + agent.HealthLimit * settings.HealPercent / 100f);
                }
            }

            activeEffects.Add(new RallyEffect(hero, agents, now + Math.Max(1f, settings.DurationSeconds), settings.DamageMultiplier));
            cooldownUntil[hero] = now + Math.Max(0f, settings.CooldownSeconds);
            affectedCount = agents.Count;
            return true;
        }

        public bool IsDamageBuffed(Agent agent, out float damageMultiplier)
        {
            damageMultiplier = 1f;
            foreach (var effect in activeEffects)
            {
                if (effect.Agents.Contains(agent))
                {
                    damageMultiplier *= effect.DamageMultiplier;
                }
            }
            return damageMultiplier > 1.0001f;
        }

        public override void OnMissionTick(float dt)
        {
            var now = Mission.Current.CurrentTime;
            for (var i = activeEffects.Count - 1; i >= 0; --i)
            {
                if (activeEffects[i].ExpiresAt <= now)
                {
                    activeEffects[i].Restore();
                    activeEffects.RemoveAt(i);
                }
            }
        }

        public override void OnAgentDeleted(Agent affectedAgent)
        {
            foreach (var effect in activeEffects)
            {
                effect.RemoveAgent(affectedAgent);
            }
        }

        private static bool IsValidTarget(Agent agent)
        {
            return agent != null
                   && agent.IsHuman
                   && agent.IsActive()
                   && agent.AgentDrivenProperties != null;
        }

        private static void ApplyAgentBuff(Agent agent, Rally.Settings settings)
        {
            foreach (var property in SpeedProperties)
            {
                agent.AgentDrivenProperties.SetStat(property,
                    agent.AgentDrivenProperties.GetStat(property) * Math.Max(0.01f, settings.SpeedMultiplier));
            }

            foreach (var property in AttackProperties)
            {
                agent.AgentDrivenProperties.SetStat(property,
                    agent.AgentDrivenProperties.GetStat(property) * Math.Max(0.01f, settings.AttackSpeedMultiplier));
            }

            agent.UpdateCustomDrivenProperties();
        }

        private class RallyEffect
        {
            private readonly Dictionary<Agent, Dictionary<DrivenProperty, float>> originalStats;

            public RallyEffect(Hero hero, IReadOnlyList<Agent> agents, float expiresAt, float damageMultiplier)
            {
                Hero = hero;
                Agents = new HashSet<Agent>(agents);
                ExpiresAt = expiresAt;
                DamageMultiplier = Math.Max(1f, damageMultiplier);
                originalStats = agents.ToDictionary(
                    a => a,
                    a => SpeedProperties.Concat(AttackProperties)
                        .Distinct()
                        .ToDictionary(p => p, p => a.AgentDrivenProperties.GetStat(p)));
            }

            public Hero Hero { get; }
            public HashSet<Agent> Agents { get; }
            public float ExpiresAt { get; }
            public float DamageMultiplier { get; }

            public void RemoveAgent(Agent agent)
            {
                Agents.Remove(agent);
                originalStats.Remove(agent);
            }

            public void Restore()
            {
                foreach (var pair in originalStats)
                {
                    var agent = pair.Key;
                    if (agent == null || !agent.IsActive() || agent.AgentDrivenProperties == null) continue;
                    foreach (var stat in pair.Value)
                    {
                        agent.AgentDrivenProperties.SetStat(stat.Key, stat.Value);
                    }
                    agent.UpdateCustomDrivenProperties();
                }
            }
        }
    }

    [HarmonyPatch]
    internal static class RallyDamagePatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Mission), "RegisterBlow")]
        private static void RegisterBlowPrefix(Agent attacker, Agent victim, WeakGameEntity realHitEntity, ref Blow b,
            ref AttackCollisionData collisionData, in MissionWeapon attackerWeapon)
        {
            try
            {
                var behavior = Mission.Current?.GetMissionBehavior<RallyMissionBehavior>();
                if (attacker == null || behavior?.IsDamageBuffed(attacker, out var multiplier) != true) return;

                b.InflictedDamage = Math.Max(0, (int)(b.InflictedDamage * multiplier));
                b.BaseMagnitude *= multiplier;
                collisionData.InflictedDamage = Math.Max(0, (int)(collisionData.InflictedDamage * multiplier));
                collisionData.BaseMagnitude *= multiplier;
            }
            catch (Exception ex)
            {
                Log.Exception("BLTRally damage patch failed", ex);
            }
        }
    }

    internal static class RallySummonAccess
    {
        // BLTSummonBehavior is internal — Type.GetType fails for internal types from external assemblies.
        private static readonly Type SummonBehaviorType =
            typeof(BLTAdoptAHeroCampaignBehavior).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "BLTSummonBehavior");

        public static IEnumerable<Agent> GetHeroAndRetinueAgents(Hero hero)
        {
            var state = GetHeroSummonState(hero);
            var heroAgent = GetField<Agent>(state, "CurrentAgent") ?? Mission.Current?.Agents?.FirstOrDefault(a => BLTAdoptAHero.AgentExtensions.GetHero(a) == hero);
            if (heroAgent != null) yield return heroAgent;

            foreach (var agent in GetRetinueAgents(state, "Retinue")) yield return agent;
            foreach (var agent in GetRetinueAgents(state, "Retinue2")) yield return agent;
        }

        private static object GetHeroSummonState(Hero hero)
        {
            if (SummonBehaviorType == null || Mission.Current == null) return null;

            var method = typeof(Mission).GetMethods()
                .FirstOrDefault(m => m.Name == "GetMissionBehavior" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var behavior = method?.MakeGenericMethod(SummonBehaviorType).Invoke(Mission.Current, null);
            return behavior == null
                ? null
                : SummonBehaviorType.GetMethod("GetHeroSummonState")?.Invoke(behavior, new object[] { hero });
        }

        private static IEnumerable<Agent> GetRetinueAgents(object summonState, string fieldName)
        {
            var entries = GetField<System.Collections.IEnumerable>(summonState, fieldName);
            if (entries == null) yield break;

            foreach (var entry in entries)
            {
                var state = GetField<AgentState>(entry, "State");
                var agent = GetField<Agent>(entry, "Agent");
                if (state == AgentState.Active && agent != null) yield return agent;
            }
        }

        private static T GetField<T>(object instance, string fieldName)
        {
            if (instance == null) return default(T);
            var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field == null ? default(T) : (T)field.GetValue(instance);
        }
    }


    // ======================================================================
    // BLTUpgrade
    // ======================================================================

public class BLTUpgradeModule : MBSubModuleBase
    {
        private static Harmony harmony;

        public BLTUpgradeModule()
        {
            ActionManager.RegisterAll(typeof(BLTUpgradeModule).Assembly);
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            if (harmony != null) return;

            try
            {
                harmony = new Harmony("mod.bannerlord.bltupgrade");
                harmony.PatchAll();
                Log.Info("[BLTUpgrade] Loaded. Commands: !autoupgrade_clan / !autoupgrade_fief / !autoupgrade_kingdom");
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTUpgrade] Load failed", ex);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // CLAN UPGRADE
    // ════════════════════════════════════════════════════════════════════════════

    [DisplayName("UpgradeClanCommand")]
    public class UpgradeClanCommand : ICommandHandler
    {
        public Type HandlerConfigType => typeof(UpgradeSettings);

        public void Execute(ReplyContext context, object config)
        {
            var settings = config as UpgradeSettings ?? new UpgradeSettings();
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);

            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            if (hero.Clan == null) { ActionManager.SendReply(context, "Your hero has no clan."); return; }

            var (count, spent) = UpgradeAllHandler.ExecuteClan(hero, out var debugMsg, settings.DryRun);
            if (count > 0)
                ActionManager.SendReply(context, $"✓ {count} clan upgrade(s) applied, spent {spent} gold.");
            else
                ActionManager.SendReply(context, $"No upgrades: {debugMsg}");
        }

        public class UpgradeSettings
        {
            [DisplayName("Dry Run (test mode)")]
            public bool DryRun { get; set; } = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // FIEF UPGRADE
    // ════════════════════════════════════════════════════════════════════════════

    [DisplayName("UpgradeFiefCommand")]
    public class UpgradeFiefCommand : ICommandHandler
    {
        public Type HandlerConfigType => typeof(UpgradeSettings);

        public void Execute(ReplyContext context, object config)
        {
            var settings = config as UpgradeSettings ?? new UpgradeSettings();
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);

            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            if (hero.Clan == null) { ActionManager.SendReply(context, "Your hero has no clan."); return; }

            var (count, spent) = UpgradeAllHandler.ExecuteFief(hero, settings.DryRun);
            if (count > 0)
                ActionManager.SendReply(context, $"✓ {count} fief upgrade(s) applied, spent {spent} gold.");
            else
                ActionManager.SendReply(context, "No fief upgrades available or not enough gold.");
        }

        public class UpgradeSettings
        {
            [DisplayName("Dry Run (test mode)")]
            public bool DryRun { get; set; } = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // KINGDOM UPGRADE
    // ════════════════════════════════════════════════════════════════════════════

    [DisplayName("UpgradeKingdomCommand")]
    public class UpgradeKingdomCommand : ICommandHandler
    {
        public Type HandlerConfigType => typeof(UpgradeSettings);

        public void Execute(ReplyContext context, object config)
        {
            var settings = config as UpgradeSettings ?? new UpgradeSettings();
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);

            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            if (hero.Clan?.Kingdom == null) { ActionManager.SendReply(context, "Your hero's clan is not in a kingdom."); return; }

            var (count, spent) = UpgradeAllHandler.ExecuteKingdom(hero, settings.DryRun);
            if (count > 0)
                ActionManager.SendReply(context, $"✓ {count} kingdom upgrade(s) applied, spent {spent} gold.");
            else
                ActionManager.SendReply(context, "No kingdom upgrades available or not enough gold.");
        }

        public class UpgradeSettings
        {
            [DisplayName("Dry Run (test mode)")]
            public bool DryRun { get; set; } = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // CORE UPGRADE LOGIC
    // ════════════════════════════════════════════════════════════════════════════

    public static class UpgradeAllHandler
    {
        // Zwraca (liczba ulepszeń, wydane złoto BLT)
        public static (int count, int spent) ExecuteClan(Hero hero, out string debugInfo, bool dryRun = false)
        {
            debugInfo = "";
            try
            {
                if (hero?.Clan == null) { debugInfo = "hero/clan null"; return (0, 0); }

                var config = GetGlobalConfig();
                var upgradeBehavior = UpgradeBehavior.Current;
                if (config == null) { debugInfo = "config null"; Log.Error("[BLTUpgrade] Missing config"); return (0, 0); }
                if (upgradeBehavior == null) { debugInfo = "behavior null"; Log.Error("[BLTUpgrade] Missing behavior"); return (0, 0); }

                var clanUpgrades = GetPropertyValue<IEnumerable>(config, "ClanUpgrades");
                if (clanUpgrades == null) { debugInfo = "ClanUpgrades null"; return (0, 0); }

                var allUpgrades = clanUpgrades.Cast<object>().ToList();
                int totalGold = BLTAdoptAHeroCampaignBehavior.Current?.GetHeroGold(hero) ?? 0;
                var owned = new HashSet<string>(upgradeBehavior.GetClanUpgrades(hero.Clan));

                int upgraded = 0;
                int spent = 0;
                int skippedOwned = 0;
                int skippedGold = 0;
                int skippedReqs = 0;

                var available = allUpgrades
                    .Where(u => {
                        var id = GetPropertyValue<string>(u, "ID");
                        if (upgradeBehavior.HasClanUpgrade(hero.Clan, id)) { skippedOwned++; return false; }
                        return true;
                    })
                    .OrderBy(u => GetPropertyValue<int>(u, "GoldCost"))
                    .ToList();

                foreach (var upgrade in available)
                {
                    var id = GetPropertyValue<string>(upgrade, "ID");
                    var cost = GetPropertyValue<int>(upgrade, "GoldCost");

                    if (totalGold < cost) { skippedGold++; continue; }

                    var required = GetPropertyValue<List<string>>(upgrade, "RequiredUpgradeIDs");
                    if (required?.Count > 0 && !required.All(r => owned.Contains(r)))
                    { skippedReqs++; continue; }

                    if (!dryRun)
                    {
                        if (upgradeBehavior.AddClanUpgrade(hero.Clan, id))
                        {
                            BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, -cost, true);
                            owned.Add(id);
                            totalGold -= cost;
                            spent += cost;
                            upgraded++;
                        }
                    }
                    else
                    {
                        totalGold -= cost;
                        spent += cost;
                        upgraded++;
                    }
                }

                debugInfo = $"total={allUpgrades.Count} owned={skippedOwned} avail={available.Count} gold={totalGold} skippedGold={skippedGold} skippedReqs={skippedReqs}";
                Log.Info($"[BLTUpgrade] CLAN: {upgraded} added, {spent}g spent | {debugInfo}");
                return (upgraded, spent);
            }
            catch (Exception ex)
            {
                debugInfo = $"exception: {ex.Message}";
                Log.Exception("[BLTUpgrade] clan error", ex);
                return (0, 0);
            }
        }

        public static (int count, int spent) ExecuteFief(Hero hero, bool dryRun = false)
        {
            try
            {
                if (hero?.Clan == null) return (0, 0);

                var config = GetGlobalConfig();
                var upgradeBehavior = UpgradeBehavior.Current;
                if (config == null || upgradeBehavior == null) return (0, 0);

                var fiefUpgrades = GetPropertyValue<IEnumerable>(config, "FiefUpgrades");
                if (fiefUpgrades == null) return (0, 0);

                var fiefs = hero.Clan.Settlements.Where(s => s.IsTown || s.IsCastle).ToList();
                if (fiefs.Count == 0) return (0, 0);

                int totalGold = BLTAdoptAHeroCampaignBehavior.Current?.GetHeroGold(hero) ?? 0;
                int upgraded = 0;
                int spent = 0;

                var upgrades = fiefUpgrades.Cast<object>()
                    .OrderBy(u => GetPropertyValue<int>(u, "GoldCost"))
                    .ToList();

                foreach (var fief in fiefs)
                {
                    if (totalGold <= 0) break;

                    var owned = new HashSet<string>(upgradeBehavior.GetFiefUpgrades(fief));

                    foreach (var upgrade in upgrades)
                    {
                        var id = GetPropertyValue<string>(upgrade, "ID");
                        var cost = GetPropertyValue<int>(upgrade, "GoldCost");

                        if (owned.Contains(id)) continue;
                        if (totalGold < cost) continue;

                        if (GetPropertyValue<bool>(upgrade, "CoastalOnly") && !GetPropertyValue<bool>(fief, "IsCoastal"))
                            continue;

                        var required = GetPropertyValue<List<string>>(upgrade, "RequiredUpgradeIDs");
                        if (required?.Count > 0 && !required.All(r => owned.Contains(r)))
                            continue;

                        if (!dryRun)
                        {
                            if (upgradeBehavior.AddFiefUpgrade(fief, id))
                            {
                                BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, -cost, true);
                                owned.Add(id);
                                totalGold -= cost;
                                spent += cost;
                                upgraded++;
                            }
                        }
                        else
                        {
                            totalGold -= cost;
                            spent += cost;
                            upgraded++;
                        }
                    }
                }

                Log.Info($"[BLTUpgrade] FIEF: {upgraded} added, {spent}g spent");
                return (upgraded, spent);
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTUpgrade] fief error", ex);
                return (0, 0);
            }
        }

        public static (int count, int spent) ExecuteKingdom(Hero hero, bool dryRun = false)
        {
            try
            {
                var kingdom = hero?.Clan?.Kingdom;
                if (kingdom == null) return (0, 0);

                var config = GetGlobalConfig();
                var upgradeBehavior = UpgradeBehavior.Current;
                if (config == null || upgradeBehavior == null) return (0, 0);

                var kingdomUpgrades = GetPropertyValue<IEnumerable>(config, "KingdomUpgrades");
                if (kingdomUpgrades == null) return (0, 0);

                int totalGold = BLTAdoptAHeroCampaignBehavior.Current?.GetHeroGold(hero) ?? 0;
                int upgraded = 0;
                int spent = 0;

                var owned = new HashSet<string>(upgradeBehavior.GetKingdomUpgrades(kingdom));

                var available = kingdomUpgrades.Cast<object>()
                    .Where(u => !owned.Contains(GetPropertyValue<string>(u, "ID")))
                    .OrderBy(u => GetPropertyValue<int>(u, "GoldCost"))
                    .ToList();

                foreach (var upgrade in available)
                {
                    var id = GetPropertyValue<string>(upgrade, "ID");
                    var cost = GetPropertyValue<int>(upgrade, "GoldCost");

                    if (totalGold < cost) break;

                    var required = GetPropertyValue<List<string>>(upgrade, "RequiredUpgradeIDs");
                    if (required?.Count > 0 && !required.All(r => owned.Contains(r)))
                        continue;

                    if (!dryRun)
                    {
                        if (upgradeBehavior.AddKingdomUpgrade(kingdom, id))
                        {
                            BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, -cost, true);
                            owned.Add(id);
                            totalGold -= cost;
                            spent += cost;
                            upgraded++;
                        }
                    }
                    else
                    {
                        totalGold -= cost;
                        spent += cost;
                        upgraded++;
                    }
                }

                Log.Info($"[BLTUpgrade] KINGDOM: {upgraded} added, {spent}g spent");
                return (upgraded, spent);
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTUpgrade] kingdom error", ex);
                return (0, 0);
            }
        }

        private static object GetGlobalConfig()
        {
            try
            {
                // GlobalCommonConfig is internal — Type.GetType fails for internal types from external assemblies.
                // Use assembly scanning instead.
                var type = typeof(UpgradeBehavior).Assembly.GetTypes()
                    .FirstOrDefault(t => t.Name == "GlobalCommonConfig");
                if (type == null) return null;
                var method = type.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                return method?.Invoke(null, null);
            }
            catch { return null; }
        }

        private static T GetPropertyValue<T>(object obj, string propertyName)
        {
            try
            {
                if (obj == null) return default;
                var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null) return default;
                var val = prop.GetValue(obj);
                return val is T t ? t : default;
            }
            catch { return default; }
        }
    }


    // ======================================================================
    // BLTDuel
    // ======================================================================

public class BLTDuelModule : MBSubModuleBase
    {
        public BLTDuelModule()
        {
            ActionManager.RegisterAll(typeof(BLTDuelModule).Assembly);
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Log.Info("[BLTDuel] Loaded.");
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            mission.AddMissionBehavior(new DuelMissionBehavior());
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // COMMAND HANDLER — widz wpisuje !duel @nazwawidza
    // ════════════════════════════════════════════════════════════════════════════

    [DisplayName("Duel")]
    public class DuelCommand : ICommandHandler
    {
        public class DuelSettings { }
        public Type HandlerConfigType => typeof(DuelSettings);

        public void Execute(ReplyContext context, object config)
        {
            // Sprawdź czy jesteśmy w bitwie
            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            {
                ActionManager.SendReply(context, "Duel można wyzwać tylko podczas aktywnej bitwy.");
                return;
            }

            var behavior = Mission.Current.GetMissionBehavior<DuelMissionBehavior>();
            if (behavior == null)
            {
                ActionManager.SendReply(context, "System dueli nie jest aktywny.");
                return;
            }

            // Pobierz bohatera wzywającego
            var challengerHero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (challengerHero == null)
            {
                ActionManager.SendReply(context, "Nie masz adoptowanego bohatera.");
                return;
            }

            // Odczytaj cel z parametru komendy (np. "!duel Marek" lub "!duel @Marek")
            string targetName = context.Args?.Trim().TrimStart('@');
            if (string.IsNullOrEmpty(targetName))
            {
                ActionManager.SendReply(context, "Użycie: !duel @nazwawidza");
                return;
            }

            var targetHero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(targetName);
            if (targetHero == null)
            {
                ActionManager.SendReply(context, $"Nie znaleziono BLT bohatera '{targetName}'.");
                return;
            }

            if (targetHero == challengerHero)
            {
                ActionManager.SendReply(context, "Nie możesz wyzwać samego siebie.");
                return;
            }

            // Sprawdź czy obaj są w tej bitwie
            var challengerAgent = GetActiveAgent(challengerHero);
            var targetAgent     = GetActiveAgent(targetHero);

            if (challengerAgent == null)
            {
                ActionManager.SendReply(context, "Twój bohater nie jest przywołany w tej bitwie.");
                return;
            }
            if (targetAgent == null)
            {
                ActionManager.SendReply(context, $"{targetHero.FirstName} nie jest obecny w tej bitwie.");
                return;
            }

            // Sprawdź czy są po przeciwnych stronach
            if (challengerAgent.Team == targetAgent.Team)
            {
                ActionManager.SendReply(context,
                    $"{challengerHero.FirstName} i {targetHero.FirstName} są po tej samej stronie — duel niemożliwy!");
                return;
            }

            // Sprawdź czy nie ma już aktywnego duelu
            if (behavior.HasActiveDuel(challengerHero) || behavior.HasActiveDuel(targetHero))
            {
                ActionManager.SendReply(context, "Jeden z bohaterów już jest w duelu.");
                return;
            }

            // Zarejestruj duel i wydaj rozkazy
            behavior.StartDuel(challengerHero, targetHero, challengerAgent, targetAgent);

            var msg = $"⚔ DUEL! {challengerHero.FirstName} wyzwał {targetHero.FirstName}! Niech walka się rozpocznie!";
            ActionManager.SendReply(context, msg);
            Log.ShowInformation(msg, challengerHero.CharacterObject);
            BroadcastToChat(msg);
        }

        private static Agent GetActiveAgent(Hero hero)
            => Mission.Current?.Agents?.FirstOrDefault(a => BLTAdoptAHero.AgentExtensions.GetHero(a) == hero && a.IsActive());

        internal static void BroadcastToChat(string message)
        {
            try
            {
                var serviceType = Type.GetType("BannerlordTwitch.Twitch.TwitchService, BannerlordTwitch");
                if (serviceType == null) return;
                var currentProp = serviceType.GetProperty("Current",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var service = currentProp?.GetValue(null);
                if (service == null) return;
                var botField = serviceType.GetField("bot", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? serviceType.GetField("Bot", BindingFlags.Instance | BindingFlags.NonPublic);
                var bot = botField?.GetValue(service);
                if (bot == null) return;
                var sendChat = bot.GetType().GetMethod("SendChat",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                sendChat?.Invoke(bot, new object[] { new[] { message } });
            }
            catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // MISSION BEHAVIOR — zarządza aktywnymi duelami w trakcie bitwy
    // ════════════════════════════════════════════════════════════════════════════

    public class DuelMissionBehavior : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        private readonly Dictionary<Hero, Hero> activeDuels = new Dictionary<Hero, Hero>();
        private float nextRetargetTime = 0f;
        private const float RetargetInterval = 1.5f;

        public bool HasActiveDuel(Hero hero) => activeDuels.ContainsKey(hero);

        public void StartDuel(Hero a, Hero b, Agent agentA, Agent agentB)
        {
            activeDuels[a] = b;
            activeDuels[b] = a;
            IssueAttack(agentA, agentB);
            IssueAttack(agentB, agentA);
        }

        public override void OnMissionTick(float dt)
        {
            if (activeDuels.Count == 0) return;
            float now = Mission.Current?.CurrentTime ?? 0f;
            if (now < nextRetargetTime) return;
            nextRetargetTime = now + RetargetInterval;
            RefreshDuels();
        }

        private void RefreshDuels()
        {
            var toRemove = new List<Hero>();

            foreach (var (hero, opponent) in activeDuels)
            {
                // Iterujemy tylko jeden kierunek każdej pary
                if (!activeDuels.TryGetValue(opponent, out var back) || back != hero) continue;
                if (hero.GetHashCode() > opponent.GetHashCode()) continue;

                var heroAgent     = GetActiveAgent(hero);
                var opponentAgent = GetActiveAgent(opponent);

                bool heroDead     = heroAgent     == null || !heroAgent.IsActive();
                bool opponentDead = opponentAgent == null || !opponentAgent.IsActive();

                if (heroDead || opponentDead)
                {
                    toRemove.Add(hero);
                    toRemove.Add(opponent);

                    // Przywróć normalne AI temu kto przeżył
                    if (!heroDead)     ClearDuelAI(heroAgent);
                    if (!opponentDead) ClearDuelAI(opponentAgent);

                    Hero winner = heroDead && !opponentDead ? opponent
                                : !heroDead && opponentDead ? hero
                                : null;
                    Hero loser  = winner == hero ? opponent : hero;

                    string msg = winner == null
                        ? "⚔ Duel zakończony — obaj padli!"
                        : $"🏆 {winner.FirstName} pokonał {loser.FirstName} w pojedynku!";

                    Log.ShowInformation(msg, winner?.CharacterObject);
                    DuelCommand.BroadcastToChat(msg);
                }
                else
                {
                    // Odśwież cele
                    IssueAttack(heroAgent, opponentAgent);
                    IssueAttack(opponentAgent, heroAgent);
                }
            }

            foreach (var h in toRemove)
                activeDuels.Remove(h);
        }

        private static void IssueAttack(Agent attacker, Agent target)
        {
            try
            {
                // Przesuń agenta fizycznie w stronę celu — bez tego agent stoi w miejscu
                // i walczy z kimkolwiek jest blisko, zamiast biec do przeciwnika duelu
                var pos = target.GetWorldPosition();
                attacker.SetScriptedPosition(ref pos, false, Agent.AIScriptedFrameFlags.None);
                // Ustaw cel walki gdy wejdzie w zasięg
                attacker.SetTargetAgent(target);
            }
            catch { }
        }

        private static void ClearDuelAI(Agent agent)
        {
            if (agent == null || !agent.IsActive()) return;
            try
            {
                // Wróć do normalnego AI — formation lub bieżąca pozycja
                if (agent.Formation != null)
                    agent.Formation.SetMovementOrder(MovementOrder.MovementOrderCharge);
                else
                {
                    var cur = agent.GetWorldPosition();
                    agent.SetScriptedPosition(ref cur, false, Agent.AIScriptedFrameFlags.None);
                }
            }
            catch { }
        }

        private static Agent GetActiveAgent(Hero hero)
            => Mission.Current?.Agents?.FirstOrDefault(a => BLTAdoptAHero.AgentExtensions.GetHero(a) == hero && a.IsActive());
    }


    // ======================================================================
    // BLTClanGold
    // ======================================================================

public class BLTClanGoldModule : MBSubModuleBase
    {
        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Log.Info("[BLTClanGold] Loaded.");
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // CAMPAIGN BEHAVIOR — dzieli dochód klanu między BLT członków
    // ════════════════════════════════════════════════════════════════════════════

    public class ClanGoldBehavior : CampaignBehaviorBase
    {
        public static ClanGoldBehavior Current { get; private set; }

        // Zapamiętujemy złoto kampanijne klanu z poprzedniego dnia
        private readonly Dictionary<string, int> lastKnownClanGold = new Dictionary<string, int>();

        public override void RegisterEvents()
        {
            Current = this;
            CampaignEvents.DailyTickClanEvent.AddNonSerializedListener(this, OnDailyTickClan);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // Gold delta is recalculated daily — no persistence needed
        }

        private void OnDailyTickClan(Clan clan)
        {
            if (clan == null) return;

            // Zbierz wszystkich BLT bohaterów w klanie (żywych)
            var bltMembers = clan.Heroes
                .Where(h => h != null && h.IsAlive && h.IsAdopted())
                .ToList();

            // Potrzeba co najmniej 2 BLT bohaterów — inaczej nie ma kogo dzielić
            if (bltMembers.Count < 2) return;

            // Lider klanu musi być BLT bohaterem
            if (clan.Leader == null || !clan.Leader.IsAdopted()) return;

            string clanId = clan.StringId;
            int currentGold = clan.Gold;

            if (!lastKnownClanGold.TryGetValue(clanId, out int lastGold))
            {
                lastKnownClanGold[clanId] = currentGold;
                return;
            }

            int delta = currentGold - lastGold;
            lastKnownClanGold[clanId] = currentGold;

            // Interesuje nas tylko dodatni dochód
            if (delta <= 0) return;

            // Podziel równo między wszystkich BLT członków
            int sharePerMember = delta / bltMembers.Count;
            if (sharePerMember <= 0) return;

            var behavior = BLTAdoptAHeroCampaignBehavior.Current;
            if (behavior == null) return;

            foreach (var member in bltMembers)
            {
                behavior.ChangeHeroGold(member, sharePerMember);
            }

            Log.Info($"[BLTClanGold] Clan {clan.Name}: +{delta}g income split {bltMembers.Count} ways " +
                     $"({sharePerMember}g each) → {string.Join(", ", bltMembers.Select(h => h.FirstName?.Raw()))}");
        }
    }


    // ======================================================================
    // BLTGrail
    // ======================================================================

// ════════════════════════════════════════════════════════════════════════════
    // USTAWIENIA JEDNEGO QUESTA (Weapon lub Armor)
    // ════════════════════════════════════════════════════════════════════════════

    [DisplayName("Grail Quest Settings")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class GrailQuestSettings
    {
        [DisplayName("Enabled"),
         Description("Czy ten quest jest aktywny?"),
         PropertyOrder(1)]
        public bool Enabled { get; set; } = true;

        [DisplayName("Quest Name"),
         Description("Nazwa questa wyświetlana w wiadomościach."),
         PropertyOrder(2)]
        public string QuestName { get; set; } = "Grail Quest";

        [DisplayName("Daily Trigger Chance (0-1)"),
         Description("Szansa na pojawienie się questa każdego dnia. 0.02 = 2%."),
         Range(0.0, 1.0), Editor(typeof(SliderFloatEditor), typeof(SliderFloatEditor)),
         PropertyOrder(3)]
        public float DailyChance { get; set; } = 0.02f;

        [DisplayName("Battles Required"),
         Description("Ile bitew bohater musi przeżyć żeby ukończyć quest."),
         PropertyOrder(4)]
        public int BattlesRequired { get; set; } = 5;

        [DisplayName("Count Player Battles"),
         Description("Zawsze true — quest liczy tylko bitwy ze streamerem (MainHero)."),
         PropertyOrder(5)]
        public bool CountPlayerBattles { get; set; } = true;

        [DisplayName("Use Hero Class Item"),
         Description("Gdy true — nagrodą jest broń/ekwipunek dopasowany do klasy bohatera " +
                     "(łucznik dostaje łuk, wojownik miecz itd.). Ignoruje pole Item Type."),
         PropertyOrder(6)]
        public bool UseHeroClassItem { get; set; } = true;

        [DisplayName("Item Type"),
         Description("Typ nagrody gdy UseHeroClassItem = false."),
         PropertyOrder(7)]
        public RewardHelpers.RewardType ItemType { get; set; } = RewardHelpers.RewardType.Weapon;

        [DisplayName("Item Tier (1-6)"),
         Description("Tier przedmiotu nagrody. 6 = najlepszy z modifierem jak !smithweapon."),
         Range(1, 6),
         PropertyOrder(8)]
        public int ItemTier { get; set; } = 6;

        [DisplayName("Item Power"),
         Description("Mnożnik siły nagrody — tak samo jak suwak w smithweapon."),
         Range(0.1, 5.0), Editor(typeof(SliderFloatEditor), typeof(SliderFloatEditor)),
         PropertyOrder(9)]
        public float ItemPower { get; set; } = 1.6f;

        [DisplayName("Item Name"),
         Description("Nazwa nagrody. {ITEMNAME} = nazwa bazowego przedmiotu."),
         PropertyOrder(10)]
        public string ItemName { get; set; } = "Holy Grail {ITEMNAME}";

        [DisplayName("Quest Start Message"),
         Description("Wiadomość gdy quest startuje. {hero} = bohater, {battles} = liczba bitew, {quest} = nazwa."),
         PropertyOrder(10)]
        public string QuestStartMessage { get; set; } =
            "⚔ {quest}! {hero} musi przeżyć {battles} bitew by zdobyć legendarny item!";

        [DisplayName("Quest Progress Message"),
         Description("Wiadomość co bitwę. {hero}, {current}, {required}, {quest}."),
         PropertyOrder(11)]
        public string QuestProgressMessage { get; set; } =
            "🛡 [{quest}] {hero} przeżył bitwę! Postęp: {current}/{required}";

        [DisplayName("Quest Complete Message"),
         Description("Wiadomość po ukończeniu. {hero}, {quest}."),
         PropertyOrder(12)]
        public string QuestCompleteMessage { get; set; } =
            "🏆 {hero} ukończył {quest} i zdobył legendarny item!";

        [DisplayName("Quest Failed Message"),
         Description("Wiadomość gdy bohater zginie. {hero}, {quest}."),
         PropertyOrder(13)]
        public string QuestFailedMessage { get; set; } =
            "💀 {hero} poległ! {quest} przepadł...";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // GLOBALNY CONFIG — 2 oddzielne questy w BLTConfigure
    // ════════════════════════════════════════════════════════════════════════════

    [DisplayName("BLT Grail Config")]
    public class GrailConfig
    {
        private const string ID = "BLTGrail";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(GrailConfig));
        internal static GrailConfig Get() => ActionManager.GetGlobalConfig<GrailConfig>(ID);

        [DisplayName("Weapon Quest"),
         Description("Quest nagradzający legendarną bronią."),
         PropertyOrder(1)]
        public GrailQuestSettings WeaponQuest { get; set; } = new GrailQuestSettings
        {
            QuestName    = "Holy Blade Quest",
            ItemType     = RewardHelpers.RewardType.Weapon,
            ItemName     = "Holy Grail Blade",
            QuestCompleteMessage = "🏆 {hero} zdobył Święty Miecz Graala!",
            QuestFailedMessage   = "💀 {hero} poległ! Quest Świętego Miecza przepadł...",
        };

        [DisplayName("Armor Quest"),
         Description("Quest nagradzający legendarną zbroją."),
         PropertyOrder(2)]
        public GrailQuestSettings ArmorQuest { get; set; } = new GrailQuestSettings
        {
            QuestName    = "Holy Armor Quest",
            ItemType     = RewardHelpers.RewardType.Armor,
            ItemName     = "Holy Grail Armor",
            QuestCompleteMessage = "🏆 {hero} zdobył Świętą Zbroję Graala!",
            QuestFailedMessage   = "💀 {hero} poległ! Quest Świętej Zbroi przepadł...",
        };
    }

    // ════════════════════════════════════════════════════════════════════════════
    // STAN AKTYWNEGO QUESTA
    // ════════════════════════════════════════════════════════════════════════════

    public class GrailActiveQuest
    {
        public Hero             Hero            { get; set; }
        public GrailQuestSettings Settings      { get; set; }
        public int              BattlesSurvived { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // MODULE
    // ════════════════════════════════════════════════════════════════════════════

    public class BLTGrailModule : MBSubModuleBase
    {
        public BLTGrailModule() { GrailConfig.Register(); }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Log.Info("[BLTGrail] Loaded.");
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarter)
        {
            base.OnGameStart(game, gameStarter);
            if (gameStarter is CampaignGameStarter cs)
                cs.AddBehavior(new GrailCampaignBehavior());
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // CAMPAIGN BEHAVIOR
    // ════════════════════════════════════════════════════════════════════════════

    public class GrailCampaignBehavior : CampaignBehaviorBase
    {
        public static GrailCampaignBehavior Current { get; private set; }

        // Dwa niezależne sloty — weapon i armor mogą działać jednocześnie
        private GrailActiveQuest _weaponQuest;
        private GrailActiveQuest _armorQuest;

        public GrailActiveQuest WeaponQuest => _weaponQuest;
        public GrailActiveQuest ArmorQuest  => _armorQuest;

        public override void RegisterEvents()
        {
            Current = this;
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, OnHeroKilled);
        }

        public override void SyncData(IDataStore dataStore) { }

        // ── Daily tick ────────────────────────────────────────────────────────

        private void OnDailyTick()
        {
            var cfg = GrailConfig.Get();
            if (cfg == null) return;

            // Tylko jeden quest na raz — jeśli cokolwiek aktywne, nie startuj kolejnego
            if (_weaponQuest == null && _armorQuest == null)
            {
                // Losuj który typ questa odpali (żeby nie faworyzować broni)
                if (MBRandom.RandomInt(2) == 0)
                {
                    TryTriggerQuest(cfg.WeaponQuest, ref _weaponQuest);
                    if (_weaponQuest == null)
                        TryTriggerQuest(cfg.ArmorQuest, ref _armorQuest);
                }
                else
                {
                    TryTriggerQuest(cfg.ArmorQuest, ref _armorQuest);
                    if (_armorQuest == null)
                        TryTriggerQuest(cfg.WeaponQuest, ref _weaponQuest);
                }
            }

            // Pokaż postęp aktywnego questa w overlaycie
            ShowProgress(_weaponQuest);
            ShowProgress(_armorQuest);
        }

        private static void TryTriggerQuest(GrailQuestSettings settings, ref GrailActiveQuest slot)
        {
            if (!settings.Enabled) return;
            if (slot != null) return;  // już aktywny

            if (MBRandom.RandomFloat > settings.DailyChance) return;

            var candidates = Hero.AllAliveHeroes
                .Where(h => h != null && h.IsAdopted())
                .ToList();
            if (candidates.Count == 0) return;

            var chosen = candidates[MBRandom.RandomInt(candidates.Count)];
            slot = new GrailActiveQuest
            {
                Hero            = chosen,
                Settings        = settings,
                BattlesSurvived = 0,
            };

            var msg = settings.QuestStartMessage
                .Replace("{quest}",   settings.QuestName)
                .Replace("{hero}",    chosen.FirstName?.Raw() ?? chosen.Name?.Raw())
                .Replace("{battles}", settings.BattlesRequired.ToString());

            Notify(msg, chosen);
            Log.Info($"[BLTGrail] Quest '{settings.QuestName}' started for {chosen.Name}");
        }

        private static void ShowProgress(GrailActiveQuest quest)
        {
            if (quest == null) return;
            var msg = $"[{quest.Settings.QuestName}] {quest.Hero?.FirstName?.Raw()}: " +
                      $"{quest.BattlesSurvived}/{quest.Settings.BattlesRequired} bitew";
            Log.ShowInformation(msg, quest.Hero?.CharacterObject);
        }

        // ── MapEvent ended ────────────────────────────────────────────────────

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            ProcessQuestBattle(mapEvent, ref _weaponQuest);
            ProcessQuestBattle(mapEvent, ref _armorQuest);
        }

        private void ProcessQuestBattle(MapEvent mapEvent, ref GrailActiveQuest slot)
        {
            if (slot == null) return;
            var hero = slot.Hero;
            if (hero == null || !hero.IsAlive) return;

            // Liczymy TYLKO bitwy w których bierze udział MainHero (streamer)
            if (!mapEvent.IsPlayerMapEvent) return;

            bool participated = mapEvent.InvolvedParties
                .Any(p => p?.LeaderHero == hero
                       || p?.MemberRoster?.Contains(hero.CharacterObject) == true);
            if (!participated) return;

            slot.BattlesSurvived++;
            Log.Info($"[BLTGrail] '{slot.Settings.QuestName}' {hero.Name}: " +
                     $"{slot.BattlesSurvived}/{slot.Settings.BattlesRequired}");

            if (slot.BattlesSurvived >= slot.Settings.BattlesRequired)
            {
                CompleteQuest(ref slot);
            }
            else
            {
                var msg = slot.Settings.QuestProgressMessage
                    .Replace("{quest}",    slot.Settings.QuestName)
                    .Replace("{hero}",     hero.FirstName?.Raw())
                    .Replace("{current}",  slot.BattlesSurvived.ToString())
                    .Replace("{required}", slot.Settings.BattlesRequired.ToString());
                Notify(msg, hero);
            }
        }

        // ── Hero killed ───────────────────────────────────────────────────────

        private void OnHeroKilled(Hero victim, Hero killer,
            TaleWorlds.CampaignSystem.Actions.KillCharacterAction.KillCharacterActionDetail detail,
            bool showNotification)
        {
            FailQuestIfVictim(victim, ref _weaponQuest);
            FailQuestIfVictim(victim, ref _armorQuest);
        }

        private static void FailQuestIfVictim(Hero victim, ref GrailActiveQuest slot)
        {
            if (slot == null || slot.Hero != victim) return;
            var msg = slot.Settings.QuestFailedMessage
                .Replace("{quest}", slot.Settings.QuestName)
                .Replace("{hero}",  victim.FirstName?.Raw());
            Notify(msg, victim);
            Log.Info($"[BLTGrail] Quest '{slot.Settings.QuestName}' failed — {victim.Name} killed.");
            slot = null;
        }

        // ── Nagroda ───────────────────────────────────────────────────────────

        private static void CompleteQuest(ref GrailActiveQuest slot)
        {
            var hero     = slot.Hero;
            var settings = slot.Settings;
            slot = null;  // wyczyść przed GenerateReward (w razie wyjątku)

            try
            {
                var heroClass = BLTAdoptAHeroCampaignBehavior.Current?.GetClass(hero);
                // tier > 5 triggers custom modifier generation (same as !smithweapon passing tier=6)
                int tier = settings.ItemTier >= 6 ? 6 : Math.Max(0, settings.ItemTier - 1);
                var modifier = GetBLTModifiers();

                // GenerateRewardType(Weapon) już używa heroClass wewnętrznie — nie nadpisuj ItemType
                // Armor Quest zostaje Armor, Weapon Quest dobiera broń do klasy bohatera
                var rewardType = settings.ItemType;

                var (item, mod, itemSlot) = RewardHelpers.GenerateRewardType(
                    rewardType, tier, hero, heroClass,
                    allowDuplicates: true,
                    modifierDef: modifier,
                    customItemName: string.IsNullOrWhiteSpace(settings.ItemName) ? null : settings.ItemName,
                    customItemPower: settings.ItemPower);

                // Jeśli nie znaleziono itemka z klasą, spróbuj bez klasy
                if (item == null && heroClass != null)
                    (item, mod, itemSlot) = RewardHelpers.GenerateRewardType(
                        rewardType, tier, hero, null,
                        allowDuplicates: true, modifierDef: modifier,
                        customItemName: string.IsNullOrWhiteSpace(settings.ItemName) ? null : settings.ItemName,
                        customItemPower: settings.ItemPower);

                if (item == null)
                {
                    Log.Error($"[BLTGrail] Could not generate reward for {hero.Name}");
                    return;
                }

                // Najpierw zarejestruj item przez BLT (dodaje do storage)
                RewardHelpers.AssignCustomReward(hero, item, mod, itemSlot);

                // Force-equip: GrailQuest to nagroda legendarana — wkładamy bezpośrednio do slotu
                // nawet jeśli ShouldReplaceItem by odmówił
                string reward = item.Name?.ToString() ?? "Holy Grail Item";
                if (itemSlot != EquipmentIndex.None)
                {
                    try
                    {
                        hero.BattleEquipment[itemSlot] = new EquipmentElement(item, mod);
                        hero.CivilianEquipment[itemSlot] = new EquipmentElement(item, mod);
                    }
                    catch { }
                }
                else
                {
                    // Brak konkretnego slotu — szukaj pierwszego pasującego wolnego lub najsłabszego
                    for (var idx = EquipmentIndex.WeaponItemBeginSlot; idx < EquipmentIndex.NumAllWeaponSlots; idx++)
                    {
                        var current = hero.BattleEquipment[idx];
                        if (current.IsEmpty || (!BLTAdoptAHeroCampaignBehavior.Current
                                .GetCustomItems(hero).Any(ci => ci.Item == current.Item)))
                        {
                            try
                            {
                                hero.BattleEquipment[idx] = new EquipmentElement(item, mod);
                                hero.CivilianEquipment[idx] = new EquipmentElement(item, mod);
                                break;
                            }
                            catch { }
                        }
                    }
                }

                var msg = settings.QuestCompleteMessage
                    .Replace("{quest}", settings.QuestName)
                    .Replace("{hero}",  hero.FirstName?.Raw())
                    + $" [{reward}]";

                Notify(msg, hero);
                Log.Info($"[BLTGrail] Quest '{settings.QuestName}' complete! {hero.Name} received: {reward}");
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTGrail] CompleteQuest failed", ex);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Pobiera CustomRewardModifiers z BLTAdoptAHeroModule.CommonConfig (internal)
        // — ten sam modifier co !smithweapon używa
        private static RandomItemModifierDef GetBLTModifiers()
        {
            try
            {
                var moduleType = typeof(BLTAdoptAHeroCampaignBehavior).Assembly
                    .GetTypes().FirstOrDefault(t => t.Name == "BLTAdoptAHeroModule");
                var prop = moduleType?.GetProperty("CommonConfig",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var commonConfig = prop?.GetValue(null);
                var modProp = commonConfig?.GetType()
                    .GetProperty("CustomRewardModifiers",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return modProp?.GetValue(commonConfig) as RandomItemModifierDef
                    ?? new RandomItemModifierDef();
            }
            catch { return new RandomItemModifierDef(); }
        }

        private static void Notify(string message, Hero hero = null)
        {
            Log.ShowInformation(message, hero?.CharacterObject);
            try
            {
                var serviceType = Type.GetType("BannerlordTwitch.Twitch.TwitchService, BannerlordTwitch");
                var service = serviceType
                    ?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(null);
                var bot = service?.GetType()
                    .GetField("bot", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(service);
                bot?.GetType()
                    .GetMethod("SendChat", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.Invoke(bot, new object[] { new[] { message } });
            }
            catch { }
        }
    }


    // ======================================================================
    // BLTAuras
    // ======================================================================

public class BLTAurasModule : MBSubModuleBase
    {
        private static Harmony harmony;

        public BLTAurasModule()
        {
            ActionManager.RegisterAll(typeof(BLTAurasModule).Assembly);
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            if (harmony != null) return;
            try
            {
                harmony = new Harmony("mod.bannerlord.bltauras");
                harmony.PatchAll();
                Log.Info("[BLTAuras] Loaded: PoisonStrike / Berserk / LastStand / Taunt / Auras / Kick / JumpAttack / Teleport");
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTAuras] Load failed", ex);
            }
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 1. POISON STRIKE
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    public enum PoisonApplyOn { Both, RangedOnly, MeleeOnly }

    [DisplayName("Poison Strike"),
     Description("Each hit poisons the target, dealing damage over time every 2 seconds"),
     UsedImplicitly]
    public class PoisonStrikePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Poison Damage Per Tick"), UsedImplicitly]
        public int PoisonDamagePerTick { get; set; } = 10;

        [DisplayName("Poison Duration (ticks)"), UsedImplicitly]
        public int PoisonDurationTicks { get; set; } = 5;

        [DisplayName("Apply On"), UsedImplicitly]
        public PoisonApplyOn ApplyOn { get; set; } = PoisonApplyOn.Both;

        [DisplayName("Show Poison Contour"), UsedImplicitly]
        public bool ShowPoisonContour { get; set; } = true;

        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly]
        public string PoisonContourColor { get; set; } = "FF00CC00";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var poisonedAgents = new Dictionary<Agent, int>();

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !victim.IsActive()) return;
                if (attacker == null || !victim.IsEnemyOf(attacker)) return;
                if (ApplyOn != PoisonApplyOn.Both)
                {
                    bool isRanged = attacker != null
                        && !attacker.WieldedWeapon.IsEmpty
                        && attacker.WieldedWeapon.CurrentUsageItem?.IsRangedWeapon == true;
                    if (ApplyOn == PoisonApplyOn.RangedOnly && !isRanged) return;
                    if (ApplyOn == PoisonApplyOn.MeleeOnly && isRanged) return;
                }
                poisonedAgents[victim] = PoisonDurationTicks;
                if (ShowPoisonContour)
                    try { victim.AgentVisuals?.SetContourColor(Convert.ToUInt32(PoisonContourColor, 16), true); }
                    catch { victim.AgentVisuals?.SetContourColor(0xFF00CC00u, true); }
            };

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                foreach (var key in poisonedAgents.Keys.ToList())
                {
                    if (key == null || !key.IsActive()) { poisonedAgents.Remove(key); continue; }
                    try
                    {
                        var dir = Vec3.Up;
                        var blow = new Blow(heroAgent.Index)
                        {
                            AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Pierce,
                            BoneIndex = key.Monster.HeadLookDirectionBoneIndex, GlobalPosition = key.Position,
                            BaseMagnitude = PoisonDamagePerTick, InflictedDamage = PoisonDamagePerTick,
                            SwingDirection = dir, Direction = dir, DamageCalculated = true,
                            VictimBodyPart = BoneBodyPartType.Chest,
                        };
                        key.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, key, blow));
                    }
                    catch { }
                    poisonedAgents[key]--;
                    if (poisonedAgents[key] <= 0)
                    {
                        if (ShowPoisonContour) try { key.AgentVisuals?.SetContourColor(null, false); } catch { }
                        poisonedAgents.Remove(key);
                    }
                }
            };

            void Cleanup()
            {
                if (ShowPoisonContour)
                    foreach (var a in poisonedAgents.Keys) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                poisonedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Poison: {PoisonDamagePerTick} dmg/2s for {PoisonDurationTicks * 2}s";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 2. BERSERK
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Berserk"),
     Description("The lower the hero's HP, the more damage they deal"),
     UsedImplicitly]
    public class BerserkPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Max Damage Bonus (%)"), UsedImplicitly]
        public float MaxDamageBonusPercent { get; set; } = 75f;

        [DisplayName("Threshold HP (%)"), UsedImplicitly]
        public float ThresholdHpPercent { get; set; } = 80f;

        [DisplayName("Show Berserk Contour"), UsedImplicitly]
        public bool ShowContour { get; set; } = true;

        [DisplayName("Berserk Contour Color (hex AARRGGBB)"), UsedImplicitly]
        public string ContourColor { get; set; } = "FF8B0000";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            Agent trackedAgent = null;
            bool berserkActive = false;

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (attacker == null || attacker.HealthLimit <= 0) return;
                var current = hero.GetAgent();
                if (current != null && current != trackedAgent)
                {
                    if (berserkActive && ShowContour)
                        try { trackedAgent?.AgentVisuals?.SetContourColor(null, false); } catch { }
                    trackedAgent = current;
                    berserkActive = false;
                }

                float hpRatio = attacker.Health / attacker.HealthLimit;
                float threshold = ThresholdHpPercent / 100f;
                if (hpRatio >= threshold)
                {
                    if (berserkActive && ShowContour)
                        try { attacker.AgentVisuals?.SetContourColor(null, false); } catch { }
                    berserkActive = false;
                    return;
                }

                if (!berserkActive)
                {
                    berserkActive = true;
                    Log.ShowInformation($"BERSERK! {hero.Name} rages, up to +{MaxDamageBonusPercent:0}% DMG!", hero.CharacterObject);
                    if (ShowContour)
                        try
                        {
                            uint color = Convert.ToUInt32(ContourColor, 16);
                            attacker.AgentVisuals?.SetContourColor(color, true);
                        }
                        catch { attacker.AgentVisuals?.SetContourColor(0xFF8B0000u, true); }
                }
                float berserkRatio = 1f - (hpRatio / threshold);
                float multiplier = 1f + (MaxDamageBonusPercent / 100f) * berserkRatio;
                blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * multiplier);
            };

            void Cleanup()
            {
                if (berserkActive && ShowContour)
                    try { trackedAgent?.AgentVisuals?.SetContourColor(null, false); } catch { }
                berserkActive = false;
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Berserk: up to +{MaxDamageBonusPercent:0}% DMG below {ThresholdHpPercent:0}% HP";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 3. LAST STAND
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Last Stand"),
     Description("When HP drops below threshold, triggers a temporary massive buff"),
     UsedImplicitly]
    public class LastStandPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("HP Trigger Threshold (%)"), UsedImplicitly]
        public float TriggerHpPercent { get; set; } = 20f;

        [DisplayName("Damage Bonus (%)"), UsedImplicitly]
        public float DamageBonusPercent { get; set; } = 100f;

        [DisplayName("Damage Reduction (%)"), UsedImplicitly]
        public float DamageReductionPercent { get; set; } = 50f;

        [DisplayName("Duration (seconds)"), UsedImplicitly]
        public float DurationSeconds { get; set; } = 10f;

        [DisplayName("Once Per Battle"), UsedImplicitly]
        public bool OncePerBattle { get; set; } = true;

        [DisplayName("Show Last Stand Contour"), UsedImplicitly]
        public bool ShowContour { get; set; } = true;

        [DisplayName("Last Stand Contour Color (hex AARRGGBB)"), UsedImplicitly]
        public string ContourColor { get; set; } = "FFFFFFFF";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            Agent trackedAgent = null;
            bool triggered = false;
            float expiryTime = 0f;

            void CheckReset()
            {
                var cur = hero.GetAgent();
                if (cur != null && cur != trackedAgent)
                {
                    if (triggered && ShowContour)
                        try { trackedAgent?.AgentVisuals?.SetContourColor(null, false); } catch { }
                    trackedAgent = cur;
                    triggered = false;
                    expiryTime = 0f;
                }
            }

            handlers.OnTakeDamage += (victim, attacker, blowParams) =>
            {
                CheckReset();
                if (triggered && OncePerBattle) return;
                if (victim.HealthLimit <= 0) return;
                float newHp = victim.Health - blowParams.blow.InflictedDamage;
                if (newHp > victim.HealthLimit * TriggerHpPercent / 100f) return;
                triggered = true;
                expiryTime = (Mission.Current?.CurrentTime ?? 0f) + DurationSeconds;
                Log.ShowInformation($"LAST STAND! {hero.Name} fights to the death!", hero.CharacterObject);
                if (ShowContour)
                    try
                    {
                        uint color = Convert.ToUInt32(ContourColor, 16);
                        victim.AgentVisuals?.SetContourColor(color, true);
                    }
                    catch { victim.AgentVisuals?.SetContourColor(0xFFFFFFFFu, true); }
            };

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (!triggered) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now > expiryTime)
                {
                    if (ShowContour) try { attacker.AgentVisuals?.SetContourColor(null, false); } catch { }
                    return;
                }
                blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * (1f + DamageBonusPercent / 100f));
            };

            handlers.OnTakeDamage += (victim, attacker, blowParams) =>
            {
                if (!triggered) return;
                if ((Mission.Current?.CurrentTime ?? 0f) > expiryTime) return;
                blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * (1f - DamageReductionPercent / 100f));
            };

            void Cleanup()
            {
                if (triggered && ShowContour)
                    try { trackedAgent?.AgentVisuals?.SetContourColor(null, false); } catch { }
                triggered = false;
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Last Stand: below {TriggerHpPercent:0}% HP -> +{DamageBonusPercent:0}% DMG, -{DamageReductionPercent:0}% DMG taken for {DurationSeconds:0}s";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 4. TAUNT
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Taunt"),
     Description("Enemies within range are forced to target this hero"),
     UsedImplicitly]
    public class TauntPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Taunt Range (meters)"), UsedImplicitly]
        public float TauntRange { get; set; } = 8f;

        [DisplayName("Max Enemies To Taunt"), UsedImplicitly]
        public int MaxEnemies { get; set; } = 10;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var nearbyBuffer = new MBList<Agent>();

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                nearbyBuffer.Clear();
                int taunted = 0;
                foreach (var enemy in Mission.Current
                    .GetNearbyAgents(heroAgent.Position.AsVec2, TauntRange, nearbyBuffer)
                    .Where(a => a.IsActive() && a.IsEnemyOf(heroAgent) && !a.IsMount && a.GetAdoptedHero() == null))
                {
                    if (taunted >= MaxEnemies) break;
                    try { enemy.SetTargetAgent(heroAgent); } catch { }
                    taunted++;
                }
            };
        }

        public override LocString Description =>
            $"Taunt: forces up to {MaxEnemies} enemies within {TauntRange:0}m to target this hero";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 5. HEAL AURA
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Heal Aura"),
     Description("Heals all friendly units within range every 2 seconds"),
     UsedImplicitly]
    public class HealAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Heal Per Tick"), UsedImplicitly]
        public float HealPerTick { get; set; } = 5f;

        [DisplayName("Heal Range (meters)"), UsedImplicitly]
        public float HealRange { get; set; } = 10f;

        [DisplayName("Heal Self"), UsedImplicitly]
        public bool HealSelf { get; set; } = true;

        [DisplayName("Show Heal Contour"), UsedImplicitly]
        public bool ShowHealContour { get; set; } = true;

        [DisplayName("Heal Contour Color (hex AARRGGBB)"), UsedImplicitly]
        public string HealContourColor { get; set; } = "FF00FF44";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var nearbyBuffer = new MBList<Agent>();
            var lastInRange = new HashSet<Agent>();

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive())
                {
                    foreach (var a in lastInRange) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                    lastInRange.Clear();
                    return;
                }
                nearbyBuffer.Clear();
                var nowInRange = new HashSet<Agent>(Mission.Current
                    .GetNearbyAgents(heroAgent.Position.AsVec2, HealRange, nearbyBuffer)
                    .Where(a => a.IsActive() && !a.IsMount && a.IsFriendOf(heroAgent)));

                foreach (var a in lastInRange)
                    if (!nowInRange.Contains(a)) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }

                if (ShowHealContour)
                {
                    uint color = Convert.ToUInt32(HealContourColor, 16);
                    foreach (var a in nowInRange) try { a.AgentVisuals?.SetContourColor(color, true); } catch { }
                }
                foreach (var ally in nowInRange)
                {
                    if (!HealSelf && ally == heroAgent) continue;
                    ally.Health = Math.Min(ally.Health + HealPerTick, ally.HealthLimit);
                }
                lastInRange.Clear();
                foreach (var a in nowInRange) lastInRange.Add(a);
            };

            void Cleanup()
            {
                foreach (var a in lastInRange) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                lastInRange.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Heal Aura: +{HealPerTick:0} HP/2s to all allies within {HealRange:0}m";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 6. DAMAGE AURA
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Damage Aura"),
     Description("Deals damage to all enemies within range every 2 seconds"),
     UsedImplicitly]
    public class DamageAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Damage Per Tick"), UsedImplicitly]
        public int DamagePerTick { get; set; } = 8;

        [DisplayName("Damage Range (meters)"), UsedImplicitly]
        public float DamageRange { get; set; } = 8f;

        [DisplayName("Show Damage Contour"), UsedImplicitly]
        public bool ShowDamageContour { get; set; } = true;

        [DisplayName("Damage Contour Color (hex AARRGGBB)"), UsedImplicitly]
        public string DamageContourColor { get; set; } = "FFFF2200";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var nearbyBuffer = new MBList<Agent>();
            var lastInRange = new HashSet<Agent>();

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive())
                {
                    foreach (var a in lastInRange) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                    lastInRange.Clear();
                    return;
                }
                nearbyBuffer.Clear();
                var nowInRange = new HashSet<Agent>(Mission.Current
                    .GetNearbyAgents(heroAgent.Position.AsVec2, DamageRange, nearbyBuffer)
                    .Where(a => a.IsActive() && !a.IsMount && a.IsEnemyOf(heroAgent)));

                foreach (var a in lastInRange)
                    if (!nowInRange.Contains(a)) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }

                if (ShowDamageContour)
                {
                    uint color = Convert.ToUInt32(DamageContourColor, 16);
                    foreach (var a in nowInRange) try { a.AgentVisuals?.SetContourColor(color, true); } catch { }
                }
                foreach (var enemy in nowInRange)
                {
                    try
                    {
                        var dir = Vec3.Up;
                        var blow = new Blow(heroAgent.Index)
                        {
                            AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Pierce,
                            BoneIndex = enemy.Monster.HeadLookDirectionBoneIndex, GlobalPosition = enemy.Position,
                            BaseMagnitude = DamagePerTick, InflictedDamage = DamagePerTick,
                            SwingDirection = dir, Direction = dir, DamageCalculated = true,
                            VictimBodyPart = BoneBodyPartType.Chest,
                        };
                        enemy.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, enemy, blow));
                    }
                    catch { }
                }
                lastInRange.Clear();
                foreach (var a in nowInRange) lastInRange.Add(a);
            };

            void Cleanup()
            {
                foreach (var a in lastInRange) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                lastInRange.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Damage Aura: {DamagePerTick} dmg/2s to all enemies within {DamageRange:0}m";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 7. COMMANDER AURA (Reward)
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Commander Aura"),
     Description("Channel points reward: buffs all allied units near the hero for the rest of the battle"),
     UsedImplicitly]
    public class CommanderAuraReward : IRewardHandler
    {
        public Type RewardConfigType => typeof(CommanderAuraSettings);

        public class CommanderAuraSettings
        {
            [DisplayName("Damage Bonus (%)"), UsedImplicitly] public float DamageBonusPercent { get; set; } = 25f;
            [DisplayName("Armor Bonus (%)"), UsedImplicitly] public float ArmorBonusPercent { get; set; } = 20f;
            [DisplayName("Speed Bonus (%)"), UsedImplicitly] public float SpeedBonusPercent { get; set; } = 15f;
            [DisplayName("Aura Range (meters)"), UsedImplicitly] public float Range { get; set; } = 20f;
            [DisplayName("Duration (seconds)"), UsedImplicitly] public float DurationSeconds { get; set; } = 60f;
            [DisplayName("Gold Cost"), UsedImplicitly] public int GoldCost { get; set; } = 0;
        }

        public void Enqueue(ReplyContext context, object config)
        {
            var settings = config as CommanderAuraSettings ?? new CommanderAuraSettings();
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You don't have an adopted hero."); return; }

            var heroAgent = hero.GetAgent();
            if (heroAgent == null || !heroAgent.IsActive()) { ActionManager.SendReply(context, "Your hero is not in an active battle."); return; }

            if (settings.GoldCost > 0)
            {
                int gold = BLTAdoptAHeroCampaignBehavior.Current?.GetHeroGold(hero) ?? 0;
                if (gold < settings.GoldCost) { ActionManager.SendReply(context, $"Not enough BLT gold (need {settings.GoldCost}, have {gold})."); return; }
                BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, -settings.GoldCost, true);
            }

            var nearbyBuffer = new MBList<Agent>();
            var allies = Mission.Current.GetNearbyAgents(heroAgent.Position.AsVec2, settings.Range, nearbyBuffer)
                .Where(a => a.IsActive() && !a.IsMount && a.IsFriendOf(heroAgent)).ToList();

            if (allies.Count == 0) { ActionManager.SendReply(context, "No allies in range to buff."); return; }

            var modifierConfig = BuildModifierConfig(settings);
            foreach (var ally in allies)
            {
                try
                {
                    if (settings.DurationSeconds > 0f)
                        BLTTimedBuffBehavior.AddTimedBuff(ally, modifierConfig, settings.DurationSeconds);
                    else
                        BLTAgentModifierBehavior.Current?.Add(ally, modifierConfig);
                }
                catch { }
            }

            string durationStr = settings.DurationSeconds > 0f ? $" for {settings.DurationSeconds:0}s" : " for the rest of the battle";
            Log.ShowInformation($"Commander Aura! {allies.Count} allies buffed{durationStr}!", hero.CharacterObject);
            ActionManager.SendReply(context, $"Commander Aura! {allies.Count} allies: +{settings.DamageBonusPercent:0}% DMG, +{settings.ArmorBonusPercent:0}% armor, +{settings.SpeedBonusPercent:0}% speed{durationStr}.");
        }

        private static AgentModifierConfig BuildModifierConfig(CommanderAuraSettings s)
        {
            var config = new AgentModifierConfig();
            if (s.DamageBonusPercent != 0)
                config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f + s.DamageBonusPercent });
            if (s.ArmorBonusPercent != 0)
                config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorHead, ModifierPercent = 100f + s.ArmorBonusPercent });
            if (s.SpeedBonusPercent != 0)
                config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.MaxSpeedMultiplier, ModifierPercent = 100f + s.SpeedBonusPercent });
            return config;
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 8. CURSE AURA
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Curse Aura"),
     Description("Passive aura: slows nearby enemies, dismounts riders, and can knock them down"),
     UsedImplicitly]
    public class CurseAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Range (m)"), UsedImplicitly] public float AuraRange { get; set; } = 8f;
        [DisplayName("Speed Slow (%)"), UsedImplicitly] public float SpeedSlowPercent { get; set; } = 40f;
        [DisplayName("Attack Speed Slow (%)"), UsedImplicitly] public float AttackSlowPercent { get; set; } = 30f;
        [DisplayName("Tick Interval (seconds)"), UsedImplicitly] public float TickInterval { get; set; } = 2f;
        [DisplayName("Dismount Enabled"), UsedImplicitly] public bool DismountEnabled { get; set; } = true;
        [DisplayName("Dismount Chance Per Tick (%)"), UsedImplicitly] public float DismountChancePercent { get; set; } = 20f;
        [DisplayName("Knockdown Enabled"), UsedImplicitly] public bool KnockdownEnabled { get; set; } = true;
        [DisplayName("Knockdown Chance Per Tick (%)"), UsedImplicitly] public float KnockdownChancePercent { get; set; } = 10f;
        [DisplayName("Weapon Drop Enabled"), UsedImplicitly] public bool WeaponDropEnabled { get; set; } = true;
        [DisplayName("Weapon Drop Chance Per Tick (%)"), UsedImplicitly] public float WeaponDropChancePercent { get; set; } = 5f;
        [DisplayName("Show Contour"), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FF8800FF";
        [DisplayName("Show Message"), UsedImplicitly] public bool ShowMessage { get; set; } = true;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var nearbyBuffer = new MBList<Agent>();
            var cursedAgents = new HashSet<Agent>();
            float lastTick = 0f;

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive())
                {
                    RestoreAll(cursedAgents);
                    cursedAgents.Clear();
                    return;
                }
                nearbyBuffer.Clear();
                var nowInRange = new HashSet<Agent>(Mission.Current
                    .GetNearbyAgents(heroAgent.Position.AsVec2, AuraRange, nearbyBuffer)
                    .Where(a => a.IsActive() && !a.IsMount && a.IsEnemyOf(heroAgent)));

                foreach (var a in cursedAgents)
                {
                    if (!nowInRange.Contains(a))
                    {
                        RestoreAgent(a);
                        if (ShowContour) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                    }
                }
                if (SpeedSlowPercent > 0f || AttackSlowPercent > 0f)
                    foreach (var a in nowInRange)
                        if (!cursedAgents.Contains(a)) ApplyCurse(a);

                if (ShowContour)
                {
                    uint color = Convert.ToUInt32(ContourColor, 16);
                    foreach (var a in nowInRange) try { a.AgentVisuals?.SetContourColor(color, true); } catch { }
                }
                cursedAgents.Clear();
                foreach (var a in nowInRange) cursedAgents.Add(a);
            };

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastTick < TickInterval) return;
                lastTick = now;

                foreach (var enemy in cursedAgents.ToList())
                {
                    if (enemy == null || !enemy.IsActive()) continue;
                    if (DismountEnabled && enemy.MountAgent != null && enemy.MountAgent.IsActive() &&
                        MBRandom.RandomFloat * 100f < DismountChancePercent)
                    {
                        try
                        {
                            var mount = enemy.MountAgent;
                            var dir = Vec3.Up;
                            var blow = new Blow(heroAgent.Index)
                            {
                                AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Blunt,
                                BoneIndex = mount.Monster.HeadLookDirectionBoneIndex, GlobalPosition = mount.Position,
                                BaseMagnitude = mount.HealthLimit, InflictedDamage = (int)mount.HealthLimit,
                                SwingDirection = dir, Direction = dir, DamageCalculated = true,
                                VictimBodyPart = BoneBodyPartType.Chest,
                            };
                            mount.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, mount, blow));
                        }
                        catch { }
                    }
                    if (KnockdownEnabled && !enemy.HasMount && MBRandom.RandomFloat * 100f < KnockdownChancePercent)
                    {
                        try
                        {
                            var fallAction = ActionIndexCache.Create("act_strike_fall_back_heavy_back_rise");
                            enemy.SetActionChannel(0, fallAction, ignorePriority: true, 0UL);
                        }
                        catch { }
                    }
                    if (WeaponDropEnabled && MBRandom.RandomFloat * 100f < WeaponDropChancePercent)
                    {
                        try
                        {
                            for (var wi = EquipmentIndex.WeaponItemBeginSlot; wi < EquipmentIndex.NumAllWeaponSlots; wi++)
                            {
                                if (!enemy.Equipment[wi].IsEmpty)
                                {
                                    enemy.DropItem(wi);
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
            };

            if (ShowMessage)
            {
                bool announced = false;
                handlers.OnSlowTick += dt =>
                {
                    if (announced) return;
                    var heroAgent = hero.GetAgent();
                    if (heroAgent != null && heroAgent.IsActive() && cursedAgents.Count > 0)
                    {
                        announced = true;
                        Log.ShowInformation($"CURSE AURA! {hero.Name} weakens nearby foes!", hero.CharacterObject);
                    }
                };
            }

            void Cleanup() { RestoreAll(cursedAgents); cursedAgents.Clear(); }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        private void ApplyCurse(Agent agent)
        {
            try
            {
                if (SpeedSlowPercent > 0f) agent.SetMaximumSpeedLimit(1f - SpeedSlowPercent / 100f, true);
                if (AttackSlowPercent > 0f)
                {
                    var config = new AgentModifierConfig();
                    config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f - AttackSlowPercent });
                    BLTAgentModifierBehavior.Current?.Add(agent, config);
                }
            }
            catch { }
        }

        private static void RestoreAgent(Agent agent)
        {
            if (agent == null || !agent.IsActive()) return;
            try { agent.SetMaximumSpeedLimit(1f, true); } catch { }
        }

        private static void RestoreAll(HashSet<Agent> agents)
        {
            foreach (var a in agents) RestoreAgent(a);
        }

        public override LocString Description =>
            $"Curse Aura: slows enemies {SpeedSlowPercent:0}% move/{AttackSlowPercent:0}% attack in {AuraRange:0}m";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 9. BUFF AURA
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Buff Aura"),
     Description("Passive aura: buffs all friendly units in range"),
     UsedImplicitly]
    public class BuffAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Range (m)"), UsedImplicitly] public float AuraRange { get; set; } = 10f;
        [DisplayName("Damage Bonus (%)"), UsedImplicitly] public float DamageBonusPercent { get; set; } = 20f;
        [DisplayName("Armor Bonus (%)"), UsedImplicitly] public float ArmorBonusPercent { get; set; } = 20f;
        [DisplayName("Move Speed Bonus (%)"), UsedImplicitly] public float MoveSpeedBonusPercent { get; set; } = 15f;
        [DisplayName("Attack Speed Bonus (%)"), UsedImplicitly] public float AttackSpeedBonusPercent { get; set; } = 15f;
        [DisplayName("Show Contour"), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FFFFD700";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var nearbyBuffer = new MBList<Agent>();
            var buffedAgents = new HashSet<Agent>();

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive())
                {
                    foreach (var a in buffedAgents) { RemoveBuff(a); if (ShowContour) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { } }
                    buffedAgents.Clear();
                    return;
                }
                nearbyBuffer.Clear();
                var nowInRange = new HashSet<Agent>(Mission.Current
                    .GetNearbyAgents(heroAgent.Position.AsVec2, AuraRange, nearbyBuffer)
                    .Where(a => a.IsActive() && !a.IsMount && a.IsFriendOf(heroAgent)));

                foreach (var a in buffedAgents)
                    if (!nowInRange.Contains(a)) { RemoveBuff(a); if (ShowContour) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { } }

                foreach (var a in nowInRange)
                    if (!buffedAgents.Contains(a)) ApplyBuff(a);

                if (ShowContour)
                {
                    uint color = Convert.ToUInt32(ContourColor, 16);
                    foreach (var a in nowInRange) try { a.AgentVisuals?.SetContourColor(color, true); } catch { }
                }
                buffedAgents.Clear();
                foreach (var a in nowInRange) buffedAgents.Add(a);
            };

            void Cleanup()
            {
                foreach (var a in buffedAgents) { RemoveBuff(a); if (ShowContour) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { } }
                buffedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        private void ApplyBuff(Agent a)
        {
            try
            {
                var config = new AgentModifierConfig();
                if (DamageBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f + DamageBonusPercent });
                if (ArmorBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorHead, ModifierPercent = 100f + ArmorBonusPercent });
                if (MoveSpeedBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.MaxSpeedMultiplier, ModifierPercent = 100f + MoveSpeedBonusPercent });
                if (AttackSpeedBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ThrustOrRangedReadySpeedMultiplier, ModifierPercent = 100f + AttackSpeedBonusPercent });
                if (config.Properties.Count > 0) BLTAgentModifierBehavior.Current?.Add(a, config);
            }
            catch { }
        }

        private void RemoveBuff(Agent a)
        {
            if (a == null || !a.IsActive()) return;
            try
            {
                var config = new AgentModifierConfig();
                if (DamageBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 10000f / (100f + DamageBonusPercent) });
                if (ArmorBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorHead, ModifierPercent = 10000f / (100f + ArmorBonusPercent) });
                if (MoveSpeedBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.MaxSpeedMultiplier, ModifierPercent = 10000f / (100f + MoveSpeedBonusPercent) });
                if (AttackSpeedBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ThrustOrRangedReadySpeedMultiplier, ModifierPercent = 10000f / (100f + AttackSpeedBonusPercent) });
                if (config.Properties.Count > 0) BLTAgentModifierBehavior.Current?.Add(a, config);
            }
            catch { }
        }

        public override LocString Description =>
            $"Buff Aura: allies in {AuraRange:0}m get +{DamageBonusPercent:0}% DMG +{ArmorBonusPercent:0}% armor +{MoveSpeedBonusPercent:0}% spd";
    }

    // TIMED BUFF TRACKER
    internal class BLTTimedBuffBehavior : MissionBehavior
    {
        public static BLTTimedBuffBehavior Current { get; private set; }
        private readonly List<(Agent agent, float expiry, AgentModifierConfig negator)> pending
            = new List<(Agent, float, AgentModifierConfig)>();

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public static void AddTimedBuff(Agent agent, AgentModifierConfig config, float duration)
        {
            BLTAgentModifierBehavior.Current?.Add(agent, config);
            var negator = new AgentModifierConfig();
            foreach (var prop in config.Properties)
                negator.Properties.Add(new PropertyModifierDef { Name = prop.Name, ModifierPercent = prop.ModifierPercent > 0f ? 10000f / prop.ModifierPercent : 100f });
            float expiry = (Mission.Current?.CurrentTime ?? 0f) + duration;
            if (Current == null) Mission.Current?.AddMissionBehavior(new BLTTimedBuffBehavior());
            Current?.pending.Add((agent, expiry, negator));
        }

        public override void OnBehaviorInitialize() { base.OnBehaviorInitialize(); Current = this; }
        public override void OnRemoveBehavior() { base.OnRemoveBehavior(); if (Current == this) Current = null; }

        public override void OnMissionTick(float dt)
        {
            if (pending.Count == 0) return;
            float now = Mission.Current?.CurrentTime ?? 0f;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var (agent, expiry, negator) = pending[i];
                if (now >= expiry)
                {
                    pending.RemoveAt(i);
                    if (agent != null && agent.IsActive())
                        try { BLTAgentModifierBehavior.Current?.Add(agent, negator); } catch { }
                }
            }
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 10. BUFF REWARD
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Buff (Reward)"),
     Description("Twitch channel point reward: temporarily boosts hero stats"),
     UsedImplicitly]
    public class BuffReward : IRewardHandler
    {
        public Type RewardConfigType => typeof(BuffRewardSettings);

        public class BuffRewardSettings
        {
            [DisplayName("Damage Bonus (%)"), UsedImplicitly] public float DamageBonusPercent { get; set; } = 30f;
            [DisplayName("Armor Bonus (%)"), UsedImplicitly] public float ArmorBonusPercent { get; set; } = 20f;
            [DisplayName("Speed Bonus (%)"), UsedImplicitly] public float SpeedBonusPercent { get; set; } = 20f;
            [DisplayName("Duration (seconds)"), UsedImplicitly] public float DurationSeconds { get; set; } = 30f;
            [DisplayName("Gold Cost"), UsedImplicitly] public int GoldCost { get; set; } = 0;
            [DisplayName("Show Message"), UsedImplicitly] public bool ShowMessage { get; set; } = true;
        }

        public void Enqueue(ReplyContext context, object config)
        {
            var s = config as BuffRewardSettings ?? new BuffRewardSettings();
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You don't have an adopted hero."); return; }
            var heroAgent = hero.GetAgent();
            if (heroAgent == null || !heroAgent.IsActive()) { ActionManager.SendReply(context, "Your hero is not in an active battle."); return; }
            if (s.GoldCost > 0)
            {
                int gold = BLTAdoptAHeroCampaignBehavior.Current?.GetHeroGold(hero) ?? 0;
                if (gold < s.GoldCost) { ActionManager.SendReply(context, $"Not enough BLT gold (need {s.GoldCost}, have {gold})."); return; }
                BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, -s.GoldCost, true);
            }
            var modifierConfig = new AgentModifierConfig();
            if (s.DamageBonusPercent != 0) modifierConfig.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f + s.DamageBonusPercent });
            if (s.ArmorBonusPercent != 0) modifierConfig.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorHead, ModifierPercent = 100f + s.ArmorBonusPercent });
            if (s.SpeedBonusPercent != 0) modifierConfig.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.MaxSpeedMultiplier, ModifierPercent = 100f + s.SpeedBonusPercent });
            try
            {
                if (s.DurationSeconds > 0f) BLTTimedBuffBehavior.AddTimedBuff(heroAgent, modifierConfig, s.DurationSeconds);
                else BLTAgentModifierBehavior.Current?.Add(heroAgent, modifierConfig);
            }
            catch { }
            if (s.ShowMessage) Log.ShowInformation($"BUFF! {hero.Name} is empowered for {s.DurationSeconds:0}s!", hero.CharacterObject);
            ActionManager.SendReply(context, $"Buffed for {s.DurationSeconds:0}s.");
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 11. TELEPORT REWARD
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    public enum TeleportMode { Ally, Enemy, Random }

    [DisplayName("Teleport (Reward)"),
     Description("Twitch channel point reward: teleports the hero to an ally or enemy"),
     UsedImplicitly]
    public class TeleportReward : IRewardHandler
    {
        public Type RewardConfigType => typeof(TeleportRewardSettings);

        public class TeleportRewardSettings
        {
            [DisplayName("Teleport Mode"), UsedImplicitly] public TeleportMode Mode { get; set; } = TeleportMode.Ally;
            [DisplayName("Search Range (m)"), UsedImplicitly] public float SearchRange { get; set; } = 80f;
            [DisplayName("Offset Distance (m)"), UsedImplicitly] public float OffsetDistance { get; set; } = 2f;
            [DisplayName("Gold Cost"), UsedImplicitly] public int GoldCost { get; set; } = 0;
            [DisplayName("Show Message"), UsedImplicitly] public bool ShowMessage { get; set; } = true;
            [DisplayName("Splash Damage On Enemy Teleport"), UsedImplicitly] public int SplashDamage { get; set; } = 20;
            [DisplayName("Splash Radius (m)"), UsedImplicitly] public float SplashRadius { get; set; } = 3f;
        }

        public void Enqueue(ReplyContext context, object config)
        {
            var s = config as TeleportRewardSettings ?? new TeleportRewardSettings();
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You don't have an adopted hero."); return; }
            var heroAgent = hero.GetAgent();
            if (heroAgent == null || !heroAgent.IsActive()) { ActionManager.SendReply(context, "Hero is not in battle."); return; }
            if (s.GoldCost > 0)
            {
                int gold = BLTAdoptAHeroCampaignBehavior.Current?.GetHeroGold(hero) ?? 0;
                if (gold < s.GoldCost) { ActionManager.SendReply(context, $"Not enough gold (need {s.GoldCost}, have {gold})."); return; }
                BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, -s.GoldCost, true);
            }
            try
            {
                var target = TeleportHelpers.FindTarget(heroAgent, s.Mode, s.SearchRange);
                if (target == null) { ActionManager.SendReply(context, "No valid target found."); return; }
                TeleportHelpers.TeleportToTarget(heroAgent, target, s.OffsetDistance);
                if (s.Mode == TeleportMode.Enemy || (s.Mode == TeleportMode.Random && target.IsEnemyOf(heroAgent)))
                    TeleportHelpers.ApplySplashDamage(heroAgent, target, s.SplashRadius, s.SplashDamage);
                if (s.ShowMessage) Log.ShowInformation($"TELEPORT! {hero.Name} vanishes in a flash!", hero.CharacterObject);
                ActionManager.SendReply(context, $"Teleported!");
            }
            catch { ActionManager.SendReply(context, "Teleport failed."); }
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 12. TELEPORT PASSIVE (active + passive)
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Teleport (Passive)"),
     Description("Auto-teleport every X seconds during battle"),
     UsedImplicitly]
    public class TeleportPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Interval (seconds)"), UsedImplicitly] public float IntervalSeconds { get; set; } = 15f;
        [DisplayName("Teleport Mode"), UsedImplicitly] public TeleportMode Mode { get; set; } = TeleportMode.Enemy;
        [DisplayName("Search Range (m)"), UsedImplicitly] public float SearchRange { get; set; } = 60f;
        [DisplayName("Offset Distance (m)"), UsedImplicitly] public float OffsetDistance { get; set; } = 2f;
        [DisplayName("Only When In Danger"), UsedImplicitly] public bool OnlyWhenInDanger { get; set; } = false;
        [DisplayName("Danger HP Threshold (%)"), UsedImplicitly] public float DangerHpPercent { get; set; } = 30f;
        [DisplayName("Show Message"), UsedImplicitly] public bool ShowMessage { get; set; } = true;
        [DisplayName("Splash Damage On Enemy Teleport"), UsedImplicitly] public int SplashDamage { get; set; } = 20;
        [DisplayName("Splash Radius (m)"), UsedImplicitly] public float SplashRadius { get; set; } = 3f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float lastTeleport = 0f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastTeleport < IntervalSeconds) return;
                if (OnlyWhenInDanger && Mode != TeleportMode.Enemy)
                    if (heroAgent.Health / heroAgent.HealthLimit * 100f >= DangerHpPercent) return;
                var target = TeleportHelpers.FindTarget(heroAgent, Mode, SearchRange);
                if (target == null) return;
                lastTeleport = now;
                try
                {
                    TeleportHelpers.TeleportToTarget(heroAgent, target, OffsetDistance);
                    if (target.IsEnemyOf(heroAgent))
                        TeleportHelpers.ApplySplashDamage(heroAgent, target, SplashRadius, SplashDamage);
                    if (ShowMessage) Log.ShowInformation($"TELEPORT! {hero.Name} vanishes!", hero.CharacterObject);
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Auto-Teleport: every {IntervalSeconds:0}s to {Mode} within {SearchRange:0}m";
    }

    internal static class TeleportHelpers
    {
        public static Agent FindTarget(Agent heroAgent, TeleportMode mode, float range)
        {
            var buffer = new MBList<Agent>();
            var nearby = Mission.Current.GetNearbyAgents(heroAgent.Position.AsVec2, range, buffer);
            if (mode == TeleportMode.Random)
                mode = MBRandom.RandomInt(2) == 0 ? TeleportMode.Ally : TeleportMode.Enemy;
            if (mode == TeleportMode.Ally)
            {
                var allies = nearby.Where(a => a != heroAgent && a.IsActive() && !a.IsMount && a.IsFriendOf(heroAgent)).ToList();
                return allies.Count > 0 ? allies[MBRandom.RandomInt(allies.Count)] : null;
            }
            return nearby.Where(a => a.IsActive() && !a.IsMount && a.IsEnemyOf(heroAgent))
                .OrderBy(a => a.Position.DistanceSquared(heroAgent.Position)).FirstOrDefault();
        }

        public static void TeleportToTarget(Agent heroAgent, Agent target, float offsetDistance)
        {
            var targetPos = target.GetWorldPosition();
            if (offsetDistance > 0f)
            {
                var dir = (heroAgent.Position - target.Position).NormalizedCopy();
                targetPos.SetVec2(targetPos.AsVec2 + dir.AsVec2 * offsetDistance);
            }
            heroAgent.TeleportToPosition(targetPos.GetNavMeshVec3());
        }

        public static void ApplySplashDamage(Agent heroAgent, Agent target, float splashRadius, int splashDamage)
        {
            if (splashRadius <= 0f || splashDamage <= 0) return;
            var buffer = new MBList<Agent>();
            foreach (var victim in Mission.Current.GetNearbyAgents(target.Position.AsVec2, splashRadius, buffer)
                .Where(a => a.IsActive() && !a.IsMount && a.IsEnemyOf(heroAgent)).ToList())
            {
                try
                {
                    var dir = Vec3.Up;
                    var blow = new Blow(heroAgent.Index)
                    {
                        AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Blunt,
                        BoneIndex = victim.Monster.HeadLookDirectionBoneIndex, GlobalPosition = victim.Position,
                        BaseMagnitude = splashDamage, InflictedDamage = splashDamage,
                        SwingDirection = dir, Direction = dir, DamageCalculated = true,
                        VictimBodyPart = BoneBodyPartType.Chest,
                    };
                    victim.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, victim, blow));
                }
                catch { }
            }
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 13. JUMP ATTACK
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Jump Attack"),
     Description("Periodically dashes to the nearest enemy with damage and knockback"),
     UsedImplicitly]
    public class JumpAttackPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Cooldown (seconds)"), UsedImplicitly] public float CooldownSeconds { get; set; } = 8f;
        [DisplayName("Detection Range (m)"), UsedImplicitly] public float DetectionRange { get; set; } = 6f;
        [DisplayName("Base Damage"), UsedImplicitly] public int BaseDamage { get; set; } = 30;
        [DisplayName("Damage Bonus (%)"), UsedImplicitly] public float DamageBonusPercent { get; set; } = 50f;
        [DisplayName("Knockback Enabled"), UsedImplicitly] public bool KnockbackEnabled { get; set; } = true;
        [DisplayName("Knockback Distance (m)"), UsedImplicitly] public float KnockbackDistance { get; set; } = 3f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float lastJump = 0f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastJump < CooldownSeconds) return;
                var buffer = new MBList<Agent>();
                var nearest = Mission.Current.GetNearbyAgents(heroAgent.Position.AsVec2, DetectionRange, buffer)
                    .Where(a => a.IsActive() && !a.IsMount && a.IsEnemyOf(heroAgent))
                    .OrderBy(a => a.Position.DistanceSquared(heroAgent.Position)).FirstOrDefault();
                if (nearest == null) return;
                lastJump = now;
                try
                {
                    var direction = (nearest.Position - heroAgent.Position).NormalizedCopy();
                    var landPos = nearest.GetWorldPosition();
                    landPos.SetVec2(landPos.AsVec2 - direction.AsVec2 * 1.5f);
                    heroAgent.TeleportToPosition(landPos.GetNavMeshVec3());
                    int finalDamage = (int)(BaseDamage * (1f + DamageBonusPercent / 100f));
                    var blow = new Blow(heroAgent.Index)
                    {
                        AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Blunt,
                        BoneIndex = nearest.Monster.HeadLookDirectionBoneIndex, GlobalPosition = nearest.Position,
                        BaseMagnitude = BaseDamage, InflictedDamage = finalDamage,
                        SwingDirection = direction, Direction = direction, DamageCalculated = true,
                        VictimBodyPart = BoneBodyPartType.Chest,
                    };
                    nearest.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, nearest, blow));
                    if (KnockbackEnabled && nearest.IsActive())
                    {
                        var knockbackPos = nearest.GetWorldPosition();
                        knockbackPos.SetVec2(knockbackPos.AsVec2 + direction.AsVec2 * KnockbackDistance);
                        nearest.SetScriptedPosition(ref knockbackPos, false, Agent.AIScriptedFrameFlags.NeverSlowDown);
                    }
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Jump Attack: every {CooldownSeconds:0}s dashes to nearest enemy in {DetectionRange:0}m, deals {BaseDamage}+{DamageBonusPercent:0}%";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 14. KICK
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Kick"),
     Description("Periodically performs a kick on the nearest close enemy, with knockback"),
     UsedImplicitly]
    public class KickPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Cooldown (seconds)"), UsedImplicitly] public float CooldownSeconds { get; set; } = 6f;
        [DisplayName("Kick Range (m)"), UsedImplicitly] public float KickRange { get; set; } = 2.5f;
        [DisplayName("Damage"), UsedImplicitly] public int Damage { get; set; } = 20;
        [DisplayName("Knockback Distance (m)"), UsedImplicitly] public float KnockbackDistance { get; set; } = 4f;
        [DisplayName("Use Left Leg"), UsedImplicitly] public bool UseLeftLeg { get; set; } = false;
        [DisplayName("Show Message"), UsedImplicitly] public bool ShowMessage { get; set; } = false;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float lastKick = 0f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastKick < CooldownSeconds) return;
                var buffer = new MBList<Agent>();
                var nearest = Mission.Current.GetNearbyAgents(heroAgent.Position.AsVec2, KickRange, buffer)
                    .Where(a => a.IsActive() && !a.IsMount && a.IsEnemyOf(heroAgent))
                    .OrderBy(a => a.Position.DistanceSquared(heroAgent.Position)).FirstOrDefault();
                if (nearest == null) return;
                lastKick = now;
                try
                {
                    var direction = (nearest.Position - heroAgent.Position).NormalizedCopy();
                    try
                    {
                        string actionName = UseLeftLeg ? "act_kick_left_leg" : "act_kick_right_leg";
                        heroAgent.SetActionChannel(1, ActionIndexCache.Create(actionName), ignorePriority: true, 0UL);
                    }
                    catch { }
                    var blow = new Blow(heroAgent.Index)
                    {
                        AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Blunt,
                        BoneIndex = nearest.Monster.HeadLookDirectionBoneIndex, GlobalPosition = nearest.Position,
                        BaseMagnitude = Damage, InflictedDamage = Damage,
                        SwingDirection = direction, Direction = direction, DamageCalculated = true,
                        VictimBodyPart = BoneBodyPartType.Abdomen,
                    };
                    nearest.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, nearest, blow));
                    if (KnockbackDistance > 0f && nearest.IsActive())
                    {
                        var knockbackPos = nearest.GetWorldPosition();
                        knockbackPos.SetVec2(knockbackPos.AsVec2 + direction.AsVec2 * KnockbackDistance);
                        nearest.SetScriptedPosition(ref knockbackPos, false, Agent.AIScriptedFrameFlags.NeverSlowDown);
                    }
                    if (ShowMessage) Log.ShowInformation($"KICK! {hero.Name} kopie wroga!", hero.CharacterObject);
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Kick: every {CooldownSeconds:0}s kicks nearest enemy within {KickRange:0.#}m for {Damage} dmg, knockback {KnockbackDistance:0.#}m";
    }


    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 16. BURNING STRIKE
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Burning Strike"),
     Description("Each hit sets the enemy on fire: contour + fire DoT damage"),
     UsedImplicitly]
    public class BurningStrikePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Fire Damage Per Tick"), UsedImplicitly] public int FireDamagePerTick { get; set; } = 12;
        [DisplayName("Burn Duration (ticks)"), UsedImplicitly] public int BurnDurationTicks { get; set; } = 4;
        [DisplayName("Refresh On Hit"), UsedImplicitly] public bool RefreshOnHit { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FFFF4400";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var burningAgents = new Dictionary<Agent, int>();

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !victim.IsActive()) return;
                if (attacker == null || !victim.IsEnemyOf(attacker)) return;
                if (RefreshOnHit || !burningAgents.ContainsKey(victim)) burningAgents[victim] = BurnDurationTicks;
                try { victim.AgentVisuals?.SetContourColor(Convert.ToUInt32(ContourColor, 16), true); }
                catch { victim.AgentVisuals?.SetContourColor(0xFFFF4400u, true); }
                // Efekt eksplozji ognia przy trafieniu
                try
                {
                    int psysId = ParticleSystemManager.GetRuntimeIdByName("explosion_fire_medium");
                    if (psysId >= 0)
                    {
                        var frame = MatrixFrame.Identity;
                        frame.origin = victim.Position + new Vec3(0f, 0f, 0.8f);
                        Mission.Current?.Scene?.CreateBurstParticle(psysId, frame);
                    }
                }
                catch { }
            };

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                foreach (var key in burningAgents.Keys.ToList())
                {
                    if (key == null || !key.IsActive()) { try { key?.AgentVisuals?.SetContourColor(null, false); } catch { } burningAgents.Remove(key); continue; }
                    try
                    {
                        var dir = Vec3.Up;
                        var blow = new Blow(heroAgent.Index)
                        {
                            AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Pierce,
                            BoneIndex = key.Monster.HeadLookDirectionBoneIndex, GlobalPosition = key.Position,
                            BaseMagnitude = FireDamagePerTick, InflictedDamage = FireDamagePerTick,
                            SwingDirection = dir, Direction = dir, DamageCalculated = true,
                            VictimBodyPart = BoneBodyPartType.Chest,
                        };
                        key.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, key, blow));
                    }
                    catch { }
                    burningAgents[key]--;
                    if (burningAgents[key] <= 0) { try { key.AgentVisuals?.SetContourColor(null, false); } catch { } burningAgents.Remove(key); }
                }
            };

            void Cleanup()
            {
                foreach (var a in burningAgents.Keys) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                burningAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Burning Strike: hit ignites enemy ({FireDamagePerTick} fire dmg/2s for {BurnDurationTicks * 2}s)";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 16. FROST STRIKE
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Frost Strike"),
     Description("Hits slow the enemy's movement speed for a duration, ice blue contour"),
     UsedImplicitly]
    public class FrostStrikePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Slow Speed Limit"), UsedImplicitly] public float SlowSpeedLimit { get; set; } = 0.4f;
        [DisplayName("Frost Duration (ticks)"), UsedImplicitly] public int FrostDurationTicks { get; set; } = 4;
        [DisplayName("Apply On"), UsedImplicitly] public PoisonApplyOn ApplyOn { get; set; } = PoisonApplyOn.Both;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FF00AAFF";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var frostedAgents = new Dictionary<Agent, int>();

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !victim.IsActive() || attacker == null || !victim.IsEnemyOf(attacker)) return;
                if (ApplyOn != PoisonApplyOn.Both)
                {
                    bool isRanged = !attacker.WieldedWeapon.IsEmpty && attacker.WieldedWeapon.CurrentUsageItem?.IsRangedWeapon == true;
                    if (ApplyOn == PoisonApplyOn.RangedOnly && !isRanged) return;
                    if (ApplyOn == PoisonApplyOn.MeleeOnly && isRanged) return;
                }
                frostedAgents[victim] = FrostDurationTicks;
                try { victim.SetMaximumSpeedLimit(SlowSpeedLimit, false); } catch { }
                try { victim.AgentVisuals?.SetContourColor(Convert.ToUInt32(ContourColor, 16), true); } catch { }
            };

            handlers.OnSlowTick += dt =>
            {
                foreach (var key in frostedAgents.Keys.ToList())
                {
                    if (key == null || !key.IsActive())
                    {
                        try { key?.AgentVisuals?.SetContourColor(null, false); } catch { }
                        frostedAgents.Remove(key); continue;
                    }
                    frostedAgents[key]--;
                    if (frostedAgents[key] <= 0)
                    {
                        try { key.SetMaximumSpeedLimit(1f, false); } catch { }
                        try { key.AgentVisuals?.SetContourColor(null, false); } catch { }
                        frostedAgents.Remove(key);
                    }
                }
            };

            void Cleanup()
            {
                foreach (var a in frostedAgents.Keys)
                {
                    try { a?.SetMaximumSpeedLimit(1f, false); } catch { }
                    try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                }
                frostedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Frost Strike: hit slows enemy to {SlowSpeedLimit:0.##}x speed for {FrostDurationTicks * 2}s";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 17. VAMPIRISM STRIKE
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Vampirism Strike"),
     Description("Each hit heals the hero for a % of damage dealt, purple contour on victim"),
     UsedImplicitly]
    public class VampirismStrikePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Lifesteal (%)"), UsedImplicitly] public float LifestealPercent { get; set; } = 20f;
        [DisplayName("Show Contour On Victim"), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FF880088";
        [DisplayName("Contour Duration (ticks)"), UsedImplicitly] public int ContourDurationTicks { get; set; } = 2;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var drainedAgents = new Dictionary<Agent, int>();

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !victim.IsActive() || attacker == null || !victim.IsEnemyOf(attacker)) return;
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || attacker != heroAgent) return;
                int dmg = blowParams.blow.InflictedDamage;
                if (dmg <= 0) return;
                float heal = dmg * LifestealPercent / 100f;
                try { heroAgent.Health = Math.Min(heroAgent.HealthLimit, heroAgent.Health + heal); } catch { }
                if (ShowContour)
                {
                    drainedAgents[victim] = ContourDurationTicks;
                    try { victim.AgentVisuals?.SetContourColor(Convert.ToUInt32(ContourColor, 16), true); } catch { }
                }
            };

            handlers.OnSlowTick += dt =>
            {
                foreach (var key in drainedAgents.Keys.ToList())
                {
                    if (key == null || !key.IsActive()) { try { key?.AgentVisuals?.SetContourColor(null, false); } catch { } drainedAgents.Remove(key); continue; }
                    drainedAgents[key]--;
                    if (drainedAgents[key] <= 0) { try { key.AgentVisuals?.SetContourColor(null, false); } catch { } drainedAgents.Remove(key); }
                }
            };

            void Cleanup()
            {
                foreach (var a in drainedAgents.Keys) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                drainedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description => $"Vampirism Strike: heals {LifestealPercent}% of damage dealt";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 18. CHAIN LIGHTNING
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Chain Lightning"),
     Description("Each hit chains lightning to nearby enemies, yellow contour"),
     UsedImplicitly]
    public class ChainLightningPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Chain Damage"), UsedImplicitly] public int ChainDamage { get; set; } = 25;
        [DisplayName("Chain Radius"), UsedImplicitly] public float ChainRadius { get; set; } = 5f;
        [DisplayName("Max Targets"), UsedImplicitly] public int MaxTargets { get; set; } = 3;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FFFFFF00";
        [DisplayName("Contour Duration (ticks)"), UsedImplicitly] public int ContourDurationTicks { get; set; } = 1;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var zappedAgents = new Dictionary<Agent, int>();

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !victim.IsActive() || attacker == null || !victim.IsEnemyOf(attacker)) return;
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || attacker != heroAgent) return;

                var nearby = Mission.Current?.Agents
                    ?.Where(a => a != null && a != victim && a.IsActive() && a.IsEnemyOf(heroAgent)
                                 && a.Position.Distance(victim.Position) <= ChainRadius)
                    .OrderBy(a => a.Position.Distance(victim.Position))
                    .Take(MaxTargets)
                    .ToList();
                if (nearby == null) return;

                var dir = Vec3.Up;
                foreach (var target in nearby)
                {
                    try
                    {
                        var blow = new Blow(heroAgent.Index)
                        {
                            AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Pierce,
                            BoneIndex = target.Monster.HeadLookDirectionBoneIndex, GlobalPosition = target.Position,
                            BaseMagnitude = ChainDamage, InflictedDamage = ChainDamage,
                            SwingDirection = dir, Direction = dir, DamageCalculated = true,
                            VictimBodyPart = BoneBodyPartType.Chest,
                        };
                        target.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, target, blow));
                    }
                    catch { }
                    zappedAgents[target] = ContourDurationTicks;
                    try { target.AgentVisuals?.SetContourColor(Convert.ToUInt32(ContourColor, 16), true); } catch { }
                }
            };

            handlers.OnSlowTick += dt =>
            {
                foreach (var key in zappedAgents.Keys.ToList())
                {
                    if (key == null || !key.IsActive()) { try { key?.AgentVisuals?.SetContourColor(null, false); } catch { } zappedAgents.Remove(key); continue; }
                    zappedAgents[key]--;
                    if (zappedAgents[key] <= 0) { try { key.AgentVisuals?.SetContourColor(null, false); } catch { } zappedAgents.Remove(key); }
                }
            };

            void Cleanup()
            {
                foreach (var a in zappedAgents.Keys) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                zappedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Chain Lightning: hit chains {ChainDamage} dmg to {MaxTargets} enemies in {ChainRadius:0.#}m";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 19. BLEED STRIKE
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Bleed Strike"),
     Description("Hits cause bleeding (stackable DoT), dark red contour"),
     UsedImplicitly]
    public class BleedStrikePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Bleed Damage Per Tick"), UsedImplicitly] public int BleedDamagePerTick { get; set; } = 8;
        [DisplayName("Bleed Duration (ticks)"), UsedImplicitly] public int BleedDurationTicks { get; set; } = 6;
        [DisplayName("Max Stacks"), UsedImplicitly] public int MaxStacks { get; set; } = 5;
        [DisplayName("Apply On"), UsedImplicitly] public PoisonApplyOn ApplyOn { get; set; } = PoisonApplyOn.MeleeOnly;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FFAA0000";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            // stacks: agent -> (ticks remaining, stack count)
            var bleedingAgents = new Dictionary<Agent, (int ticks, int stacks)>();

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !victim.IsActive() || attacker == null || !victim.IsEnemyOf(attacker)) return;
                if (ApplyOn != PoisonApplyOn.Both)
                {
                    bool isRanged = !attacker.WieldedWeapon.IsEmpty && attacker.WieldedWeapon.CurrentUsageItem?.IsRangedWeapon == true;
                    if (ApplyOn == PoisonApplyOn.RangedOnly && !isRanged) return;
                    if (ApplyOn == PoisonApplyOn.MeleeOnly && isRanged) return;
                }
                int stacks = bleedingAgents.TryGetValue(victim, out var cur) ? Math.Min(cur.stacks + 1, MaxStacks) : 1;
                bleedingAgents[victim] = (BleedDurationTicks, stacks);
                try { victim.AgentVisuals?.SetContourColor(Convert.ToUInt32(ContourColor, 16), true); } catch { }
            };

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                foreach (var key in bleedingAgents.Keys.ToList())
                {
                    if (key == null || !key.IsActive()) { try { key?.AgentVisuals?.SetContourColor(null, false); } catch { } bleedingAgents.Remove(key); continue; }
                    var (ticks, stacks) = bleedingAgents[key];
                    try
                    {
                        int dmg = BleedDamagePerTick * stacks;
                        var dir = Vec3.Up;
                        var blow = new Blow(heroAgent.Index)
                        {
                            AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Pierce,
                            BoneIndex = key.Monster.HeadLookDirectionBoneIndex, GlobalPosition = key.Position,
                            BaseMagnitude = dmg, InflictedDamage = dmg,
                            SwingDirection = dir, Direction = dir, DamageCalculated = true,
                            VictimBodyPart = BoneBodyPartType.Chest,
                        };
                        key.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, key, blow));
                    }
                    catch { }
                    ticks--;
                    if (ticks <= 0) { try { key.AgentVisuals?.SetContourColor(null, false); } catch { } bleedingAgents.Remove(key); }
                    else bleedingAgents[key] = (ticks, stacks);
                }
            };

            void Cleanup()
            {
                foreach (var a in bleedingAgents.Keys) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                bleedingAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Bleed Strike: {BleedDamagePerTick} dmg/2s per stack (max {MaxStacks}), melee only";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 20. FEAR AURA
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Fear Aura"),
     Description("Enemies near the hero have a chance to flee/panic each tick, dark purple contour"),
     UsedImplicitly]
    public class FearAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Radius"), UsedImplicitly] public float Radius { get; set; } = 6f;
        [DisplayName("Fear Chance Per Tick (%)"), UsedImplicitly] public float FearChancePercent { get; set; } = 15f;
        [DisplayName("Fear Tick Interval (seconds)"), UsedImplicitly] public float FearTickInterval { get; set; } = 2f;
        [DisplayName("Contour Update Interval (seconds)"), UsedImplicitly] public float ContourUpdateInterval { get; set; } = 0.5f;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FF440044";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var fearedAgents = new HashSet<Agent>();
            var lastFearTime = new Dictionary<Agent, float>();
            float lastContourUpdate = -999f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                float now = Mission.Current?.CurrentTime ?? 0f;

                // Contour i cleanup co ContourUpdateInterval sekund
                if (now - lastContourUpdate >= ContourUpdateInterval)
                {
                    lastContourUpdate = now;

                    foreach (var a in fearedAgents.ToList())
                        if (a == null || !a.IsActive()) { try { a?.AgentVisuals?.SetContourColor(null, false); } catch { } fearedAgents.Remove(a); lastFearTime.Remove(a); }

                    var inRange = Mission.Current?.Agents
                        ?.Where(a => a != null && a.IsActive() && a.IsEnemyOf(heroAgent)
                                     && a.Position.Distance(heroAgent.Position) <= Radius)
                        .ToList();
                    if (inRange == null) return;

                    foreach (var enemy in inRange)
                    {
                        fearedAgents.Add(enemy);
                        try { enemy.AgentVisuals?.SetContourColor(Convert.ToUInt32(ContourColor, 16), true); } catch { }
                    }

                    foreach (var a in fearedAgents.ToList())
                    {
                        if (a == null || !a.IsActive()) { try { a?.AgentVisuals?.SetContourColor(null, false); } catch { } fearedAgents.Remove(a); lastFearTime.Remove(a); continue; }
                        if (a.Position.Distance(heroAgent.Position) > Radius)
                        {
                            try { a.AgentVisuals?.SetContourColor(null, false); } catch { }
                            fearedAgents.Remove(a); lastFearTime.Remove(a);
                        }
                    }
                }

                // Fear push co FearTickInterval sekund per agent
                foreach (var enemy in fearedAgents.ToList())
                {
                    if (enemy == null || !enemy.IsActive()) continue;
                    if (lastFearTime.TryGetValue(enemy, out float last) && now - last < FearTickInterval) continue;
                    if (MBRandom.RandomFloat * 100f >= FearChancePercent) { lastFearTime[enemy] = now; continue; }
                    try
                    {
                        Vec3 awayDir3 = (enemy.Position - heroAgent.Position);
                        if (awayDir3.Length > 0.01f) awayDir3 = awayDir3.NormalizedCopy();
                        var awayDir2 = new Vec2(awayDir3.x, awayDir3.y).Normalized();
                        enemy.SetMovementDirection(awayDir2);
                    }
                    catch { }
                    lastFearTime[enemy] = now;
                }
            };

            void Cleanup()
            {
                foreach (var a in fearedAgents) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                fearedAgents.Clear(); lastFearTime.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Fear Aura: {FearChancePercent}% chance to flee every {FearTickInterval:0.#}s within {Radius:0.#}m";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 21. SLOW AURA
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Slow Aura"),
     Description("Enemies within radius are slowed, cyan contour"),
     UsedImplicitly]
    public class SlowAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Radius"), UsedImplicitly] public float Radius { get; set; } = 5f;
        [DisplayName("Slow Speed Limit"), UsedImplicitly] public float SlowSpeedLimit { get; set; } = 0.5f;
        [DisplayName("Update Interval (seconds)"), UsedImplicitly] public float UpdateInterval { get; set; } = 0.5f;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FF00FFCC";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var slowedAgents = new HashSet<Agent>();
            float lastUpdate = -999f;

            handlers.OnMissionTick += dt =>
            {
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastUpdate < UpdateInterval) return;
                lastUpdate = now;

                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;

                var inRange = Mission.Current?.Agents
                    ?.Where(a => a != null && a.IsActive() && a.IsEnemyOf(heroAgent)
                                 && a.Position.Distance(heroAgent.Position) <= Radius)
                    .ToHashSet() ?? new HashSet<Agent>();

                foreach (var a in slowedAgents.ToList())
                {
                    if (a == null || !a.IsActive() || !inRange.Contains(a))
                    {
                        try { a?.SetMaximumSpeedLimit(1f, false); a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                        slowedAgents.Remove(a);
                    }
                }

                foreach (var a in inRange)
                {
                    if (!slowedAgents.Contains(a))
                    {
                        try { a.SetMaximumSpeedLimit(SlowSpeedLimit, false); a.AgentVisuals?.SetContourColor(Convert.ToUInt32(ContourColor, 16), true); } catch { }
                        slowedAgents.Add(a);
                    }
                }
            };

            void Cleanup()
            {
                foreach (var a in slowedAgents)
                {
                    try { a?.SetMaximumSpeedLimit(1f, false); a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                }
                slowedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Slow Aura: enemies within {Radius:0.#}m slowed to {SlowSpeedLimit:0.##}x speed";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 22. WEAKNESS AURA
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Weakness Aura"),
     Description("Enemies within radius deal reduced damage, grey contour"),
     UsedImplicitly]
    public class WeaknessAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Radius"), UsedImplicitly] public float Radius { get; set; } = 6f;
        [DisplayName("Damage Reduction (%)"), UsedImplicitly] public float DamageReductionPercent { get; set; } = 30f;
        [DisplayName("Update Interval (seconds)"), UsedImplicitly] public float UpdateInterval { get; set; } = 0.5f;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FF888888";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var weakenedAgents = new HashSet<Agent>();
            float lastUpdate = -999f;

            handlers.OnMissionTick += dt =>
            {
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastUpdate < UpdateInterval) return;
                lastUpdate = now;

                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;

                var inRange = Mission.Current?.Agents
                    ?.Where(a => a != null && a.IsActive() && a.IsEnemyOf(heroAgent)
                                 && a.Position.Distance(heroAgent.Position) <= Radius)
                    .ToHashSet() ?? new HashSet<Agent>();

                foreach (var a in weakenedAgents.ToList())
                {
                    if (a == null || !a.IsActive() || !inRange.Contains(a))
                    {
                        try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                        weakenedAgents.Remove(a);
                    }
                }

                foreach (var a in inRange)
                {
                    if (!weakenedAgents.Contains(a))
                    {
                        try { a.AgentVisuals?.SetContourColor(Convert.ToUInt32(ContourColor, 16), true); } catch { }
                        weakenedAgents.Add(a);
                    }
                }
            };

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (attacker == null || !weakenedAgents.Contains(attacker)) return;
                float mult = 1f - DamageReductionPercent / 100f;
                blowParams.blow.BaseMagnitude *= mult;
                blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * mult);
            };

            void Cleanup()
            {
                foreach (var a in weakenedAgents) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                weakenedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Weakness Aura: enemies within {Radius:0.#}m deal {DamageReductionPercent}% less damage";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 23. BATTLE CRY AURA
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Battle Cry Aura"),
     Description("Allies within radius move/attack faster, gold contour"),
     UsedImplicitly]
    public class BattleCryAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Radius"), UsedImplicitly] public float Radius { get; set; } = 8f;
        [DisplayName("Speed Boost Multiplier"), UsedImplicitly] public float SpeedBoostMultiplier { get; set; } = 1.3f;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FFFFAA00";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var boostedAgents = new HashSet<Agent>();

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;

                var inRange = Mission.Current?.Agents
                    ?.Where(a => a != null && a.IsActive() && !a.IsEnemyOf(heroAgent) && a != heroAgent
                                 && a.Position.Distance(heroAgent.Position) <= Radius)
                    .ToHashSet() ?? new HashSet<Agent>();

                foreach (var a in boostedAgents.ToList())
                {
                    if (a == null || !a.IsActive() || !inRange.Contains(a))
                    {
                        try { a?.SetMaximumSpeedLimit(1f, false); a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                        boostedAgents.Remove(a);
                    }
                }

                foreach (var a in inRange)
                {
                    if (!boostedAgents.Contains(a))
                    {
                        try { a.SetMaximumSpeedLimit(SpeedBoostMultiplier, false); a.AgentVisuals?.SetContourColor(Convert.ToUInt32(ContourColor, 16), true); } catch { }
                        boostedAgents.Add(a);
                    }
                }
            };

            void Cleanup()
            {
                foreach (var a in boostedAgents)
                {
                    try { a?.SetMaximumSpeedLimit(1f, false); a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                }
                boostedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Battle Cry Aura: allies within {Radius:0.#}m get {SpeedBoostMultiplier:0.##}x speed";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 24. BLOOD RAGE
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Blood Rage"),
     Description("Each kill adds a damage bonus stack that decays over time, orange-red contour on hero"),
     UsedImplicitly]
    public class BloodRagePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Damage Bonus Per Stack (%)"), UsedImplicitly] public float DamageBonusPerStack { get; set; } = 10f;
        [DisplayName("Max Stacks"), UsedImplicitly] public int MaxStacks { get; set; } = 8;
        [DisplayName("Stack Duration (ticks)"), UsedImplicitly] public int StackDurationTicks { get; set; } = 5;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FFFF4400";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            int stacks = 0;
            int ticksRemaining = 0;

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                // Kill detection: victim dies from this blow
                if (attacker == heroAgent && victim != null && victim.IsEnemyOf(heroAgent)
                    && victim.Health - blowParams.blow.InflictedDamage <= 0f)
                {
                    stacks = Math.Min(stacks + 1, MaxStacks);
                    ticksRemaining = StackDurationTicks;
                    try { heroAgent.AgentVisuals?.SetContourColor(Convert.ToUInt32(ContourColor, 16), true); } catch { }
                    Log.ShowInformation($"BLOOD RAGE! {hero.Name} — {stacks} stacks ({stacks * DamageBonusPerStack:0}% bonus dmg)", hero.CharacterObject);
                }
                // Apply damage bonus
                if (stacks > 0 && attacker == heroAgent)
                {
                    float mult = 1f + stacks * DamageBonusPerStack / 100f;
                    blowParams.blow.BaseMagnitude *= mult;
                    blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * mult);
                }
            };

            handlers.OnSlowTick += dt =>
            {
                if (stacks <= 0) return;
                ticksRemaining--;
                if (ticksRemaining <= 0)
                {
                    stacks = Math.Max(0, stacks - 1);
                    ticksRemaining = stacks > 0 ? StackDurationTicks : 0;
                    if (stacks == 0)
                    {
                        var heroAgent = hero.GetAgent();
                        try { heroAgent?.AgentVisuals?.SetContourColor(null, false); } catch { }
                    }
                }
            };

            void Cleanup()
            {
                stacks = 0;
                var heroAgent = hero.GetAgent();
                try { heroAgent?.AgentVisuals?.SetContourColor(null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Blood Rage: +{DamageBonusPerStack}% dmg per kill stack (max {MaxStacks})";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 25. VENGEANCE
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Vengeance"),
     Description("When hero takes heavy damage, instantly counter-attacks the attacker"),
     UsedImplicitly]
    public class VengeancePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Trigger Damage Threshold"), UsedImplicitly] public int TriggerDamageThreshold { get; set; } = 20;
        [DisplayName("Counter Damage Multiplier"), UsedImplicitly] public float CounterDamageMultiplier { get; set; } = 1.5f;
        [DisplayName("Cooldown (seconds)"), UsedImplicitly] public float CooldownSeconds { get; set; } = 5f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float lastTrigger = -999f;

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || victim != heroAgent) return;
                if (blowParams.blow.InflictedDamage < TriggerDamageThreshold) return;

                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastTrigger < CooldownSeconds) return;
                if (attacker == null || !attacker.IsActive()) return;
                lastTrigger = now;

                int counterDmg = (int)(blowParams.blow.InflictedDamage * CounterDamageMultiplier);
                try
                {
                    var dir = (attacker.Position - heroAgent.Position);
                    if (dir.Length > 0.01f) dir = dir.NormalizedCopy();
                    var blow = new Blow(heroAgent.Index)
                    {
                        AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Blunt,
                        BoneIndex = attacker.Monster.HeadLookDirectionBoneIndex, GlobalPosition = attacker.Position,
                        BaseMagnitude = counterDmg, InflictedDamage = counterDmg,
                        SwingDirection = dir, Direction = dir, DamageCalculated = true,
                        VictimBodyPart = BoneBodyPartType.Chest,
                    };
                    attacker.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, attacker, blow));
                    Log.ShowInformation($"VENGEANCE! {hero.Name} counters for {counterDmg} dmg!", hero.CharacterObject);
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Vengeance: counter-attack {CounterDamageMultiplier:0.#}x damage when hit for {TriggerDamageThreshold}+";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 26. PHOENIX REBIRTH
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Phoenix Rebirth"),
     Description("Once per battle, when hero would die, survive at 1 HP with golden contour"),
     UsedImplicitly]
    public class PhoenixRebirthPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Post-Rebirth Invulnerability (seconds)"), UsedImplicitly] public float InvulnerabilitySeconds { get; set; } = 3f;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FFFFEE00";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            bool used = false;
            float invulUntil = -1f;

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || victim != heroAgent) return;

                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now < invulUntil)
                {
                    blowParams.blow.BaseMagnitude = 0f;
                    blowParams.blow.InflictedDamage = 0;
                    return;
                }

                if (used) return;
                if (heroAgent.Health - blowParams.blow.InflictedDamage > 1f) return;

                used = true;
                blowParams.blow.BaseMagnitude = 0f;
                blowParams.blow.InflictedDamage = 0;
                invulUntil = now + InvulnerabilitySeconds;

                try { heroAgent.Health = Math.Max(1f, heroAgent.Health); } catch { }
                try { heroAgent.AgentVisuals?.SetContourColor(Convert.ToUInt32(ContourColor, 16), true); } catch { }
                Log.ShowInformation($"PHOENIX REBIRTH! {hero.Name} cheats death!", hero.CharacterObject);
            };

            handlers.OnMissionTick += dt =>
            {
                if (!used) return;
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now >= invulUntil && invulUntil > 0f)
                {
                    invulUntil = -1f;
                    try { heroAgent.AgentVisuals?.SetContourColor(null, false); } catch { }
                }
            };

            void Cleanup()
            {
                var heroAgent = hero.GetAgent();
                try { heroAgent?.AgentVisuals?.SetContourColor(null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Phoenix Rebirth: once per battle survive death, {InvulnerabilitySeconds}s invulnerable after";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 27. SHADOWSTEP
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Shadowstep"),
     Description("Periodically teleports hero behind the nearest enemy"),
     UsedImplicitly]
    public class ShadowstepPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Cooldown (seconds)"), UsedImplicitly] public float CooldownSeconds { get; set; } = 10f;
        [DisplayName("Max Range"), UsedImplicitly] public float MaxRange { get; set; } = 30f;
        [DisplayName("Behind Distance"), UsedImplicitly] public float BehindDistance { get; set; } = 1.5f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float lastStep = -999f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastStep < CooldownSeconds) return;

                var target = Mission.Current?.Agents
                    ?.Where(a => a != null && a.IsActive() && a.IsEnemyOf(heroAgent)
                                 && a.Position.Distance(heroAgent.Position) <= MaxRange)
                    .OrderBy(a => a.Position.Distance(heroAgent.Position))
                    .FirstOrDefault();
                if (target == null) return;

                lastStep = now;
                try
                {
                    Vec3 facing = (target.Position - heroAgent.Position).NormalizedCopy();
                    Vec3 behindPos = target.Position - facing * BehindDistance;
                    behindPos.z = target.Position.z;
                    heroAgent.TeleportToPosition(behindPos);
                    Log.ShowInformation($"SHADOWSTEP! {hero.Name} appears behind {target.Name}!", hero.CharacterObject);
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Shadowstep: teleport behind nearest enemy every {CooldownSeconds:0}s";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 28. IRON SKIN
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Iron Skin"),
     Description("Periodically activates a damage reduction shield, silver contour on hero"),
     UsedImplicitly]
    public class IronSkinPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Damage Reduction (%)"), UsedImplicitly] public float DamageReductionPercent { get; set; } = 50f;
        [DisplayName("Active Duration (seconds)"), UsedImplicitly] public float ActiveDurationSeconds { get; set; } = 4f;
        [DisplayName("Cooldown (seconds)"), UsedImplicitly] public float CooldownSeconds { get; set; } = 15f;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FFC0C0C0";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float activeUntil = -1f;
            float nextActivation = 0f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                float now = Mission.Current?.CurrentTime ?? 0f;

                if (activeUntil > 0f && now >= activeUntil)
                {
                    activeUntil = -1f;
                    nextActivation = now + CooldownSeconds;
                    try { heroAgent.AgentVisuals?.SetContourColor(null, false); } catch { }
                }

                if (activeUntil < 0f && now >= nextActivation)
                {
                    activeUntil = now + ActiveDurationSeconds;
                    try { heroAgent.AgentVisuals?.SetContourColor(Convert.ToUInt32(ContourColor, 16), true); } catch { }
                    Log.ShowInformation($"IRON SKIN! {hero.Name} hardens for {ActiveDurationSeconds:0}s!", hero.CharacterObject);
                }
            };

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || victim != heroAgent) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now >= activeUntil) return;
                float mult = 1f - DamageReductionPercent / 100f;
                blowParams.blow.BaseMagnitude *= mult;
                blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * mult);
            };

            void Cleanup()
            {
                var heroAgent = hero.GetAgent();
                try { heroAgent?.AgentVisuals?.SetContourColor(null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Iron Skin: {DamageReductionPercent}% dmg reduction for {ActiveDurationSeconds:0}s every {CooldownSeconds:0}s";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 29. AUTO PICKUP
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Auto Pickup"),
     Description("When hero has no weapons, automatically picks up the best weapon from the ground nearby"),
     UsedImplicitly]
    public class AutoPickupPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Search Radius"), UsedImplicitly] public float SearchRadius { get; set; } = 8f;
        [DisplayName("Check Interval (seconds)"), UsedImplicitly] public float CheckInterval { get; set; } = 1f;
        [DisplayName("Only When No Weapons"), UsedImplicitly] public bool OnlyWhenNoWeapons { get; set; } = true;
        [DisplayName("Contour Color On Pickup (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FF00FF44";
        [DisplayName("Contour Duration (seconds)"), UsedImplicitly] public float ContourDurationSeconds { get; set; } = 2f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float lastCheck = -999f;
            float contourUntil = -1f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;

                float now = Mission.Current?.CurrentTime ?? 0f;

                // Zdejmij kontur po czasie
                if (contourUntil > 0f && now >= contourUntil)
                {
                    contourUntil = -1f;
                    try { heroAgent.AgentVisuals?.SetContourColor(null, false); } catch { }
                }

                if (now - lastCheck < CheckInterval) return;
                lastCheck = now;

                // Sprawdź czy hero nie ma broni
                if (OnlyWhenNoWeapons)
                {
                    bool hasWeapon = false;
                    for (var wi = EquipmentIndex.WeaponItemBeginSlot; wi < EquipmentIndex.NumAllWeaponSlots; wi++)
                    {
                        try
                        {
                            if (!heroAgent.Equipment[wi].IsEmpty && heroAgent.Equipment[wi].Item?.HasWeaponComponent == true)
                            { hasWeapon = true; break; }
                        }
                        catch { }
                    }
                    if (hasWeapon) return;
                }

                // Znajdź najlepszą broń na ziemi w promieniu
                SpawnedItemEntity bestItem = null;
                float bestScore = -1f;

                try
                {
                    foreach (var mObj in Mission.Current.MissionObjects.ToList())
                    {
                        if (mObj == null) continue;
                        var spawned = mObj as SpawnedItemEntity;
                        if (spawned == null) continue;

                        var weapon = spawned.WeaponCopy;
                        if (weapon.IsEmpty) continue;
                        var item = weapon.Item;
                        if (item == null || !item.HasWeaponComponent) continue;

                        var ge = spawned.GameEntity;
                        Vec3 itemPos = ge != null ? ge.GetGlobalFrame().origin : Vec3.Zero;
                        if (heroAgent.Position.Distance(itemPos) > SearchRadius) continue;

                        // Score: tier * 10 + swing + thrust damage
                        float score = (int)item.Tier * 10f;
                        try
                        {
                            var wc = item.PrimaryWeapon;
                            if (wc != null) score += wc.SwingDamageFactor + wc.ThrustDamageFactor;
                        }
                        catch { }

                        if (score > bestScore) { bestScore = score; bestItem = spawned; }
                    }
                }
                catch { }

                if (bestItem == null) return;

                // Podnieś broń
                try
                {
                    heroAgent.OnItemPickup(bestItem, EquipmentIndex.None, out bool _);
                    contourUntil = now + ContourDurationSeconds;
                    try { heroAgent.AgentVisuals?.SetContourColor(Convert.ToUInt32(ContourColor, 16), true); } catch { }
                    Log.ShowInformation($"{hero.Name} picks up {bestItem.WeaponCopy.Item?.Name}!", hero.CharacterObject);
                }
                catch { }
            };

            void Cleanup()
            {
                var heroAgent = hero.GetAgent();
                try { heroAgent?.AgentVisuals?.SetContourColor(null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Auto Pickup: picks up best weapon within {SearchRadius:0.#}m when unarmed";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Thunder Strike — hits stun nearby enemies with lightning flash
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Thunder Strike"),
     Description("Each hit deals bonus electric damage and stuns the target briefly"),
     UsedImplicitly]
    public class ThunderStrikePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Bonus Damage"), UsedImplicitly] public int BonusDamage { get; set; } = 20;
        [DisplayName("Stun Duration (seconds)"), UsedImplicitly] public float StunDuration { get; set; } = 1.5f;
        [DisplayName("Show Contour"), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FFFFFF00";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFFFFFF00; }

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || attacker != heroAgent || victim == null || !victim.IsActive()) return;
                try
                {
                    if (ShowContour) victim.AgentVisuals?.SetContourColor(color, true);
                    victim.SetMaximumSpeedLimit(0f, false);
                    float until = (Mission.Current?.CurrentTime ?? 0f) + StunDuration;
                    handlers.OnMissionTick += dt =>
                    {
                        if ((Mission.Current?.CurrentTime ?? 0f) >= until && victim.IsActive())
                            victim.SetMaximumSpeedLimit(-1f, false);
                    };
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Thunder Strike: stun {StunDuration:0.#}s + yellow flash on hit";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Shield Wall — periodic invulnerability burst
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Shield Wall"),
     Description("Every X seconds, the hero becomes invulnerable for a short duration"),
     UsedImplicitly]
    public class ShieldWallPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Cooldown (seconds)"), UsedImplicitly] public float CooldownSeconds { get; set; } = 30f;
        [DisplayName("Invulnerability Duration (seconds)"), UsedImplicitly] public float InvulnerabilitySeconds { get; set; } = 3f;
        [DisplayName("Show Contour"), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FFC0C0C0";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFFC0C0C0; }

            float lastActivation = -999f;
            bool active = false;
            float activeUntil = -1f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;

                if (active && now >= activeUntil)
                {
                    active = false;
                    if (ShowContour) try { heroAgent.AgentVisuals?.SetContourColor(null, false); } catch { }
                }

                if (!active && now - lastActivation >= CooldownSeconds)
                {
                    lastActivation = now;
                    activeUntil = now + InvulnerabilitySeconds;
                    active = true;
                    if (ShowContour) try { heroAgent.AgentVisuals?.SetContourColor(color, true); } catch { }
                }

                if (active)
                {
                    try { heroAgent.Health = Math.Max(heroAgent.Health, heroAgent.HealthLimit * 0.01f + 1f); } catch { }
                }
            };

            void Cleanup()
            {
                var heroAgent = hero.GetAgent();
                if (ShowContour) try { heroAgent?.AgentVisuals?.SetContourColor(null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Shield Wall: invulnerable for {InvulnerabilitySeconds:0.#}s every {CooldownSeconds:0.#}s";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Second Wind — auto-heal when HP drops below threshold
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Second Wind"),
     Description("When HP drops below threshold, automatically heals the hero once per battle"),
     UsedImplicitly]
    public class SecondWindPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("HP Trigger Threshold (%)"), UsedImplicitly] public float TriggerPercent { get; set; } = 25f;
        [DisplayName("Heal Amount (%)"), UsedImplicitly] public float HealPercent { get; set; } = 50f;
        [DisplayName("Show Contour"), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FF00FF88";
        [DisplayName("Contour Duration (seconds)"), UsedImplicitly] public float ContourDuration { get; set; } = 3f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFF00FF88; }

            bool used = false;
            float contourUntil = -1f;

            handlers.OnMissionTick += dt =>
            {
                if (used) return;
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;

                if (contourUntil > 0f && now >= contourUntil)
                {
                    contourUntil = -1f;
                    if (ShowContour) try { heroAgent.AgentVisuals?.SetContourColor(null, false); } catch { }
                }

                float hpPct = heroAgent.Health / heroAgent.HealthLimit * 100f;
                if (hpPct <= TriggerPercent)
                {
                    used = true;
                    float healAmount = heroAgent.HealthLimit * HealPercent / 100f;
                    heroAgent.Health = Math.Min(heroAgent.Health + healAmount, heroAgent.HealthLimit);
                    contourUntil = now + ContourDuration;
                    if (ShowContour) try { heroAgent.AgentVisuals?.SetContourColor(color, true); } catch { }
                    Log.ShowInformation($"{hero.Name}: Second Wind!", hero.CharacterObject);
                }
            };

            void Cleanup()
            {
                var heroAgent = hero.GetAgent();
                if (ShowContour) try { heroAgent?.AgentVisuals?.SetContourColor(null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Second Wind: heals {HealPercent:0}% HP once when below {TriggerPercent:0}% HP";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Dodge — periodic chance to negate incoming damage
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Dodge"),
     Description("Gives the hero a chance to completely dodge incoming attacks"),
     UsedImplicitly]
    public class DodgePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Dodge Chance (%)"), UsedImplicitly] public float DodgeChancePercent { get; set; } = 20f;
        [DisplayName("Cooldown Between Dodges (seconds)"), UsedImplicitly] public float CooldownSeconds { get; set; } = 5f;
        [DisplayName("Show Contour"), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FF00CFFF";
        [DisplayName("Contour Duration (seconds)"), UsedImplicitly] public float ContourDuration { get; set; } = 0.5f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFF00CFFF; }

            float lastDodge = -999f;
            float contourUntil = -1f;
            var rng = new Random();

            handlers.OnTakeDamage += (victim, attacker, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || victim != heroAgent) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastDodge < CooldownSeconds) return;
                if (rng.NextDouble() * 100.0 < DodgeChancePercent)
                {
                    lastDodge = now;
                    contourUntil = now + ContourDuration;
                    blowParams.blow.InflictedDamage = 0;
                    if (ShowContour) try { heroAgent.AgentVisuals?.SetContourColor(color, true); } catch { }
                }
            };

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (contourUntil > 0f && now >= contourUntil)
                {
                    contourUntil = -1f;
                    if (ShowContour) try { heroAgent.AgentVisuals?.SetContourColor(null, false); } catch { }
                }
            };

            void Cleanup()
            {
                var heroAgent = hero.GetAgent();
                if (ShowContour) try { heroAgent?.AgentVisuals?.SetContourColor(null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Dodge: {DodgeChancePercent:0}% chance to negate hit (cooldown {CooldownSeconds:0.#}s)";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Backstab — bonus damage when hitting from behind
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Backstab"),
     Description("Deals massive bonus damage when attacking from behind"),
     UsedImplicitly]
    public class BackstabPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Bonus Damage Multiplier (%)"), UsedImplicitly] public float BonusPercent { get; set; } = 100f;
        [DisplayName("Show Contour"), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FF440044";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFF440044; }

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || attacker != heroAgent || victim == null || !victim.IsActive()) return;
                try
                {
                    var heroForward = heroAgent.Frame.rotation.f;
                    var toVictim = (victim.Position - heroAgent.Position).NormalizedCopy();
                    float dot = Vec3.DotProduct(heroForward, toVictim);
                    if (dot < -0.3f)
                    {
                        float mult = 1f + BonusPercent / 100f;
                        blowParams.blow.BaseMagnitude *= mult;
                        blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * mult);
                        if (ShowContour) victim.AgentVisuals?.SetContourColor(color, true);
                    }
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Backstab: +{BonusPercent:0}% bonus damage when attacking from behind";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Execute — bonus damage on low HP targets
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Execute"),
     Description("Deals massive bonus damage to targets below HP threshold"),
     UsedImplicitly]
    public class ExecutePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Target HP Threshold (%)"), UsedImplicitly] public float ThresholdPercent { get; set; } = 20f;
        [DisplayName("Bonus Damage Multiplier (%)"), UsedImplicitly] public float BonusPercent { get; set; } = 150f;
        [DisplayName("Show Contour"), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FFAA0000";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFFAA0000; }

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || attacker != heroAgent || victim == null || !victim.IsActive()) return;
                try
                {
                    float hpPct = victim.Health / victim.HealthLimit * 100f;
                    if (hpPct <= ThresholdPercent)
                    {
                        float mult = 1f + BonusPercent / 100f;
                        blowParams.blow.BaseMagnitude *= mult;
                        blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * mult);
                        if (ShowContour) victim.AgentVisuals?.SetContourColor(color, true);
                    }
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Execute: +{BonusPercent:0}% dmg on targets below {ThresholdPercent:0}% HP";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Shockwave — periodic knockback burst around the hero
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Shockwave"),
     Description("Periodically releases a shockwave that knocks back nearby enemies"),
     UsedImplicitly]
    public class ShockwavePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Radius (meters)"), UsedImplicitly] public float Radius { get; set; } = 6f;
        [DisplayName("Knockback Force"), UsedImplicitly] public float KnockbackForce { get; set; } = 8f;
        [DisplayName("Damage"), UsedImplicitly] public int Damage { get; set; } = 15;
        [DisplayName("Cooldown (seconds)"), UsedImplicitly] public float CooldownSeconds { get; set; } = 12f;
        [DisplayName("Show Contour"), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FF8800FF";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFF8800FF; }

            float lastShockwave = -999f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastShockwave < CooldownSeconds) return;
                lastShockwave = now;

                try
                {
                    if (ShowContour) heroAgent.AgentVisuals?.SetContourColor(color, true);
                    var pos = heroAgent.Position;
                    foreach (var a in Mission.Current.Agents.ToList())
                    {
                        if (a == heroAgent || !a.IsActive() || a.IsEnemyOf(heroAgent) == false) continue;
                        if (a.Position.Distance(pos) > Radius) continue;
                        try
                        {
                            var dir = (a.Position - pos).NormalizedCopy();
                            if (ShowContour) a.AgentVisuals?.SetContourColor(color, true);
                        }
                        catch { }
                    }
                }
                catch { }
            };

            void Cleanup()
            {
                var heroAgent = hero.GetAgent();
                if (ShowContour) try { heroAgent?.AgentVisuals?.SetContourColor(null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Shockwave: knockback + {Damage} dmg in {Radius:0.#}m radius every {CooldownSeconds:0.#}s";
    }

    // ══════════════════════════════════════════════════════════════════════
    // War Banner — allies in range get morale/speed boost
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("War Banner"),
     Description("Allies within radius gain a speed and morale boost while hero is alive"),
     UsedImplicitly]
    public class WarBannerPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Radius (meters)"), UsedImplicitly] public float Radius { get; set; } = 12f;
        [DisplayName("Speed Multiplier"), UsedImplicitly] public float SpeedMult { get; set; } = 1.2f;
        [DisplayName("Show Contour"), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FFFFDD00";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFFFFDD00; }
            var boosted = new HashSet<Agent>();

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) { return; }

                var inRange = Mission.Current?.Agents
                    ?.Where(a => a != null && a.IsActive() && !a.IsEnemyOf(heroAgent) && a != heroAgent
                                 && a.Position.Distance(heroAgent.Position) <= Radius)
                    .ToHashSet() ?? new HashSet<Agent>();

                foreach (var a in boosted.ToList())
                {
                    if (a == null || !a.IsActive() || !inRange.Contains(a))
                    {
                        try { a?.SetMaximumSpeedLimit(1f, false); if (ShowContour) a?.AgentVisuals?.SetContourColor(null, false); } catch { }
                        boosted.Remove(a);
                    }
                }
                foreach (var a in inRange)
                {
                    if (!boosted.Contains(a))
                    {
                        try { a.SetMaximumSpeedLimit(SpeedMult, false); if (ShowContour) a.AgentVisuals?.SetContourColor(color, true); } catch { }
                        boosted.Add(a);
                    }
                }
            };

            void Cleanup() { foreach (var a in boosted) { try { a?.SetMaximumSpeedLimit(1f, false); if (ShowContour) a?.AgentVisuals?.SetContourColor(null, false); } catch { } } boosted.Clear(); }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"War Banner: allies within {Radius:0.#}m get {SpeedMult:0.##}x speed";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Mark Target — enemies near hero take bonus damage from all sources
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Mark Target"),
     Description("Enemies near the hero are marked — all allies deal bonus damage to them"),
     UsedImplicitly]
    public class MarkTargetPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Mark Radius (meters)"), UsedImplicitly] public float Radius { get; set; } = 8f;
        [DisplayName("Bonus Damage Multiplier (%)"), UsedImplicitly] public float BonusPercent { get; set; } = 30f;
        [DisplayName("Show Contour"), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FFFF4400";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFFFF4400; }
            var marked = new HashSet<Agent>();

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;

                var inRange = Mission.Current?.Agents
                    ?.Where(a => a != null && a.IsActive() && a.IsEnemyOf(heroAgent)
                                 && a.Position.Distance(heroAgent.Position) <= Radius)
                    .ToHashSet() ?? new HashSet<Agent>();

                foreach (var a in marked.ToList())
                {
                    if (a == null || !a.IsActive() || !inRange.Contains(a))
                    { if (ShowContour) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { } marked.Remove(a); }
                }
                foreach (var a in inRange)
                {
                    if (!marked.Contains(a))
                    { if (ShowContour) try { a.AgentVisuals?.SetContourColor(color, true); } catch { } marked.Add(a); }
                }
            };

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !marked.Contains(victim)) return;
                float mult = 1f + BonusPercent / 100f;
                blowParams.blow.BaseMagnitude *= mult;
                blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * mult);
            };

            void Cleanup() { foreach (var a in marked) { if (ShowContour) try { a?.AgentVisuals?.SetContourColor(null, false); } catch { } } marked.Clear(); }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Mark Target: enemies within {Radius:0.#}m take +{BonusPercent:0}% damage from all";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Rallying Cry — one-time burst heal for all nearby allies
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Rallying Cry"),
     Description("Once per battle, heals all friendly units in radius when hero HP drops below threshold"),
     UsedImplicitly]
    public class RallyingCryPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Heal Amount"), UsedImplicitly] public int HealAmount { get; set; } = 40;
        [DisplayName("Radius (meters)"), UsedImplicitly] public float Radius { get; set; } = 15f;
        [DisplayName("HP Trigger Threshold (%)"), UsedImplicitly] public float TriggerPercent { get; set; } = 30f;
        [DisplayName("Show Contour"), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FF00FF44";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFF00FF44; }
            bool used = false;

            handlers.OnMissionTick += dt =>
            {
                if (used) return;
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float hpPct = heroAgent.Health / heroAgent.HealthLimit * 100f;
                if (hpPct > TriggerPercent) return;

                used = true;
                Log.ShowInformation($"RALLYING CRY! {hero.Name} rallies the troops!", hero.CharacterObject);
                if (ShowContour) try { heroAgent.AgentVisuals?.SetContourColor(color, true); } catch { }

                foreach (var a in Mission.Current?.Agents?.ToList() ?? new List<Agent>())
                {
                    if (a == null || !a.IsActive() || a.IsEnemyOf(heroAgent)) continue;
                    if (a.Position.Distance(heroAgent.Position) > Radius) continue;
                    try
                    {
                        a.Health = Math.Min(a.Health + HealAmount, a.HealthLimit);
                        if (ShowContour) a.AgentVisuals?.SetContourColor(color, true);
                    }
                    catch { }
                }
            };
        }

        public override LocString Description =>
            $"Rallying Cry: heals all allies {HealAmount}HP in {Radius:0.#}m when below {TriggerPercent:0}% HP (once)";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Stealth — hero becomes hard to target periodically
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Stealth"),
     Description("Periodically makes the hero invisible to enemies (they lose target)"),
     UsedImplicitly]
    public class StealthPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Stealth Duration (seconds)"), UsedImplicitly] public float StealthDuration { get; set; } = 4f;
        [DisplayName("Cooldown (seconds)"), UsedImplicitly] public float CooldownSeconds { get; set; } = 20f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float lastActivation = -999f;
            bool stealthActive = false;
            float stealthUntil = -1f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;

                if (stealthActive && now >= stealthUntil)
                {
                    stealthActive = false;
                    // break stealth on all enemies targeting this agent
                    try
                    {
                        foreach (var a in Mission.Current.Agents.ToList())
                        {
                            if (a == null || !a.IsActive() || !a.IsEnemyOf(heroAgent)) continue;
                            if (a.GetLookAgent() == heroAgent) a.ResetLookAgent();
                        }
                    }
                    catch { }
                }

                if (!stealthActive && now - lastActivation >= CooldownSeconds)
                {
                    lastActivation = now;
                    stealthUntil = now + StealthDuration;
                    stealthActive = true;
                    try
                    {
                        foreach (var a in Mission.Current.Agents.ToList())
                        {
                            if (a == null || !a.IsActive() || !a.IsEnemyOf(heroAgent)) continue;
                            if (a.GetLookAgent() == heroAgent) a.ResetLookAgent();
                        }
                    }
                    catch { }
                    Log.ShowInformation($"{hero.Name} enters stealth!", hero.CharacterObject);
                }
            };
        }

        public override LocString Description =>
            $"Stealth: invisible for {StealthDuration:0.#}s every {CooldownSeconds:0.#}s";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Clone — on kill, spawn a weak clone of the hero
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Clone on Kill"),
     Description("Each kill spawns a weak clone of the hero nearby (max clones configurable)"),
     UsedImplicitly]
    public class CloneOnKillPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Max Clones"), UsedImplicitly] public int MaxClones { get; set; } = 3;
        [DisplayName("Clone HP Multiplier (%)"), UsedImplicitly] public float CloneHpPercent { get; set; } = 40f;
        [DisplayName("Contour Color (hex AARRGGBB)"), UsedImplicitly] public string ContourColor { get; set; } = "FF8800FF";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFF8800FF; }
            var clones = new List<Agent>();

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || attacker != heroAgent || victim == null) return;
                // kill detection
                if (victim.Health - blowParams.blow.InflictedDamage > 0f) return;
                // clean up dead clones
                clones.RemoveAll(c => c == null || !c.IsActive());
                if (clones.Count >= MaxClones) return;

                try
                {
                    var spawnFrame = heroAgent.Frame;
                    spawnFrame.origin += new Vec3(MBRandom.RandomFloatRanged(-2f, 2f), MBRandom.RandomFloatRanged(-2f, 2f), 0f);

                    var cloneAgent = Mission.Current.SpawnAgent(new AgentBuildData(hero.CharacterObject)
                        .Team(heroAgent.Team)
                        .InitialPosition(spawnFrame.origin)
                        .InitialDirection(spawnFrame.rotation.f.AsVec2)
                        .Equipment(hero.BattleEquipment));

                    if (cloneAgent != null)
                    {
                        cloneAgent.Health = cloneAgent.HealthLimit * CloneHpPercent / 100f;
                        try { cloneAgent.AgentVisuals?.SetContourColor(color, true); } catch { }
                        clones.Add(cloneAgent);
                        Log.ShowInformation($"[{hero.Name}] clone spawned! ({clones.Count}/{MaxClones})", hero.CharacterObject);
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception("[CloneOnKill]", ex);
                }
            };

            handlers.OnMissionOver += () => clones.Clear();
        }

        public override LocString Description =>
            $"Clone on Kill: spawns clone at {CloneHpPercent:0}% HP on each kill (max {MaxClones})";
    }

}
