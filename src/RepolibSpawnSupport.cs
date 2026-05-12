using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using REPOLib.Modules;

namespace NobleMod;

/// <summary>
/// REPOLib est une dépendance BepInEx dure ; ce helper reste défensif si le plugin n'est pas dans la chaîne.
/// Avec <c>Assembly-CSharp</c> en ref de build, <see cref="Enemies.AllEnemies"/> est utilisable directement.
/// </summary>
internal static class RepolibSpawnSupport
{
    internal static bool IsRepolibLoaded =>
        Chainloader.PluginInfos != null &&
        Chainloader.PluginInfos.TryGetValue(REPOLib.MyPluginInfo.PLUGIN_GUID, out var p) &&
        p is { Instance: not null };

    /// <summary>
    /// Setups ennemis via <see cref="Enemies.AllEnemies"/>, filtrés par le type du résultat de <c>EnemyDirector.GetEnemy</c>.
    /// </summary>
    internal static bool TryGetAllEnemySetupsAsObjects(Type expectedSetupType, out List<object> setups)
    {
        setups = null;
        if (!IsRepolibLoaded || expectedSetupType == null)
            return false;

        try
        {
            var all = Enemies.AllEnemies;
            if (all == null)
                return false;

            var list = new List<object>();
            foreach (var entry in all)
            {
                if (entry == null)
                    continue;
                if (!expectedSetupType.IsInstanceOfType(entry))
                    continue;
                if (!list.Contains(entry))
                    list.Add(entry);
            }

            setups = list;
            return list.Count > 0;
        }
        catch
        {
            setups = null;
            return false;
        }
    }
}
