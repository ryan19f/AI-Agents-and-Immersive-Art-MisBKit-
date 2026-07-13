using UnityEngine;

public class TwoLegKeyboardGait : MonoBehaviour
{
    [SerializeField] private Connection connection;

    [Header("Motor IDs")]
    [SerializeField] private int leftLegMotorId = 1;
    [SerializeField] private int rightLegMotorId = 2;

    [Header("Gait")]
    [Range(0.1f, 3f)]
    [SerializeField] private float frequency = 0.8f;

    [Range(0f, 1f)]
    [SerializeField] private float stride = 0.4f;

    [Range(0f, 1f)]
    [SerializeField] private float motorSpeed = 0.6f;

    [Header("Offsets")]
    [Range(-1f, 1f)]
    [SerializeField] private float restingBias = -0.2f;

    [Range(0f, 1f)]
    [SerializeField] private float turnAmount = 0.15f;

    private MisBKitMotor leftMotor;
    private MisBKitMotor rightMotor;
    private bool cached;

    private void Update()
    {
        if (connection == null || !connection.IsKitConnected)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.S))
        {
            connection.SendScanCommand();
            Invoke(nameof(CacheMotors), 0.5f);
        }

        if (!cached)
        {
            return;
        }

        if (Input.GetKey(KeyCode.Space))
        {
            ApplyRestingPose();
            return;
        }

        float forward = 0f;
        if (Input.GetKey(KeyCode.UpArrow)) forward += 1f;
        if (Input.GetKey(KeyCode.DownArrow)) forward -= 1f;

        float turn = 0f;
        if (Input.GetKey(KeyCode.RightArrow)) turn += 1f;
        if (Input.GetKey(KeyCode.LeftArrow)) turn -= 1f;

        Drive(forward, turn);
    }

    private void CacheMotors()
    {
        leftMotor = connection.GetMotorById(leftLegMotorId);
        rightMotor = connection.GetMotorById(rightLegMotorId);

        if (leftMotor == null || rightMotor == null)
        {
            Debug.LogWarning("Could not find both leg motors. Check the motor IDs.");
            cached = false;
            return;
        }

        cached = true;
        Debug.Log($"Cached leg motors: left={leftMotor.Id}, right={rightMotor.Id}");
    }

    private void Drive(float forward, float turn)
    {
        float t = Time.time * frequency * 2f * Mathf.PI;

        float leftWave = Mathf.Sin(t);
        float rightWave = Mathf.Sin(t + Mathf.PI); // opposite phase

        float forwardScale = forward * stride;
        float turnScale = turn * turnAmount;

        float leftGoal = Mathf.Clamp(restingBias + (leftWave * forwardScale) + turnScale, -1f, 1f);
        float rightGoal = Mathf.Clamp(restingBias + (rightWave * forwardScale) - turnScale, -1f, 1f);

        leftMotor.SetMode(MisBKitMotorMode.Joint);
        rightMotor.SetMode(MisBKitMotorMode.Joint);

        leftMotor.Joint(leftGoal, motorSpeed);
        rightMotor.Joint(rightGoal, motorSpeed);
    }

    private void ApplyRestingPose()
    {
        leftMotor.SetMode(MisBKitMotorMode.Joint);
        rightMotor.SetMode(MisBKitMotorMode.Joint);

        leftMotor.Joint(restingBias, motorSpeed);
        rightMotor.Joint(restingBias, motorSpeed);
    }
}