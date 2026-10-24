using System.IO;
using UnityEngine;

// Captures the Game view to PNG frames so the runs can be assembled into MP4 videos.
//   V = start / stop capturing (about 20 frames per second)
// Frames go to  <project>/../recordings/run_<timestamp>/frame_000001.png ...
// Added to the scene automatically when Play starts, so no scene setup is needed.
public class RunRecorder : MonoBehaviour
{
    public float fps = 20f;
    public string folderName = "recordings";

    bool recording;
    string relDir, absDir, recFile;
    int frame;
    float nextCap;

    void Start()
    {
        // recording runs while data/rec.txt exists, so runs can be started remotely
        recFile = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "data", "rec.txt"));
    }

    void Update()
    {
        var k = UnityEngine.InputSystem.Keyboard.current;
        if (k != null && k.vKey.wasPressedThisFrame) recording = !recording;

        bool want = recording || File.Exists(recFile);
        if (want && absDir == null) StartRec();
        if (!want && absDir != null) { absDir = null; Debug.Log("RunRecorder: stopped at " + frame + " frames"); }

        if (want && Time.unscaledTime >= nextCap)
        {
            nextCap = Time.unscaledTime + 1f / fps;
            ScreenCapture.CaptureScreenshot(relDir + "/frame_" + (frame + 1).ToString("D6") + ".png");
            frame++;
        }
    }

    void StartRec()
    {
        string name = "run_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        relDir = Path.Combine("..", folderName, name);          // relative to the Unity project folder
        absDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", folderName, name));
        Directory.CreateDirectory(absDir);
        frame = 0;
        Debug.Log("RunRecorder: recording to " + absDir);
    }

    void OnGUI()
    {
        GUI.Label(new Rect(12, 144, 800, 24),
            recording ? $"● REC  {frame} frames  (press V to stop)" : "Press V to record video frames");
    }
}

static class RunRecorderBoot
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Object.FindFirstObjectByType<RunRecorder>() == null)
            new GameObject("RunRecorder").AddComponent<RunRecorder>();
    }
}
