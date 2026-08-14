using UnityEngine;

public class PersonFollowController : MonoBehaviour
{
    [Header("Gait reference")]
    [SerializeField] private QuadrupedGait gait;

    [Header("Target input (from Pi)")]
    [Range(-1f, 1f)] public float targetXOffset = 0f;
    [Range(0f, 1f)] public float targetDistance = 0.5f;
    public bool personVisible = false;

    [Header("Steering tuning")]
    [SerializeField] private float turnGain = 0.3f;
    [SerializeField] private float forwardGain = 0.4f;
    [SerializeField] private float deadZoneOffset = 0.1f;
    [SerializeField] private float deadZoneDistance = 0.15f;
    [SerializeField] private bool followingEnabled = true;

    [Header("Search behavior")]
    [SerializeField] private bool searchWhenLost = true;
    [SerializeField] private float timeBeforeSearch = 2f;
    [SerializeField] private float searchTurnSpeed = 0.2f;
    [SerializeField] private float searchForwardSpeed = 0f;
    // NEW:
    [SerializeField] private float searchTurnDuration = 1.5f;
    [SerializeField] private float searchPauseDuration = 1.5f;

    private float timeSinceLastDetection = 0f;
    private float searchStateTimer = 0f;
    private bool searchIsPaused = false;
    private bool sweepingRight = true;

    private void Update()
    {
        if (!followingEnabled || gait == null) return;

        if (!personVisible)
        {
            timeSinceLastDetection += Time.deltaTime;

            if (searchWhenLost && timeSinceLastDetection > timeBeforeSearch)
            {
                RunSearchSweep();
            }
            else
            {
                gait.SetWalking(false);
            }

            return;
        }
        // NEW:
        timeSinceLastDetection = 0f;
        searchStateTimer = 0f;
        searchIsPaused = false;

        float turn = 0f;
        if (Mathf.Abs(targetXOffset) > deadZoneOffset)
        {
            turn = targetXOffset * turnGain;
        }

        float forward = 0f;
        if (targetDistance > deadZoneDistance)
        {
            forward = targetDistance * forwardGain;
        }

        gait.SetSteering(turn, forward);
        gait.SetWalking(forward > 0f || Mathf.Abs(turn) > 0f);
    }

    private void RunSearchSweep()
    {
        searchStateTimer += Time.deltaTime;

        if (searchIsPaused)
        {
            gait.SetWalking(false);

            if (searchStateTimer > searchPauseDuration)
            {
                searchStateTimer = 0f;
                searchIsPaused = false;
                sweepingRight = !sweepingRight;
            }
        }
        else
        {
            float turn = sweepingRight ? searchTurnSpeed : -searchTurnSpeed;
            gait.SetSteering(turn, searchForwardSpeed);
            gait.SetWalking(true);

            if (searchStateTimer > searchTurnDuration)
            {
                searchStateTimer = 0f;
                searchIsPaused = true;
            }
        }
    }
}