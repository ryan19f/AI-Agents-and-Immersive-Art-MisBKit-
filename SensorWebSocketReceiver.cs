using UnityEngine;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class SensorWebSocketReceiver : MonoBehaviour
{
    [Header("WebSocket Settings")]
    [SerializeField] private string serverUri = "ws://192.168.1.100:81";
    [SerializeField] private bool connectOnStart = true;

    [Header("Target Controller & Motion Settings")]
    [SerializeField] private PersonFollowController followController;
    [SerializeField] private Transform targetTransform;
    [SerializeField] private float smoothSpeed = 10f;

    [Header("Debug - Last Received Values")]
    [SerializeField] private bool debugLastVisible;
    [SerializeField] private float debugLastXOffset;
    [SerializeField] private float debugLastDistance;
    [SerializeField] private Vector3 debugLastEuler;
    [SerializeField] private float secondsSinceLastPacket;

    // Background Thread / Socket Control
    private ClientWebSocket webSocket;
    private CancellationTokenSource cts;
    private Thread receiveThread;
    private volatile bool running;

    // Shared thread-safe state between background thread and main thread
    private readonly object lockObj = new object();
    private bool pendingVisible;
    private float pendingXOffset;
    private float pendingDistance;
    private Quaternion pendingRotation = Quaternion.identity;
    private Vector3 pendingPosition = Vector3.zero;
    private bool hasNewData;
    private float lastPacketTimestamp;

    private void Start()
    {
        if (targetTransform == null)
        {
            targetTransform = this.transform;
        }

        if (connectOnStart)
        {
            StartWebSocketConnection();
        }
    }

    public void StartWebSocketConnection()
    {
        if (running) return;

        running = true;
        cts = new CancellationTokenSource();
        
        // Run WebSocket loop on background thread to keep Unity's main thread smooth
        receiveThread = new Thread(async () => await ReceiveLoopAsync(cts.Token))
        {
            IsBackground = true
        };
        receiveThread.Start();

        Debug.Log($"[SensorWS] Connecting to WebSocket at {serverUri}...");
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        webSocket = new ClientWebSocket();

        try
        {
            Uri uri = new Uri(serverUri);
            await webSocket.ConnectAsync(uri, token);
            Debug.Log("[SensorWS] Connected successfully!");

            byte[] buffer = new byte[8192];

            while (running && webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", token);
                    Debug.Log("[SensorWS] Server requested connection close.");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    ParseJsonPayload(json);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on application stop or disconnect request
        }
        catch (Exception e)
        {
            Debug.LogWarning("[SensorWS] Receive error: " + e.Message);
        }
    }

    /// <summary>
    /// Parses incoming Json outputted from SensorManager on the background thread.
    /// Supports distance/follow offsets and 3D rotation/position.
    /// </summary>
    private void ParseJsonPayload(string json)
    {
        // 1. Check person / movement presence
        bool visible = json.Contains("\"movementDetected\": true") || 
                       json.Contains("\"movementDetected\":true") || 
                       json.Contains("\"personVisible\": true") ||
                       json.Contains("\"personVisible\":true");

        // 2. Extract Distance & Tracking Offsets
        float xOffset = ExtractFloat(json, "xOffset");
        float distance = ExtractFloat(json, "distance");

        // 3. Extract Motion / IMU / Rotation values (Euler or Quaternions)
        float pitch = ExtractFloat(json, "pitch");
        float roll = ExtractFloat(json, "roll");
        float yaw = ExtractFloat(json, "yaw");

        float qx = ExtractFloat(json, "qx");
        float qy = ExtractFloat(json, "qy");
        float qz = ExtractFloat(json, "qz");
        float qw = ExtractFloat(json, "qw");

        Quaternion rot = Quaternion.identity;
        if (qw != 0 || qx != 0 || qy != 0 || qz != 0)
        {
            rot = new Quaternion(qx, qy, qz, qw);
        }
        else if (pitch != 0 || roll != 0 || yaw != 0)
        {
            rot = Quaternion.Euler(pitch, yaw, roll);
        }

        // Thread-safe update of pending parameters
        lock (lockObj)
        {
            pendingVisible = visible;
            pendingXOffset = xOffset;
            pendingDistance = distance;
            pendingRotation = rot;
            hasNewData = true;
        }
    }

    private static float ExtractFloat(string json, string key)
    {
        int idx = json.IndexOf("\"" + key + "\"");
        if (idx < 0) return 0f;

        int colonIdx = json.IndexOf(':', idx);
        if (colonIdx < 0) return 0f;

        int start = colonIdx + 1;
        int end = start;
        while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']')
        {
            end++;
        }

        string numStr = json.Substring(start, end - start).Trim();
        float.TryParse(numStr, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float result);
        return result;
    }

    private void Update()
    {
        bool gotData;
        bool visible;
        float xOffset, distance;
        Quaternion targetRot;

        // Fetch thread-safe updates on the Unity main thread
        lock (lockObj)
        {
            gotData = hasNewData;
            visible = pendingVisible;
            xOffset = pendingXOffset;
            distance = pendingDistance;
            targetRot = pendingRotation;

            if (gotData)
            {
                hasNewData = false;
                lastPacketTimestamp = Time.realtimeSinceStartup;
            }
        }

        // Update controllers if new packet was received
        if (gotData)
        {
            if (followController != null)
            {
                followController.personVisible = visible;
                followController.targetXOffset = xOffset;
                followController.targetDistance = distance;
            }

            debugLastVisible = visible;
            debugLastXOffset = xOffset;
            debugLastDistance = distance;
            debugLastEuler = targetRot.eulerAngles;
        }

        // Smoothly apply 3D transform rotation to target object
        if (targetTransform != null && targetRot != Quaternion.identity)
        {
            targetTransform.localRotation = Quaternion.Slerp(
                targetTransform.localRotation, 
                targetRot, 
                Time.deltaTime * smoothSpeed
            );
        }

        // Calculate time passed since last packet arrived
        secondsSinceLastPacket = Time.realtimeSinceStartup - lastPacketTimestamp;

        // Safety timeout - if no message was received for 1.5 seconds, reset state
        if (secondsSinceLastPacket > 1.5f && followController != null)
        {
            followController.personVisible = false;
        }
    }

    private void OnApplicationQuit()
    {
        Shutdown();
    }

    private void OnDestroy()
    {
        Shutdown();
    }

    private void Shutdown()
    {
        running = false;
        cts?.Cancel();

        if (webSocket != null && webSocket.State == WebSocketState.Open)
        {
            webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Unity Shutdown", CancellationToken.None);
            webSocket.Dispose();
        }

        if (receiveThread != null && receiveThread.IsAlive)
        {
            receiveThread.Join(500);
        }
    }
}
