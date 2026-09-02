using UnityEngine;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class SensorWebSocketReceiver : MonoBehaviour
{
    [Header("WebSocket Settings")]
    [SerializeField] private string serverUri = "ws://192.168.0.125/ws";
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private float pollIntervalMs = 50f;

    [Header("Port Settings")]
    [Tooltip("Target Port ID (0 matches portC in config)")]
    [SerializeField] private int sensorPortId = 0;

    [Header("Target Controller")]
    [SerializeField] private PersonFollowController followController;
    [SerializeField] private Transform targetTransform;

    [Header("Debug Live Readout")]
    [SerializeField] private float debugLastDistance;
    [SerializeField] private bool debugLastVisible;

    private ClientWebSocket webSocket;
    private CancellationTokenSource cts;
    private Thread receiveThread;
    private volatile bool running;

    private readonly object lockObj = new object();
    private float pendingDistance;
    private bool pendingVisible;
    private bool hasNewData;

    private void Start()
    {
        if (targetTransform == null) targetTransform = this.transform;
        if (connectOnStart) StartWebSocketConnection();
    }

    public void StartWebSocketConnection()
    {
        if (running) return;

        running = true;
        cts = new CancellationTokenSource();
        receiveThread = new Thread(async () => await SocketLifecycleLoopAsync(cts.Token)) { IsBackground = true };
        receiveThread.Start();
    }

    private async Task SocketLifecycleLoopAsync(CancellationToken token)
    {
        webSocket = new ClientWebSocket();
        try
        {
            await webSocket.ConnectAsync(new Uri(serverUri), token);
            Debug.LogWarning($"<color=cyan>[SensorWS] Successfully Connected to: {serverUri}</color>");

            await Task.Delay(200, token);

            // Configure Port 0 for generic sensor input
            string enablePortCmd = $"{{\"cmd\":0,\"val\":{{\"ports\":[{{\"id\":{sensorPortId},\"units\":[{{\"model\":\"sensor_generic\",\"enabled\":true,\"alpha\":0.5}}]}}]}}}}";
            await SendStringAsync(enablePortCmd, token);

            var receiveTask = ReceiveLoopAsync(token);
            var pollTask = PollLoopAsync(token);

            await Task.WhenAll(receiveTask, pollTask);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Debug.LogError($"[SensorWS] Connection failure: {e.Message}");
        }
    }

    private async Task SendStringAsync(string message, CancellationToken token)
    {
        if (webSocket != null && webSocket.State == WebSocketState.Open)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        // Polling command for MisBKit telemetry (cmd 2 for live sensor data stream)
        string pollCmd = "{\"cmd\":2}";

        while (running && webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            try
            {
                await SendStringAsync(pollCmd, token);
            }
            catch { break; }

            await Task.Delay((int)pollIntervalMs, token);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        byte[] buffer = new byte[8192];
        while (running && webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Close", token);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                
                // Direct Diagnostic Log (Filters out repetitive configuration & position replies)
                if (!json.Contains("\"sensorconfig\"") && 
                    !json.Contains("\"rc_config\"") && 
                    !json.Contains("\"pair\"") && 
                    !json.Contains("\"positions\""))
                {
                    Debug.LogWarning($"[MisBKit Live Sensor Packet]: {json}");
                }

                ParseMisBKitJson(json);
            }
        }
    }

    private void ParseMisBKitJson(string json)
    {
        // Ignore handshake and config echoes
        if (json.Contains("\"sensorconfig\"") || 
            json.Contains("\"rc_config\"") || 
            json.Contains("\"pair\"") || 
            json.Contains("\"positions\""))
        {
            return;
        }

        // Parse distance / analog / telemetry keys
        float distance = ExtractNumericValue(json, "distance");
        if (distance <= 0f) distance = ExtractNumericValue(json, "dist");
        if (distance <= 0f) distance = ExtractNumericValue(json, "data");
        if (distance <= 0f) distance = ExtractNumericValue(json, "analog");

        if (distance > 0f)
        {
            bool visible = distance > 2.0f && distance < 400.0f;
            lock (lockObj)
            {
                pendingDistance = distance;
                pendingVisible = visible;
                hasNewData = true;
            }
        }
    }

    private static float ExtractNumericValue(string json, string key)
    {
        int idx = json.IndexOf("\"" + key + "\"");
        if (idx < 0) return 0f;

        int colonIdx = json.IndexOf(':', idx);
        if (colonIdx < 0) return 0f;

        int start = colonIdx + 1;
        while (start < json.Length && (json[start] == ' ' || json[start] == '[' || json[start] == '\"'))
        {
            start++;
        }

        // Avoid objects and non-numeric starts
        if (start >= json.Length || json[start] == '{') return 0f;

        int end = start;
        while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']' && json[end] != '\"')
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
        float distance;
        bool visible;

        lock (lockObj)
        {
            gotData = hasNewData;
            distance = pendingDistance;
            visible = pendingVisible;
            hasNewData = false;
        }

        if (gotData)
        {
            debugLastVisible = visible;
            debugLastDistance = distance;

            if (followController != null)
            {
                followController.personVisible = visible;
                followController.targetDistance = distance;
            }
        }
    }

    private void OnDestroy()
    {
        running = false;
        cts?.Cancel();
        webSocket?.Dispose();
    }
}
