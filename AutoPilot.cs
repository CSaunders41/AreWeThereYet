using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using SharpDX;
using AreWeThereYet.Utils;
using System.Windows.Forms;

namespace AreWeThereYet;

public class AutoPilot
{
    private Coroutine autoPilotCoroutine;
    private readonly Random random = new Random();
        
    private Vector3 lastTargetPosition;
    private Vector3 lastPlayerPosition;
    private Entity followTarget;

    private List<TaskNode> tasks = new List<TaskNode>();
    
    // Debug messages for visual display
    private List<string> debugMessages = new List<string>();

    private LineOfSight LineOfSight => AreWeThereYet.Instance.lineOfSight;

    private string _lastKnownLeaderZone = "";
    private DateTime _leaderZoneChangeTime = DateTime.MinValue;
    
    private bool _isTransitioning = false;

    private void ResetPathing()
    {
        tasks = new List<TaskNode>();
        followTarget = null;
        lastTargetPosition = Vector3.Zero;
        lastPlayerPosition = Vector3.Zero;
    }


    public void AreaChange()
    {
        // If we triggered this area change ourselves...
        if (_isTransitioning)
        {
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"We are transitioning, starting pause coroutine.");
            }

            // ...start a new coroutine to handle the post-transition grace period.
            var gracePeriodCoroutine = new Coroutine(PostTransitionGracePeriod(), AreWeThereYet.Instance, "PostTransitionGracePeriod");
            Core.ParallelRunner.Run(gracePeriodCoroutine);
        }
        ResetPathing();
            
    }

    public void StartCoroutine()
    {
        autoPilotCoroutine = new Coroutine(AutoPilotLogic(), AreWeThereYet.Instance, "AutoPilot");
        Core.ParallelRunner.Run(autoPilotCoroutine);
    }

    private PartyElementWindow GetLeaderPartyElement()
    {
        try
        {
            var leaderName = AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value;
            
            // Debug: Log the leader name being searched for
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"[GetLeaderPartyElement] Searching for leader: '{leaderName}'");
            }
            
            if (string.IsNullOrEmpty(leaderName))
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"[GetLeaderPartyElement] ERROR: Leader name is null or empty!");
                }
                return null;
            }
            
            var partyElements = PartyElements.GetPlayerInfoElementList();
            
            // Debug: Log all party members found
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"[GetLeaderPartyElement] Found {partyElements.Count} party members:");
                foreach (var partyElement in partyElements)
                {
                    var playerName = partyElement?.PlayerName ?? "NULL";
                    var zoneName = partyElement?.ZoneName ?? "NULL";
                    var matches = string.Equals(playerName, leaderName, StringComparison.OrdinalIgnoreCase);
                    AreWeThereYet.Instance.LogMessage($"  - Player: '{playerName}' (Zone: '{zoneName}') -> {(matches ? "MATCH!" : "No match")}");
                }
            }
            
            foreach (var partyElementWindow in partyElements)
            {
                if (string.Equals(partyElementWindow?.PlayerName, leaderName, StringComparison.OrdinalIgnoreCase))
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"[GetLeaderPartyElement] FOUND leader in party: '{partyElementWindow.PlayerName}' in zone '{partyElementWindow.ZoneName}'");
                    }
                    return partyElementWindow;
                }
            }
            
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"[GetLeaderPartyElement] Leader NOT FOUND in party");
            }
            
            return null;
        }
        catch (Exception ex)
        {
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"[GetLeaderPartyElement] Exception: {ex.Message}");
            }
            return null;
        }
    }
    
    private bool IsLeaderZoneInfoReliable(PartyElementWindow leaderPartyElement)
    {
        try
        {
            // Check if zone name looks valid (not empty, not obviously stale)
            var zoneName = leaderPartyElement.ZoneName;
            var currentZone = AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName;
            
            // Invalid if empty or same as current zone when leader should be elsewhere
            if (string.IsNullOrEmpty(zoneName) || zoneName.Equals(currentZone))
                return false;
                
            // Check if zone name changed very recently (might still be updating)
            var timeSinceChange = DateTime.Now - _leaderZoneChangeTime;
            if (timeSinceChange < TimeSpan.FromMilliseconds(AreWeThereYet.Instance.Settings.AutoPilot.ZoneUpdateBuffer.Value))
                return false;
                
            return true;
        }
        catch
        {
            return false;
        }
    }

    private LabelOnGround GetBestPortalLabel(PartyElementWindow leaderPartyElement)
    {
        try
        {
            var currentZoneName = AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName;
            var isHideout = (bool)AreWeThereYet.Instance?.GameController?.Area?.CurrentArea?.IsHideout;
            var realLevel = AreWeThereYet.Instance.GameController?.Area?.CurrentArea?.RealLevel ?? 0;

            // Enhanced logic: differentiate between leveling zones and endgame content
            if (isHideout || realLevel >= 68)
            {
                // ENDGAME/HIDEOUT: Any portal is fine (maps, hideout transitions)
                var portalLabels = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels
                    .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid &&
                            x.Label.IsVisible && x.ItemOnGround != null &&
                            (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") ||
                                x.ItemOnGround.Metadata.ToLower().Contains("portal")))
                    .OrderBy(x => Vector3.Distance(lastTargetPosition, x.ItemOnGround.Pos))
                    .ToList();

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"Endgame/Hideout portal search: Found {portalLabels?.Count ?? 0} portals");
                }

                return isHideout && portalLabels?.Count > 0
                    ? portalLabels[random.Next(portalLabels.Count)] // Random portal in hideout
                    : portalLabels?.FirstOrDefault(); // Closest portal in endgame
            }
            else
            {
                // LEVELING ZONES: Must find portal that leads to leader's specific zone
                var portalLabels = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels
                    .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid &&
                            x.Label.IsVisible && x.ItemOnGround != null &&
                            (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") ||
                                x.ItemOnGround.Metadata.ToLower().Contains("portal")) &&
                            x.Label.Text.ToLower().Contains(leaderPartyElement.ZoneName.ToLower())) // IMPORTANT KEY IMPROVEMENT: Check portal text
                    .OrderBy(x => Vector3.Distance(lastTargetPosition, x.ItemOnGround.Pos))
                    .ToList();

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    var allPortals = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels
                        .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid &&
                                x.Label.IsVisible && x.ItemOnGround != null &&
                                (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") ||
                                    x.ItemOnGround.Metadata.ToLower().Contains("portal")))
                        .ToList();

                    AreWeThereYet.Instance.LogMessage($"Leveling zone portal search:");
                    AreWeThereYet.Instance.LogMessage($"  - Leader zone: '{leaderPartyElement.ZoneName}'");
                    AreWeThereYet.Instance.LogMessage($"  - All portals: {allPortals?.Count ?? 0}");
                    AreWeThereYet.Instance.LogMessage($"  - Matching portals: {portalLabels?.Count ?? 0}");

                    if (allPortals != null)
                    {
                        foreach (var portal in allPortals)
                        {
                            var matches = portal.Label.Text.Contains(leaderPartyElement.ZoneName);
                            AreWeThereYet.Instance.LogMessage($"    Portal: '{portal.Label.Text}' -> {(matches ? "MATCH" : "No match")}");
                        }
                    }
                }

                // EXPLICIT NULL CHECK: If no matching portals found in leveling zone, return null for teleport fallback
                if (portalLabels == null || portalLabels.Count == 0)
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"No matching portal found for leader zone '{leaderPartyElement.ZoneName}' - will use teleport button fallback");
                    }
                    return null; // Force teleport button usage
                }

                return portalLabels.FirstOrDefault(); // Return closest matching portal
            }
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetBestPortalLabel failed: {ex.Message}");
            return null; // Exception fallback
        }
    }

    private LabelOnGround GetMercenaryOptInButton()
    {
        try
        {
            // Better null checking to prevent the exception
            if (AreWeThereYet.Instance?.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels == null)
                return null;

            var mercenaryLabels = AreWeThereYet.Instance.GameController.Game.IngameState.IngameUi.ItemsOnGroundLabels
                .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid && 
                        x.Label.IsVisible && x.ItemOnGround != null &&
                        !string.IsNullOrEmpty(x.ItemOnGround.Metadata) &&
                        x.ItemOnGround.Metadata.ToLower().Contains("mercenary") &&
                        x.Label.Children?.Count > 2 && x.Label.Children[2] != null &&
                        x.Label.Children[2].IsVisible)
                .OrderBy(x => Vector3.Distance(AreWeThereYet.Instance.playerPosition, x.ItemOnGround.Pos))
                .ToList();

            return mercenaryLabels?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetMercenaryOptInButton failed: {ex.Message}");
            return null;
        }
    }

    private Vector2 GetMercenaryOptInButtonPosition(LabelOnGround mercenaryLabel)
    {
        try
        {
            if (mercenaryLabel?.Label?.Children?.Count > 2 && mercenaryLabel.Label.Children[2] != null)
            {
                var windowOffset = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle().TopLeft;
                var optInButton = mercenaryLabel.Label.Children[2];
                var buttonCenter = optInButton.GetClientRectCache.Center;
                var finalPos = new Vector2(buttonCenter.X + windowOffset.X, buttonCenter.Y + windowOffset.Y);

                return finalPos;
            }
            return Vector2.Zero;
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetMercenaryOptInButtonPosition failed: {ex.Message}");
            return Vector2.Zero;
        }
    }
    
    private Vector2 GetTpButton(PartyElementWindow leaderPartyElement)
    {
        try
        {
            var windowOffset = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle().TopLeft;
            var elemCenter = (Vector2)leaderPartyElement?.TpButton?.GetClientRectCache.Center;
            var finalPos = new Vector2(elemCenter.X + windowOffset.X, elemCenter.Y + windowOffset.Y);

            return finalPos;
        }
        catch
        {
            return Vector2.Zero;
        }
    }

    private Element GetTpConfirmation()
    {
        try
        {
            var ui = AreWeThereYet.Instance.GameController?.IngameState?.IngameUi?.PopUpWindow;

            if (ui.Children[0].Children[0].Children[0].Text.Equals("Are you sure you want to teleport to this player's location?"))
                return ui.Children[0].Children[0].Children[3].Children[0];

            return null;
        }
        catch
        {
            return null;
        }
    }

    private IEnumerator MouseoverItem(Entity item)
    {
        var uiLoot = AreWeThereYet.Instance.GameController.IngameState.IngameUi.ItemsOnGroundLabels.FirstOrDefault(I => I.IsVisible && I.ItemOnGround.Id == item.Id);
        if (uiLoot == null) yield return null;
        var clickPos = uiLoot?.Label?.GetClientRect().Center;
        if (clickPos != null)
        {
            Mouse.SetCursorPos(new Vector2(
                clickPos.Value.X + random.Next(-15, 15),
                clickPos.Value.Y + random.Next(-10, 10)));
        }
	        
        yield return new WaitTime(30 + random.Next(AreWeThereYet.Instance.Settings.AutoPilot.InputFrequency));
    }
    
        private IEnumerator PostTransitionGracePeriod()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        const int TIMEOUT_MS = 10000; // 10-second timeout.

        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
        {
            AreWeThereYet.Instance.LogMessage("[GracePeriod] Entered post-transition grace period. Waiting for leader entity to sync...");
        }

        while (stopwatch.ElapsedMilliseconds < TIMEOUT_MS)
        {
            var leaderPartyElement = GetLeaderPartyElement();
            var followTarget = GetFollowingTarget();
            var currentAreaName = AreWeThereYet.Instance.GameController.Area.CurrentArea.DisplayName;

            // Success Condition: The leader's entity is found and they are in the same zone as us.
            if (leaderPartyElement != null && followTarget != null && leaderPartyElement.ZoneName.Equals(currentAreaName))
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"[GracePeriod] SUCCESS: Leader entity found and synced in '{currentAreaName}'. Resuming normal logic.");
                }
                stopwatch.Stop();
                _isTransitioning = false; // Unlock the main logic.
                yield break;              // Exit the coroutine.
            }

            yield return new WaitTime(100);
        }

        // If we reach here, the loop timed out. Now we must determine why.
        var finalLeaderPartyElement = GetLeaderPartyElement();
        var finalCurrentAreaName = AreWeThereYet.Instance.GameController.Area.CurrentArea.DisplayName;

        // --- THE NEW FAILSAFE LOGIC ---
        // Check for the "Same Zone, Different Instance" problem.
        if (finalLeaderPartyElement != null && finalLeaderPartyElement.ZoneName.Equals(finalCurrentAreaName))
        {
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"[GracePeriod] DEADLOCK DETECTED: In same zone ('{finalCurrentAreaName}') but different instance. Forcing UI teleport to sync instances.", 10, Color.Red);
            }

            // Check for and click the "Are you sure?" confirmation box if it's open.
            var tpConfirmation = GetTpConfirmation();
            if (tpConfirmation != null)
            {
                yield return Mouse.SetCursorPosHuman(tpConfirmation.GetClientRect().Center);
                yield return new WaitTime(200);
                yield return Mouse.LeftClick();
                yield return new WaitTime(1000);
            }

            // Click the teleport button on the party UI to force an instance sync.
            var tpButton = GetTpButton(finalLeaderPartyElement);
            if (!tpButton.Equals(Vector2.Zero))
            {
                yield return Mouse.SetCursorPosHuman(tpButton, false);
                yield return new WaitTime(200);
                yield return Mouse.LeftClick();
                yield return new WaitTime(200);
            }
        }
        else
        {
            // The timeout was for a different reason (e.g., leader zoned again). Let the main logic handle it.
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage("[GracePeriod] TIMEOUT: Leader entity did not sync. Resuming logic with fallback.", 5, Color.Orange);
            }
        }

        _isTransitioning = false; // Unlock the main logic in all timeout cases.
    }
    
    private IEnumerator AutoPilotLogic()
    {
        while (true)
        {
            // =================================================================
            // SECTION 1: INITIAL CHECKS & UI CLEANUP
            // =================================================================
            if (!AreWeThereYet.Instance.Settings.Enable.Value || !AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value || AreWeThereYet.Instance.localPlayer == null || !AreWeThereYet.Instance.localPlayer.IsAlive ||
                !AreWeThereYet.Instance.GameController.IsForeGroundCache || MenuWindow.IsOpened || AreWeThereYet.Instance.GameController.IsLoading || !AreWeThereYet.Instance.GameController.InGame)
            {
                yield return new WaitTime(100);
                continue;
            }
            
            // =================================================================
            // SECTION 2: SETTINGS VERIFICATION
            // =================================================================
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                var leaderName = AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value;
                AreWeThereYet.Instance.LogMessage($"[AutoPilotLogic] Settings Check:");
                AreWeThereYet.Instance.LogMessage($"  - Plugin Enabled: {AreWeThereYet.Instance.Settings.Enable.Value}");
                AreWeThereYet.Instance.LogMessage($"  - AutoPilot Enabled: {AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value}");
                AreWeThereYet.Instance.LogMessage($"  - Leader Name: '{leaderName}'");
                AreWeThereYet.Instance.LogMessage($"  - Leader Name Empty: {string.IsNullOrEmpty(leaderName)}");
                AreWeThereYet.Instance.LogMessage($"  - Local Player: {(AreWeThereYet.Instance.localPlayer != null ? "Found" : "NULL")}");
                AreWeThereYet.Instance.LogMessage($"  - Local Player Alive: {(AreWeThereYet.Instance.localPlayer?.IsAlive ?? false)}");
                AreWeThereYet.Instance.LogMessage($"  - Game In Foreground: {AreWeThereYet.Instance.GameController.IsForeGroundCache}");
                AreWeThereYet.Instance.LogMessage($"  - Game In Loading: {AreWeThereYet.Instance.GameController.IsLoading}");
                AreWeThereYet.Instance.LogMessage($"  - Game In Game: {AreWeThereYet.Instance.GameController.InGame}");
            }
            
            // Check for empty leader name
            if (string.IsNullOrEmpty(AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value))
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"[AutoPilotLogic] ERROR: Leader name is not set! Please configure the leader name in settings.");
                }
                yield return new WaitTime(1000); // Wait longer when settings are not configured
                continue;
            }
            
            // TODO: custom settings if user want automatically close all ui shits.
            // var ingameUi = AreWeThereYet.Instance.GameController.IngameState.IngameUi;

            // if (new List<Element> { ingameUi.TreePanel, ingameUi.AtlasTreePanel, ingameUi.OpenLeftPanel, ingameUi.OpenRightPanel, ingameUi.InventoryPanel, ingameUi.SettingsPanel, ingameUi.ChatPanel.Children.FirstOrDefault() }.Any(panel => panel != null && panel.IsVisible))
            // {
            //     Keyboard.KeyPress(Keys.Escape);
            //     yield return new WaitTime(150);
            //     continue;
            // }

            followTarget = GetFollowingTarget();
            var leaderPartyElement = GetLeaderPartyElement();

            // Debug: Log the current status
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                var currentZone = AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName ?? "NULL";
                var leaderZone = leaderPartyElement?.ZoneName ?? "NULL";
                AreWeThereYet.Instance.LogMessage($"[AutoPilotLogic] Status Check:");
                AreWeThereYet.Instance.LogMessage($"  - Follow Target: {(followTarget != null ? "FOUND" : "NULL")}");
                AreWeThereYet.Instance.LogMessage($"  - Leader Party Element: {(leaderPartyElement != null ? "FOUND" : "NULL")}");
                AreWeThereYet.Instance.LogMessage($"  - Current Zone: '{currentZone}'");
                AreWeThereYet.Instance.LogMessage($"  - Leader Zone: '{leaderZone}'");
                AreWeThereYet.Instance.LogMessage($"  - Same Zone: {(leaderPartyElement != null && leaderZone.Equals(currentZone))}");
                AreWeThereYet.Instance.LogMessage($"  - Is Transitioning: {_isTransitioning}");
            }

            if (leaderPartyElement == null)
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"[AutoPilotLogic] Leader party element not found. Cannot proceed with portal/teleport logic.");
                }
                yield return new WaitTime(100); // Wait a bit before retrying
                continue;
            }

            if (followTarget == null && !leaderPartyElement.ZoneName.Equals(AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName) && !_isTransitioning)
            {
                // Track zone changes for buffer timing
                if (!_lastKnownLeaderZone.Equals(leaderPartyElement.ZoneName))
                {
                    // Leader zone changed - start buffer timer
                    _lastKnownLeaderZone = leaderPartyElement.ZoneName;
                    _leaderZoneChangeTime = DateTime.Now;

                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"Leader zone change detected: '{_lastKnownLeaderZone}' - starting reliability check");
                    }
                }

                // Use smarter zone detection to check if leader zone info is reliable
                if (IsLeaderZoneInfoReliable(leaderPartyElement))
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"Leader zone info reliable: '{leaderPartyElement.ZoneName}' - proceeding with portal/teleport logic");
                    }

                    var portal = GetBestPortalLabel(leaderPartyElement);
                    if (portal != null)
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            AreWeThereYet.Instance.LogMessage($"Found reliable portal: {portal.ItemOnGround.Metadata}");
                        }
                        tasks.Add(new TaskNode(portal, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value, TaskNodeType.Transition));
                    }
                    else
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            AreWeThereYet.Instance.LogMessage($"No portal found - using teleport button");
                        }
                        
                        // Check for and click the "Are you sure?" confirmation box if it's open.
                        var tpConfirmation = GetTpConfirmation();
                        if (tpConfirmation != null)
                        {
                            yield return Mouse.SetCursorPosHuman(tpConfirmation.GetClientRect().Center);
                            yield return new WaitTime(200);
                            yield return Mouse.LeftClick();
                            yield return new WaitTime(1000);
                        }

                        // Use teleport button as fallback
                        var tpButton = GetTpButton(leaderPartyElement);
                        if (!tpButton.Equals(Vector2.Zero))
                        {
                            yield return Mouse.SetCursorPosHuman(tpButton, false);
                            yield return new WaitTime(200);
                            yield return Mouse.LeftClick();
                            yield return new WaitTime(200);
                        }
                    }
                }
                else
                {
                    // Leader zone info not reliable yet, wait for it to stabilize
                    var timeSinceChange = DateTime.Now - _leaderZoneChangeTime;
                    var bufferTime = TimeSpan.FromMilliseconds(AreWeThereYet.Instance.Settings.AutoPilot.ZoneUpdateBuffer?.Value ?? 2000);

                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        var remaining = bufferTime - timeSinceChange;
                        AreWeThereYet.Instance.LogMessage($"Zone info not reliable yet - waiting {remaining.TotalMilliseconds:F0}ms more (Current: '{leaderPartyElement.ZoneName}')");
                    }

                    yield return new WaitTime(200); // Wait a bit longer for zone info to stabilize
                }
            }
            else if (followTarget != null)
            {
                // Reset zone tracking when leader is found
                _lastKnownLeaderZone = "";
                _leaderZoneChangeTime = DateTime.MinValue;
                
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"[AutoPilotLogic] Leader found! Processing follow logic...");
                }
                
                var distanceToLeader = Vector3.Distance(AreWeThereYet.Instance.playerPosition, followTarget.Pos);
                
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"  - Distance to leader: {distanceToLeader:F1}");
                    AreWeThereYet.Instance.LogMessage($"  - Transition distance threshold: {AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value}");
                    AreWeThereYet.Instance.LogMessage($"  - Keep within distance: {AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value}");
                    AreWeThereYet.Instance.LogMessage($"  - Current tasks: {tasks.Count}");
                }
                
                // Validate distance thresholds to prevent impossible scenarios
                var transitionDistance = AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value;
                var keepWithinDistance = AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value;
                
                if (transitionDistance <= keepWithinDistance)
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"[WARNING] Transition distance ({transitionDistance}) should be greater than keep within distance ({keepWithinDistance})");
                    }
                }
                
                if (distanceToLeader >= transitionDistance)
                {
                    var distanceMoved = Vector3.Distance(lastTargetPosition, followTarget.Pos);
                    
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"  - Distance moved by leader: {distanceMoved:F1}");
                        AreWeThereYet.Instance.LogMessage($"  - Leader far away - creating movement task");
                    }
                    
                    if (lastTargetPosition != Vector3.Zero && distanceMoved > transitionDistance)
                    {
                        // Leader moved far - check for portal first
                        var transition = GetBestPortalLabel(leaderPartyElement);
                        if (transition != null && transition.ItemOnGround.DistancePlayer < 80)
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"  - Adding portal transition task");
                            }
                            tasks.Add(new TaskNode(transition, 200, TaskNodeType.Transition));
                        }
                        else
                        {
                            // No portal, add movement task
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"  - Adding movement task to leader position");
                            }
                            tasks.Add(new TaskNode(followTarget.Pos, keepWithinDistance));
                        }
                    }
                    else if (tasks.Count == 0 && distanceMoved < 2000 && distanceToLeader > 200 && distanceToLeader < 2000)
                    {
                        // Leader is stationary but far - create movement task
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            AreWeThereYet.Instance.LogMessage($"  - Adding movement task to stationary leader");
                        }
                        tasks.Add(new TaskNode(followTarget.Pos, keepWithinDistance));
                    }
                    else if (tasks.Count > 0)
                    {
                        // Add waypoint if leader moved far from last task
                        var distanceFromLastTask = Vector3.Distance(tasks.Last().WorldPosition, followTarget.Pos);
                        if (distanceFromLastTask >= keepWithinDistance)
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"  - Adding waypoint task (distance from last task: {distanceFromLastTask:F1})");
                            }
                            tasks.Add(new TaskNode(followTarget.Pos, keepWithinDistance));
                        }
                    }
                }
                else
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"  - Leader close by - processing close follow logic");
                    }
                    
                    if (tasks.Count > 0)
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            AreWeThereYet.Instance.LogMessage($"  - Clearing {tasks.Count} existing movement/transition tasks");
                        }
                        
                        for (var i = tasks.Count - 1; i >= 0; i--)
                            if (tasks[i].Type == TaskNodeType.Movement || tasks[i].Type == TaskNodeType.Transition)
                                tasks.RemoveAt(i);
                        yield return null;
                    }
                    
                    if (AreWeThereYet.Instance.Settings.AutoPilot.CloseFollow.Value)
                    {
                        if (distanceToLeader >= keepWithinDistance)
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"  - Close follow: Adding movement task (distance {distanceToLeader:F1} >= {keepWithinDistance})");
                            }
                            tasks.Add(new TaskNode(followTarget.Pos, keepWithinDistance));
                        }
                        else
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"  - Close follow: Within range (distance {distanceToLeader:F1} < {keepWithinDistance}) - no action needed");
                            }
                        }
                    }
                    else
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            AreWeThereYet.Instance.LogMessage($"  - Close follow disabled - no close follow tasks created");
                        }
                    }

                    var isHideout = (bool)AreWeThereYet.Instance?.GameController?.Area?.CurrentArea?.IsHideout;
                    if (!isHideout)
                    {
                        var questLoot = GetQuestItem();
                        if (questLoot != null &&
                            Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos) < AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value &&
                            tasks.FirstOrDefault(I => I.Type == TaskNodeType.Loot) == null)
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                var distance = Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos);
                                AreWeThereYet.Instance.LogMessage($"Adding quest loot task - Distance: {distance:F1}, Item: {questLoot.Metadata}");
                            }
                            tasks.Add(new TaskNode(questLoot.Pos, AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance, TaskNodeType.Loot));
                        }
                        else if (questLoot != null && AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            var distance = Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos);
                            var hasLootTask = tasks.FirstOrDefault(I => I.Type == TaskNodeType.Loot) != null;
                            AreWeThereYet.Instance.LogMessage($"Quest loot NOT added - Distance: {distance:F1}, TooFar: {distance >= AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value}, HasLootTask: {hasLootTask}");
                        }


                        var mercenaryOptIn = GetMercenaryOptInButton();
                        if (mercenaryOptIn != null &&
                            Vector3.Distance(AreWeThereYet.Instance.playerPosition, mercenaryOptIn.ItemOnGround.Pos) < AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value &&
                            tasks.FirstOrDefault(I => I.Type == TaskNodeType.MercenaryOptIn) == null)
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"Found mercenary OPT-IN button - adding to tasks");
                            }
                            tasks.Add(new TaskNode(mercenaryOptIn, AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance, TaskNodeType.MercenaryOptIn));
                        }
                    }

                }
                if (followTarget?.Pos != null)
                    lastTargetPosition = followTarget.Pos;
            }

            if (tasks?.Count > 0)
            {
                var currentTask = tasks.First();
                var taskDistance = Vector3.Distance(AreWeThereYet.Instance.playerPosition, currentTask.WorldPosition);
                var playerDistanceMoved = Vector3.Distance(AreWeThereYet.Instance.playerPosition, lastPlayerPosition);

                if (currentTask.Type == TaskNodeType.Transition &&
                    playerDistanceMoved >= AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                {
                    tasks.RemoveAt(0);
                    lastPlayerPosition = AreWeThereYet.Instance.playerPosition;
                    yield return null;
                    continue;
                }
                switch (currentTask.Type)
                {
                    case TaskNodeType.Movement:
                        if (AreWeThereYet.Instance.Settings.AutoPilot.DashEnabled &&
                        ShouldUseDash(currentTask.WorldPosition.WorldToGrid()))
                        {
                            yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(currentTask.WorldPosition));
                            yield return new WaitTime(random.Next(25) + 30);
                            Keyboard.KeyPress(AreWeThereYet.Instance.Settings.AutoPilot.DashKey);
                            yield return new WaitTime(random.Next(25) + 30);
                        }
                        else
                        {
                            yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(currentTask.WorldPosition));
                            yield return new WaitTime(random.Next(25) + 30);
                            Keyboard.KeyDown(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                            yield return new WaitTime(random.Next(25) + 30);
                            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                        }

                        if (taskDistance <= AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value * 1.5)
                            tasks.RemoveAt(0);
                        yield return null;
                        yield return null;
                        continue;

                    case TaskNodeType.Loot:
                        {
                            currentTask.AttemptCount++;
                            var questLoot = GetQuestItem();
                            if (questLoot == null
                                || currentTask.AttemptCount > 2
                                || Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos) >=
                                AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                            {
                                tasks.RemoveAt(0);
                                yield return null;
                            }

                            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                            yield return new WaitTime(AreWeThereYet.Instance.Settings.AutoPilot.InputFrequency);
                            if (questLoot != null)
                            {
                                var targetInfo = questLoot.GetComponent<Targetable>();
                                switch (targetInfo.isTargeted)
                                {
                                    case false:
                                        yield return MouseoverItem(questLoot);
                                        break;
                                    case true:
                                        yield return Mouse.LeftClick();
                                        yield return new WaitTime(1000);
                                        break;
                                }
                            }

                            break;
                        }

                    case TaskNodeType.Transition:
                        {
                            // Re-validate portal exists and is still valid before attempting to use it
                            if (currentTask.LabelOnGround?.Label?.IsValid != true ||
                                currentTask.LabelOnGround?.IsVisible != true ||
                                currentTask.LabelOnGround?.ItemOnGround == null)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage("Portal became invalid - removing transition task, will re-evaluate in main loop");
                                }

                                tasks.RemoveAt(0);
                                yield return null;
                                continue;
                            }
                            
                            // SET THE FLAG: We are about to change zones.
                            _isTransitioning = true;

                            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                            yield return new WaitTime(60);
                            yield return Mouse.SetCursorPosAndLeftClickHuman(new Vector2(currentTask.LabelOnGround.Label.GetClientRect().Center.X, currentTask.LabelOnGround.Label.GetClientRect().Center.Y), 100);
                            yield return new WaitTime(300);

                            currentTask.AttemptCount++;
                            if (currentTask.AttemptCount > 6)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage("Transition task failed after 6 attempts - removing task");
                                }
                                tasks.RemoveAt(0);
                                _isTransitioning = false; // Reset transition flag on failure
                            }
                            
                            yield return null;
                            continue;
                        }

                    case TaskNodeType.MercenaryOptIn:
                        {
                            currentTask.AttemptCount++;
                            var mercenaryOptIn = GetMercenaryOptInButton();

                            // Remove task if button disappeared, too many attempts, or we're too far
                            if (mercenaryOptIn == null ||
                                currentTask.AttemptCount > 3 ||
                                Vector3.Distance(AreWeThereYet.Instance.playerPosition, mercenaryOptIn.ItemOnGround.Pos) >=
                                AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    var reason = mercenaryOptIn == null ? "button disappeared" :
                                                currentTask.AttemptCount > 3 ? "too many attempts" : "too far away";
                                    AreWeThereYet.Instance.LogMessage($"Removing mercenary OPT-IN task: {reason}");
                                }

                                tasks.RemoveAt(0);
                                yield return null;
                                continue;
                            }

                            // Stop movement and click the button
                            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                            yield return new WaitTime(AreWeThereYet.Instance.Settings.AutoPilot.InputFrequency);

                            var buttonPos = GetMercenaryOptInButtonPosition(mercenaryOptIn);
                            if (!buttonPos.Equals(Vector2.Zero))
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage($"Clicking mercenary OPT-IN button at {buttonPos}");
                                }

                                yield return Mouse.SetCursorPosHuman(buttonPos, false);
                                yield return new WaitTime(200);
                                yield return Mouse.LeftClick();
                                yield return new WaitTime(500); // Wait for button to process click

                                // Remove task after clicking (button should disappear)
                                tasks.RemoveAt(0);
                            }
                            else
                            {
                                // Couldn't get button position, remove task
                                tasks.RemoveAt(0);
                            }

                            break;
                        }
                }
            }

            // =================================================================
            // SECTION 4: MANDATORY END-OF-LOOP HOUSEKEEPING
            // =================================================================
            // This block is OUTSIDE all other logic and will run on EVERY
            // single iteration of the while loop, guaranteeing correctness.
            lastPlayerPosition = AreWeThereYet.Instance.playerPosition;
            yield return new WaitTime(50);
        }
    }

    private bool ShouldUseDash(Vector2 targetPosition)
    {
        try
        {
            // 1. Initial checks
            if (LineOfSight == null ||
                AreWeThereYet.Instance?.GameController?.Player?.GridPos == null ||
                AreWeThereYet.Instance?.Settings?.AutoPilot?.DashEnabled?.Value != true)
                return false;

            var playerPos = AreWeThereYet.Instance.GameController.Player.GridPos;
            var distance = Vector2.Distance(playerPos, targetPosition);

            var minDistance = AreWeThereYet.Instance.Settings.AutoPilot.Dash.DashMinDistance.Value;
            var maxDistance = AreWeThereYet.Instance.Settings.AutoPilot.Dash.DashMaxDistance.Value;

            if (distance < minDistance || distance > maxDistance)
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    AreWeThereYet.Instance.LogMessage($"ShouldUseDash: Distance {distance:F1} outside dash range ({minDistance}-{maxDistance})");
                return false;
            }

            // 2. Convert to System.Numerics.Vector2
            var playerPosNumerics = new System.Numerics.Vector2(playerPos.X, playerPos.Y);
            var targetPosNumerics = new System.Numerics.Vector2(targetPosition.X, targetPosition.Y);

            // 3. THE FIX: Call the new method and check the result
            var pathStatus = LineOfSight.GetPathStatus(playerPosNumerics, targetPosNumerics);

            // The new logic: only dash if the path is specifically blocked by a dashable obstacle.
            var shouldDash = pathStatus == PathStatus.Dashable;

            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"ShouldUseDash: RESULT = {shouldDash} (distance: {distance:F1}, pathStatus: {pathStatus})");
            }

            return shouldDash;
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"ShouldUseDash failed: {ex.Message}");
            return false; // Safe fallback - don't dash if terrain check fails
        }
    }

    private Entity GetFollowingTarget()
    {
        try
        {
            string leaderName = AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value;
            
            // Debug: Add visual debugging to see what's happening
            debugMessages.Add($"GetFollowingTarget: Searching for leader: '{leaderName}'");
            
            if (string.IsNullOrEmpty(leaderName))
            {
                debugMessages.Add("GetFollowingTarget: ERROR - Leader name is null or empty!");
                return null;
            }
            
            var playerEntities = AreWeThereYet.Instance.GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Player];
            var currentPlayerName = AreWeThereYet.Instance.localPlayer?.GetComponent<Player>()?.PlayerName;
            
            debugMessages.Add($"GetFollowingTarget: Found {playerEntities.Count} entities from EntityListWrapper");
            debugMessages.Add($"GetFollowingTarget: Current player: '{currentPlayerName}'");
            
            // Check each entity in detail
            foreach (var entity in playerEntities)
            {
                try
                {
                    var playerComponent = entity.GetComponent<Player>();
                    var playerName = playerComponent?.PlayerName ?? "NULL";
                    var isCurrentPlayer = string.Equals(playerName, currentPlayerName, StringComparison.OrdinalIgnoreCase);
                    var matches = !isCurrentPlayer && string.Equals(playerName, leaderName, StringComparison.OrdinalIgnoreCase);
                    
                    debugMessages.Add($"  Entity: '{playerName}' (Current: {isCurrentPlayer}) -> {(matches ? "MATCH!" : "No match")}");
                    
                    if (matches)
                    {
                        debugMessages.Add($"GetFollowingTarget: SUCCESS - Found leader entity '{playerName}'");
                        return entity;
                    }
                }
                catch (Exception entityEx)
                {
                    debugMessages.Add($"GetFollowingTarget: Error checking entity: {entityEx.Message}");
                }
            }
            
            debugMessages.Add($"GetFollowingTarget: FAILED - No matching entity found for '{leaderName}'");
            return null;
        }
        catch (Exception ex)
        {
            debugMessages.Add($"GetFollowingTarget: Exception: {ex.Message}");
            return null;
        }
    }

    private static Entity GetQuestItem()
    {
        try
        {
            var questItemLabels = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels
                .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid && 
                        x.Label.IsVisible && x.ItemOnGround != null && 
                        x.ItemOnGround.Type == EntityType.WorldItem && 
                        x.ItemOnGround.IsTargetable && x.ItemOnGround.HasComponent<WorldItem>())
                .Where(x =>
                {
                    try
                    {
                        var itemEntity = x.ItemOnGround.GetComponent<WorldItem>().ItemEntity;
                        return AreWeThereYet.Instance.GameController.Files.BaseItemTypes.Translate(itemEntity.Path).ClassName == "QuestItem";
                    }
                    catch
                    {
                        return false;
                    }
                })
                .OrderBy(x => Vector3.Distance(AreWeThereYet.Instance.playerPosition, x.ItemOnGround.Pos))
                .ToList();

            // Return the Entity from the closest quest item label
            return questItemLabels?.FirstOrDefault()?.ItemOnGround;
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetQuestItem failed: {ex.Message}");
            return null;
        }
    }

    public void Render()
    {
        if (AreWeThereYet.Instance.Settings.AutoPilot.ToggleKey.PressedOnce())
        {
            AreWeThereYet.Instance.Settings.AutoPilot.Enabled.SetValueNoEvent(!AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value);
            tasks = new List<TaskNode>();
        }

        if (!AreWeThereYet.Instance.Settings.AutoPilot.Enabled || AreWeThereYet.Instance.GameController.IsLoading || !AreWeThereYet.Instance.GameController.InGame)
            return;

        // VISIBLE DEBUG INDICATOR - Show on screen instead of log
        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
        {
            try
            {
                var debugY = 200f;
                var debugColor = Color.Yellow;
                
                // Show debug status
                AreWeThereYet.Instance.Graphics.DrawText(
                    "*** DEBUG MODE ENABLED ***", 
                    new System.Numerics.Vector2(10, debugY), 
                    debugColor);
                debugY += 20;
                
                // Show leader search results
                var leaderName = AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value ?? "NULL";
                var currentPlayerName = AreWeThereYet.Instance.localPlayer?.GetComponent<Player>()?.PlayerName ?? "NULL";
                
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"Leader Name: '{leaderName}'", 
                    new System.Numerics.Vector2(10, debugY), 
                    debugColor);
                debugY += 20;
                
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"Current Player: '{currentPlayerName}'", 
                    new System.Numerics.Vector2(10, debugY), 
                    debugColor);
                debugY += 20;
                
                // Show player entity count
                var playerEntities = AreWeThereYet.Instance.GameController?.EntityListWrapper?.ValidEntitiesByType?[EntityType.Player];
                var playerCount = playerEntities?.Count ?? 0;
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"Player Entities Found: {playerCount}", 
                    new System.Numerics.Vector2(10, debugY), 
                    debugColor);
                debugY += 20;
                
                // Show party member count
                var partyMembers = PartyElements.GetPlayerInfoElementList();
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"Party Members Found: {partyMembers?.Count ?? 0}", 
                    new System.Numerics.Vector2(10, debugY), 
                    debugColor);
                debugY += 20;
                
                // Show autopilot status
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"AutoPilot Coroutine: {(autoPilotCoroutine?.Running == true ? "RUNNING" : "DEAD")}", 
                    new System.Numerics.Vector2(10, debugY), 
                    debugColor);
                debugY += 20;
                
                // Show followTarget status
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"Follow Target: {(followTarget != null ? "FOUND" : "NULL")}", 
                    new System.Numerics.Vector2(10, debugY), 
                    debugColor);
                debugY += 20;
                
                // Show task count
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"Current Tasks: {tasks?.Count ?? 0}", 
                    new System.Numerics.Vector2(10, debugY), 
                    debugColor);
                debugY += 20;
                
                // Show detailed player entity information
                if (playerEntities?.Count > 0)
                {
                    AreWeThereYet.Instance.Graphics.DrawText(
                        "--- PLAYER ENTITIES ---", 
                        new System.Numerics.Vector2(10, debugY), 
                        debugColor);
                    debugY += 20;
                    
                    foreach (var entity in playerEntities.Take(5)) // Show first 5 to avoid screen clutter
                    {
                        try
                        {
                            var playerComponent = entity?.GetComponent<Player>();
                            var playerName = playerComponent?.PlayerName ?? "NULL";
                            var isCurrentPlayer = string.Equals(playerName, currentPlayerName, StringComparison.OrdinalIgnoreCase);
                            var matches = !isCurrentPlayer && string.Equals(playerName, leaderName, StringComparison.OrdinalIgnoreCase);
                            
                            var statusText = isCurrentPlayer ? "(YOU)" : matches ? "(MATCH!)" : "(No match)";
                            var color = matches ? Color.Green : isCurrentPlayer ? Color.Blue : Color.Gray;
                            
                            AreWeThereYet.Instance.Graphics.DrawText(
                                $"  {playerName} {statusText}", 
                                new System.Numerics.Vector2(10, debugY), 
                                color);
                            debugY += 20;
                        }
                        catch (Exception ex)
                        {
                            AreWeThereYet.Instance.Graphics.DrawText(
                                $"  ERROR: {ex.Message}", 
                                new System.Numerics.Vector2(10, debugY), 
                                Color.Red);
                            debugY += 20;
                        }
                    }
                }
                
                // Show party member information
                if (partyMembers?.Count > 0)
                {
                    AreWeThereYet.Instance.Graphics.DrawText(
                        "--- PARTY MEMBERS ---", 
                        new System.Numerics.Vector2(10, debugY), 
                        debugColor);
                    debugY += 20;
                    
                    foreach (var partyMember in partyMembers.Take(5)) // Show first 5 to avoid screen clutter
                    {
                        try
                        {
                            var playerName = partyMember?.PlayerName ?? "NULL";
                            var zoneName = partyMember?.ZoneName ?? "NULL";
                            var matches = string.Equals(playerName, leaderName, StringComparison.OrdinalIgnoreCase);
                            
                            var color = matches ? Color.Green : Color.Gray;
                            
                            AreWeThereYet.Instance.Graphics.DrawText(
                                $"  {playerName} in {zoneName} {(matches ? "(MATCH!)" : "")}", 
                                new System.Numerics.Vector2(10, debugY), 
                                color);
                            debugY += 20;
                        }
                        catch (Exception ex)
                        {
                            AreWeThereYet.Instance.Graphics.DrawText(
                                $"  ERROR: {ex.Message}", 
                                new System.Numerics.Vector2(10, debugY), 
                                Color.Red);
                            debugY += 20;
                        }
                    }
                }
                
                // Show debug messages from methods
                if (debugMessages.Count > 0)
                {
                    AreWeThereYet.Instance.Graphics.DrawText(
                        "--- DEBUG MESSAGES ---", 
                        new System.Numerics.Vector2(10, debugY), 
                        debugColor);
                    debugY += 20;
                    
                    foreach (var message in debugMessages)
                    {
                        AreWeThereYet.Instance.Graphics.DrawText(
                            message, 
                            new System.Numerics.Vector2(10, debugY), 
                            debugColor);
                        debugY += 20;
                    }
                }
            }
            catch (Exception ex)
            {
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"DEBUG ERROR: {ex.Message}", 
                    new System.Numerics.Vector2(10, 200), 
                    Color.Red);
            }
        }
        
        // Clear debug messages for next frame
        debugMessages.Clear();

        try
        {
            var portalLabels =
                AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels.Where(x =>
                    x != null && x.IsVisible && x.Label != null && x.Label.IsValid && x.Label.IsVisible &&
                    x.ItemOnGround != null &&
                    (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") ||
                     x.ItemOnGround.Metadata.ToLower().Contains("portal"))).ToList();

            foreach (var portal in portalLabels)
            {
                AreWeThereYet.Instance.Graphics.DrawLine(portal.Label.GetClientRectCache.TopLeft, portal.Label.GetClientRectCache.TopRight, 2f, Color.Firebrick);
            }
        }
        catch (Exception)
        {
        }

        // Quest Item rendering
        try
        {
            var questItemLabels =
                AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels.Where(x =>
                    x != null && x.IsVisible && x.Label != null && x.Label.IsValid && x.Label.IsVisible &&
                    x.ItemOnGround != null && x.ItemOnGround.Type == EntityType.WorldItem &&
                    x.ItemOnGround.IsTargetable && x.ItemOnGround.HasComponent<WorldItem>()).Where(x =>
                {
                    try
                    {
                        var itemEntity = x.ItemOnGround.GetComponent<WorldItem>().ItemEntity;
                        return AreWeThereYet.Instance.GameController.Files.BaseItemTypes.Translate(itemEntity.Path).ClassName == "QuestItem";
                    }
                    catch
                    {
                        return false;
                    }
                }).ToList();

            foreach (var questItem in questItemLabels)
            {
                AreWeThereYet.Instance.Graphics.DrawLine(questItem.Label.GetClientRectCache.TopLeft, questItem.Label.GetClientRectCache.TopRight, 4f, Color.Lime);
            }
        }
        catch (Exception)
        {
        }

        // Mercenary OPT-IN button rendering (simple version)
        try
        {
            var mercenaryLabels =
                AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels.Where(x =>
                    x != null && x.IsVisible && x.Label != null && x.Label.IsValid && x.Label.IsVisible &&
                    x.ItemOnGround != null &&
                    x.ItemOnGround.Metadata.ToLower().Contains("mercenary") &&
                    x.Label.Children?.Count > 2 && x.Label.Children[2] != null &&
                    x.Label.Children[2].IsVisible).ToList();

            foreach (var mercenary in mercenaryLabels)
            {
                var optInButton = mercenary.Label.Children[2];
                AreWeThereYet.Instance.Graphics.DrawLine(optInButton.GetClientRectCache.TopLeft, optInButton.GetClientRectCache.TopRight, 3f, Color.Cyan);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            var taskCount = 0;
            var dist = 0f;
            var cachedTasks = tasks;

            var lineWidth = (float)AreWeThereYet.Instance.Settings.AutoPilot.Visual.TaskLineWidth.Value;
            var lineColor = AreWeThereYet.Instance.Settings.AutoPilot.Visual.TaskLineColor.Value;
            if (cachedTasks?.Count > 0)
            {
                var taskTypeName = cachedTasks[0].Type == TaskNodeType.MercenaryOptIn ? "Mercenary OPT-IN" : cachedTasks[0].Type.ToString();
                AreWeThereYet.Instance.Graphics.DrawText(
                    "Current Task: " + taskTypeName,
                    new Vector2(500, 180));
                foreach (var task in cachedTasks.TakeWhile(task => task?.WorldPosition != null))
                {
                    if (taskCount == 0)
                    {
                        AreWeThereYet.Instance.Graphics.DrawLine(
                            Helper.WorldToValidScreenPosition(AreWeThereYet.Instance.playerPosition),
                            Helper.WorldToValidScreenPosition(task.WorldPosition), lineWidth, lineColor);
                        dist = Vector3.Distance(AreWeThereYet.Instance.playerPosition, task.WorldPosition);
                    }
                    else
                    {
                        AreWeThereYet.Instance.Graphics.DrawLine(Helper.WorldToValidScreenPosition(task.WorldPosition),
                            Helper.WorldToValidScreenPosition(cachedTasks[taskCount - 1].WorldPosition), lineWidth, lineColor);
                    }

                    taskCount++;
                }
            }
            if (AreWeThereYet.Instance.localPlayer != null)
            {
                var targetDist = Vector3.Distance(AreWeThereYet.Instance.playerPosition, lastTargetPosition);
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"Follow Enabled: {AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value}", new System.Numerics.Vector2(500, 120));
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"Task Count: {taskCount:D} Next WP Distance: {dist:F} Target Distance: {targetDist:F}",
                    new System.Numerics.Vector2(500, 140));
            }
        }
        catch (Exception)
        {
        }

        AreWeThereYet.Instance.Graphics.DrawText("AutoPilot: Active", new System.Numerics.Vector2(350, 120));
        AreWeThereYet.Instance.Graphics.DrawText("Coroutine: " + (autoPilotCoroutine.Running ? "Active" : "Dead"), new System.Numerics.Vector2(350, 140));
        AreWeThereYet.Instance.Graphics.DrawText("Leader: " + "[ " + AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value + " ] " + (followTarget != null ? "Found" : "Null"), new System.Numerics.Vector2(500, 160));
        AreWeThereYet.Instance.Graphics.DrawLine(new System.Numerics.Vector2(490, 110), new System.Numerics.Vector2(490, 210), 1, Color.White);

        // --- WATCHDOG & RESTART LOGIC ---
        // Check if the coroutine is null (hasn't started yet) or if it has stopped running.
        if (autoPilotCoroutine == null || !autoPilotCoroutine.Running)
        {
            // Log a message so you know a restart is happening.
            AreWeThereYet.Instance.LogMessage("[AutoPilot] Coroutine is dead or not started. Restarting...");

            // Call your existing method to start it.
            StartCoroutine();
        }
        // --- END OF WATCHDOG LOGIC ---    
    }
}
