using System;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace NobleMod;

internal static class ClipFileLoader
{
    public static AudioClip LoadFromPath(string path)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant();
        if (ext != ".ogg")
            return null;

        using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path), AudioType.OGGVORBIS))
        {
            var op = request.SendWebRequest();
            while (!op.isDone)
            {
            }

            if (request.result != UnityWebRequest.Result.Success)
                return null;

            return DownloadHandlerAudioClip.GetContent(request);
        }
    }
}
