using UnityEngine;

namespace BigSteps;

// cached handles to the local bird
static class Game
{
    internal static PlayerCharacter Pc;
    internal static Camera Cam;

    internal static void Refresh()
    {
        if (Pc == null || Pc.WasCollected) { Pc = FindLocal(); Cam = null; }
        if (Pc != null && Cam == null)
        {
            try { Cam = Pc.cameraMinder.playerCameraReferences.playerCamera; } catch { }
        }
    }

    static PlayerCharacter FindLocal()
    {
        var all = PlayerCharacter.allPlayerCharacters;
        if (all == null) return null;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p != null && p.isLocalPlayer) return p;
        }
        return null;
    }

    // rewired shuts down before our updates stop on quit: reading input then logs an error every frame
    internal static bool InputUp { get { try { return Rewired.ReInput.isReady; } catch { return false; } } }

    internal static bool IsLocal(PlayerCharacter p) => p != null && Pc != null && p.Pointer == Pc.Pointer;
}
