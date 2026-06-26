using UnityEngine;

public class PersonFollowController : MonoBehaviour
{
    [Header("Gait reference")]
    [SerializeField] private QuadrupedGait gait;

    [Header("Target input (will come from Pi later)")]
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
    [SerializeField] private float timeBeforeSearch = 2f;     // seconds with nothing detected before searching
    [SerializeField] private float searchTurnSpeed = 0.2f;    // gentle constant turn while searching
    [SerializeField] private float searchForwardSpeed = 0f;   // 0 = turn in place, >0 = wander forward while turning

    private float timeSinceLastDetection = 0f;

    private void Update()
    {
        if (!followingEnabled || gait == null) return;

        if (!personVisible)
        {
            timeSinceLastDetection += Time.deltaTime;

            if (searchWhenLost && timeSinceLastDetection > timeBeforeSearch)
            {
                // Nothing detected for a while - rotate slowly to scan the area
                gait.SetSteering(searchTurnSpeed, searchForwardSpeed);
                gait.SetWalking(true);
            }
            else
            {
                // Brief gap in detection - just pause, don't spin yet
                gait.SetWalking(false);
            }

            return;
        }

        // Person/movement detected - reset the search timer
        timeSinceLastDetection = 0f;

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
}