using System;

namespace NobleMod;

/// <summary>
/// Niveau courant du run (lecture vanilla via <see cref="RunManager"/>).
/// </summary>
internal static class RunLevel
{
    /// <summary>
    /// Retourne 1 pour le premier niveau, etc., ou 0 si indisponible.
    /// </summary>
    internal static int TryGetCurrentLevelNumber()
    {
        try
        {
            if (RunManager.instance == null)
                return 0;
            return RunManager.instance.levelsCompleted + 1;
        }
        catch
        {
            return 0;
        }
    }
}
