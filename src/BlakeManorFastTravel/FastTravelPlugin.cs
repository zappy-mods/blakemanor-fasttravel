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
    // chapters/times-of-day/story states sharing one handle) - so "room.<handle> == 2"
    // only proves *some* variant of that room was visited, not necessarily the specific
    // variant a given menu entry points to. Fast traveling into an unvisited variant can
    // load a scene whose state/prerequisites were never set up, which has crashed the game
    // before. A stricter, crash-proof version of this check (tracking the exact visited
    // SceneCollection rather than the shared handle) is available if that trade-off isn't
    // wanted.
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

        private bool _menuOpen;
        private Vector2 _scrollPos;
        private GameState _previousGameState = GameState.Normal;
        private List<EHSceneCollection> _destinations = new List<EHSceneCollection>();
        private string _statusMessage = "";

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
            List<EHSceneCollection> list = new List<EHSceneCollection>();

            SpookyDoorway.SceneCollectionsManager manager = EHKickStarter.SceneCollectionsManager;
            if (manager == null || manager.Collections == null)
            {
                return list;
            }

            SpookyDoorway.SceneCollection current = manager.GetCurrentlyOpenCollection();

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

                list.Add(ehCollection);
            }

            list.Sort((a, b) => string.Compare(DisplayName(a), DisplayName(b), StringComparison.OrdinalIgnoreCase));
            return list;
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

            // Dim the world behind the menu, same as the game's own popups do.
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), MenuTheme.Overlay);

            const float w = 440f;
            const float h = 520f;
            Rect windowRect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);

            GUI.Box(windowRect, GUIContent.none, MenuTheme.Panel);
            GUILayout.BeginArea(windowRect);
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
                    if (GUILayout.Button(DisplayName(destination), MenuTheme.DestinationButton, GUILayout.Height(36)))
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
            if (GUILayout.Button("Close", MenuTheme.CloseButton, GUILayout.Width(120), GUILayout.Height(30)))
            {
                CloseMenu();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(14);
            GUILayout.EndArea();
        }

        private void TravelTo(EHSceneCollection destination)
        {
            try
            {
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
