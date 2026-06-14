using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MakeGamesPlay.WebBuildHost.Editor
{
    /// <summary>
    /// After a WebGL build, if no Build Host window is open, offers to open one so
    /// the user can serve the fresh build to a phone or browser. Skipped in batch
    /// mode (CI) and once the user picks "Don't ask again" (stored per project).
    /// </summary>
    public class WebBuildHostPostBuild : IPostprocessBuildWithReport
    {
        const string SuppressKeyPrefix = "WebBuildHost.PostBuildPrompt.Suppress.";
        static string SuppressKey => SuppressKeyPrefix + PlayerSettings.productGUID;

        // Late, so the WebAR (or any other) build callbacks run first.
        public int callbackOrder => 1000;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report == null || report.summary.platform != BuildTarget.WebGL) return;
            if (Application.isBatchMode) return;                    // never prompt in CI
            if (EditorPrefs.GetBool(SuppressKey, false)) return;    // user opted out
            if (EditorWindow.HasOpenInstances<WebBuildHostWindow>()) return;

            // Defer so the dialog opens after the build pipeline finishes unwinding.
            EditorApplication.delayCall += Prompt;
        }

        static void Prompt()
        {
            if (EditorWindow.HasOpenInstances<WebBuildHostWindow>()) return;

            int choice = EditorUtility.DisplayDialogComplex(
                "WebGL Build Host",
                "Build complete.\n\nOpen the WebGL Build Host to serve this build to a phone " +
                "or browser over HTTPS?",
                "Open Build Host",   // 0
                "Not now",           // 1
                "Don't ask again");  // 2

            if (choice == 0)
                EditorApplication.ExecuteMenuItem("Tools/WebGL Build Host");
            else if (choice == 2)
                EditorPrefs.SetBool(SuppressKey, true);
        }
    }
}
