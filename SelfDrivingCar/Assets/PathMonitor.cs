using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

// Records the road loop once (press K), then measures how far the car is from it.
// If the car gets farther than "crashDistance" from the loop it counts as a CRASH:
// the car is frozen where it left the road and the event is written to data/runs.csv.
//   K = start / stop recording the loop path (drive ONE clean lap between the two presses)
[RequireComponent(typeof(CarController))]
public class PathMonitor : MonoBehaviour
{
    public float crashDistance = 4f;     // metres from the recorded loop = left the road
    public float recordSpacing = 1f;     // metres between recorded path points
    public string folderName = "data";
    public string runLabel = "A";

    public bool Crashed { get; private set; }
    public float CrossTrack { get; private set; }
    public bool HasPath { get { return pts.Count >= 4; } }
    public bool LapComplete { get; private set; }
    public float LapTime { get; private set; }

    CarController car;
    readonly List<Vector2> pts = new List<Vector2>();
    float[] turnAhead;                   // degrees of turning within the next lookAhead metres of each point
    bool[] visitedBucket; int visitedCount;
    int nearestIdx;
    Vector2 lapStartPos; float lapStartTime;
    bool recordingPath;
    Vector2 lastRec;
    string pathFile, runsFile;
    float runStart, nextLoadTry;
    GUIStyle style;

    void Start()
    {
        car = GetComponent<CarController>();
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", folderName));
        Directory.CreateDirectory(root);
        pathFile = Path.Combine(root, "path.csv");
        runsFile = Path.Combine(root, "runs.csv");
        LoadPath();
        lapStartPos = new Vector2(transform.position.x, transform.position.z);
        lapStartTime = Time.time;
        runStart = Time.time;
    }

    void LoadPath()
    {
        pts.Clear();
        if (!File.Exists(pathFile)) return;
        foreach (var line in File.ReadLines(pathFile))
        {
            var p = line.Split(',');
            if (p.Length < 2) continue;
            if (float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                pts.Add(new Vector2(x, z));
        }
        ComputeTurnAhead();
        visitedBucket = new bool[36];
        visitedCount = 0;
        Debug.Log("PathMonitor: loaded " + pts.Count + " path points");
    }

    // turnAhead[i] = total absolute turning (degrees) from point i over the next lookAhead metres.
    // Used by the speed governor: hold a lower speed approaching sharp corners.
    void ComputeTurnAhead()
    {
        int n = pts.Count;
        if (n < 8) { turnAhead = null; return; }
        int look = Mathf.Max(3, 25);   // ~25 m at the recorded 1 m spacing
        turnAhead = new float[n];
        // sliding sum of per-point turn angles over the next 'look' segments
        float[] segTurn = new float[n];
        for (int i = 0; i < n; i++)
        {
            Vector2 a = pts[i], b = pts[(i + 1) % n], c = pts[(i + 2) % n];
            Vector2 v1 = b - a, v2 = c - b;
            float ang = Vector2.SignedAngle(v1, v2);
            segTurn[i] = Mathf.Abs(ang);
        }
        for (int i = 0; i < n; i++)
        {
            float s = 0f;
            for (int k = 0; k < look; k++) s += segTurn[(i + k) % n];
            turnAhead[i] = s;
        }
    }

    // Speed the car should hold at its current position: baseSpeed on open road,
    // cornerSpeed while a sharp corner is within the next ~15 m.
    public float CornerSpeedLimit(float baseSpeed, float cornerSpeed)
    {
        if (turnAhead == null) return baseSpeed;
        return turnAhead[nearestIdx] > 25f ? cornerSpeed : baseSpeed;
    }

    void SavePath()
    {
        using (var w = new StreamWriter(pathFile, false))
        {
            w.WriteLine("x,z");
            foreach (var p in pts)
                w.WriteLine(p.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                            p.y.ToString("F3", CultureInfo.InvariantCulture));
        }
        Debug.Log("PathMonitor: saved " + pts.Count + " points to " + pathFile);
    }

    void Update()
    {
        var k = Keyboard.current;
        Vector2 p = new Vector2(transform.position.x, transform.position.z);

        // self-heal: if this instance lost its path (e.g. a mid-play script reload),
        // reload it from disk until it has one
        if (!HasPath && !recordingPath && Time.unscaledTime > nextLoadTry)
        {
            nextLoadTry = Time.unscaledTime + 3f;
            LoadPath();
        }

        if (k != null && k.kKey.wasPressedThisFrame)
        {
            if (!recordingPath) { pts.Clear(); pts.Add(p); lastRec = p; recordingPath = true; }
            else { recordingPath = false; SavePath(); }
        }

        if (recordingPath && Vector2.Distance(p, lastRec) >= recordSpacing)
        {
            pts.Add(p);
            lastRec = p;
        }

        if (HasPath && !recordingPath)
        {
            CrossTrack = DistanceToLoop(p, out nearestIdx);

            // progress bookkeeping for lap completion (36 buckets around the loop)
            int b = nearestIdx * 36 / pts.Count;
            if (!visitedBucket[b]) { visitedBucket[b] = true; visitedCount++; }
            if (!LapComplete && !Crashed && visitedCount >= 32 &&
                Vector2.Distance(p, lapStartPos) < 8f && Time.time - lapStartTime > 30f)
            {
                LapComplete = true;
                LapTime = Time.time - lapStartTime;
                LogEvent("LAP_COMPLETE", "lap_time_s=" + LapTime.ToString("F1", CultureInfo.InvariantCulture));
            }

            if (!Crashed && CrossTrack > crashDistance)
            {
                Crashed = true;
                car.frozen = true;
                LogEvent("CRASH", "left the road");
            }
        }
    }

    float DistanceToLoop(Vector2 p, out int bestIdx)
    {
        float best = float.MaxValue;
        bestIdx = 0;
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = pts[i], b = pts[(i + 1) % n];
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
            float d = Vector2.Distance(p, a + ab * t);
            if (d < best) { best = d; bestIdx = i; }
        }
        return best;
    }

    public void ResetRun()
    {
        Crashed = false;
        CrossTrack = 0f;
        LapComplete = false;
        LapTime = 0f;
        if (visitedBucket != null) System.Array.Clear(visitedBucket, 0, visitedBucket.Length);
        visitedCount = 0;
        lapStartPos = new Vector2(transform.position.x, transform.position.z);
        lapStartTime = Time.time;
        runStart = Time.time;
    }

    // Appends one row to data/runs.csv (open it in Excel after the demo).
    public void LogEvent(string evt, string note)
    {
        bool header = !File.Exists(runsFile) || new FileInfo(runsFile).Length == 0;
        using (var w = new StreamWriter(runsFile, true))
        {
            if (header) w.WriteLine("run,time_s,event,x,z,speed_mps,cross_track_m,note");
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1:F2},{2},{3:F2},{4:F2},{5:F2},{6:F2},{7}",
                runLabel, Time.time - runStart, evt,
                transform.position.x, transform.position.z, car.Speed, CrossTrack, note));
        }
    }

    void OnGUI()
    {
        if (style == null) { style = new GUIStyle(GUI.skin.label); style.fontSize = 16; }
        string s;
        if (recordingPath) s = "RECORDING PATH: " + pts.Count + " points  (drive one lap, press K to finish)";
        else if (HasPath) s = "Path: " + pts.Count + " points | distance from road centre: " + CrossTrack.ToString("F1") + " m (crash at " + crashDistance + " m)";
        else s = "No path yet. Press K at the start of a clean lap and K again at the end.";
        GUI.color = Color.white;
        GUI.Label(new Rect(12, 150, 1000, 26), s, style);

        if (LapComplete)
        {
            var good = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold };
            GUI.color = Color.green;
            GUI.Label(new Rect(12, 190, 900, 60), "LAP COMPLETE in " + LapTime.ToString("F1") + " s", good);
            GUI.color = Color.white;
        }

        if (Crashed)
        {
            var big = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold };
            GUI.color = Color.red;
            GUI.Label(new Rect(12, 190, 900, 60), "CRASH - car left the road (press X to reset)", big);
            GUI.color = Color.white;
        }
    }
}
