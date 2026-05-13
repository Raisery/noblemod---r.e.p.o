using HarmonyLib;
using NobleMod.Multiplayer;
using Photon.Pun;

namespace NobleMod.Patches;

/// <summary>
/// BepInEx n'appelle pas toujours <see cref="Plugin.Update"/> sur <see cref="BepInEx.BaseUnityPlugin"/>
/// (comportement observé en jeu : aucun log depuis <c>DriverTick</c>). PUN fournit
/// <see cref="PhotonHandler"/> avec un <c>LateUpdate</c> (pas de <c>Update</c> déclaré sur ce type) exécuté
/// chaque frame tant que le réseau est actif ; on y accroche le polling réseau léger (déjà limité à ~1 Hz
/// dans <see cref="NobleModSoundSyncNet.DriverTick"/>).
/// </summary>
[HarmonyPatch(typeof(PhotonHandler), "LateUpdate")]
internal static class PhotonHandlerSoundSyncTickPatch
{
    static void Postfix() => NobleModSoundSyncNet.DriverTick();
}
