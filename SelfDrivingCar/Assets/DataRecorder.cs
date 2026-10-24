using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

// Records training data while you drive manually.
// Press R to start / stop recording. Each start = one new "episode" (lap).
// Output (next to the Unity project folder):
//   <project>/../data/images/img_<episode>_<frame>.jpg   (320x160 DashCam frames)
//   <project>/../data/labels.csv   columns: image,steering,speed,episode
// Steering convention: -1 = full left, +1 = full right (same as CarController).
[RequireComponent(typeof(CarController))]
public class DataRecorder : MonoBehaviour
{
    [Header("Setup")]
    public Camera dashCam;                 // auto-found if left empty
    public int width = 320;
    public int height = 160;
    public float captureInterval = 0.1f;   // seconds between frames (0.1 = 10 fps)
    public float minSpeed = 0.5f;          // don't record while (almost) stopped
    public string folderName = "data";

    CarController car;
    RenderTexture rt;
    Texture2D tex;
    StreamWriter csv;
    string imageDir, csvPath;
    bool recording;
    float timer;
    int episode, frameInEpisode, totalFrames;

    void Start()
    {
        car = GetComponent<CarController>();
        if (dashCam == null) dashCam = GetComponentInChildren<Camera>();
        if (dashCam == null)
        {
            Debug.LogError("DataRecorder: no camera found. Put the DashCam under Car or assign it.");
            enabled = false;
            return;
        }

        rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        tex = new Texture2D(width, height, TextureFormat.RGB24, false);

        string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", folderName));
        imageDir = Path.Combine(root, "images");
        Directory.CreateDirectory(imageDir);
        csvPath = Path.Combine(root, "labels.csv");
        episode = NextEpisodeId();
        Debug.Log("DataRecorder ready. Saving to: " + root);
    }

    int NextEpisodeId()
    {
        if (!File.Exists(csvPath)) return 1;
        int max = 0;
        foreach (var line in File.ReadLines(csvPath))
        {
            var p = line.Split(',');
            if (p.Length >= 4 && int.TryParse(p[3], out int e) && e > max) max = e;
        }
        return max + 1;
    }

    void Update()
    {
        var k = Keyboard.current;
        if (k == null) return;

        if (k.rKey.wasPressedThisFrame)
        {
            if (recording) StopRecording(); else StartRecording();
        }

        if (!recording) return;

        timer += Time.deltaTime;
        if (timer >= captureInterval && car.Speed >= minSpeed)
        {
            timer = 0f;
            Capture();
        }
    }

    void StartRecording()
    {
        bool newFile = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
        csv = new StreamWriter(csvPath, true) { AutoFlush = true };
        if (newFile) csv.WriteLine("image,steering,speed,episode");
        frameInEpisode = 0;
        timer = 0f;
        recording = true;
        Debug.Log("Recording episode " + episode);
    }

    void StopRecording()
    {
        recording = false;
        if (csv != null) { csv.Close(); csv = null; }
        Debug.Log($"Stopped episode {episode}: {frameInEpisode} frames. Total this session: {totalFrames}");
        episode++;
    }

    void Capture()
    {
        // Render the DashCam into a small texture, then read it back.
        var prevTarget = dashCam.targetTexture;
        dashCam.targetTexture = rt;
        dashCam.Render();

        var prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        RenderTexture.active = prevActive;
        dashCam.targetTexture = prevTarget;

        string name = $"img_{episode:D3}_{frameInEpisode:D5}.jpg";
        File.WriteAllBytes(Path.Combine(imageDir, name), tex.EncodeToJPG(90));

        string steer = car.Steering.ToString("F4", CultureInfo.InvariantCulture);
        string speed = car.Speed.ToString("F3", CultureInfo.InvariantCulture);
        csv.WriteLine($"{name},{steer},{speed},{episode}");

        frameInEpisode++;
        totalFrames++;
    }

    void OnGUI()
    {
        string msg = recording
            ? $"● REC  episode {episode}  frames {frameInEpisode}  (press R to stop)"
            : $"Not recording  (press R to start)  episodes saved so far: {episode - 1}";
        GUI.Label(new Rect(12, 10, 700, 24), msg);
    }

    void OnDestroy()
    {
        if (csv != null) csv.Close();
        if (rt != null) rt.Release();
    }
}
