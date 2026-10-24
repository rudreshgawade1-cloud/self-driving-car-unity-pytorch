using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

// Lets the Python neural network drive the car, with a kill switch and a safety fail-safe.
//   G = hand control to the neural network        M = take manual control back
//   N = network ON / OFF (OFF simulates the network failing)
//   Tab = safety ON / OFF (ON = brake to a stop and hand over to manual if the network fails)
//   X = reset the car to its start position       1 / 2 / 3 = label the run A / B / C
// Start the Python server first:  python drive_server.py
[RequireComponent(typeof(CarController))]
public class NNDriver : MonoBehaviour
{
    public enum Mode { Manual, NNDriving, NNOffUnsafe, SafeStopping }

    [Header("Link to Python")]
    public string host = "127.0.0.1";
    public int port = 5005;
    public Camera dashCam;               // auto-found if empty
    public int width = 320;
    public int height = 160;
    public float sendInterval = 0.05f;   // 20 frames per second to the network
    public float staleTimeout = 0.5f;    // no reply for this long = network considered dead

    [Header("Driving")]
    public float cruiseSpeed = 5f;       // m/s held while the network drives
    public float cornerSpeed = 2.0f;     // m/s held while a sharp corner is <25 m ahead (speed governor)
    [Range(0.05f, 1f)] public float smoothing = 0.5f;   // higher = follows the network faster
    public float steerGain = 1.5f;                      // multiplies the network's steering (tuned controller gain)
    [Range(0.1f, 1f)] public float safeBrake = 0.5f;    // fraction of full braking during safe stop

    [Header("Switches")]
    public bool networkEnabled = true;
    public bool safetyOn = true;

    public Mode mode = Mode.Manual;

    CarController car;
    PathMonitor path;
    string cmdFile;
    RenderTexture rt;
    Texture2D tex;
    Vector3 startPos; Quaternion startRot;
    float nextSend, holdSteer;
    Vector3 failPos;
    string message = "Press G to let the network drive";
    GUIStyle style;
    string recDir; int recFrame; float nextRec;

    // network thread shared state
    readonly object lk = new object();
    byte[] pendingFrame;
    float lastPred;
    double lastReplyAt = -100;
    bool connected;
    volatile bool running;
    Thread th;
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static double Now { get { return clock.Elapsed.TotalSeconds; } }

    void Start()
    {
        car = GetComponent<CarController>();
        path = GetComponent<PathMonitor>();
        cmdFile = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "data", "cmd.txt"));
        if (dashCam == null) dashCam = GetComponentInChildren<Camera>();
        if (dashCam == null) { Debug.LogError("NNDriver: no camera found under Car."); enabled = false; return; }
        rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        startPos = transform.position; startRot = transform.rotation;
        staleTimeout = 5f;   // tolerate brief editor stalls/unfocused freezes
        smoothing = 0.5f;    // damp frame-to-frame steering noise (anti-wobble)
        running = true;
        th = new Thread(NetLoop) { IsBackground = true };
        th.Start();
    }

    void OnDestroy()
    {
        running = false;
        if (th != null) th.Join(300);
        if (rt != null) rt.Release();
    }

    // ---------- background thread: sends frames, receives steering ----------
    void NetLoop()
    {
        while (running)
        {
            TcpClient cli = null;
            try
            {
                cli = new TcpClient();
                cli.NoDelay = true;
                cli.Connect(host, port);
                var s = cli.GetStream();
                s.ReadTimeout = 2000; s.WriteTimeout = 2000;
                lock (lk) connected = true;
                var predBuf = new byte[4];
                while (running)
                {
                    byte[] frame;
                    lock (lk) { frame = pendingFrame; pendingFrame = null; }
                    if (frame == null) { Thread.Sleep(1); continue; }
                    s.Write(BitConverter.GetBytes(frame.Length), 0, 4);
                    s.Write(frame, 0, frame.Length);
                    int got = 0;
                    while (got < 4)
                    {
                        int r = s.Read(predBuf, got, 4 - got);
                        if (r <= 0) throw new Exception("connection closed");
                        got += r;
                    }
                    float p = BitConverter.ToSingle(predBuf, 0);
                    lock (lk) { lastPred = p; lastReplyAt = Now; }
                }
            }
            catch (Exception) { /* server not running or dropped: retry below */ }
            finally
            {
                lock (lk) connected = false;
                if (cli != null) cli.Close();
            }
            Thread.Sleep(500);
        }
    }

    byte[] CaptureJpg()
    {
        var prev = dashCam.targetTexture;
        dashCam.targetTexture = rt;
        dashCam.Render();
        var prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        RenderTexture.active = prevActive;
        dashCam.targetTexture = prev;
        return tex.EncodeToJPG(90);
    }

    float Cruise()
    {
        float target = path != null ? path.CornerSpeedLimit(cruiseSpeed, cornerSpeed) : cruiseSpeed;
        return Mathf.Clamp(target - car.Speed, -1f, 1f);
    }

    string Note(string extra)
    {
        return "mode=" + mode + ";safety=" + (safetyOn ? "ON" : "OFF") + (extra.Length > 0 ? ";" + extra : "");
    }

    void Log(string evt, string extra)
    {
        if (path != null) path.LogEvent(evt, Note(extra));
    }

    void NetworkFailure(string why)
    {
        failPos = transform.position;
        holdSteer = car.Steering;
        Log("NN_FAIL", why);
        if (safetyOn) { mode = Mode.SafeStopping; message = "NETWORK LOST - SAFETY STOP"; }
        else { mode = Mode.NNOffUnsafe; message = "NETWORK LOST - NO SAFETY, steering gone"; }
    }

    void Update()
    {
        var k = Keyboard.current;
        if (k != null)
        {
            if (k.nKey.wasPressedThisFrame) { networkEnabled = !networkEnabled; message = "Network " + (networkEnabled ? "ON" : "OFF"); }
            if (k.tabKey.wasPressedThisFrame) { safetyOn = !safetyOn; message = "Safety " + (safetyOn ? "ON" : "OFF"); }
            if (k.mKey.wasPressedThisFrame) { mode = Mode.Manual; car.externalControl = false; message = "Manual control"; }
            if (k.xKey.wasPressedThisFrame) ResetRun();
            if (path != null)
            {
                if (k.digit1Key.wasPressedThisFrame) path.runLabel = "A";
                if (k.digit2Key.wasPressedThisFrame) path.runLabel = "B";
                if (k.digit3Key.wasPressedThisFrame) path.runLabel = "C";
            }
        }

        // send the newest camera frame to Python (only while the network switch is ON)
        if (networkEnabled && Time.unscaledTime >= nextSend)
        {
            nextSend = Time.unscaledTime + sendInterval;
            byte[] jpg = CaptureJpg();
            lock (lk) pendingFrame = jpg;
        }

        float pred; double replyAt; bool conn;
        lock (lk) { pred = lastPred; replyAt = lastReplyAt; conn = connected; }
        bool fresh = conn && networkEnabled && (Now - replyAt) < staleTimeout;
        CheckCmd(fresh, conn);
        RecordDashcam();

        if (k != null && k.gKey.wasPressedThisFrame)
        {
            if (fresh && !car.frozen)
            {
                mode = Mode.NNDriving;
                car.externalSteer = car.Steering;
                message = "Network driving";
                Log("NN_ENGAGED", "");
            }
            else message = "Cannot start: " + (!conn ? "Python server not connected" : !networkEnabled ? "network is OFF (press N)" : "no recent prediction");
        }

        switch (mode)
        {
            case Mode.Manual:
                car.externalControl = false;
                break;

            case Mode.NNDriving:
                if (!fresh) { NetworkFailure(!networkEnabled ? "network switched off" : "no reply from network"); break; }
                car.externalControl = true;
                car.externalSteer = Mathf.Lerp(car.externalSteer, Mathf.Clamp(pred * steerGain, -1f, 1f), smoothing);
                car.externalThrottle = Cruise();
                break;

            case Mode.NNOffUnsafe:
                car.externalControl = true;
                car.externalSteer = 0f;               // steering lost
                car.externalThrottle = Cruise();      // speed keeps being held
                break;

            case Mode.SafeStopping:
                car.externalControl = true;
                car.externalSteer = holdSteer;        // hold the last steering, do not straighten mid-turn
                car.externalThrottle = -safeBrake;    // smooth braking
                if (car.Speed <= 0.05f)
                {
                    float d = Vector3.Distance(transform.position, failPos);
                    mode = Mode.Manual;
                    car.externalControl = false;
                    message = "STOPPED SAFELY - MANUAL CONTROL (stopped " + d.ToString("F1") + " m after failure)";
                    Log("STOPPED_SAFE", "stop_distance_m=" + d.ToString("F1"));
                }
                break;
        }
    }

    // Dashcam recording for the demo videos: while data/rec.txt exists, save ~20
    // dashcam JPEGs per second to recordings/rec_<timestamp>/. Delete rec.txt to stop.
    void RecordDashcam()
    {
        string flag = Path.Combine(Path.GetDirectoryName(cmdFile) ?? "", "rec.txt");
        if (!File.Exists(flag)) { recDir = null; return; }
        if (Time.unscaledTime < nextRec) return;
        nextRec = Time.unscaledTime + 0.05f;
        try
        {
            if (recDir == null)
            {
                string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "recordings"));
                recDir = Path.Combine(root, "rec_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(recDir);
                recFrame = 0;
                Debug.Log("NNDriver: recording dashcam to " + recDir);
            }
            File.WriteAllBytes(Path.Combine(recDir, "f" + (recFrame++).ToString("D6") + ".jpg"), CaptureJpg());
        }
        catch (IOException) { }
    }

    // Remote control without keyboard focus: write one word to data/cmd.txt and it is
    // executed next frame, then the file is deleted. Used by the automated test runs.
    //   RESET ENGAGE KILL RESUME SAFETY_ON SAFETY_OFF MANUAL LABEL_A LABEL_B LABEL_C
    void CheckCmd(bool fresh, bool conn)
    {
        if (cmdFile == null || !File.Exists(cmdFile)) return;
        string c;
        try { c = File.ReadAllText(cmdFile).Trim().ToUpperInvariant(); File.Delete(cmdFile); }
        catch (IOException) { return; }   // still being written; try again next frame
        switch (c)
        {
            case "RESET": ResetRun(); break;
            case "ENGAGE":
                if (fresh && !car.frozen)
                {
                    mode = Mode.NNDriving;
                    car.externalSteer = car.Steering;
                    message = "Network driving";
                    Log("NN_ENGAGED", "");
                }
                else message = "Cannot start: " + (!conn ? "Python server not connected" : !networkEnabled ? "network is OFF" : "no recent prediction");
                break;
            case "KILL": networkEnabled = false; message = "Network OFF (remote)"; break;
            case "RESUME": networkEnabled = true; message = "Network ON"; break;
            case "SAFETY_OFF": safetyOn = false; message = "Safety OFF"; break;
            case "SAFETY_ON": safetyOn = true; message = "Safety ON"; break;
            case "MANUAL": mode = Mode.Manual; car.externalControl = false; message = "Manual control"; break;
            case "LABEL_A": if (path != null) path.runLabel = "A"; break;
            case "LABEL_B": if (path != null) path.runLabel = "B"; break;
            case "LABEL_C": if (path != null) path.runLabel = "C"; break;
        }
    }

    void ResetRun()
    {
        transform.SetPositionAndRotation(startPos, startRot);
        car.ResetState();
        mode = Mode.Manual;
        networkEnabled = true;
        if (path != null) path.ResetRun();
        message = "Reset. Press G to let the network drive";
    }

    void OnGUI()
    {
        if (style == null) { style = new GUIStyle(GUI.skin.label); style.fontSize = 16; }
        bool conn; lock (lk) conn = connected;
        string line1 = "Mode: " + mode + "   Network: " + (networkEnabled ? "ON" : "OFF") + (conn ? " (server connected)" : " (server NOT connected)") +
                       "   Safety: " + (safetyOn ? "ON" : "OFF");
        string line2 = "Speed " + car.Speed.ToString("F1") + " m/s   Steering " + car.Steering.ToString("F2") + "   Run " + (path != null ? path.runLabel : "-");
        GUI.color = Color.white;
        GUI.Label(new Rect(12, 40, 1100, 26), line1, style);
        GUI.Label(new Rect(12, 66, 1100, 26), line2, style);
        GUI.color = mode == Mode.SafeStopping || mode == Mode.NNOffUnsafe ? Color.yellow : Color.white;
        GUI.Label(new Rect(12, 92, 1100, 26), message, style);
        GUI.color = Color.white;
        GUI.Label(new Rect(12, 118, 1100, 26), "G go | N network on/off | Tab safety on/off | M manual | X reset | 1/2/3 run A/B/C | K record path | V record video", style);
    }
}
