using System;
using System.IO;
using UnityEditor;

namespace MakeGamesPlay.WebBuildHost.Editor
{
    /// <summary>
    /// Touches the Build Host heartbeat file every ~30s while the editor is open.
    /// The detached server self-terminates if this file goes stale for more than
    /// 10 minutes (Unity closed), so a forgotten host doesn't keep serving and
    /// holding a Cloudflare tunnel forever.
    ///
    /// Runs independent of the window (InitializeOnLoad), so closing the Build Host
    /// window keeps the server alive while Unity itself is open - the heartbeat
    /// tracks the editor, not the window. Only writes while a server is running
    /// (its status file exists), so projects that never use the host stay untouched.
    /// </summary>
    [InitializeOnLoad]
    static class WebBuildHostHeartbeat
    {
        const double IntervalSec = 30;
        static double lastBeat;

        static WebBuildHostHeartbeat()
        {
            EditorApplication.update += Beat;
        }

        static void Beat()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - lastBeat < IntervalSec) return;
            lastBeat = now;
            try
            {
                if (!File.Exists(WebBuildHostWindow.StatusFilePath())) return; // no server running
                File.WriteAllText(WebBuildHostWindow.HeartbeatFilePath(),
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            }
            catch
            {
                // Temp dir unavailable; the server falls back to its startup grace
                // window and will exit if the heartbeat stays stale.
            }
        }
    }
}
