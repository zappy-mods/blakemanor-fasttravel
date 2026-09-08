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
        private Vector2 _scrollPos;
        private Rect _windowRect = new Rect(0f, 0f, 520f, 480f);
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
                _menuOpen = !_menuOpen;
                if (_menuOpen)
                {
                    RescanForFixes();
                }
            }
        }

        private void OnGUI()
        {
            if (!_menuOpen)
            {
                return;
            }
            _windowRect.x = (Screen.width - _windowRect.width) / 2f;
            _windowRect.y = (Screen.height - _windowRect.height) / 2f;
            _windowRect = GUILayout.Window(GetHashCode(), _windowRect, DrawFixWindow, "Evidence Fix Checklist");
        }

        private void DrawFixWindow(int windowId)
        {
            GUILayout.Label(
                "Proposed fixes for discovered mysteries whose hypothesis is currently " +
                "blocked by missing/unfiled essential evidence. Nothing here is applied " +
                "until you check it and press Apply.");

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUILayout.Label(_statusMessage);
            }

            _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(320f));
            if (_proposedFixes.Count == 0)
            {
                GUILayout.Label("No blocked hypotheses found among discovered mysteries.");
            }
            else
            {
                string lastMystery = null;
                foreach (ProposedFix fix in _proposedFixes)
                {
                    if (fix.MysteryName != lastMystery)
                    {
                        GUILayout.Space(8f);
                        GUILayout.Label($"{fix.MysteryLabel} ({fix.MysteryName})");
                        lastMystery = fix.MysteryName;
                    }

                    GUILayout.BeginHorizontal();
                    if (fix.Applied)
                    {
                        GUILayout.Label($"  [done] {fix.ItemLabel}");
                    }
                    else
                    {
                        fix.Approved = GUILayout.Toggle(fix.Approved, "");
                        string action = fix.NeedsAdd ? "add + file" : "file only";
                        GUILayout.Label($"{fix.ItemLabel} (id={fix.ItemId}, {action})");
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Rescan"))
            {
                RescanForFixes();
            }
            if (GUILayout.Button("Apply checked"))
            {
                ApplyApprovedFixes();
            }
            if (GUILayout.Button("Close"))
            {
                _menuOpen = false;
            }
            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
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
