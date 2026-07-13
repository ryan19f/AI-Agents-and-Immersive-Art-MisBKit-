using System.Text;
using UnityEngine;
using NativeWebSocket;
using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Unity example — attach this script to a GameObject in your scene.
///
/// Setup:
///   1. Import NativeWebSocket via UPM:
///      Window > Package Manager > "+" > Add package from git URL:
///      https://github.com/endel/NativeWebSocket.git#upm
///
///   2. Create an empty GameObject and attach this script.
///
///   3. Start the test server: cd NodeServer && npm install && node index.js
///
///   4. Press Play.
///
/// For NativeWebSocket 1.x, DispatchMessageQueue() is required in Update()
/// on non-WebGL targets to process websocket callbacks.
/// </summary>
/// 


public class Connection : MonoBehaviour
{
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
    }

    public enum MotorLinkParameter
    {
        Wheel,
        Speed,
        JointGoal,
        JointSpeed,
    }

    [Serializable]
    public class MotorRuntimeLink
    {
        public bool enabled = true;
        public int motorId;
        public MotorLinkParameter parameter = MotorLinkParameter.Wheel;
        public RuntimeFloatVariable source;
        public float fallbackValue;
        public float changeThreshold = 0.01f;
        public float jointGoalWhenSpeedDriven = 0f;
        public float jointSpeedWhenGoalDriven = 0.5f;

        [NonSerialized] public float lastAppliedValue = float.NaN;
    }

    [Serializable]
    private class ReplyValue
    {
        public string version;
        public int[] ids;
    }

    [Serializable]
    private class ReplyMessage
    {
        public string reply;
        public ReplyValue val;
    }

    [Serializable]
    private class SensorUnit
    {
        public int id;
        public string name;
        public float[] val;
    }

    [Serializable]
    private class SensorPort
    {
        public int id;
        public SensorUnit[] units;
    }

    [Serializable]
    private class SensorDataVal
    {
        public SensorPort[] ports;
    }

    [Serializable]
    private class SensorDataMessage
    {
        public string reply;
        public SensorDataVal val;
    }

    private struct SensorEntry
    {
        public float[] values;
        public float lastUpdatedTime;
    }

    WebSocket websocket;

    [SerializeField] private MisBKitConnectionSettings connectionSettings;
    [SerializeField] private bool sendCommandsAsBuffer = true;
    [SerializeField] private int maxBufferListSize = 30;
    [SerializeField] private float bufferFlushIntervalSeconds = 0.1f;
    [SerializeField] private bool applyRuntimeLinks = true;
    [SerializeField] private List<MotorRuntimeLink> motorRuntimeLinks = new List<MotorRuntimeLink>();

    [SerializeField] private float sensorPollIntervalSeconds = 0.1f;
    [SerializeField] private float sensorStaleAfterSeconds = 1.5f;

    private bool kitConnected;
    private bool isQuitting = false;
    private ConnectionState connectionState = ConnectionState.Disconnected;
    private string kitVersion = string.Empty;
    private readonly List<int> detectedMotorIds = new List<int>();
    private readonly List<MisBKitMotor> motors = new List<MisBKitMotor>();
    private readonly List<string> commandBuffer = new List<string>();
    private readonly Dictionary<string, SensorEntry> latestSensorValues = new Dictionary<string, SensorEntry>();

    public IReadOnlyList<int> DetectedMotorIds => detectedMotorIds;
    public IReadOnlyList<MisBKitMotor> Motors => motors;
    public IReadOnlyList<MotorRuntimeLink> MotorRuntimeLinks => motorRuntimeLinks;
    public bool IsKitConnected => kitConnected;
    public string KitVersion => kitVersion;
    public ConnectionState State => connectionState;

    public bool TryGetSensorValue(string unitName, int index, out float value)
    {
        value = 0f;
        if (!latestSensorValues.TryGetValue(unitName, out var entry))
        {
            return false;
        }

        if (Time.time - entry.lastUpdatedTime > sensorStaleAfterSeconds)
            {
                return false;
            }

        if (index < 0 || entry.values == null || index >= entry.values.Length)
            {
                return false;
            }

        value = entry.values[index];
        return true;
    }
    public bool ApplyRuntimeLinks
    {
        get => applyRuntimeLinks;
        set => applyRuntimeLinks = value;
    }

    private string BuildServerUrl()
    {
        var kitIp = connectionSettings != null ? connectionSettings.kitIp.Trim() : string.Empty;
        if (string.IsNullOrEmpty(kitIp))
        {
            Debug.LogError("Missing kit IP in MisBKitConnectionSettings.");
            return string.Empty;
        }

        return $"ws://{kitIp}/ws";
    }

    async void Start()
    {
        Application.runInBackground = true;
        await ConnectAsync();
    }

    private async System.Threading.Tasks.Task ConnectAsync()
    {
        var serverUrl = BuildServerUrl();
        if (string.IsNullOrEmpty(serverUrl))
        {
            return;
        }

        // Close and dispose old socket cleanly before creating a new one
        if (websocket != null)
        {
            try { await websocket.Close(); } catch { }
            websocket = null;
        }

        if (connectionState == ConnectionState.Connected || connectionState == ConnectionState.Connecting)
        {
            connectionState = ConnectionState.Reconnecting;
        }
        else
        {
            connectionState = ConnectionState.Connecting;
        }

        Debug.Log("[MisBKitWS] Connecting to " + serverUrl);
        websocket = new WebSocket(serverUrl);

        websocket.OnOpen += () =>
        {
            connectionState = ConnectionState.Connected;
            Debug.Log("[MisBKitWS] Connection open.");
        };

        websocket.OnError += (e) =>
        {
            Debug.LogWarning("[MisBKitWS] Error: " + e);
        };

        websocket.OnClose += (code) =>
        {
            Debug.Log("[MisBKitWS] Connection closed. Code: " + code);
            kitConnected = false;
            connectionState = ConnectionState.Disconnected;
            UpdateConnectionStatus();
            OnConnectionClosed();
        };

        websocket.OnMessage += (bytes) =>
        {
            var message = Encoding.UTF8.GetString(bytes);
            Debug.Log("[MisBKitWS] Inbound (" + bytes.Length + " bytes): " + message);
            HandleIncomingReply(message);
        };

        // CancelInvoke before InvokeRepeating prevents stacked timers on reconnect
        CancelInvoke(nameof(SendCommandBuffer));
        if (sendCommandsAsBuffer && bufferFlushIntervalSeconds > 0f)
        {
            InvokeRepeating(nameof(SendCommandBuffer), bufferFlushIntervalSeconds, bufferFlushIntervalSeconds);
        }

        StartSensorPollTimer();

        await websocket.Connect();
    }

    private async void OnConnectionClosed()
    {
        if (isQuitting || !Application.isPlaying)
        {
            return;
        }

        await System.Threading.Tasks.Task.Delay(3000);

        if (isQuitting || !Application.isPlaying)
        {
            return;
        }

        Debug.Log("[MisBKitWS] Attempting reconnect...");

        // Clear kit state so pair handshake runs again after reconnect
        kitConnected = false;
        detectedMotorIds.Clear();
        motors.Clear();

        await ConnectAsync();
    }

    private void Update()
    {
        // NativeWebSocket 1.x requires manual queue dispatch outside WebGL runtime.
#if !UNITY_WEBGL || UNITY_EDITOR
        if (websocket != null)
        {
            websocket.DispatchMessageQueue();
        }
#endif

        if (!Application.isPlaying || !applyRuntimeLinks || !kitConnected)
        {
            return;
        }

        ApplyMotorRuntimeLinks();
    }

    public int CommandBufferHasCommands()
    {
        return commandBuffer.Count;
    }

    public void SendWsCommand(string name)
    {
        SendWsCommandInternal(name, null, null);
    }

    public void SendScanCommand()
    {
        Debug.Log("Queueing scan command.");
        SendWsCommand("scan");
    }

    public MisBKitMotor GetMotorById(int id)
    {
        return motors.Find(motor => motor.Id == id);
    }

    public void SendWsCommand(string name, int id)
    {
        SendWsCommandInternal(name, id, null);
    }

    public void SendWsCommand(string name, int id, float val)
    {
        SendWsCommandInternal(name, id, val.ToString("0.###", CultureInfo.InvariantCulture));
    }

    public void SendWsCommand(string name, int id, int val)
    {
        SendWsCommandInternal(name, id, val.ToString(CultureInfo.InvariantCulture));
    }

    public void SendWsCommand(string name, int id, bool val)
    {
        SendWsCommandInternal(name, id, val ? "true" : "false");
    }

    public void SendWsCommandWithRawVal(string name, string rawJsonVal)
    {
        SendWsCommandInternal(name, null, rawJsonVal);
    }

    public void SendWsCommandWithRawVal(string name, int id, string rawJsonVal)
    {
        SendWsCommandInternal(name, id, rawJsonVal);
    }

    public void BufferCommand(string name, int? id = null, string rawJsonVal = null)
    {
        var commandJson = MakeCommandJson(name, id, rawJsonVal);
        BufferCommandJson(commandJson);
    }

    public void SendCommandBuffer()
    {
        if (commandBuffer.Count == 0)
        {
            return;
        }

        if (websocket == null || websocket.State != WebSocketState.Open)
        {
            Debug.Log("[MisBKitWS] Websocket not open. Buffer send skipped.");
            return;
        }

        var payload = "{\"cmds\":[" + string.Join(",", commandBuffer) + "]}";
        Debug.Log("[MisBKitWS] Outbound buffer: " + payload);
        SendRawText(payload);
        commandBuffer.Clear();
    }

    private void SendWsCommandInternal(string name, int? id, string rawJsonVal)
    {
        var commandJson = MakeCommandJson(name, id, rawJsonVal);

        if (kitConnected)
        {
            if (sendCommandsAsBuffer)
            {
                BufferCommandJson(commandJson);
            }
            else
            {
                SendRawText(commandJson);
            }
        }
        else
        {
            if (sendCommandsAsBuffer)
            {
                BufferCommandJson(commandJson);
            }

            Debug.Log("[MisBKitWS] Kit not connected.");
        }
    }

    private string MakeCommandJson(string name, int? id = null, string rawJsonVal = null)
    {
        var escapedName = EscapeJsonString(name);
        var builder = new StringBuilder();
        builder.Append("{\"cmd\":\"").Append(escapedName).Append("\"");

        if (id.HasValue)
        {
            builder.Append(",\"id\":").Append(id.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (rawJsonVal != null)
        {
            builder.Append(",\"val\":").Append(rawJsonVal);
        }

        builder.Append("}");
        return builder.ToString();
    }

    private void BufferCommandJson(string commandJson)
    {
        if (commandBuffer.Count >= maxBufferListSize)
        {
            Debug.Log("[MisBKitWS] Command buffer full. Dropping: " + commandJson);
            return;
        }

        commandBuffer.Add(commandJson);
    }

    private static string EscapeJsonString(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private async void SendRawText(string payload)
    {
        if (websocket == null || websocket.State != WebSocketState.Open)
        {
            Debug.Log("[MisBKitWS] Websocket not open. Send skipped: " + payload);
            return;
        }

        await websocket.SendText(payload);
    }

    private void ApplyMotorRuntimeLinks()
    {
        for (var i = 0; i < motorRuntimeLinks.Count; i++)
        {
            var link = motorRuntimeLinks[i];
            if (link == null || !link.enabled)
            {
                continue;
            }

            var motor = GetMotorById(link.motorId);
            if (motor == null)
            {
                continue;
            }

            var value = link.source != null ? link.source.Value : link.fallbackValue;
            if (!float.IsNaN(link.lastAppliedValue) && Mathf.Abs(value - link.lastAppliedValue) < Mathf.Abs(link.changeThreshold))
            {
                continue;
            }

            switch (link.parameter)
            {
                case MotorLinkParameter.Wheel:
                    motor.Wheel(Mathf.Clamp(value, -1f, 1f));
                    break;
                case MotorLinkParameter.Speed:
                    motor.Speed(Mathf.Clamp01(value));
                    break;
                case MotorLinkParameter.JointGoal:
                    motor.Joint(Mathf.Clamp(value, -1f, 1f), Mathf.Clamp01(link.jointSpeedWhenGoalDriven));
                    break;
                case MotorLinkParameter.JointSpeed:
                    motor.Joint(Mathf.Clamp(link.jointGoalWhenSpeedDriven, -1f, 1f), Mathf.Clamp01(value));
                    break;
                default:
                    continue;
            }

            link.lastAppliedValue = value;
        }
    }

    private void HandleIncomingReply(string message)
    {
        ReplyMessage parsed;
        try
        {
            parsed = JsonUtility.FromJson<ReplyMessage>(message);
        }
        catch (Exception e)
        {
            Debug.LogWarning("Failed to parse reply JSON: " + e.Message);
            return;
        }

        if (parsed == null || string.IsNullOrEmpty(parsed.reply))
        {
            return;
        }

        if (!kitConnected)
        {
            if (parsed.reply == "pair")
            {
                kitConnected = true;
                kitVersion = parsed.val != null ? parsed.val.version : string.Empty;

                var kitIp = connectionSettings != null ? connectionSettings.kitIp : "unknown";
                Debug.Log("[MisBKitWS] Paired with kit " + kitIp + " (version " + kitVersion + ")");
                UpdateConnectionStatus();
            }
            else if (parsed.reply == "sensorconfig")
            {
                Debug.Log("[MisBKitWS] Received sensor configuration reply.");
            }
        }
        else
        {
            if (parsed.reply == "sensordata")
            {
                ParseSensorData(message);
                Debug.Log("[MisBKitWS] Received sensordata reply.");
                LogFlatDistValue(message);
            }
            else if (parsed.reply == "scan")
            {
                detectedMotorIds.Clear();
                motors.Clear();

                if (parsed.val != null && parsed.val.ids != null)
                {
                    detectedMotorIds.AddRange(parsed.val.ids);
                    foreach (var id in parsed.val.ids)
                    {
                        motors.Add(new MisBKitMotor(this, id));
                    }
                }

                Debug.Log("[MisBKitWS] Received scan reply. Detected motor IDs: " + string.Join(",", detectedMotorIds));
            }
        }
    }

    private void UpdateConnectionStatus()
    {
        Debug.Log("[MisBKitWS] Kit status: " + (kitConnected ? "Connected" : "Disconnected") + ", Socket state: " + connectionState);
    }

    private async void OnApplicationQuit()
    {
        isQuitting = true;
        CancelInvoke(nameof(SendCommandBuffer));

        if (websocket != null)
        {
            await websocket.Close();
        }

        connectionState = ConnectionState.Disconnected;
        CancelInvoke(nameof(RequestSensorData));
    }

    [Serializable]
    private class FlatSensorVal
    {
        public string dist;
    }

    [Serializable]
    private class FlatSensorMessage
    {
        public string reply;
        public FlatSensorVal val;
    }

    private void LogFlatDistValue(string rawMessage)
    {
        FlatSensorMessage flatMsg;
        try
        {
            flatMsg = JsonUtility.FromJson<FlatSensorMessage>(rawMessage);
        }
        catch (Exception e)
        {
            Debug.LogWarning("Failed to parse flat sensordata JSON: " + e.Message);
            return;
        }

        Debug.Log("[MisBKitWS] dist = " + (flatMsg?.val?.dist ?? "null"));
    }
    private void ParseSensorData(string rawMessage)
    {
        SensorDataMessage sensorMsg;
        try
        {
            sensorMsg = JsonUtility.FromJson<SensorDataMessage>(rawMessage);
        }
        catch (Exception e)
        {
                Debug.LogWarning("Failed to parse sensordata JSON: " + e.Message);
                return;
        }

        if (sensorMsg?.val?.ports == null)
            {
                return;
            }

        var now = Time.time;
        foreach (var port in sensorMsg.val.ports)
            {
                if (port.units == null) continue;
                foreach (var unit in port.units)
                    {
                        if (string.IsNullOrEmpty(unit.name) || unit.val == null) continue;
        latestSensorValues[unit.name] = new SensorEntry { values = unit.val, lastUpdatedTime = now };
                    }
            }
    }

    private void StartSensorPollTimer()
    {
        CancelInvoke(nameof(RequestSensorData));
        if (sensorPollIntervalSeconds <= 0f)
        {
            sensorPollIntervalSeconds = 0.1f;
        }

    InvokeRepeating(nameof(RequestSensorData), sensorPollIntervalSeconds, sensorPollIntervalSeconds);
    }

    private void RequestSensorData()
    {
            if (!kitConnected) return;
            SendWsCommand("sensordata");
    }
}