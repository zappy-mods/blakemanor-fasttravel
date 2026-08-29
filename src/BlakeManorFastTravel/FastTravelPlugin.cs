using System;
using System.Collections.Generic;
using System.IO;
using AC;
using BepInEx;
using HarmonyLib;
using SpookyDoorway.EldritchHouse.Runtime.AC;
using SpookyDoorway.EldritchHouse.Runtime.AC.UI.Journal.Map;
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
    // player has actually physically loaded into that room at least once (set
    // unconditionally in EHSceneSettings.OnStart() whenever that scene starts, for *any*
    // reason - a normal door, but equally a forced cutscene/vision/dream sequence sharing
    // the same handle - so on its own this only ever means "this scene has started once",
    // not "you got past whatever normally gates it"). We gate fast travel on 2 as a
    // baseline, then narrow further with HasPassableChecks() - see the comment on
    // _conditionChecksByHandle - for anything that's actually key- or time-gated.
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
    //
    // A few more things this plugin does around that load, all because fast travel's direct
    // cross-region jump exposed base-game rough edges that door-by-door movement mostly
    // hides:
    //
    // (1) A Harmony patch silences MapArea.GetTimeTableDataForLocationAndTime()'s per-entry
    // Debug.Log/Debug.LogWarning spam - that method is a synchronous, unbatched scan run
    // once per journal map area on every scene change, and Unity's Debug.Log is expensive
    // enough (stack-trace capture per call) that logging alone can make a multi-region jump
    // look like a hang.
    //
    // (2) A second Harmony patch silences the same kind of spam in
    // SceneCollectionsManager.GetCurrentlyOpenCollection(): on a miss it string-concatenates
    // every registered scene collection's names (~140 of them) into an error, and it misses
    // on every call made while the active scene is still the intermediate "Loading" scene.
    //
    // Both patches only suppress logging - the patched methods' actual return values are
    // untouched. (3) TravelTo() shows a small "Traveling..." toast for as long as that load
    // is still in flight (polling GetCurrentlyOpenCollection(), throttled and skipped while
    // on "Loading" - an earlier version of this polled every frame with no such guard, which
    // hit exactly the GetCurrentlyOpenCollection() cost described in (2) and was itself a
    // worse hang than the one it was meant to cover for), so a slow load reads as "working",
    // not "frozen".
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
        private const float TravelTimeoutSeconds = 45f;
        private const float TravelPollIntervalSeconds = 1f;

        private bool _menuOpen;
        private Vector2 _scrollPos;
        private GameState _previousGameState = GameState.Normal;
        private List<EHSceneCollection> _destinations = new List<EHSceneCollection>();
        private string _statusMessage = "";
        private Harmony _harmony;

        // Menu toggle to bypass HasPassableChecks() (keys/time-of-day/etc.) - resets to off
        // every launch, on purpose, so a forgotten toggle from last session can't surprise
        // you. Deliberately does NOT touch the crash-safety checks (the appearance-index
        // bounds check, or the IsLoading concurrency guard) - those stay mandatory no matter
        // what, since skipping either is what used to crash/hang the game outright rather
        // than just letting you somewhere the story wouldn't otherwise let you in yet.
        private bool _ignoreAccessChecks;

        // Re-centered on screen each time the menu is opened, and kept centered as it's
        // resized (grip drag grows/shrinks symmetrically about the center rather than
        // anchoring the top-left); size is kept across opens.
        private Rect _windowRect = new Rect(0f, 0f, DefaultWidth, DefaultHeight);
        private bool _resizingWindow;

        // Tracks an in-flight fast travel so OnGUI can show a "Traveling..." toast for as
        // long as it's still loading - see UpdateTravelingState().
        private bool _traveling;
        private string _travelDestinationPath;
        private float _travelStartTime;
        private float _lastTravelPollTime;

        // DEV-ONLY diagnostic (see LogKeyedHandleCandidatesOnce): true once we've logged
        // real handle strings, so we can build an accurate DoorKeys->handle table instead
        // of guessing. Remove once that table is filled in and verified.
        private bool _loggedKeyedHandleCandidates;

        // DEV-ONLY diagnostic (see ScanForDoorLinksThrottled) turned real mechanism: scans
        // every AC.ActionList currently loaded for ones that also change scenes
        // (ActionScene_EH), and caches every AC.ActionCheck-derived action found alongside
        // it - an inventory check for a key door, an ActionEHCheckTime for something like
        // the Dining Room's "closed outside of meal times" gate, or any other condition -
        // keyed by destination handle. No hand-built key/handle table needed: whatever gets
        // captured is authoritative by construction, since HasPassableChecks() calls the
        // game's own CheckCondition() live rather than reimplementing what it means.
        // Doors only exist as live objects in whatever scene they're placed in, so this
        // only ever covers doors in scenes that have actually loaded - it fills in as you
        // walk/fast-travel around, not all at once; anything not yet scanned just falls
        // back to the plain room.<handle> >= 2 check, same as before this existed.
        private const float DoorScanIntervalSeconds = 2f;
        private float _lastDoorScanTime;
        private readonly HashSet<int> _scannedActionListIds = new HashSet<int>();
        private string _doorLinksLogPath;
        private readonly Dictionary<string, List<AC.ActionCheck>> _conditionChecksByHandle = new Dictionary<string, List<AC.ActionCheck>>();

        // Fails open (returns true) for a handle with no captured checks - nothing scanned
        // there yet, or it genuinely has no extra condition - so this can only ever narrow
        // what room.<handle> already allowed, never expand it or be the sole reason a
        // legitimately-visited room becomes unreachable.
        private bool HasPassableChecks(string handle)
        {
            if (!_conditionChecksByHandle.TryGetValue(handle, out List<AC.ActionCheck> checks))
            {
                return true;
            }
            foreach (AC.ActionCheck check in checks)
            {
                if (check != null && !check.CheckCondition())
                {
                    return false;
                }
            }
            return true;
        }

        private void Awake()
        {
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            string pluginDir = Path.GetDirectoryName(typeof(FastTravelPlugin).Assembly.Location) ?? ".";
            _doorLinksLogPath = Path.Combine(pluginDir, "door_links.log");
            try
            {
                File.AppendAllText(_doorLinksLogPath, $"--- session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[BlakeManorFastTravel] Failed to open door_links.log: " + ex.Message);
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        private void Update()
        {
            if (_traveling)
            {
                UpdateTravelingState();
            }

            ScanForDoorLinksThrottled();
            LogHeartbeatStateThrottled();

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            bool shiftHeld = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;

            if (keyboard.f9Key.wasPressedThisFrame)
            {
                if (shiftHeld)
                {
                    // Emergency escape hatch: several hangs we've hit leave gameState stuck
                    // off Normal (which is also what TryOpenMenu() requires, on purpose, to
                    // avoid popping the menu open mid-cutscene) - meaning plain F9 does
                    // nothing and looks exactly like a full deadlock even when it isn't one.
                    // Shift+F9 bypasses that gate specifically to get out of a stuck room,
                    // rather than force-quitting. It does NOT bypass the IsLoading() check
                    // in TravelTo() - that one guards against colliding with a load that's
                    // still genuinely in progress, which forcing through would only make
                    // worse, not better.
                    if (_menuOpen)
                    {
                        CloseMenu();
                    }
                    else
                    {
                        ForceOpenMenu();
                    }
                }
                else if (_menuOpen)
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

        // Periodic, always-on state snapshot (not tied to an in-flight travel) - gameState
        // is what actually gates player movement/animation throughout AC, so a continuous
        // trace of it (plus IsLoading/active scene/player-null) is the only way to catch
        // "it got stuck on some non-Normal value and never came back" after the fact,
        // regardless of whether a fast travel was even involved. Cheap: BepInEx's own
        // Logger, not Unity's Debug.Log, and only once every HeartbeatIntervalSeconds.
        private const float HeartbeatIntervalSeconds = 5f;
        private float _lastHeartbeatTime;

        private void LogHeartbeatStateThrottled()
        {
            if (Time.unscaledTime - _lastHeartbeatTime < HeartbeatIntervalSeconds)
            {
                return;
            }
            _lastHeartbeatTime = Time.unscaledTime;

            Logger.LogInfo(
                $"[BlakeManorFastTravel] heartbeat: gameState={KickStarter.stateHandler?.gameState} " +
                $"IsLoading={KickStarter.sceneChanger?.IsLoading()} " +
                $"activeScene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}' " +
                $"playerNull={KickStarter.player == null}");
        }

        // Clears _traveling once the world actually reflects the destination we asked for
        // (i.e. the load finished), or after TravelTimeoutSeconds regardless - a failsafe
        // so a toast can never get stuck on-screen forever if something else goes wrong.
        //
        // GetCurrentlyOpenCollection() logs a very expensive error (string-concatenating
        // every one of the ~140 registered scene collection paths) whenever the active
        // scene doesn't match any of them - which is exactly true for the entire time
        // we're still on the intermediate "Loading" scene. An earlier version of this
        // polled it every frame, which spammed that error at 60fps for the whole load -
        // a worse hang than the one this toast was meant to cover for. Skip the call
        // outright while still on "Loading", and throttle it the rest of the time; once a
        // second is more than enough responsiveness for a UI toast.
        private void UpdateTravelingState()
        {
            if (Time.unscaledTime - _travelStartTime > TravelTimeoutSeconds)
            {
                // If this ever actually fires, it means the destination never became the
                // open collection within 45s of a successful ChangeScene() call - i.e. a
                // load that really did hang, not just run long. Worth a loud log line: this
                // is the single strongest signal we have for diagnosing a stuck black
                // screen after the fact, since nothing else here would otherwise record it.
                Logger.LogWarning(
                    $"[BlakeManorFastTravel] Travel to '{_travelDestinationPath}' did not complete within " +
                    $"{TravelTimeoutSeconds}s (still active scene: " +
                    $"'{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}', " +
                    $"IsLoading={KickStarter.sceneChanger?.IsLoading()}). Giving up on the toast.");
                _traveling = false;
                return;
            }
            if (Time.unscaledTime - _lastTravelPollTime < TravelPollIntervalSeconds ||
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Loading")
            {
                return;
            }
            _lastTravelPollTime = Time.unscaledTime;

            SpookyDoorway.SceneCollection current = EHKickStarter.SceneCollectionsManager?.GetCurrentlyOpenCollection();
            if (current != null && current.Path == _travelDestinationPath)
            {
                // gameState is what actually gates player movement/animation throughout AC -
                // logging it here lets us tell "scene loaded fine but player control never
                // came back" (gameState stuck off Normal) apart from "scene itself never
                // finished" (the timeout branch above), which look identical in-game but
                // need different fixes.
                Logger.LogInfo(
                    $"[BlakeManorFastTravel] Travel to '{_travelDestinationPath}' completed after " +
                    $"{Time.unscaledTime - _travelStartTime:0.0}s. gameState={KickStarter.stateHandler?.gameState} " +
                    $"playerNull={KickStarter.player == null}");
                _traveling = false;
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
            if (KickStarter.sceneChanger.IsLoading())
            {
                // Don't let a second fast travel get queued up while one is still loading -
                // see the comment on the same check in TravelTo() for why that matters.
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

        // Shift+F9's target: same as TryOpenMenu() but skips its gameState/IsLoading gates
        // entirely - see the comment where this is called from Update(). Forces gameState
        // to Normal (rather than reading/restoring whatever it currently is) since a stuck
        // non-Normal value is the most likely reason this was needed in the first place;
        // CloseMenu() will restore back to Normal on exit either way.
        private void ForceOpenMenu()
        {
            Logger.LogWarning(
                $"[BlakeManorFastTravel] Emergency menu open (Shift+F9): gameState was " +
                $"{KickStarter.stateHandler?.gameState}, IsLoading={KickStarter.sceneChanger?.IsLoading()}, " +
                $"activeScene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'");

            _destinations = GetDiscoveredDestinations();
            _statusMessage = "Emergency mode - locks/loading checks bypassed to open this menu.";
            // Paused is what actually triggers AC's own menu-mode behavior (frees the mouse
            // cursor, suspends first-person camera control while a UI is up) - the same
            // state TryOpenMenu() uses normally. But jumping straight to Paused from
            // whatever broken state we're recovering from only fixed the cursor, not
            // camera-look - it kept consuming raw mouse movement independently of the
            // cursor. What actually cleared that (confirmed by testing) was transitioning
            // all the way back to Normal first - closing the menu via Escape, which sets
            // gameState = Normal, is what re-armed it. So: pass through Normal here too,
            // as an explicit assignment of its own, before settling on Paused - that gets
            // the same fix without making the player Escape out and back in manually.
            // _previousGameState stays Normal either way, so CloseMenu() resolves to a
            // working state on exit regardless.
            _previousGameState = GameState.Normal;
            if (KickStarter.stateHandler != null)
            {
                KickStarter.stateHandler.gameState = GameState.Normal;
                KickStarter.stateHandler.gameState = GameState.Paused;
            }
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

        private List<EHSceneCollection> GetDiscoveredDestinations()
        {
            SpookyDoorway.SceneCollectionsManager manager = EHKickStarter.SceneCollectionsManager;
            if (manager == null || manager.Collections == null)
            {
                return new List<EHSceneCollection>();
            }

            LogKeyedHandleCandidatesOnce(manager);

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

                // Having been in a room once doesn't mean it's currently accessible the
                // normal way: a corridor/room key you don't have (yet, or ever, in a given
                // save) still gates entry, and a handful of rooms (the Dining Room) are only
                // open during specific times regardless of visited state. Fails open for
                // anything we don't have data on, so this can only narrow what room.<handle>
                // already allowed, never expand it. _ignoreAccessChecks (the menu toggle)
                // skips this specific gate on purpose; it never touches the crash-safety
                // checks above/below it.
                if (!_ignoreAccessChecks && !HasPassableChecks(ehCollection.handle))
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

        // DEV-ONLY: logs every unique handle in the game (with its label) via BepInEx's own
        // Logger (cheap, one-shot - not Unity's Debug.Log, so none of the performance
        // concerns elsewhere in this file apply). This exists only to get real handle
        // strings for building an accurate DoorKeys->handle table instead of guessing off
        // scene names seen in unrelated logs - remove once that table is filled in and
        // verified against this output.
        private void LogKeyedHandleCandidatesOnce(SpookyDoorway.SceneCollectionsManager manager)
        {
            if (_loggedKeyedHandleCandidates)
            {
                return;
            }
            _loggedKeyedHandleCandidates = true;

            SortedDictionary<string, string> labelByHandle = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SpookyDoorway.SceneCollection collection in manager.Collections)
            {
                EHSceneCollection ehCollection = collection as EHSceneCollection;
                if (ehCollection == null || string.IsNullOrEmpty(ehCollection.handle))
                {
                    continue;
                }
                labelByHandle[ehCollection.handle] = ehCollection.label ?? "";
            }

            Logger.LogInfo($"[BlakeManorFastTravel] Dumping all {labelByHandle.Count} unique handles:");
            foreach (KeyValuePair<string, string> entry in labelByHandle)
            {
                Logger.LogInfo($"[BlakeManorFastTravel]   handle='{entry.Key}' label='{entry.Value}'");
            }
            Logger.LogInfo("[BlakeManorFastTravel] End of handle dump.");
        }

        // Every DoorScanIntervalSeconds, scans every AC.ActionList currently loaded
        // (regardless of which scene it's in) for ones that also contain an ActionScene_EH
        // - i.e. a door's Interaction. Any AC.ActionCheck-derived action found in the same
        // list (an inventory check for a key door, an ActionEHCheckTime for a time-gated
        // one, etc.) gets registered against that destination handle in
        // _conditionChecksByHandle for HasPassableChecks() to call live later - see the
        // comment on that field for why we don't need to know or hand-verify what kind of
        // check it is. Also logs what it finds to _doorLinksLogPath, which is how we
        // originally identified ActionEHCheckTime as the Dining Room's gate.
        //
        // Doors only exist as live objects in whatever scene they're placed in, so this
        // only ever sees doors in scenes that have actually loaded - it builds up coverage
        // as you walk/fast-travel around, not all at once. Already-scanned ActionLists are
        // skipped on later passes so walking back through an area doesn't redo the work.
        private void ScanForDoorLinksThrottled()
        {
            if (Time.unscaledTime - _lastDoorScanTime < DoorScanIntervalSeconds)
            {
                return;
            }
            _lastDoorScanTime = Time.unscaledTime;

            AC.ActionList[] actionLists = UnityEngine.Object.FindObjectsByType<AC.ActionList>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (AC.ActionList actionList in actionLists)
            {
                int id = actionList.GetInstanceID();
                if (!_scannedActionListIds.Add(id))
                {
                    continue; // already scanned this one
                }

                ActionScene_EH sceneAction = null;
                List<AC.ActionCheck> checks = new List<AC.ActionCheck>();
                foreach (AC.Action action in actionList.actions)
                {
                    if (action is ActionScene_EH s)
                    {
                        sceneAction = s;
                    }
                    else if (action is AC.ActionCheck check)
                    {
                        checks.Add(check);
                    }
                }

                if (sceneAction != null)
                {
                    RegisterDoorLink(actionList, sceneAction, checks);
                }
            }
        }

        private void RegisterDoorLink(AC.ActionList actionList, ActionScene_EH sceneAction, List<AC.ActionCheck> checks)
        {
            string handle = sceneAction.sceneHandle;
            if (checks.Count > 0)
            {
                if (!_conditionChecksByHandle.TryGetValue(handle, out List<AC.ActionCheck> existing))
                {
                    existing = new List<AC.ActionCheck>();
                    _conditionChecksByHandle[handle] = existing;
                }
                existing.AddRange(checks);
            }

            EHSceneCollection current = EHKickStarter.SceneCollectionsManager?.GetCurrentlyOpenCollection() as EHSceneCollection;
            string fromHandle = current?.handle ?? "unknown";
            string checkTypeNames = checks.Count == 0 ? "none" : string.Join(", ", checks.ConvertAll(c => c.GetType().Name));
            string line =
                $"[{DateTime.Now:HH:mm:ss}] destHandle='{handle}' fromRoom='{fromHandle}' " +
                $"actionList='{actionList.name}' checks=[{checkTypeNames}]";

            Logger.LogInfo("[BlakeManorFastTravel] " + line);
            try
            {
                File.AppendAllText(_doorLinksLogPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[BlakeManorFastTravel] Failed to write door_links.log: " + ex.Message);
            }
        }

        private void OnGUI()
        {
            MenuTheme.EnsureBuilt();

            if (_traveling)
            {
                DrawTravelingToast();
            }

            if (!_menuOpen)
            {
                return;
            }

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
            // Solid gold fill (LockToggleOn) means bypass is on; the dark wine fill every
            // destination button uses means it's off, reading as blending into the panel
            // rather than a fully transparent gap.
            GUIStyle toggleStyle = _ignoreAccessChecks ? MenuTheme.LockToggleOn : MenuTheme.DestinationButton;
            if (GUILayout.Button(_ignoreAccessChecks ? "Bypass Locks: Enabled" : "Bypass Locks: Disabled", toggleStyle, GUILayout.Width(190f * scale), GUILayout.Height(30f * scale)))
            {
                _ignoreAccessChecks = !_ignoreAccessChecks;
                _destinations = GetDiscoveredDestinations();
            }
            GUILayout.Space(14f * scale);
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

        // Drag-to-resize from the bottom-right corner, growing/shrinking symmetrically
        // about the window's center so it never drifts off-center while resizing. The
        // handle still tracks the mouse 1:1: since both edges move, width/height change by
        // 2x the mouse delta, and x/y shift by half of whatever change actually applied
        // (post-clamp) so the center stays put even right at the min/max size.
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

        // A small always-on-top toast, independent of the main menu (which is already
        // closed by the time this matters) - just enough to say "still working" during a
        // load that might take a while, instead of leaving the screen looking frozen.
        private void DrawTravelingToast()
        {
            const float w = 240f;
            const float h = 46f;
            Rect toastRect = new Rect((Screen.width - w) / 2f, 28f, w, h);
            GUI.Box(toastRect, GUIContent.none, MenuTheme.Panel);
            GUI.Label(toastRect, "Traveling" + TravelingDots(), MenuTheme.Subtitle);
        }

        private static string TravelingDots()
        {
            int count = 1 + Mathf.FloorToInt(Time.unscaledTime * 2f) % 3;
            return new string('.', count);
        }

        private void TravelTo(EHSceneCollection destination)
        {
            Logger.LogInfo(
                $"[BlakeManorFastTravel] TravelTo requested: dest='{destination.Path}' handle='{destination.handle}' " +
                $"IsLoading={KickStarter.sceneChanger?.IsLoading()} " +
                $"activeScene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'");
            try
            {
                // EHSceneChanger.ChangeScene() opens with:
                //   if (isLoading || ...) { return; }
                // - if a scene change is already in progress, calling it again is a silent
                // no-op: the actual load is dropped, but everything ChangeScene does *before*
                // that check (hiding menus, exiting analysis mode, etc.) still runs. That's a
                // real failure mode we hit: fast travel triggered while a previous one hadn't
                // finished loading leaves the screen stuck on the old (already-partway-torn-
                // down) scene while whatever ambient audio/cues fired regardless keep playing -
                // audio, but no picture. Checking IsLoading() here (the same public accessor
                // TryOpenMenu() uses to keep the menu from reopening mid-load) turns that into
                // a clean, visible refusal instead of a silent half-transition.
                if (KickStarter.sceneChanger != null && KickStarter.sceneChanger.IsLoading())
                {
                    Logger.LogWarning($"[BlakeManorFastTravel] Refused travel to '{destination.Path}' - a scene change was already in progress.");
                    _statusMessage = "Still loading the last destination - try again in a moment.";
                    return;
                }

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

                // Same re-check pattern as above: GetDiscoveredDestinations() already
                // filters on this, but a key/time condition can flip between menu-build and
                // click (spend the key, or the clock ticks past meal time), so verify again
                // right before actually loading. Also skipped by _ignoreAccessChecks.
                if (!_ignoreAccessChecks && !HasPassableChecks(destination.handle))
                {
                    _statusMessage = "Can't fast travel there right now - it's currently locked.";
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

                Logger.LogInfo(
                    $"[BlakeManorFastTravel] ChangeScene called for '{destination.Path}', now " +
                    $"IsLoading={KickStarter.sceneChanger?.IsLoading()}");

                _traveling = true;
                _travelDestinationPath = destination.Path;
                _travelStartTime = Time.unscaledTime;
            }
            catch (Exception ex)
            {
                _statusMessage = "Fast travel failed: " + ex.Message;
                Debug.LogError("[BlakeManorFastTravel] Fast travel failed: " + ex);
            }
        }
    }

    // MapArea.GetTimeTableDataForLocationAndTime() runs once per journal-map area on every
    // scene change (fast-traveled or not) and, for every active-bucket timetable entry,
    // unconditionally does 1-2 Debug.Log/Debug.LogWarning calls - including one warning
    // per entry for every map area that structurally has no area data (e.g. closets), which
    // can add up to a lot of log calls in one frame. Debug.Log in Unity captures a stack
    // trace per call, which is slow enough that this logging alone can make an otherwise-
    // ordinary scene load look like a freeze - most noticeably on fast travel's direct
    // cross-region jumps, since door-by-door movement seems to warm/avoid this path.
    //
    // This only silences logging for the duration of that one method call (saved/restored
    // around it) - it doesn't change what the method computes or returns.
    [HarmonyPatch(typeof(MapArea), "GetTimeTableDataForLocationAndTime")]
    internal static class MapArea_GetTimeTableDataForLocationAndTime_SilenceLogSpam
    {
        private static bool _wasLogEnabled;

        private static void Prefix()
        {
            _wasLogEnabled = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false;
        }

        private static void Postfix()
        {
            Debug.unityLogger.logEnabled = _wasLogEnabled;
        }
    }

    // SceneCollectionsManager.GetCurrentlyOpenCollection() does the same expensive thing on
    // a miss: it string-concatenates every registered scene collection's runtime scene
    // names (~140 of them) into an error, which fires every time it's called while the
    // active scene doesn't match any collection - true for the entire "Loading" screen.
    // We throttle our own polling of this method (see UpdateTravelingState()), but this
    // patches the method itself so it's silenced no matter who calls it, including the
    // base game. Same as above: only logging is suppressed, the lookup/return value is
    // untouched.
    [HarmonyPatch(typeof(SpookyDoorway.SceneCollectionsManager), "GetCurrentlyOpenCollection")]
    internal static class SceneCollectionsManager_GetCurrentlyOpenCollection_SilenceLogSpam
    {
        private static bool _wasLogEnabled;

        private static void Prefix()
        {
            _wasLogEnabled = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false;
        }

        private static void Postfix()
        {
            Debug.unityLogger.logEnabled = _wasLogEnabled;
        }
    }

    // The actual root cause of the black-screen-with-audio and player-frozen hangs we've
    // hit fast-traveling into some rooms: EHSceneSettings.GetPlayerStart() falls back to
    //     Debug.Log("Can't find any starter, return null");
    //     return null;
    // whenever none of a room's PlayerStart markers match wherever the player is arriving
    // from - one of its match conditions is the exact SceneCollectionInfo.playerStart name,
    // which our own TravelTo() always passes as null, and another is "previous scene", which
    // fast travel can make anything (a real door only ever connects scenes actually wired
    // together at design time, so this path is never exercised that way). The caller,
    // AC.SceneSettings.OnStart(), then does playerStart.transform.position immediately after
    // with NO null check:
    //     LoadedPlayerStart = true;
    //     LastLoadedPlayerStart = KickStarter.sceneChanger.GetStartPosition(playerStart.transform.position);
    // - an unhandled NullReferenceException that silently aborts the rest of OnStart(),
    // including (we believe) whatever re-enables player movement/animation, while whatever
    // ran earlier in the method (ambience audio, etc.) already fired. Scene state itself
    // still ends up fully loaded/current, which is why our own "did the travel complete"
    // check (GetCurrentlyOpenCollection() matching the destination) sees success even when
    // the player is left stuck.
    //
    // Fix: fall back to a PlayerStart present in the newly-loaded scene rather than
    // returning null - the player may spawn at a not-quite-right spot instead of exactly
    // where a door would have placed them, but that beats a broken load every time.
    //
    // Scoped to __instance's own scene, not just "any PlayerStart currently loaded
    // anywhere": this game keeps several scene layers loaded concurrently (and briefly
    // overlapping during a transition), so an unscoped search can hand back a PlayerStart
    // belonging to a *different* scene entirely - not null, so no crash, but physically
    // nonsensical (e.g. inside unloaded/foreign geometry). That still passes our own "did
    // the travel complete" check (the destination scene collection genuinely is current)
    // while leaving the player stuck unable to move - same visible symptom as the null
    // crash this patch was written for, just with the crash itself avoided.
    [HarmonyPatch(typeof(EHSceneSettings), "GetPlayerStart")]
    internal static class EHSceneSettings_GetPlayerStart_FallbackWhenUnresolved
    {
        private static readonly BepInEx.Logging.ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource(FastTravelPlugin.PluginName);

        private static void Postfix(EHSceneSettings __instance, ref PlayerStart __result)
        {
            if (__result != null)
            {
                return;
            }

            UnityEngine.SceneManagement.Scene ownScene = __instance.gameObject.scene;
            PlayerStart[] allPlayerStarts = UnityEngine.Object.FindObjectsByType<PlayerStart>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (PlayerStart candidate in allPlayerStarts)
            {
                if (candidate.gameObject.scene == ownScene)
                {
                    __result = candidate;
                    return;
                }
            }

            // No scene-scoped match either - true last resort, logged since this spawn
            // location is unverified and could still be wrong.
            if (allPlayerStarts.Length > 0)
            {
                Log.LogWarning(
                    $"[BlakeManorFastTravel] GetPlayerStart(): no PlayerStart found in scene '{ownScene.name}' - " +
                    "falling back to one from a different scene, spawn position may be wrong.");
                __result = allPlayerStarts[0];
            }
        }
    }
}
