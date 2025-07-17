// PartyElement.cs
using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;

namespace AreWeThereYet;

public static class PartyElements
{
    public static List<string> ListOfPlayersInParty(int child)
    {
        var playersInParty = new List<string>();

        try
        {
            var baseWindow = AreWeThereYet.Instance.GameController.IngameState.IngameUi.Children[child];
            if (baseWindow != null)
            {
                var partyList = baseWindow.Children[0]?.Children[0]?.Children;
                playersInParty.AddRange(from player in partyList where player != null && player.ChildCount >= 3 select player.Children[0].Text);
            }

        }
        catch (Exception)
        {
            // ignored
        }

        return playersInParty;
    }

    public static List<PartyElementWindow> GetPlayerInfoElementList()
    {
        var playersInParty = new List<PartyElementWindow>();

        try
        {
            var baseWindow = AreWeThereYet.Instance.GameController?.IngameState?.IngameUi?.PartyElement;
            if (baseWindow?.Children?.Count > 0)
            {
                var firstChild = baseWindow.Children[0];
                if (firstChild?.Children?.Count > 0)
                {
                    var secondChild = firstChild.Children[0];
                    var partElementList = secondChild?.Children;
                    
                    if (partElementList != null)
                    {
                        foreach (var partyElement in partElementList)
                        {
                            if (partyElement?.Children?.Count > 0)
                            {
                                var playerName = partyElement.Children[0]?.Text;
                                
                                // Skip if no valid player name
                                if (string.IsNullOrEmpty(playerName))
                                    continue;
                                
                                var newElement = new PartyElementWindow
                                {
                                    PlayerName = playerName,
                                    Element = partyElement,
                                    // More robust zone name and teleport button detection
                                    ZoneName = GetZoneNameFromPartyElement(partyElement),
                                    TpButton = GetTpButtonFromPartyElement(partyElement)
                                };

                                playersInParty.Add(newElement);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            AreWeThereYet.Instance.LogError($"GetPlayerInfoElementList failed: {e.Message}", 5);
        }

        return playersInParty;
    }
    
    private static string GetZoneNameFromPartyElement(Element partyElement)
    {
        try
        {
            if (partyElement?.Children?.Count >= 3)
            {
                // If there are 4 children, the zone name is in child 2
                if (partyElement.ChildCount == 4)
                {
                    return partyElement.Children[2]?.Text ?? AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName;
                }
            }
            
            // Default to current area if we can't determine zone from UI
            return AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName ?? "";
        }
        catch
        {
            return AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName ?? "";
        }
    }
    
    private static Element GetTpButtonFromPartyElement(Element partyElement)
    {
        try
        {
            if (partyElement?.Children?.Count >= 3)
            {
                // The teleport button is in child 3 if there are 4 children, child 2 if there are 3
                int tpButtonIndex = partyElement.ChildCount == 4 ? 3 : 2;
                if (tpButtonIndex < partyElement.Children.Count)
                {
                    return partyElement.Children[tpButtonIndex];
                }
            }
            
            return new Element(); // Return empty element if can't find button
        }
        catch
        {
            return new Element(); // Return empty element on error
        }
    }
}

public class PartyElementWindow
{
    public string PlayerName { get; set; } = string.Empty;
    public PlayerData Data { get; set; } = new PlayerData();
    public Element Element { get; set; } = new Element();
    public string ZoneName { get; set; } = string.Empty;
    public Element TpButton { get; set; } = new Element();

    public override string ToString()
    {
        return $"PlayerName: {PlayerName}, Data.PlayerEntity.Distance: {Data.PlayerEntity.Distance(Entity.Player).ToString() ?? "Null"}";
    }
}

public class PlayerData
{
    public Entity PlayerEntity { get; set; } = null;
}
