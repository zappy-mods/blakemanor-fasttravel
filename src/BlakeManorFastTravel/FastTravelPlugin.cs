using System;
using System.Collections.Generic;
using System.IO;
using AC;
using BepInEx;
using BepInEx.Configuration;
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
    // _doorPathsByHandle - for anything that's actually key- or time-gated.
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
        public const string PluginVersion = "0.0.1";

        private const float DefaultWidth = 440f;
        private const float DefaultHeight = 520f;
        private const float MinWidth = 320f;
        private const float MinHeight = 300f;
        private const float MaxWidth = 900f;
        private const float MaxHeight = 820f;
        private const float ResizeHandleSize = 18f;
        // The false positive this timeout exists to filter out (timeScale reading non-1 for
        // a moment right as the destination becomes current, before the game's own normal
        // completion sequence gets to resetting it) resolves within about a frame of real
        // time - nowhere near multiple seconds. 6s/6 poll cycles gives that several times
        // over as margin while still cutting the wait for a genuine hang down drastically
        // from the original 45s pick (which was just a generic large safety margin, not
        // tuned to an observed value).
        private const float TravelTimeoutSeconds = 6f;
        private const float TravelPollIntervalSeconds = 1f;

        private bool _menuOpen;
        private Vector2 _scrollPos;
        private GameState _previousGameState = GameState.Normal;
        private List<EHSceneCollection> _destinations = new List<EHSceneCollection>();
        private string _statusMessage = "";
        private Harmony _harmony;

        // User-facing config (BepInEx/config/zappymods.blakemanor.fasttravel.cfg).
        private ConfigEntry<Key> _hotkeyConfig;
        private ConfigEntry<bool> _diagnosticsConfig;

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
        private bool _travelDestinationConfirmed;

        // Diagnostic-only, gated behind _diagnosticsConfig (see LogKeyedHandleCandidatesOnce):
        // true once we've logged every registered handle - useful for troubleshooting, not
        // needed for the plugin to function.
        private bool _loggedKeyedHandleCandidates;

        // Core mechanism (see ScanForDoorLinksThrottled), always runs regardless of the
        // diagnostics toggle: scans every AC.ActionList currently loaded for ones that also
        // change scenes (ActionScene_EH), and caches every AC.ActionCheck-derived action
        // found alongside it - an inventory check for a key door, an ActionEHCheckTime for
        // something like the Dining Room's "closed outside of meal times" gate, or any other
        // condition - keyed by destination handle. No hand-built key/handle table needed:
        // whatever gets captured is authoritative by construction, since HasPassableChecks()
        // calls the game's own CheckCondition() live rather than reimplementing what it
        // means. Doors only exist as live objects in whatever scene they're placed in, so
        // this only ever covers doors in scenes that have actually loaded - it fills in as
        // you walk/fast-travel around, not all at once; anything not yet scanned just falls
        // back to the plain room.<handle> >= 2 check, same as before this existed. Only its
        // logging (RegisterDoorLink) is gated behind _diagnosticsConfig.
        // Now primarily a safety net, not the main trigger - see OnSceneLoadedScanForDoors().
        // Kept fairly generous since a scan is only "wasted" work once nothing new is loaded to
        // find, which is most of the time once the scene-loaded event has already covered it.
        private const float DoorScanIntervalSeconds = 30f;
        private float _lastDoorScanTime;
        // Grace delay after SceneManager.sceneLoaded before actually scanning: confirmed via a
        // save load into the Atrium that scanning on the very next frame can run before AC has
        // finished restoring Hotspot/interaction state from the save file, finding nothing even
        // though the objects exist a moment later (fixed itself once the player left and
        // re-entered, a plain scene transition rather than a save load). Not a fixed number of
        // frames since that's timestep-dependent; a short wall-clock delay comfortably covers it
        // without meaningfully hurting discovery latency.
        private const float SceneLoadScanDelaySeconds = 1f;
        private float? _pendingSceneLoadScanTime;
        private readonly HashSet<int> _scannedActionListIds = new HashSet<int>();
        private string _doorLinksLogPath;

        // Each entry is one door's full action sequence plus which action in it is the actual
        // scene change - not a pre-filtered list of "genuine" checks. An earlier version tried
        // to classify individual ActionCheck instances as "real gates" ahead of time via
        // structural reachability tracing, but that can't handle multiple checks working
        // together (e.g. "check5==false AND (check6==false OR check7==false)" - the Bar's real
        // gate): each check considered alone is structurally escapable via some combination of
        // the others, so all three got wrongly filtered out despite jointly blocking the door
        // every time. The only way to get this right in general is to not pre-judge individual
        // checks at all - walk the actual sequence live, using each check's real, current
        // CheckCondition() result to pick the one branch AC's own runtime would actually take,
        // exactly like a real click does. See HasPassableChecks() and IsDoorReachableLive().
        private readonly Dictionary<string, List<(List<AC.Action> Actions, AC.Action Target)>> _doorPathsByHandle =
            new Dictionary<string, List<(List<AC.Action> Actions, AC.Action Target)>>();

        // A Hotspot-level scan (AC.Button.isDisabled, Hotspot.IsOn(), Hotspot.
        // provideUseInteraction, and re-checking button.interaction hasn't been swapped to a
        // different ActionList) was built and tested while chasing the Bar's lock, on the
        // theory that not every gate lives inside the door's own ActionList. All four read as
        // "enabled" for the Bar the entire time it was confirmed closed in-game - its real gate
        // turned out to be a 3-check AND/OR combination *inside* "Bar: Open" itself, which
        // IsDoorReachableLive() now handles correctly and completely on its own. Removed rather
        // than kept around unproven: it never once correctly caught a real lock, and it caused
        // a confirmed regression (the Atrium, which is never locked, showing as blocked after a
        // save reload) - reloading a save destroys and recreates Hotspot/Interaction
        // components, and these dictionaries never got cleared, so a stale, Unity-"destroyed"
        // reference from before the reload permanently poisoned the handle even after a fresh,
        // valid scan re-registered it right alongside the stale one.

        // Fails open (returns true) for a handle with no captured checks - nothing scanned
        // there yet, or it genuinely has no extra condition - so this can only ever narrow
        // what room.<handle> already allowed, never expand it or be the sole reason a
        // legitimately-visited room becomes unreachable.
        private bool HasPassableChecks(string handle)
        {
            if (!_doorPathsByHandle.TryGetValue(handle, out List<(List<AC.Action> Actions, AC.Action Target)> paths))
            {
                return true;
            }

            // OR across paths, not AND: a handle can be reachable via more than one physical
            // door (multiple Hotspots leading to the same room), each with its own independent
            // gating - the room should count as passable if any one of the doors we've actually
            // scanned currently leads there, not only if every door we happen to know about
            // does.
            if (_diagnosticsConfig.Value)
            {
                Logger.LogInfo($"[BlakeManorFastTravel] HasPassableChecks('{handle}'): {paths.Count} registered path(s), sizes=[{string.Join(",", paths.ConvertAll(p => p.Actions.Count))}]");
            }
            foreach ((List<AC.Action> actions, AC.Action target) in paths)
            {
                if (IsDoorReachableLive(actions, 0, target, new HashSet<int>(), handle))
                {
                    return true;
                }
            }
            if (_diagnosticsConfig.Value)
            {
                Logger.LogInfo($"[BlakeManorFastTravel] HasPassableChecks('{handle}'): no known path currently reaches the door -> FAIL");
            }
            return false;
        }

        // Walks one door's action sequence starting from index 0 (the same entry point AC uses
        // for a real click - see ActionList.Interact()/BeginActionList(0, ...)), using each
        // ActionCheck's real, current CheckCondition() to pick the one branch that would
        // actually be taken, until it either reaches target (door reachable right now) or runs
        // out of path (Stop/RunCutscene without ever reaching it - genuinely blocked). visited
        // guards against infinite loops on any backward jump.
        private bool IsDoorReachableLive(List<AC.Action> actions, int index, AC.Action target, HashSet<int> visited, string handleForLogging)
        {
            if (index < 0 || index >= actions.Count || !visited.Add(index))
            {
                return false;
            }

            AC.Action current = actions[index];
            if (current == target)
            {
                return true;
            }

            if (current is AC.ActionCheck check)
            {
                bool passed = check.CheckCondition();
                if (_diagnosticsConfig.Value)
                {
                    Logger.LogInfo(
                        $"[BlakeManorFastTravel] HasPassableChecks('{handleForLogging}'): " +
                        $"[{index}] {check.GetType().Name} -> {(passed ? "true" : "fail")}");
                }
                return passed
                    ? ResolveNextIndex(actions, check.resultActionTrue, check.skipActionTrue, check.skipActionTrueActual, index, out int trueNext) &&
                        IsDoorReachableLive(actions, trueNext, target, visited, handleForLogging)
                    : ResolveNextIndex(actions, check.resultActionFail, check.skipActionFail, check.skipActionFailActual, index, out int failNext) &&
                        IsDoorReachableLive(actions, failNext, target, visited, handleForLogging);
            }

            return ResolveNextIndex(actions, current.endAction, current.skipAction, current.skipActionActual, index, out int next) &&
                IsDoorReachableLive(actions, next, target, visited, handleForLogging);
        }

        private void Awake()
        {
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            _hotkeyConfig = Config.Bind(
                "General", "Hotkey", Key.F9,
                "The key that opens/closes the fast travel menu.");
            _diagnosticsConfig = Config.Bind(
                "Diagnostics", "EnableDiagnosticLogging", false,
                "Logs extra troubleshooting info (door discovery scans, a periodic game-state " +
                "heartbeat, and a door_links.log file next to this plugin) to help diagnose bug " +
                "reports. Off by default - only turn this on if asked to when reporting an issue.");

            if (_diagnosticsConfig.Value)
            {
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

            // Investigation-only (Bar mechanism): every check we can read off Hotspot/Button
            // state directly (isDisabled, IsOn(), provideUseInteraction, interaction-target
            // swap) came back "enabled" for a door confirmed closed in-game, so read AC's own
            // ground truth instead of inferring it - AC.EventManager.OnHotspotInteract fires
            // with the actual resolved Button (or null) right after PlayerInteraction.
            // ClickButton() picks it, the same event InteractionUIPanel subscribes to for its
            // own UI updates. This tells us directly whether a click is even reaching Use with
            // the door's button at all, without needing a live debugger.
            AC.EventManager.OnHotspotInteract += LogHotspotInteractDiagnostic;

            // Door discovery used to run purely on a 2s poll, forever, for the entire session -
            // calling FindObjectsByType<AC.ActionList>() (a full walk of every loaded object)
            // even during the overwhelming majority of play spent just standing in one room
            // with nothing new to find. Scanning right when a scene actually finishes loading
            // is both cheaper (only runs when there's realistically something new) and more
            // responsive (no up-to-2s lag before a freshly-loaded room's doors are known).
            // SceneManager.sceneLoaded is the standard Unity event, unrelated to AC/EHKickStarter
            // specifically, so it's reliable REGARDLESS of how a scene got loaded (walking, fast
            // travel, a cutscene). The Update() poll stays on as a much-less-frequent safety net
            // (see DoorScanIntervalSeconds) in case anything ever loads without that event firing.
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoadedScanForDoors;
        }

        private void OnSceneLoadedScanForDoors(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            _pendingSceneLoadScanTime = Time.unscaledTime + SceneLoadScanDelaySeconds;
        }

        private void LogHotspotInteractDiagnostic(AC.Hotspot hotspot, AC.Button button)
        {
            if (!_diagnosticsConfig.Value)
            {
                return;
            }
            string interactionName = button?.interaction != null ? button.interaction.name
                : button?.assetFile != null ? $"(asset: {button.assetFile.name})"
                : button != null ? "(button set, no interaction/asset)"
                : "(null - nothing ran)";
            Logger.LogInfo(
                $"[BlakeManorFastTravel] OnHotspotInteract: hotspot='{hotspot?.name}' -> {interactionName}");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            AC.EventManager.OnHotspotInteract -= LogHotspotInteractDiagnostic;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoadedScanForDoors;
        }

        private void Update()
        {
            if (_traveling)
            {
                UpdateTravelingState();
            }

            // Always runs, regardless of the diagnostics toggle: this is what populates
            // _doorPathsByHandle, which the key/time-gating feature depends on - only
            // its *logging* is diagnostics-gated (see RegisterDoorLink).
            ScanForDoorLinksThrottled();
            if (_diagnosticsConfig.Value)
            {
                LogHeartbeatStateThrottled();
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            bool shiftHeld = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;

            if (keyboard[_hotkeyConfig.Value].wasPressedThisFrame)
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
                $"playerNull={KickStarter.player == null} timeScale={Time.timeScale}");
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
                // Getting here means one of two genuinely-stuck cases, both worth a loud log
                // line since nothing else would otherwise record it: either the destination
                // never became the open collection at all within 45s (the load itself hung),
                // or it did become current but Time.timeScale never came back to 1 in all that
                // time (the on-enter-cutscene/timescale race below, but given a full 45s to
                // resolve on its own first rather than judged off a single reading). Waiting
                // out the whole timeout before escalating is deliberate: a single "timeScale
                // != 1" reading taken the instant the destination becomes current turned out
                // to be too eager a trigger (see below) and doesn't distinguish a genuine hang
                // from a normal load that just hasn't finished its own cleanup yet.
                Logger.LogWarning(
                    $"[BlakeManorFastTravel] Travel to '{_travelDestinationPath}' did not complete within " +
                    $"{TravelTimeoutSeconds}s (destinationConfirmed={_travelDestinationConfirmed}, " +
                    $"timeScale={Time.timeScale}, active scene: " +
                    $"'{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}', " +
                    $"IsLoading={KickStarter.sceneChanger?.IsLoading()}). Escalating to full recovery.");
                RecoverFromPossibleHang();
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
            if (current == null || current.Path != _travelDestinationPath)
            {
                return;
            }

            if (!_travelDestinationConfirmed)
            {
                // gameState is what actually gates player movement/animation throughout AC -
                // logging it here lets us tell "scene loaded fine but player control never
                // came back" (gameState stuck off Normal) apart from "scene itself never
                // finished" (the timeout branch above), which look identical in-game but
                // need different fixes. Logged once, on the poll where the destination first
                // matches, rather than every poll thereafter while we wait on timeScale below.
                Logger.LogInfo(
                    $"[BlakeManorFastTravel] Travel to '{_travelDestinationPath}' completed after " +
                    $"{Time.unscaledTime - _travelStartTime:0.0}s. gameState={KickStarter.stateHandler?.gameState} " +
                    $"playerNull={KickStarter.player == null} timeScale={Time.timeScale}");
                _travelDestinationConfirmed = true;
            }

            // The actual root cause behind the stuck-Cutscene/hung-ActionList hangs, best
            // evidence to date: every one we've caught mid-freeze via LogActiveActionLists
            // shows the stuck action is an early step in a room's on-enter "OnStart"
            // sequence (ActionFade, ActionFMODTriggerParameterChange, etc.) whose Run()
            // deliberately defers to a later frame (see ActionFMODTriggerParameterChange's
            // skippedFrame gate) - and every hang's Player.log shows "Setting timescale to
            // 1" only ever appearing as part of forced-shutdown cleanup, never mid-hang,
            // meaning Time.timeScale stayed at its paused-for-loading value (0) the whole
            // time. If that on-enter cutscene's frame-deferral is scaled-time-based, a race
            // between "cutscene starts" and "timescale resets to 1 after loading" would
            // freeze it on frame one forever if it loses that race - explaining both the
            // specific stuck actions we've seen and why this is intermittent (a race, not a
            // deterministic bug) rather than affecting every room every time.
            //
            // Still, timeScale often reads non-1 for a moment right as the destination
            // becomes current simply because the game's own normal completion sequence
            // hasn't gotten to resetting it yet - not because anything is actually stuck.
            // Escalating to the full reset (KillAllLists/StopConversation/subsystem toggles)
            // off that single reading turned out not to be harmless: it was resetting AC's
            // input/interaction systems and stopping any brand-new conversation right as one
            // started (e.g. opening a book moments after a totally healthy arrival), causing
            // exactly the kind of input jank/half-working clicks that motivated this rework.
            // So: keep polling (stay "traveling", no escalation) for as long as timeScale
            // hasn't self-corrected, and only escalate once we hit the timeout branch above -
            // giving a normal-but-slightly-delayed reset the full 45s to resolve on its own
            // before we conclude it's a genuine hang.
            if (Time.timeScale == 1f)
            {
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

            RecoverFromPossibleHang();

            // Paused is what actually triggers AC's own menu-mode behavior (frees the mouse
            // cursor, suspends first-person camera control while a UI is up) - the same
            // state TryOpenMenu() uses normally. _previousGameState stays Normal so
            // CloseMenu() resolves to a working state on exit regardless of what was stuck.
            _previousGameState = GameState.Normal;
            if (KickStarter.stateHandler != null)
            {
                KickStarter.stateHandler.gameState = GameState.Paused;
            }
            _menuOpen = true;

            _windowRect.x = (Screen.width - _windowRect.width) / 2f;
            _windowRect.y = (Screen.height - _windowRect.height) / 2f;
        }

        // The full-strength reset: KillAllLists(), StopConversation(), re-enabling AC's
        // subsystem toggles, and forcing FadeIn/timeScale. Always called unconditionally
        // from ForceOpenMenu() (Shift+F9) - the player invoking that is itself the signal
        // something's wrong. From the normal travel-completion and menu-close paths it's
        // only called when Time.timeScale != 1f, which we've confirmed is a reliable tell
        // for the underlying stuck-cutscene/dialogue race - NOT unconditionally: an earlier
        // version ran this on every clean travel/close on the theory that it was harmless
        // when unnecessary, but that turned out to be wrong specifically for
        // StopConversation() and the subsystem toggles - resetting AC's input/interaction
        // systems and killing any active conversation right as a brand-new, perfectly
        // healthy one started (e.g. opening a book moments after a clean fast travel)
        // produced jerky/half-working clicks. Gate on an actual detected problem instead.
        //
        // - KillAllLists() (AC.ActionListManager) force-stops any still-running AC
        //   ActionList/Cutscene - covers a stuck on-enter cutscene whose face/look action
        //   would otherwise keep re-applying itself every frame regardless of gameState.
        // - DialogueManager.StopConversation() covers PixelCrushers Dialogue System
        //   conversations, a separate subsystem KillAllLists() doesn't touch - the game's
        //   own EHSceneChanger.ChangeScene() already calls this defensively before every
        //   scene change, so this mirrors an established pattern, not a guess.
        // - The AC.StateHandler subsystem toggles and MainCamera.FadeIn(0f) cover cases
        //   where killing the ActionList driving them doesn't undo the state it already
        //   left behind (camera-look disabled, screen mid-fade).
        // - Forcing Time.timeScale back to 1 covers the race described on
        //   UpdateTravelingState()'s completion branch.
        private void RecoverFromPossibleHang()
        {
            // AC.StateHandler tracks camera/movement/cursor/input/interaction/menu/trigger/
            // player as independent enable flags, entirely separate from gameState - an
            // interrupted cutscene/dialogue that disabled one of these (e.g. SetCameraSystem
            // (false), to lock out player look during a scripted beat) and never got to turn
            // it back on would leave that specific system broken regardless of gameState
            // being fine, which fits "camera detached/stuck" better than anything gameState-
            // level explains. Only two of these expose a public getter (the rest are
            // write-only from here), so this can't be fully diagnostic, but re-enabling all
            // of them is safe and idempotent when nothing was actually stuck - same
            // reasoning as everything else in this method.
            if (KickStarter.stateHandler != null)
            {
                Logger.LogWarning(
                    $"[BlakeManorFastTravel] Before re-enabling AC subsystems: MovementIsOff={KickStarter.stateHandler.MovementIsOff} " +
                    $"CursorIsOff={KickStarter.stateHandler.GetCursorIsOff()}");
                try
                {
                    KickStarter.stateHandler.SetACState(true);
                    KickStarter.stateHandler.SetCameraSystem(true);
                    KickStarter.stateHandler.SetMovementSystem(true);
                    KickStarter.stateHandler.SetCursorSystem(true);
                    KickStarter.stateHandler.SetInputSystem(true);
                    KickStarter.stateHandler.SetInteractionSystem(true);
                    KickStarter.stateHandler.SetMenuSystem(true);
                    KickStarter.stateHandler.SetTriggerSystem(true);
                    KickStarter.stateHandler.SetPlayerSystem(true);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("[BlakeManorFastTravel] Re-enabling AC subsystems failed: " + ex.Message);
                }
            }

            // gameState is what actually gates player movement/input/animation throughout AC
            // (see TryOpenMenu()'s check on it) - entirely separate from the StateHandler
            // subsystem flags just reset above. A stuck on-enter cutscene/ActionList leaves
            // this at Cutscene, and killing the list below doesn't undo that: nothing else
            // sets it back. Confirmed missing in practice - the automatic (non-Shift+F9) path
            // used to leave gameState stuck at Cutscene even after everything else here ran,
            // only actually clearing once the player opened and closed the emergency menu,
            // which happens to reset it as a side effect (see CloseMenu()). Both callers of
            // this method run before anything of ours has put up a menu of its own, so there's
            // no legitimate Paused-for-our-own-UI state here to preserve - forcing back to
            // Normal is safe and, per everything else in this method, idempotent when nothing
            // was actually stuck.
            if (KickStarter.stateHandler != null && KickStarter.stateHandler.gameState != GameState.Normal)
            {
                Logger.LogWarning($"[BlakeManorFastTravel] gameState was {KickStarter.stateHandler.gameState} - forcing back to Normal.");
                KickStarter.stateHandler.gameState = GameState.Normal;
            }

            LogActiveActionLists();
            SkipStuckActionLists();

            try
            {
                if (PixelCrushers.DialogueSystem.DialogueManager.isConversationActive)
                {
                    Logger.LogWarning("[BlakeManorFastTravel] Stopping an active conversation as part of hang recovery.");
                    PixelCrushers.DialogueSystem.DialogueManager.StopConversation();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[BlakeManorFastTravel] StopConversation() failed: " + ex.Message);
            }

            if (Time.timeScale != 1f)
            {
                Logger.LogWarning($"[BlakeManorFastTravel] timeScale was {Time.timeScale} - forcing back to 1.");
                Time.timeScale = 1f;
            }

            // Covers the black-screen symptom specifically: AC.MainCamera.FadeIn/FadeOut
            // manage a persistent alpha/fadeTimer directly on the camera component, entirely
            // independent of whatever ActionList (e.g. ActionFade) started the fade -
            // killing that list above stops the *logic* driving the fade, but doesn't reset
            // the fade's own visual state. If it was killed mid-fade-to-black, the screen
            // would otherwise stay stuck at that alpha forever with nothing left to bring it
            // back. FadeIn(0f) forces it fully visible instantly - the exact same defensive
            // call the base game's own EHSceneChanger.ChangeScene() uses when it detects a
            // blocked traversal, not a guess.
            try
            {
                KickStarter.mainCamera?.FadeIn(0f);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[BlakeManorFastTravel] FadeIn(0f) failed: " + ex.Message);
            }
        }

        // Logs every currently-active ActionList/ActionListAsset and, for each one, every
        // action it contains with its type name - and *which specific action* has
        // AC.Action.isRunning set, since that's the exact one still mid-execution when we
        // hit this. Called right before KillAllLists() so this is a snapshot of what we're
        // about to force-stop - the isRunning-marked action is the actual culprit, not just
        // "some cutscene somewhere".
        private void LogActiveActionLists()
        {
            LogActiveListsFrom("scene", KickStarter.actionListManager?.activeLists);
            LogActiveListsFrom("asset", KickStarter.actionListAssetManager?.activeLists);
        }

        // Replaces the AC.ActionListManager.KillAll() this used to call. KillAll() doesn't
        // just unstick the one frozen action - it drops every remaining step in that same
        // list too, including legitimate later ones (var/inventory setup, chained
        // ActionRunActionList calls, etc.) that would have run fine once the stuck action got
        // past. Confirmed root cause of a real regression: killing a room's OnStart list
        // partway through was truncating setup that later interactions (e.g. examining a book)
        // depended on, breaking dialogue/interaction in that room even though the visible
        // stuck-fade/frozen-camera symptom itself was gone.
        //
        // ActionList.Skip(startIndex) is AC's own built-in "skip cutscene" mechanic - the
        // same one the game's own skip-cutscene button uses - and doesn't have that problem:
        // it re-runs the list from its original start index calling each action's own Skip()
        // override (e.g. ActionFade.Skip() jumps straight to the fade's end state) all the
        // way through to the list's natural completion, rather than truncating it. Actions
        // without a Skip() override fall back to Action.Skip() calling Run() - re-running an
        // already-completed step like a var-set is idempotent by AC's own convention, since
        // this is the exact path an official cutscene skip takes regardless of where the
        // player currently is in the sequence when they trigger it.
        //
        // ActiveList itself also exposes a Skip(), but it's gated on an internal skip-queue
        // flag (inSkipQueue) that a stuck list was never enqueued into, so calling it directly
        // would silently no-op - going straight to the underlying ActionList.Skip() avoids
        // that gate. Asset-based ActionLists aren't handled here: every hang caught so far has
        // logged "No active asset ActionLists", so there's no observed case to fix, and
        // replicating ActiveList.Skip()'s asset-list path (DestroyAssetList +
        // AdvGame.SkipActionListAsset) blind isn't worth the risk without one to verify against.
        private void SkipStuckActionLists()
        {
            List<AC.ActiveList> lists = KickStarter.actionListManager?.activeLists;
            if (lists == null)
            {
                return;
            }

            foreach (AC.ActiveList activeList in lists)
            {
                if (activeList?.actionList == null)
                {
                    continue;
                }

                try
                {
                    Logger.LogWarning(
                        $"[BlakeManorFastTravel] Skipping stuck scene ActionList '{activeList.actionList.name}' " +
                        $"(from index {activeList.startIndex}) instead of killing it, so later steps still run.");
                    activeList.actionList.Skip(activeList.startIndex);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[BlakeManorFastTravel] Skip() on '{activeList.actionList.name}' failed: {ex.Message}");
                }
            }

            List<AC.ActiveList> assetLists = KickStarter.actionListAssetManager?.activeLists;
            if (assetLists != null && assetLists.Count > 0)
            {
                Logger.LogWarning(
                    $"[BlakeManorFastTravel] {assetLists.Count} active asset ActionList(s) present during recovery " +
                    "- not auto-skipped (never observed stuck in practice); flag for follow-up if this shows up.");
            }
        }

        private void LogActiveListsFrom(string kind, List<AC.ActiveList> lists)
        {
            if (lists == null || lists.Count == 0)
            {
                Logger.LogWarning($"[BlakeManorFastTravel] No active {kind} ActionLists.");
                return;
            }

            foreach (AC.ActiveList activeList in lists)
            {
                string listName = activeList.actionList != null ? activeList.actionList.name
                    : activeList.actionListAsset != null ? activeList.actionListAsset.name
                    : "unknown";
                List<AC.Action> actions = activeList.actionList != null ? activeList.actionList.actions
                    : activeList.actionListAsset?.actions;

                if (actions == null)
                {
                    Logger.LogWarning($"[BlakeManorFastTravel] Active {kind} list '{listName}': (no actions available)");
                    continue;
                }

                List<string> actionDescriptions = new List<string>();
                foreach (AC.Action action in actions)
                {
                    string marker = action != null && action.isRunning ? "*RUNNING*" : "";
                    actionDescriptions.Add((action?.GetType().Name ?? "null") + marker);
                }
                Logger.LogWarning($"[BlakeManorFastTravel] Active {kind} list '{listName}': [{string.Join(", ", actionDescriptions)}]");
            }
        }

        private void CloseMenu()
        {
            _menuOpen = false;
            if (KickStarter.stateHandler != null)
            {
                KickStarter.stateHandler.gameState = _previousGameState;
            }

            // Reported symptom: the camera detaching/spinning after a plain F9 open+close,
            // with no travel involved at all. The full reset (KillAllLists/StopConversation/
            // subsystem toggles) used to run here too whenever Time.timeScale looked stuck,
            // but a single close is a one-shot event with no chance to wait and see whether
            // that's a genuine hang or just momentary load-completion lag (unlike
            // UpdateTravelingState(), which now waits out the full timeout before
            // escalating) - and firing the full reset eagerly is exactly what caused
            // brand-new conversations right after a clean close/travel to lose auto-advance.
            // So this only does the cheap, side-effect-free part: nudge timeScale itself
            // back to 1 if it's stuck. If something is actually wrong beyond that, Shift+F9
            // (ForceOpenMenu(), a deliberate user action) still runs the full recovery.
            if (Time.timeScale != 1f)
            {
                Logger.LogWarning($"[BlakeManorFastTravel] timeScale was {Time.timeScale} on menu close - forcing back to 1.");
                Time.timeScale = 1f;
            }
        }

        private List<EHSceneCollection> GetDiscoveredDestinations()
        {
            SpookyDoorway.SceneCollectionsManager manager = EHKickStarter.SceneCollectionsManager;
            if (manager == null || manager.Collections == null)
            {
                return new List<EHSceneCollection>();
            }

            if (_diagnosticsConfig.Value)
            {
                LogKeyedHandleCandidatesOnce(manager);
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
                    if (_diagnosticsConfig.Value)
                    {
                        Logger.LogInfo(
                            $"[BlakeManorFastTravel] Excluding '{ehCollection.handle}': room.{ehCollection.handle} = " +
                            $"{(discoveredVar == null ? "(no such variable)" : discoveredVar.val.ToString())} (needs >= 2).");
                    }
                    continue; // not yet actually visited by the player
                }

                // Skip anything that can't support today's global appearance state - but
                // only for collections that actually use generatedScenesLoadingGroup in the
                // first place. Confirmed via EHSceneChanger.LoadLevelASync's own source: it
                // guards this exact array with `.Count > 0` before ever indexing into it, and
                // falls back to RuntimeSceneAssets (unindexed, no appearance-state lookup)
                // when it's empty - which is the normal case for nearly every room, not a
                // crash risk. The original version of this check omitted that guard and
                // ended up excluding almost every candidate unconditionally, mistaking "this
                // room doesn't use the appearance-indexed group at all" for "this room can't
                // support today's appearance" - see the 0-rooms-in-menu bug this caused.
                if (ehCollection.generatedScenesLoadingGroup.Count > 0 &&
                    (currentAppearanceIndex < 0 ||
                     currentAppearanceIndex >= ehCollection.generatedScenesLoadingGroup.Count))
                {
                    if (_diagnosticsConfig.Value)
                    {
                        Logger.LogInfo(
                            $"[BlakeManorFastTravel] Excluding '{ehCollection.handle}': currentAppearanceIndex=" +
                            $"{currentAppearanceIndex} out of range for generatedScenesLoadingGroup.Count=" +
                            $"{ehCollection.generatedScenesLoadingGroup.Count}.");
                    }
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

            if (_diagnosticsConfig.Value)
            {
                Logger.LogInfo(
                    $"[BlakeManorFastTravel] GetDiscoveredDestinations(): {list.Count} destination(s) " +
                    $"from {manager.Collections.Count} total collection(s); currentAppearanceIndex=" +
                    $"{currentAppearanceIndex}; current='{current?.Path ?? "(null)"}'.");
            }

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

        // Diagnostics-only (see _diagnosticsConfig): logs every unique handle in the game
        // (with its label) via BepInEx's own Logger (cheap, one-shot - not Unity's
        // Debug.Log, so none of the performance concerns elsewhere in this file apply).
        // Purely informational for troubleshooting bug reports.
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
        // - i.e. a door's Interaction. The whole action sequence plus which action is the door
        // gets registered against that destination handle in _doorPathsByHandle for
        // HasPassableChecks() to walk live later - see the comment on that field for why the
        // whole sequence is kept rather than pre-classifying individual checks. Also logs what
        // it finds to _doorLinksLogPath, which is how we originally identified ActionEHCheckTime
        // as the Dining Room's gate.
        //
        // Doors only exist as live objects in whatever scene they're placed in, so this
        // only ever sees doors in scenes that have actually loaded - it builds up coverage
        // as you walk/fast-travel around, not all at once. Already-scanned ActionLists are
        // skipped on later passes so walking back through an area doesn't redo the work.
        private void ScanForDoorLinksThrottled()
        {
            bool pendingSceneLoadScanDue = _pendingSceneLoadScanTime.HasValue && Time.unscaledTime >= _pendingSceneLoadScanTime.Value;
            bool periodicSafetyNetDue = Time.unscaledTime - _lastDoorScanTime >= DoorScanIntervalSeconds;
            if (!pendingSceneLoadScanDue && !periodicSafetyNetDue)
            {
                return;
            }
            _pendingSceneLoadScanTime = null;
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

        // Continue -> next sequential index; Skip -> skipActionActual's index if set, else the
        // raw skipAction index; Stop/RunCutscene -> no further actions reachable in this list
        // (RunCutscene diverges to a whole separate Cutscene/asset, not indices in this one).
        private static bool ResolveNextIndex(
            List<AC.Action> actions, ResultAction resultAction, int skipAction, AC.Action skipActionActual, int currentIndex, out int nextIndex)
        {
            switch (resultAction)
            {
                case ResultAction.Continue:
                    nextIndex = currentIndex + 1;
                    return nextIndex < actions.Count;
                case ResultAction.Skip:
                    nextIndex = (skipActionActual != null && actions.Contains(skipActionActual))
                        ? actions.IndexOf(skipActionActual)
                        : (skipAction < 0 ? 0 : skipAction);
                    return nextIndex >= 0 && nextIndex < actions.Count;
                default: // Stop, RunCutscene
                    nextIndex = -1;
                    return false;
            }
        }

        private void RegisterDoorLink(AC.ActionList actionList, ActionScene_EH sceneAction, List<AC.ActionCheck> checks)
        {
            string handle = sceneAction.sceneHandle;
            if (!_doorPathsByHandle.TryGetValue(handle, out List<(List<AC.Action> Actions, AC.Action Target)> existingPaths))
            {
                existingPaths = new List<(List<AC.Action> Actions, AC.Action Target)>();
                _doorPathsByHandle[handle] = existingPaths;
            }
            // A snapshot copy, not a live reference to actionList.actions: confirmed via the
            // Walled Garden (three separate doors leading to it) that AC mutates/clears an
            // ActionList's own .actions list at some point after registration - possibly on
            // scene teardown/ResetList() as the originating room unloads - and since we'd
            // stored a direct reference to that same list object, every registered path for a
            // handle went from its real action count down to 0 once its room was left, making
            // the door look permanently unreachable from then on even though nothing about the
            // actual gate changed. The individual Action objects inside are still the same
            // live references (needed so CheckCondition() stays accurate) - only the
            // *container* is copied, so external mutation of the original list can't affect us.
            existingPaths.Add((new List<AC.Action>(actionList.actions), sceneAction));

            if (!_diagnosticsConfig.Value)
            {
                return;
            }

            EHSceneCollection current = EHKickStarter.SceneCollectionsManager?.GetCurrentlyOpenCollection() as EHSceneCollection;
            string fromHandle = current?.handle ?? "unknown";
            string checkTypeNames = checks.Count == 0 ? "none" : string.Join(", ", checks.ConvertAll(c => c.GetType().Name));
            string line =
                $"[{DateTime.Now:HH:mm:ss}] destHandle='{handle}' fromRoom='{fromHandle}' " +
                $"actionList='{actionList.name}' checks=[{checkTypeNames}]";

            // Full ordered dump too: this is what found the Bar's real gate - a three-check
            // AND/OR combination (see _doorPathsByHandle) that no per-check classification
            // could have caught, only seeing the actual sequence and each action's own
            // branching (every AC.Action has endAction/skipAction, not just ActionCheck) could.
            // Kept as a one-time structural reference; HasPassableChecks()'s own live walk logs
            // each check's real pass/fail result on top of this when it actually runs.
            List<string> actionDump = new List<string>();
            for (int i = 0; i < actionList.actions.Count; i++)
            {
                AC.Action a = actionList.actions[i];
                if (a == null)
                {
                    actionDump.Add($"[{i}] null");
                    continue;
                }
                if (a is AC.ActionCheck ac)
                {
                    actionDump.Add(
                        $"[{i}] {a.GetType().Name} (true->{ac.resultActionTrue}/{ac.skipActionTrue}, " +
                        $"fail->{ac.resultActionFail}/{ac.skipActionFail})");
                }
                else
                {
                    actionDump.Add($"[{i}] {a.GetType().Name} (end->{a.endAction}/{a.skipAction})");
                }
            }
            Logger.LogInfo($"[BlakeManorFastTravel] '{actionList.name}' full actions: {string.Join(" | ", actionDump)}");

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
            GUILayout.Label($"{_hotkeyConfig.Value} or Esc to close", MenuTheme.Subtitle);
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
                // EHSceneChanger.LoadLevelASync's generatedScenesLoadingGroup[index] lookup -
                // which the game itself only ever reaches after its own `.Count > 0` guard,
                // so an empty group here is the normal "this room doesn't use that array"
                // case, not a crash risk - see the matching comment in
                // GetDiscoveredDestinations()).
                int currentAppearanceIndex = (int)SceneAppearanceController.sceneState;
                if (destination.generatedScenesLoadingGroup.Count > 0 &&
                    (currentAppearanceIndex < 0 ||
                     currentAppearanceIndex >= destination.generatedScenesLoadingGroup.Count))
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
                _travelDestinationConfirmed = false;
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
