using System;
using System.Collections.Generic;
using System.Linq;
using AC;
using BepInEx;
using BepInEx.Configuration;
using SpookyDoorway.EldritchHouse.Runtime.AC;
using SpookyDoorway.EldritchHouse.Runtime.UI.Journal;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BlakeManorEvidenceTracker
{
    // Checks every discovered mystery's essentialEvidenceIDs (the actual gate
    // RuntimeMysteries.CheckIfHypothesisUnlocked() uses - not just "linked to this mystery",
    // which turned out to be a looser, wrong signal: an item can be collected and linked to a
    // mystery yet still block its hypothesis if it hasn't been filed specifically under THAT
    // mystery, since filing is tracked per-mystery, not per-item) against what you actually
    // have, and proposes fixes for anything blocking. Nothing is ever applied automatically -
    // every fix has to be individually checked and then explicitly applied.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class EvidenceTrackerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "brian.blakemanor.evidencetracker";
        public const string PluginName = "Blake Manor Evidence Tracker";
        public const string PluginVersion = "0.0.1";

        // Same resize scheme as Blake Manor Fast Travel (see MenuTheme.cs's copy-over note -
        // a shared library covering this is worth doing once both mods stop actively
        // changing, not mid-iteration).
        private const float DefaultWidth = 560f;
        private const float DefaultHeight = 520f;
        private const float MinWidth = 420f;
        private const float MinHeight = 340f;
        private const float MaxWidth = 1000f;
        private const float MaxHeight = 860f;
        private const float ResizeHandleSize = 18f;

        private class ProposedFix
        {
            public string MysteryName;
            public string MysteryLabel;
            public int ItemId;
            public string ItemLabel;
            public bool NeedsAdd; // not currently in inventory at all
            public bool NeedsFile; // NeedsFiling and not filed under this specific mystery
            public bool Approved;
            public bool Applied;
        }

        // F9 is already taken by Blake Manor Fast Travel - this mod is commonly run
        // alongside it, so default to different keys outright rather than risk a collision.
        // F10/F11 were the original defaults but turned out to be commonly intercepted by the
        // OS/overlay (F11 especially - "toggle fullscreen" is close to universal) before ever
        // reaching the game, so this defaults to less-contested keys from the start.
        private ConfigEntry<Key> _dumpHotkeyConfig;
        private ConfigEntry<Key> _mysteryDumpHotkeyConfig;
        private ConfigEntry<Key> _fixMenuHotkeyConfig;

        private bool _menuOpen;
        private GameState _previousGameState = GameState.Normal;
        private Vector2 _scrollPos;
        private Rect _windowRect = new Rect(0f, 0f, DefaultWidth, DefaultHeight);
        private bool _resizingWindow;
        private readonly List<ProposedFix> _proposedFixes = new List<ProposedFix>();
        private string _statusMessage = "";

        private void Awake()
        {
            _dumpHotkeyConfig = Config.Bind(
                "General", "DumpHotkey", Key.F6,
                "Diagnostic dump: logs every item in the game's inventory database, its " +
                "category/bin, evidence-related flags, and whether you currently have it.");
            _mysteryDumpHotkeyConfig = Config.Bind(
                "General", "MysteryDumpHotkey", Key.F7,
                "Diagnostic dump: logs every discovered mystery, its linked evidence, and the " +
                "exact essentialEvidenceIDs gate that actually unlocks its hypothesis.");
            _fixMenuHotkeyConfig = Config.Bind(
                "General", "FixMenuHotkey", Key.F8,
                "Opens the evidence-fix checklist: proposed fixes for any discovered " +
                "mystery's hypothesis currently blocked by missing/unfiled essential evidence. " +
                "Nothing is applied until you check an item and press Apply.");
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }
            if (keyboard[_dumpHotkeyConfig.Value].wasPressedThisFrame)
            {
                DumpItemDatabase();
            }
            if (keyboard[_mysteryDumpHotkeyConfig.Value].wasPressedThisFrame)
            {
                DumpMysteries();
            }
            if (keyboard[_fixMenuHotkeyConfig.Value].wasPressedThisFrame)
            {
                if (_menuOpen)
                {
                    CloseMenu();
                }
                else
                {
                    TryOpenMenu();
                }
            }
            if (_menuOpen && keyboard.escapeKey.wasPressedThisFrame)
            {
                CloseMenu();
            }
        }

        // Mirrors Blake Manor Fast Travel's TryOpenMenu(): GameState.Paused is what actually
        // frees AC's mouse cursor and suspends first-person camera/movement input while a UI
        // is up - without setting it, clicks and cursor movement all still go to the game's
        // own camera-look/movement handling instead of this window, which is why the first
        // version of this menu was unusable (no mouse control at all).
        private void TryOpenMenu()
        {
            if (KickStarter.stateHandler == null)
            {
                return; // AC hasn't finished booting yet (e.g. still on the title screen)
            }
            if (KickStarter.stateHandler.gameState != GameState.Normal)
            {
                return; // don't pop the menu open mid-cutscene/dialogue/etc.
            }

            RescanForFixes();
            _previousGameState = KickStarter.stateHandler.gameState;
            KickStarter.stateHandler.gameState = GameState.Paused;
            _menuOpen = true;

            _windowRect.x = (Screen.width - _windowRect.width) / 2f;
            _windowRect.y = (Screen.height - _windowRect.height) / 2f;
        }

        private void CloseMenu()
        {
            _menuOpen = false;
            if (KickStarter.stateHandler != null)
            {
                KickStarter.stateHandler.gameState = _previousGameState;
            }
        }

        private void OnGUI()
        {
            if (!_menuOpen)
            {
                return;
            }
            MenuTheme.EnsureBuilt();

            // Text (and button sizing) scales with the window, using width as the driver -
            // clamped to the same ratio range MinWidth/MaxWidth already imply.
            float scale = Mathf.Clamp(_windowRect.width / DefaultWidth, MinWidth / DefaultWidth, MaxWidth / DefaultWidth);
            MenuTheme.ApplyScale(scale);

            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), MenuTheme.Overlay);

            HandleResize();

            GUI.Box(_windowRect, GUIContent.none, MenuTheme.Panel);
            GUILayout.BeginArea(_windowRect);
            GUILayout.Space(18);
            GUILayout.Label("EVIDENCE FIX CHECKLIST", MenuTheme.Title);
            GUILayout.Space(4);

            Rect ruleRect = GUILayoutUtility.GetRect(1f, 2f, GUILayout.ExpandWidth(true));
            ruleRect.x += 60f;
            ruleRect.width -= 120f;
            GUI.DrawTexture(ruleRect, MenuTheme.Rule);

            GUILayout.Space(10);
            GUILayout.Label(
                "Proposed fixes for discovered mysteries whose hypothesis is currently blocked " +
                "by missing/unfiled essential evidence. Nothing is applied until checked and " +
                "confirmed with Apply.", MenuTheme.Subtitle);
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUILayout.Label(_statusMessage, MenuTheme.Status);
            }
            GUILayout.Space(12);

            GUILayout.BeginHorizontal();
            GUILayout.Space(20);
            GUILayout.BeginVertical(MenuTheme.ScrollBackground, GUILayout.ExpandHeight(true));
            GUILayout.Space(6);

            if (_proposedFixes.Count == 0)
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label("No blocked hypotheses found among discovered mysteries.", MenuTheme.Body);
                GUILayout.FlexibleSpace();
            }
            else
            {
                _scrollPos = GUILayout.BeginScrollView(_scrollPos);
                string lastMystery = null;
                foreach (ProposedFix fix in _proposedFixes)
                {
                    if (fix.MysteryName != lastMystery)
                    {
                        GUILayout.Space(10);
                        GUILayout.Label($"{fix.MysteryLabel} ({fix.MysteryName})", MenuTheme.Subtitle);
                        lastMystery = fix.MysteryName;
                    }

                    GUILayout.BeginHorizontal();
                    GUILayout.Space(10);
                    if (fix.Applied)
                    {
                        GUILayout.Label($"[done] {fix.ItemLabel}", MenuTheme.Body);
                    }
                    else
                    {
                        fix.Approved = GUILayout.Toggle(fix.Approved, "", GUILayout.Width(24f));
                        string action = fix.NeedsAdd ? "add + file" : "file only";
                        GUILayout.Label($"{fix.ItemLabel} (id={fix.ItemId}, {action})", MenuTheme.Body);
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            GUILayout.EndVertical();
            GUILayout.Space(20);
            GUILayout.EndHorizontal();

            GUILayout.Space(14);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Rescan", MenuTheme.CloseButton, GUILayout.Width(100f), GUILayout.Height(32f)))
            {
                RescanForFixes();
            }
            GUILayout.Space(10);
            if (GUILayout.Button("Apply checked", MenuTheme.DestinationButton, GUILayout.Width(140f), GUILayout.Height(32f)))
            {
                ApplyApprovedFixes();
            }
            GUILayout.Space(10);
            if (GUILayout.Button("Close", MenuTheme.CloseButton, GUILayout.Width(100f), GUILayout.Height(32f)))
            {
                CloseMenu();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(16);

            GUILayout.EndArea();
            DrawResizeGrip();
        }

        // Drag-to-resize from the bottom-right corner, growing/shrinking symmetrically about
        // the window's center - ported from Blake Manor Fast Travel's identical HandleResize()/
        // DrawResizeGrip() (see the comment near DefaultWidth on why this is duplicated rather
        // than shared for now).
        private void HandleResize()
        {
            Event e = Event.current;
            Rect handleRect = new Rect(
                _windowRect.xMax - ResizeHandleSize,
                _windowRect.yMax - ResizeHandleSize,
                ResizeHandleSize,
                ResizeHandleSize);

            if (e.type == EventType.MouseDown && e.button == 0 && handleRect.Contains(e.mousePosition))
            {
                _resizingWindow = true;
                e.Use();
            }
            else if (_resizingWindow && e.type == EventType.MouseDrag)
            {
                float newWidth = Mathf.Clamp(_windowRect.width + e.delta.x * 2f, MinWidth, MaxWidth);
                float newHeight = Mathf.Clamp(_windowRect.height + e.delta.y * 2f, MinHeight, MaxHeight);
                _windowRect.x -= (newWidth - _windowRect.width) / 2f;
                _windowRect.y -= (newHeight - _windowRect.height) / 2f;
                _windowRect.width = newWidth;
                _windowRect.height = newHeight;
                e.Use();
            }
            else if (_resizingWindow && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp))
            {
                _resizingWindow = false;
                e.Use();
            }
        }

        private void DrawResizeGrip()
        {
            const float dot = 3f;
            const float gap = 5f;
            float baseX = _windowRect.xMax - 6f;
            float baseY = _windowRect.yMax - 6f;
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col <= row; col++)
                {
                    float x = baseX - row * gap + col * gap;
                    float y = baseY - row * gap;
                    GUI.DrawTexture(new Rect(x, y, dot, dot), MenuTheme.Rule);
                }
            }
        }

        // Rebuilds the proposed-fix list from scratch using the exact same gate
        // RuntimeMysteries.CheckIfHypothesisUnlocked() uses, restricted to Discovered
        // mysteries only - see the class comment on why that gate specifically, and why only
        // discovered ones.
        private void RescanForFixes()
        {
            _proposedFixes.Clear();
            _statusMessage = "";

            RuntimeMysteries runtimeMysteries = EHKickStarter.runtimeMysteries;
            if (runtimeMysteries == null || runtimeMysteries.GameData == null)
            {
                _statusMessage = "RuntimeMysteries not ready yet - is a save loaded?";
                return;
            }

            foreach (DSQuest quest in runtimeMysteries.GameData.QuestsByNameMap.Values)
            {
                if (!quest.Discovered || quest.HypothesisHasBeenUnlocked)
                {
                    continue;
                }

                string essentialIdsRaw = quest.Item?.LookupValue("essentialEvidenceIDs");
                if (string.IsNullOrEmpty(essentialIdsRaw))
                {
                    continue;
                }

                List<int> essentialIds;
                try
                {
                    essentialIds = essentialIdsRaw.Split(',').Select(int.Parse).ToList();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[EvidenceTracker] Failed to parse essentialEvidenceIDs for '{quest.QuestTechnicalTitle}': {ex.Message}");
                    continue;
                }

                foreach (int essentialId in essentialIds)
                {
                    EHInvItem item = EHKickStarter.runtimeInventory?.GetItem(essentialId);
                    bool inInventory = item != null;
                    bool filedHere = inInventory && runtimeMysteries.IsEvidenceFiledUnderMystery(essentialId, quest.TechTitle);
                    bool needsAdd = !inInventory;
                    // If it's not even in inventory we don't know its NeedsFiling flag from the
                    // live item, but every essential-evidence item observed so far needsFiling
                    // - and even if some don't, filing an item that doesn't need it is a no-op
                    // per FileEvidenceUnderMystery's own logic, so defaulting to "file it too"
                    // is safe either way.
                    bool needsFile = needsAdd || (item.NeedsFiling && !filedHere);
                    if (!needsAdd && !needsFile)
                    {
                        continue; // this specific essential item isn't what's blocking it
                    }

                    _proposedFixes.Add(new ProposedFix
                    {
                        MysteryName = quest.QuestTechnicalTitle,
                        MysteryLabel = quest.Label,
                        ItemId = essentialId,
                        ItemLabel = item?.label ?? GetItemLabelFromDatabase(essentialId),
                        NeedsAdd = needsAdd,
                        NeedsFile = needsFile,
                    });
                }
            }

            _statusMessage = $"Found {_proposedFixes.Count} proposed fix(es) across discovered mysteries.";
        }

        private string GetItemLabelFromDatabase(int itemId)
        {
            InvItem item = KickStarter.inventoryManager?.items?.FirstOrDefault(i => i.id == itemId);
            return item != null ? item.label : $"item {itemId}";
        }

        // Grant (if missing) then file (if needed), in that order - filing an item that isn't
        // actually held yet is a no-op for the hypothesis check either way, since
        // CheckIfHypothesisUnlocked requires GetItem() != null independent of filing state.
        private void ApplyApprovedFixes()
        {
            RuntimeMysteries runtimeMysteries = EHKickStarter.runtimeMysteries;
            if (runtimeMysteries == null)
            {
                _statusMessage = "RuntimeMysteries not available.";
                return;
            }

            int applied = 0;
            foreach (ProposedFix fix in _proposedFixes)
            {
                if (!fix.Approved || fix.Applied)
                {
                    continue;
                }

                try
                {
                    if (fix.NeedsAdd)
                    {
                        EHKickStarter.runtimeInventory.Add(fix.ItemId, 1, selectAfter: false);
                        Logger.LogInfo($"[EvidenceTracker] Added item id={fix.ItemId} ('{fix.ItemLabel}') to inventory.");
                    }
                    if (fix.NeedsFile)
                    {
                        runtimeMysteries.FileEvidenceUnderMystery(fix.ItemId, fix.MysteryName, silently: true);
                        Logger.LogInfo($"[EvidenceTracker] Filed item id={fix.ItemId} ('{fix.ItemLabel}') under mystery '{fix.MysteryName}'.");
                    }
                    fix.Applied = true;
                    applied++;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[EvidenceTracker] Failed to apply fix for id={fix.ItemId} under '{fix.MysteryName}': {ex.Message}");
                }
            }
            _statusMessage = $"Applied {applied} fix(es). Rescan to confirm hypotheses unlocked.";
        }

        private void DumpMysteries()
        {
            RuntimeMysteries runtimeMysteries = EHKickStarter.runtimeMysteries;
            if (runtimeMysteries == null || runtimeMysteries.GameData == null)
            {
                Logger.LogWarning("[EvidenceTracker] RuntimeMysteries/GameData not ready yet (still on title screen?).");
                return;
            }

            HashSet<int> collectedIds = KickStarter.runtimeInventory?.localItems != null
                ? new HashSet<int>(KickStarter.runtimeInventory.localItems.Select(i => i.id))
                : new HashSet<int>();

            int discoveredCount = 0;
            foreach (DSQuest quest in runtimeMysteries.GameData.QuestsByNameMap.Values)
            {
                if (!quest.Discovered)
                {
                    continue;
                }
                discoveredCount++;

                bool hasSubMysteries = runtimeMysteries.GameData.SubMysteriesByQuestNameMap.TryGetValue(quest.QuestTechnicalTitle, out List<DSQuest> subMysteries);
                bool hasParent = runtimeMysteries.GameData.ParentQuestsByChildQuestName.TryGetValue(quest.QuestTechnicalTitle, out List<DSQuest> parents);

                Logger.LogInfo(
                    $"[EvidenceTracker] Mystery '{quest.QuestTechnicalTitle}' ('{quest.Label}') state={quest.State} " +
                    $"complete={quest.Complete} subMysteries=[{(hasSubMysteries ? string.Join(", ", subMysteries.Select(m => m.QuestTechnicalTitle)) : "none")}] " +
                    $"parents=[{(hasParent ? string.Join(", ", parents.Select(m => m.QuestTechnicalTitle)) : "none")}]");

                if (!runtimeMysteries.GameData.ItemsByQuestNameMap.TryGetValue(quest.QuestTechnicalTitle, out HashSet<EHInvItem> items) || items.Count == 0)
                {
                    Logger.LogInfo($"[EvidenceTracker]   (no linked evidence items)");
                }
                else
                {
                    foreach (EHInvItem item in items)
                    {
                        bool collected = collectedIds.Contains(item.id);
                        bool filed = runtimeMysteries.IsEvidenceFiledUnderMystery(item.id, quest.TechTitle);
                        Logger.LogInfo(
                            $"[EvidenceTracker]   evidence id={item.id} label='{item.label}' needsFiling={item.NeedsFiling} " +
                            $"collected={collected} filedUnderThisMystery={filed}");
                    }
                }

                // The actual hypothesis-unlock gate (RuntimeMysteries.CheckIfHypothesisUnlocked):
                // every id in this quest's own essentialEvidenceIDs field must both be in
                // inventory AND, if NeedsFiling, filed specifically under *this* quest - not
                // just any mystery it happens to be linked to. This is a narrower, stricter set
                // than ItemsByQuestNameMap above (which is everything *linked* to the mystery,
                // not necessarily required for its hypothesis) and is what actually blocks
                // progress, so it needs checking separately with the same exact logic the game
                // itself uses.
                string essentialIdsRaw = quest.Item?.LookupValue("essentialEvidenceIDs");
                if (string.IsNullOrEmpty(essentialIdsRaw))
                {
                    Logger.LogInfo($"[EvidenceTracker]   hypothesisUnlocked={quest.HypothesisHasBeenUnlocked} (no essentialEvidenceIDs field)");
                }
                else
                {
                    List<int> essentialIds;
                    try
                    {
                        essentialIds = essentialIdsRaw.Split(',').Select(int.Parse).ToList();
                    }
                    catch
                    {
                        Logger.LogWarning($"[EvidenceTracker]   Failed to parse essentialEvidenceIDs='{essentialIdsRaw}' for '{quest.QuestTechnicalTitle}'");
                        continue;
                    }

                    Logger.LogInfo($"[EvidenceTracker]   hypothesisUnlocked={quest.HypothesisHasBeenUnlocked} essentialEvidenceIDs=[{string.Join(",", essentialIds)}]");
                    foreach (int essentialId in essentialIds)
                    {
                        EHInvItem essentialItem = EHKickStarter.runtimeInventory?.GetItem(essentialId);
                        bool inInventory = essentialItem != null;
                        bool filedHere = inInventory && runtimeMysteries.IsEvidenceFiledUnderMystery(essentialId, quest.TechTitle);
                        bool blocksHypothesis = !inInventory || (inInventory && essentialItem.NeedsFiling && !filedHere);
                        Logger.LogInfo(
                            $"[EvidenceTracker]     essential id={essentialId} label='{essentialItem?.label ?? "(not in inventory)"}' " +
                            $"inInventory={inInventory} filedUnderThisMystery={filedHere} BLOCKS_HYPOTHESIS={blocksHypothesis}");
                    }
                }
            }
            Logger.LogInfo($"[EvidenceTracker] --- {discoveredCount} discovered mysteries (of {runtimeMysteries.GameData.QuestsByNameMap.Count} total) ---");
        }

        private void DumpItemDatabase()
        {
            if (KickStarter.inventoryManager == null)
            {
                Logger.LogWarning("[EvidenceTracker] KickStarter.inventoryManager not ready yet (still on title screen?).");
                return;
            }

            HashSet<int> collectedIds = KickStarter.runtimeInventory?.localItems != null
                ? new HashSet<int>(KickStarter.runtimeInventory.localItems.Select(i => i.id))
                : new HashSet<int>();

            Logger.LogInfo($"[EvidenceTracker] --- Item database dump: {KickStarter.inventoryManager.items.Count} items, " +
                $"{KickStarter.inventoryManager.bins.Count} bins ---");
            for (int i = 0; i < KickStarter.inventoryManager.bins.Count; i++)
            {
                Logger.LogInfo($"[EvidenceTracker] bin[{i}] = '{KickStarter.inventoryManager.bins[i].label}'");
            }

            foreach (InvItem item in KickStarter.inventoryManager.items)
            {
                bool collected = collectedIds.Contains(item.id);
                if (item is EHInvItem ehItem)
                {
                    Logger.LogInfo(
                        $"[EvidenceTracker] id={item.id} label='{item.label}' bin={item.binID} " +
                        $"needsFiling={ehItem.NeedsFiling} mysteries='{ehItem.Mysteries}' " +
                        $"critical={ehItem.Critical} dupeOf={ehItem.DupeOf} collected={collected}");
                }
                else
                {
                    Logger.LogInfo($"[EvidenceTracker] id={item.id} label='{item.label}' bin={item.binID} (not EHInvItem) collected={collected}");
                }
            }
            Logger.LogInfo("[EvidenceTracker] --- End of dump ---");
        }
    }
}
