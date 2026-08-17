using UnityEngine;
using System;

public class QuadrupedGait : MonoBehaviour
{
    [Serializable]
    public class LegConfig
    {
        public string label;
        public int motorId;
        [Range(0f, 1f)] public float phase;
        [Range(-1f, 1f)] public float amplitude;
        [Range(-1f, 1f)] public float asymmetry;
        [Range(-1f, 1f)] public float bias;          // shifts resting position
        [Range(0f, 1f)] public float motorSpeed = 0.6f;

        [NonSerialized]
        public float baseSign = 1f; // captured once, never overwritten by steering
    }

    [Header("Steering")]
    [SerializeField] private float baseAmplitude = 0.4f;
    [SerializeField] private float steeringStrength = 0.5f;
    [SerializeField] private float maxAmplitudeMagnitude = 0.5f; // hard ceiling per leg
    [SerializeField] private float minAmplitudeMagnitude = 0.15f; // keeps legs from going nearly still

    [Header("Connection")]
    [SerializeField] private Connection connection;

    [Header("Tools")]
    [SerializeField] private bool applyRestingPoseNow;

    [Header("Gait")]
    [Range(0.1f, 3f)] [SerializeField] private float frequency = 0.8f;
    [SerializeField] private bool walking = true;

    [Header("Legs")]
    [SerializeField]
    private LegConfig[] legs = new[]
    {
        new LegConfig { label="FL", motorId=1, phase=0f,    amplitude= 0.4f, asymmetry=0.3f, bias=-0.2f, motorSpeed=0.6f },
        new LegConfig { label="FR", motorId=2, phase=0.25f, amplitude=-0.4f, asymmetry=0.3f, bias=-0.2f, motorSpeed=0.6f },
        new LegConfig { label="RR", motorId=3, phase=0.5f,  amplitude=-0.4f, asymmetry=0.3f, bias=-0.2f, motorSpeed=0.6f },
        new LegConfig { label="RL", motorId=4, phase=0.75f, amplitude= 0.4f, asymmetry=0.3f, bias=-0.2f, motorSpeed=0.6f },
    };



    [ContextMenu("Apply Resting Pose")]
    public void ApplyRestingPose()
    {
        if (connection == null || !connection.IsKitConnected) return;
        foreach (var leg in legs)
        {
            var motor = connection.GetMotorById(leg.motorId);
            if (motor == null) continue;
            motor.Joint(leg.bias, leg.motorSpeed);
        }
    }

    private void Start()
    {
        foreach (var leg in legs)
        {
            leg.baseSign = leg.amplitude >= 0f ? 1f : -1f;
        }
    }

    private void Update()
    {
        if (!walking || connection == null || !connection.IsKitConnected)
            return;

        float t = Time.time * frequency * 2f * Mathf.PI;

        foreach (var leg in legs)
        {
            var motor = connection.GetMotorById(leg.motorId);
            if (motor == null) continue;

            float angle = t + leg.phase * 2f * Mathf.PI;
            float raw = Mathf.Sin(angle);
            float skewed = raw / Mathf.Sqrt(1f - leg.asymmetry * raw + float.Epsilon);
            skewed = Mathf.Clamp(skewed, -1f, 1f);

            // bias shifts the whole oscillation backward/forward
            float goal = Mathf.Clamp(leg.amplitude * skewed + leg.bias, -1f, 1f);
            motor.Joint(goal, leg.motorSpeed);
        }
    }

    public void SetWalking(bool value) => walking = value;

    public void SetSteering(float turn, float forward)
    {
        foreach (var leg in legs)
        {
            bool isLeftLeg = leg.label == "FL" || leg.label == "RL";
            float turnAdjust = isLeftLeg ? -turn * steeringStrength : turn * steeringStrength;

            float magnitude = Mathf.Abs(baseAmplitude * forward + turnAdjust);

            if (forward > 0f || Mathf.Abs(turn) > 0.01f)
            {
                magnitude = Mathf.Clamp(magnitude, minAmplitudeMagnitude, maxAmplitudeMagnitude);
            }
            else
            {
                magnitude = 0f;
            }

            leg.amplitude = leg.baseSign * magnitude;
        }
    }

}
