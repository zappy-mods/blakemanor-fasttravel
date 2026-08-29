using System;
using System.Collections.Generic;
using AC;
using BepInEx;
using SpookyDoorway.EldritchHouse.Runtime.AC;
using SpookyDoorway.EldritchHouse.Runtime.Tools;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BlakeManorFastTravel
{
    // A simple fast-travel menu for The Seance of Blake Manor.
    //
    // The game (Adventure Creator + a custom "scene slinger" layer) already tracks room
    // discovery via a per-room AC Global Variable named "room.<handle>": 0 = never seen,
    // 1 = merely spotted (e.g. via the paper map object - may still be locked), 2 = the
    // player has actually physically loaded into that room at least once (set in
    // EHSceneSettings.OnStart(), i.e. only after getting past any lock/requirement). We
    // gate fast travel on 2 so it only offers places already reached the normal way.
    //
    // Caveat: this variable is keyed on the room's *handle*, and a single physical room
    // can be represented by several distinct SceneCollection assets (different
    // chapters/times-of-day/story states sharing one handle). Fast travel should still
    // work across those - e.g. having visited the Lobby on day 2 evening should let you
    // fast travel there on day 3 morning - but it must land you in *today's* variant of
    // the room, not the literal historical asset you happened to visit it through.
    //
    // The crash this used to hit: EHSceneChanger.LoadLevelASync indexes
    // destination.generatedScenesLoadingGroup[(int)SceneAppearanceController.sceneState]
    // with no bounds check. sceneState is a global, ever-advancing appearance/chapter
    // index, and each SceneCollection asset's generatedScenesLoadingGroup list is only
    // ever as long as the range of states that asset was authored to support. Handing it
    // a stale asset (e.g. the day-2-evening Lobby, on day 3 morning) can index past the
    // end of that list and hard-crash the game.
    //
    // GetDiscoveredDestinations() below fixes this at the source: it dedupes by handle
    // and, among all assets sharing a handle, only offers ones where sceneState is
    // actually in range for that asset's generatedScenesLoadingGroup, preferring
    // whichever variant's TickZoneDay1/TickZoneDay2 matches the current day. TravelTo()
    // also re-checks the bounds right before loading, so even if that selection is ever
    // wrong we fail soft (a status message) instead of crashing.
    //
    // We reuse the exact scene-change call the game's own doors/debug menu use
    // (EHSceneChanger.ChangeScene) so loading, saving of room state, and player placement
    // in the destination scene all behave exactly like a normal room transition.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class FastTravelPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "zappymods.blakemanor.fasttravel";
        public const string PluginName = "Blake Manor Fast Travel";
        public const string PluginVersion = "1.0.0";

        private const float DefaultWidth = 440f;
        private const float DefaultHeight = 520f;
        private const float MinWidth = 320f;
        private const float MinHeight = 300f;
        private const float MaxWidth = 900f;
        private const float MaxHeight = 820f;
        private const float ResizeHandleSize = 18f;

        private bool _menuOpen;
        private Vector2 _scrollPos;
        private GameState _previousGameState = GameState.Normal;
        private List<EHSceneCollection> _destinations = new List<EHSceneCollection>();
        private string _statusMessage = "";

        // Top-left-anchored so resizing (via the bottom-right grip) only ever grows/shrinks
        // to the right/down - the window doesn't jump around under the cursor while dragging.
        // Re-centered on screen each time the menu is opened; size is kept across opens.
        private Rect _windowRect = new Rect(0f, 0f, DefaultWidth, DefaultHeight);
        private bool _resizingWindow;

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard.f9Key.wasPressedThisFrame)
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
            else if (_menuOpen && keyboard.escapeKey.wasPressedThisFrame)
            {
                CloseMenu();
            }
        }

        private void TryOpenMenu()
        {
            if (KickStarter.stateHandler == null || KickStarter.sceneChanger == null || KickStarter.settingsManager == null)
            {
                // AC hasn't finished booting yet (e.g. still on the title screen).
                return;
            }
            if (KickStarter.stateHandler.gameState != GameState.Normal)
            {
                // Don't pop the menu open mid-cutscene/dialogue/etc.
                return;
            }

            _destinations = GetDiscoveredDestinations();
            _statusMessage = "";
            _previousGameState = KickStarter.stateHandler.gameState;
            KickStarter.stateHandler.gameState = GameState.Paused;
            _menuOpen = true;

            // Re-center on screen, but keep whatever size the player last resized it to.
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

        private List<EHSceneCollection> GetDiscoveredDestinations()
        {
            SpookyDoorway.SceneCollectionsManager manager = EHKickStarter.SceneCollectionsManager;
            if (manager == null || manager.Collections == null)
            {
                return new List<EHSceneCollection>();
            }

            SpookyDoorway.SceneCollection current = manager.GetCurrentlyOpenCollection();
            int currentAppearanceIndex = (int)SceneAppearanceController.sceneState;

            // One entry per handle - among all assets sharing a handle, keep only the
            // best candidate for "today's" version of that room.
            Dictionary<string, EHSceneCollection> bestByHandle = new Dictionary<string, EHSceneCollection>();

            foreach (SpookyDoorway.SceneCollection collection in manager.Collections)
            {
                EHSceneCollection ehCollection = collection as EHSceneCollection;
                if (ehCollection == null || string.IsNullOrEmpty(ehCollection.handle))
                {
                    continue;
                }
                if (current != null && ehCollection.Path == current.Path)
                {
                    continue; // already here
                }

                // val == 1 only means "spotted on the map" (e.g. via the paper map object) -
                // the room may still be behind a locked door the player hasn't opened yet.
                // val == 2 is only ever set by EHSceneSettings.OnStart(), which runs after the
                // player has actually physically loaded into that room - i.e. they've already
                // gotten past whatever lock/requirement stood in the way at least once. Gating
                // on 2 here is what keeps fast travel from skipping locked doors/key requirements.
                GVar discoveredVar = GlobalVariables.GetVariable("room." + ehCollection.handle);
                if (discoveredVar == null || discoveredVar.val < 2)
                {
                    continue; // not yet actually visited by the player
                }

                // Skip anything that can't support today's global appearance state - this
                // is the exact bounds check EHSceneChanger.LoadLevelASync itself omits
                // before indexing generatedScenesLoadingGroup, so anything that fails it
                // would crash on load.
                if (currentAppearanceIndex < 0 ||
                    currentAppearanceIndex >= ehCollection.generatedScenesLoadingGroup.Count)
                {
                    continue;
                }

                if (!bestByHandle.TryGetValue(ehCollection.handle, out EHSceneCollection existing) ||
                    IsBetterVariantForToday(ehCollection, existing))
                {
                    bestByHandle[ehCollection.handle] = ehCollection;
                }
            }

            List<EHSceneCollection> list = new List<EHSceneCollection>(bestByHandle.Values);
            list.Sort((a, b) => string.Compare(DisplayName(a), DisplayName(b), StringComparison.OrdinalIgnoreCase));
            return list;
        }

        // Prefers whichever candidate's tick zone for the current day actually applies
        // (i.e. isn't None) - a best-effort match for "the version of this room that's
        // current right now", using the same day/tick-zone fields EHSceneChanger reads.
        private static bool IsBetterVariantForToday(EHSceneCollection candidate, EHSceneCollection existing)
        {
            int currentDay = EHKickStarter.RuntimeTimeManager.ReturnCopyOfActiveBucket.day;
            EHSceneCollection.TickZone candidateZone = currentDay > 1 ? candidate.TickZoneDay2 : candidate.TickZoneDay1;
            EHSceneCollection.TickZone existingZone = currentDay > 1 ? existing.TickZoneDay2 : existing.TickZoneDay1;
            return candidateZone != EHSceneCollection.TickZone.None && existingZone == EHSceneCollection.TickZone.None;
        }

        private static string DisplayName(EHSceneCollection collection)
        {
            return string.IsNullOrEmpty(collection.label) ? collection.Path : collection.label;
        }

        private void OnGUI()
        {
            if (!_menuOpen)
            {
                return;
            }

            MenuTheme.EnsureBuilt();

            // Text (and button sizing) scales with the window, using width as the driver -
            // clamped to the same ratio range MinWidth/MaxWidth already imply, spelled out
            // explicitly here so it stays correct if those constants ever change.
            float scale = Mathf.Clamp(_windowRect.width / DefaultWidth, MinWidth / DefaultWidth, MaxWidth / DefaultWidth);
            MenuTheme.ApplyScale(scale);

            // Dim the world behind the menu, same as the game's own popups do.
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), MenuTheme.Overlay);

            HandleResize();

            GUI.Box(_windowRect, GUIContent.none, MenuTheme.Panel);
            GUILayout.BeginArea(_windowRect);
            GUILayout.Space(18);
            GUILayout.Label("FAST TRAVEL", MenuTheme.Title);
            GUILayout.Space(4);

            Rect ruleRect = GUILayoutUtility.GetRect(1f, 2f, GUILayout.ExpandWidth(true));
            ruleRect.x += 60f;
            ruleRect.width -= 120f;
            GUI.DrawTexture(ruleRect, MenuTheme.Rule);

            GUILayout.Space(10);
            GUILayout.Label("Choose a location you've already visited", MenuTheme.Subtitle);
            GUILayout.Label("F9 or Esc to close", MenuTheme.Subtitle);
            GUILayout.Space(12);

            GUILayout.BeginHorizontal();
            GUILayout.Space(20);
            GUILayout.BeginVertical(MenuTheme.ScrollBackground, GUILayout.ExpandHeight(true));
            GUILayout.Space(6);

            if (_destinations.Count == 0)
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label("No other visited locations yet.", MenuTheme.Body);
                GUILayout.FlexibleSpace();
            }
            else
            {
                _scrollPos = GUILayout.BeginScrollView(_scrollPos);
                foreach (EHSceneCollection destination in _destinations)
                {
                    if (GUILayout.Button(DisplayName(destination), MenuTheme.DestinationButton, GUILayout.Height(36f * scale)))
                    {
                        TravelTo(destination);
                        break;
                    }
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(6);
            GUILayout.EndVertical();
            GUILayout.Space(20);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUILayout.Label(_statusMessage, MenuTheme.Status);
            }

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", MenuTheme.CloseButton, GUILayout.Width(120f * scale), GUILayout.Height(30f * scale)))
            {
                CloseMenu();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(14);
            GUILayout.EndArea();

            DrawResizeGrip();
        }

        // Drag-to-resize from the bottom-right corner. _windowRect's top-left stays put,
        // so this is just "grow/shrink width and height by however far the mouse moved".
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
                _windowRect.width = Mathf.Clamp(_windowRect.width + e.delta.x, MinWidth, MaxWidth);
                _windowRect.height = Mathf.Clamp(_windowRect.height + e.delta.y, MinHeight, MaxHeight);
                e.Use();
            }
            else if (_resizingWindow && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp))
            {
                _resizingWindow = false;
                e.Use();
            }
        }

        // A small diagonal dot-grid in the corner, echoing the panel's gold rule line -
        // just enough to read as "draggable" without looking like a modern OS widget.
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

        private void TravelTo(EHSceneCollection destination)
        {
            try
            {
                // Belt-and-suspenders re-check: GetDiscoveredDestinations() should only
                // ever hand us an in-bounds destination, but re-verify right before the
                // scene load actually happens (the source of truth this mirrors is
                // EHSceneChanger.LoadLevelASync's generatedScenesLoadingGroup[index]
                // lookup, which has no bounds check of its own and is what crashes the
                // game if handed a stale/incompatible variant).
                int currentAppearanceIndex = (int)SceneAppearanceController.sceneState;
                if (currentAppearanceIndex < 0 ||
                    currentAppearanceIndex >= destination.generatedScenesLoadingGroup.Count)
                {
                    _statusMessage = "Can't fast travel there right now - try again after moving normally.";
                    return;
                }

                SpookyDoorway.SceneCollection current = EHKickStarter.SceneCollectionsManager.GetCurrentlyOpenCollection();
                string unloadPath = current != null ? current.Path : string.Empty;

                EHSceneChanger.SceneCollectionInfo info = new EHSceneChanger.SceneCollectionInfo(
                    ChooseSceneBy.Name,
                    destination.Path,
                    0,
                    unloadPath,
                    SceneAppearanceController.sceneState,
                    null);

                EHSceneChanger ehSceneChanger = KickStarter.sceneChanger as EHSceneChanger;
                ehSceneChanger?.SetCurrentSceneInfo(info);

                _menuOpen = false;
                KickStarter.stateHandler.gameState = GameState.Normal;

                EHKickStarter.EHSceneChanger.ChangeScene(
                    info,
                    saveRoomData: true,
                    forceReload: false,
                    _removeNPCID: 0,
                    _takeNPCPosition: false,
                    altLoadingScreen: string.Empty,
                    minLoadingTime: 1,
                    useLoadingMusic: KickStarter.settingsManager.useLoadingMusic,
                    loadingMusicID: KickStarter.settingsManager.loadingMusicID,
                    loopLoading: true);
            }
            catch (Exception ex)
            {
                _statusMessage = "Fast travel failed: " + ex.Message;
                Debug.LogError("[BlakeManorFastTravel] Fast travel failed: " + ex);
            }
        }
    }
}
