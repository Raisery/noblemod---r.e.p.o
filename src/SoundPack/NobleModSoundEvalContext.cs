using UnityEngine;

namespace NobleMod.SoundPack;

/// <summary>
/// Pendant <c>AudioSourceAdditionalData.Update</c> (SoundAPI, chemins <c>update_every_frame</c>),
/// expose la source audio pour les conditions NobleMod qui doivent lire <see cref="AudioSource.time"/>.
/// </summary>
internal static class NobleModSoundEvalContext
{
    static int _depth;
    static AudioSource _current;

    internal static void Enter(AudioSource source)
    {
        _current = source;
        _depth++;
    }

    internal static void Exit()
    {
        _depth--;
        if (_depth <= 0)
        {
            _depth = 0;
            _current = null;
        }
    }

    /// <summary>Non-null uniquement pendant l'appel SoundAPI à <c>Update</c> sur une source suivie.</summary>
    internal static AudioSource CurrentAudioSource => _depth > 0 ? _current : null;
}
