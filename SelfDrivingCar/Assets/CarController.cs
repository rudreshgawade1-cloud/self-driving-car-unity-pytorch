using UnityEngine;
using UnityEngine.InputSystem;

// Driving for the self-driving car project (version 2).
// Keyboard: W/S throttle + brake, A/D steer.
// NNDriver can take over through the "external" fields below.
// Steering convention: -1 = full left, +1 = full right.
public class CarController : MonoBehaviour
{
    [Header("Tuning")]
    public float maxSpeed = 6f;          // m/s
    public float acceleration = 4f;      // m/s^2 at full throttle
    public float brakeDecel = 8f;        // m/s^2 at full brake
    public float coastDecel = 2f;        // m/s^2 when no key pressed
    public float maxSteerAngle = 30f;    // degrees at steering = +/-1
    public float wheelBase = 2.7f;       // metres
    public float steerSpeed = 2f;        // how fast steering moves toward the input

    [Header("External control (set by NNDriver)")]
    public bool externalControl;
    [Range(-1f, 1f)] public float externalSteer;
    [Range(-1f, 1f)] public float externalThrottle;   // +1 accelerate, -1 full brake
    public bool frozen;                  // true after a crash: car stays stopped until reset

    [Header("State (read only)")]
    public float Steering;               // -1..1
    public float Speed;                  // m/s

    public void ResetState()
    {
        Steering = 0f; Speed = 0f;
        externalControl = false; externalSteer = 0f; externalThrottle = 0f;
        frozen = false;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (frozen) { Speed = 0f; return; }

        float steerIn = 0f, throttle = 0f;
        if (externalControl)
        {
            steerIn = externalSteer;
            throttle = externalThrottle;
        }
        else
        {
            var k = Keyboard.current;
            if (k == null) return;
            if (k.leftArrowKey.isPressed  || k.aKey.isPressed) steerIn -= 1f;
            if (k.rightArrowKey.isPressed || k.dKey.isPressed) steerIn += 1f;
            if (k.upArrowKey.isPressed    || k.wKey.isPressed) throttle += 1f;
            if (k.downArrowKey.isPressed  || k.sKey.isPressed) throttle -= 1f;
        }

        Steering = Mathf.MoveTowards(Steering, steerIn, steerSpeed * dt);

        if (throttle > 0f)      Speed = Mathf.MoveTowards(Speed, maxSpeed, acceleration * throttle * dt);
        else if (throttle < 0f) Speed = Mathf.MoveTowards(Speed, 0f, brakeDecel * (-throttle) * dt);
        else                    Speed = Mathf.MoveTowards(Speed, 0f, coastDecel * dt);

        // Kinematic bicycle model
        float steerRad = Steering * maxSteerAngle * Mathf.Deg2Rad;
        float yawRate = (Speed / wheelBase) * Mathf.Tan(steerRad);   // rad/s
        transform.Rotate(0f, yawRate * Mathf.Rad2Deg * dt, 0f);
        transform.position += transform.forward * Speed * dt;
    }
}
