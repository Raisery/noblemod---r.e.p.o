using System;
using System.IO;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using REPOLib.Modules;
using UnityEngine;

namespace NobleMod.Multiplayer;

/// <summary>
/// Pool d'entiers partage entre tous les joueurs (Photon room) pour rendre les tirages aleatoires
/// SoundAPI / NobleMod coherents en multijoueur. Le master tire le pool et le diffuse via REPOLib
/// <see cref="NetworkedEvent"/>. Chaque client indexe ce pool via une cle stable
/// (cf. <see cref="NobleModSoundSyncKey"/>) ⇒ pas de communication par tirage.
///
/// Architecture : polling via Harmony postfix sur <c>Photon.Pun.PhotonHandler.LateUpdate</c> (cf.
/// <see cref="Patches.PhotonHandlerSoundSyncTickPatch"/>) — <see cref="BepInEx.BaseUnityPlugin.Update"/> n'est
/// pas fiable dans ce contexte BepInEx / REPO (aucun tick observé en log). <c>PhotonHandler</c> n'expose pas
/// de <c>Update</c> déclaré mais fournit <c>LateUpdate</c> chaque frame. Couts negligeables
/// (throttle ~1 Hz dans <see cref="DriverTick"/>).
/// </summary>
internal static class NobleModSoundSyncNet
{
    /// <summary>Code de version du protocole serialise. A bumper si on change le format.</summary>
    const byte ProtocolVersion = 1;

    /// <summary>Petite tolerance pour eviter un refresh agressif si plusieurs sources demandent en simultane.</summary>
    const float RefreshDebounceSeconds = 0.5f;

    /// <summary>Frequence du polling de re-broadcast par le master (player count change, master switch, etc.).</summary>
    const float MasterPollingIntervalSeconds = 1.0f;

    /// <summary>Frequence d'un log d'etat (heartbeat) pour diagnostic, meme hors room. Limite la verbosite.</summary>
    const float HeartbeatLogIntervalSeconds = 15f;

    static int[] _pool;
    static long _poolGeneration;
    static float _localPoolBornAtRealtime = -1f;
    static bool _initialized;
    static NetworkedEvent _event;
    static int _lastBroadcastFrame = int.MinValue;

    // Snapshot de l'etat reseau pour detecter player join / master switch / left room sans callbacks Photon.
    static bool _lastInRoom;
    static bool _lastIsMaster;
    static int _lastPlayerCount;
    static float _nextMasterPollAt;
    static float _nextHeartbeatLogAt;
    static bool _firstTickLogged;

    /// <summary>
    /// Options Photon utilisees pour le broadcast du pool. <see cref="EventCaching.AddToRoomCacheGlobal"/>
    /// fait que les nouveaux joueurs recoivent automatiquement les events cached a leur join — critique pour
    /// que l'ami qui join apres le master ne rate pas le pool. <see cref="ReceiverGroup.All"/> = master inclus
    /// (le master applique aussi en local).
    /// </summary>
    static readonly RaiseEventOptions PoolRaiseOptions = new()
    {
        Receivers = ReceiverGroup.All,
        CachingOption = EventCaching.AddToRoomCacheGlobal,
    };

    /// <summary>Vrai si l'instance est en room Photon, multijoueur (&gt; 1 joueur). N'utilise PAS
    /// <see cref="PhotonNetwork.IsConnectedAndReady"/> qui peut etre faux meme apres InRoom selon les versions
    /// Photon / le wrapper Steam de R.E.P.O.</summary>
    internal static bool IsActiveInRoom
    {
        get
        {
            try
            {
                return PhotonNetwork.InRoom
                    && PhotonNetwork.CurrentRoom != null
                    && PhotonNetwork.CurrentRoom.PlayerCount > 1;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static int CurrentPoolSize => _pool?.Length ?? 0;
    internal static long CurrentPoolGeneration => _poolGeneration;

    internal static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

        try
        {
            _event = new NetworkedEvent("NobleMod:SoundSyncPool", OnEventReceived);
            Plugin.Log.LogInfo($"[NobleMod] SoundSyncNet : NetworkedEvent enregistre (eventCode={_event.EventCode}). IMPORTANT : les deux joueurs DOIVENT avoir le meme eventCode pour que la sync fonctionne.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[NobleMod] SoundSyncNet : echec enregistrement NetworkedEvent : {ex}");
            _event = null;
        }

        // Le polling est declenche par PhotonHandlerSoundSyncTickPatch (Harmony sur Photon.Pun.PhotonHandler.LateUpdate).
    }

    /// <summary>
    /// Tente de resoudre un entier deterministe a partir de <paramref name="seedKey"/>.
    /// Retourne <c>false</c> si la sync est OFF, le pool absent, ou pas en multi : l'appelant fait son fallback.
    /// </summary>
    internal static bool TryLookup(uint seedKey, out int value)
    {
        value = 0;
        if (ModConfig.EnableMultiplayerSoundSync == null || !ModConfig.EnableMultiplayerSoundSync.Value)
            return false;
        if (!IsActiveInRoom)
            return false;
        var pool = _pool;
        if (pool == null || pool.Length == 0)
            return false;
        var idx = (int)(seedKey % (uint)pool.Length);
        value = pool[idx];
        return true;
    }

    /// <summary>
    /// Variante helper : retourne une valeur uniforme dans <c>[minInclusive, maxInclusive]</c> derivee du pool.
    /// </summary>
    internal static bool TryRange(uint seedKey, int minInclusive, int maxInclusive, out int value)
    {
        value = minInclusive;
        if (maxInclusive < minInclusive)
            return false;
        if (!TryLookup(seedKey, out var raw))
            return false;
        var span = maxInclusive - minInclusive + 1;
        var mod = (int)((uint)raw % (uint)span);
        value = minInclusive + mod;
        return true;
    }

    /// <summary>
    /// Appel periodique par le driver. Polling complet : detection in-room / out-of-room / master / player count,
    /// refresh par intervalle, fallback no-op si Photon pas pret. Tout est entoure de try/catch.
    /// </summary>
    internal static void DriverTick()
    {
        try
        {
            DriverTickInternal();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[NobleMod] SoundSyncNet : exception DriverTick : {ex}");
        }
    }

    static void DriverTickInternal()
    {
        if (!_firstTickLogged)
        {
            _firstTickLogged = true;
            Plugin.Log.LogInfo("[NobleMod] SoundSyncNet : driver PhotonHandler.LateUpdate tourne (premier tick).");
        }

        if (ModConfig.EnableMultiplayerSoundSync == null || !ModConfig.EnableMultiplayerSoundSync.Value)
            return;

        var now = Time.realtimeSinceStartup;
        if (now < _nextMasterPollAt)
            return;
        _nextMasterPollAt = now + MasterPollingIntervalSeconds;

        bool inRoom;
        bool isMaster;
        int playerCount;
        try
        {
            inRoom = PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null;
            isMaster = inRoom && PhotonNetwork.IsMasterClient;
            playerCount = inRoom ? PhotonNetwork.CurrentRoom.PlayerCount : 0;
        }
        catch (Exception exPhoton)
        {
            Plugin.Log.LogWarning($"[NobleMod] SoundSyncNet : exception lecture etat Photon (poll skip) : {exPhoton.Message}");
            return;
        }

        // Transition out-of-room : reset pool pour que le prochain master broadcast un pool neuf.
        if (_lastInRoom && !inRoom)
        {
            _pool = null;
            _localPoolBornAtRealtime = -1f;
            Plugin.Log.LogInfo("[NobleMod] SoundSyncNet : pool efface (left room detecte par polling).");
        }

        // Transition in-room (premier polling apres join) : on logue pour visibilite.
        if (!_lastInRoom && inRoom)
        {
            Plugin.Log.LogInfo($"[NobleMod] SoundSyncNet : in-room detecte (master={isMaster}, players={playerCount}).");
        }

        var becameMaster = isMaster && !_lastIsMaster;
        var playerJoined = inRoom && playerCount > _lastPlayerCount;
        var playerLeft = inRoom && playerCount < _lastPlayerCount;
        if (playerJoined && _lastPlayerCount > 0)
            Plugin.Log.LogInfo($"[NobleMod] SoundSyncNet : player joined ({_lastPlayerCount} -> {playerCount}, master={isMaster}).");
        if (playerLeft && _lastPlayerCount > 0)
            Plugin.Log.LogInfo($"[NobleMod] SoundSyncNet : player left ({_lastPlayerCount} -> {playerCount}, master={isMaster}).");
        if (becameMaster)
            Plugin.Log.LogInfo($"[NobleMod] SoundSyncNet : devenu master (players={playerCount}).");
        _lastInRoom = inRoom;
        _lastIsMaster = isMaster;
        _lastPlayerCount = playerCount;

        // Heartbeat diagnostique : etat complet toutes les 15s (limite la verbosite).
        if (now >= _nextHeartbeatLogAt)
        {
            _nextHeartbeatLogAt = now + HeartbeatLogIntervalSeconds;
            Plugin.Log.LogInfo($"[NobleMod] SoundSyncNet : heartbeat inRoom={inRoom} master={isMaster} players={playerCount} hasPool={_pool != null} poolGen={_poolGeneration}.");
        }

        // Conditions pour broadcast :
        // 1. On est master en room (PlayerCount >= 1, meme tout seul).
        //    L'event est mis en cache Photon (AddToRoomCacheGlobal), donc tout joueur qui join ulterieurement
        //    recoit le pool immediatement, sans race condition avec PlayerCount.
        // 2. ET (pas de pool encore, OU on vient de devenir master, OU un joueur vient de rejoindre, OU interval ecoule).
        if (!isMaster)
            return;

        if (_pool == null)
        {
            MasterBroadcastFreshPoolIfMaster("driver:no_pool");
            return;
        }

        if (becameMaster)
        {
            MasterBroadcastFreshPoolIfMaster("driver:became_master");
            return;
        }

        if (playerJoined)
        {
            MasterBroadcastFreshPoolIfMaster("driver:player_joined");
            return;
        }

        var intervalSec = ModConfig.MultiplayerSoundPoolRefreshIntervalSeconds?.Value ?? 300;
        if (intervalSec <= 0)
            return;
        if (_localPoolBornAtRealtime < 0)
            return;
        if (now - _localPoolBornAtRealtime + RefreshDebounceSeconds < intervalSec)
            return;
        MasterBroadcastFreshPoolIfMaster("driver:interval_elapsed");
    }

    /// <summary>
    /// Le master genere un nouveau pool et le diffuse a tout le monde (master inclus en local).
    /// No-op si pas master ou pas en room (les non-masters attendent que le master diffuse).
    /// </summary>
    internal static void MasterBroadcastFreshPoolIfMaster(string reason)
    {
        if (ModConfig.EnableMultiplayerSoundSync == null || !ModConfig.EnableMultiplayerSoundSync.Value)
            return;
        if (_event == null)
            return;
        try
        {
            if (!PhotonNetwork.IsConnectedAndReady || !PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient)
                return;
            if (Time.frameCount == _lastBroadcastFrame)
                return;
            _lastBroadcastFrame = Time.frameCount;

            var size = Mathf.Clamp(ModConfig.MultiplayerSoundPoolSize?.Value ?? 256, 16, 4096);
            var generation = unchecked(_poolGeneration + 1);
            var pool = new int[size];
            for (var i = 0; i < size; i++)
                pool[i] = UnityEngine.Random.Range(int.MinValue, int.MaxValue);

            var payload = SerializePool(pool, generation);
            ApplyPool(pool, generation);

            _event.RaiseEvent(payload, PoolRaiseOptions, SendOptions.SendReliable);
            Plugin.Log.LogInfo($"[NobleMod] SoundSyncNet : master broadcast pool generation={generation} size={size} bytes={payload.Length} eventCode={_event.EventCode} (reason={reason}, cache=AddToRoomCacheGlobal).");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[NobleMod] SoundSyncNet : exception MasterBroadcast : {ex}");
        }
    }

    static void OnEventReceived(EventData eventData)
    {
        try
        {
            var raw = eventData.CustomData;
            if (raw is not byte[] bytes)
            {
                Plugin.Log.LogWarning($"[NobleMod] SoundSyncNet : event recu mais CustomData n'est pas byte[] (type={raw?.GetType().FullName ?? "null"}).");
                return;
            }

            if (!TryDeserializePool(bytes, out var pool, out var generation))
            {
                Plugin.Log.LogWarning($"[NobleMod] SoundSyncNet : echec deserialisation pool ({bytes.Length} bytes).");
                return;
            }

            // Ignorer les generations strictement plus anciennes que celle deja en place (re-ordonnancement).
            if (_pool != null && generation < _poolGeneration)
            {
                Plugin.Log.LogInfo($"[NobleMod] SoundSyncNet : ignore pool generation={generation} (< current {_poolGeneration}).");
                return;
            }

            ApplyPool(pool, generation);
            Plugin.Log.LogInfo($"[NobleMod] SoundSyncNet : pool recu generation={generation} size={pool.Length} (sender={eventData.Sender}, eventCode={eventData.Code}).");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[NobleMod] SoundSyncNet : erreur OnEventReceived : {ex}");
        }
    }

    static void ApplyPool(int[] pool, long generation)
    {
        _pool = pool;
        _poolGeneration = generation;
        _localPoolBornAtRealtime = Time.realtimeSinceStartup;
    }

    static byte[] SerializePool(int[] pool, long generation)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(ProtocolVersion);
        bw.Write(generation);
        bw.Write(pool.Length);
        for (var i = 0; i < pool.Length; i++)
            bw.Write(pool[i]);
        bw.Flush();
        return ms.ToArray();
    }

    static bool TryDeserializePool(byte[] bytes, out int[] pool, out long generation)
    {
        pool = null;
        generation = 0;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var br = new BinaryReader(ms);
            var version = br.ReadByte();
            if (version != ProtocolVersion)
            {
                Plugin.Log.LogWarning($"[NobleMod] SoundSyncNet : version protocol inconnue {version}, ignore.");
                return false;
            }

            generation = br.ReadInt64();
            var size = br.ReadInt32();
            if (size <= 0 || size > 4096)
                return false;
            pool = new int[size];
            for (var i = 0; i < size; i++)
                pool[i] = br.ReadInt32();
            return true;
        }
        catch
        {
            pool = null;
            return false;
        }
    }

}

