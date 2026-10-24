using System.IO;
using UnityEditor;
using UnityEngine;

// Lets an external script start/stop Play mode without keyboard focus:
// write PLAY or STOP to  <project>/../data/editor_cmd.txt  and it executes next editor tick.
[InitializeOnLoad]
static class EditorWatch
{
    static readonly string file =
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "data", "editor_cmd.txt"));

    static EditorWatch() { EditorApplication.update += Tick; Debug.Log("EditorWatch v2 loaded"); }

    static void Tick()
    {
        // Keep the player loop ticking even when the editor throttles an unfocused window.
        if (EditorApplication.isPlaying) UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        if (!File.Exists(file)) return;
        string c = "";
        try { c = File.ReadAllText(file).Trim().ToUpperInvariant(); File.Delete(file); }
        catch (IOException) { return; }   // still being written; try again next tick
        if (c == "PLAY") EditorApplication.isPlaying = true;
        else if (c == "STOP") EditorApplication.isPlaying = false;
        else if (c == "RECOMPILE") UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
        else if (c == "BG")
        {
            UnityEditor.PlayerSettings.runInBackground = true;
            Debug.Log("EditorWatch: runInBackground=" + UnityEditor.PlayerSettings.runInBackground);
        }
        else if (c == "TIMEON")
        {
            Time.timeScale = 1f;
            Debug.Log("EditorWatch: timeScale set to " + Time.timeScale + ", paused=" + EditorApplication.isPaused);
        }
        else if (c == "UNFOCUSED")
        {
            // Game view mode "Play Unfocused" (Unity 6) via reflection - keeps the player
            // loop running when the editor window is not focused.
            var t = System.Type.GetType("UnityEditor.PlayModeWindow,UnityEditor");
            var m = t == null ? null : t.GetMethod("SetPlayModeWindowMode",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var et = t == null ? null : t.GetNestedType("PlayModeMode");
            if (m != null && et != null) m.Invoke(null, new object[] { System.Enum.Parse(et, "PlayModeUnfocused") });
            Debug.Log("EditorWatch: PlayModeUnfocused requested (api found=" + (m != null) + ")");
        }
    }
}
