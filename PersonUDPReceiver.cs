using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Text;

public class PersonUDPReceiver : MonoBehaviour
{
    [SerializeField] private int listenPort = 5005;
    [SerializeField] private PersonFollowController followController;

    [Header("Debug - last received values")]
    [SerializeField] private bool debugLastVisible;
    [SerializeField] private float debugLastXOffset;
    [SerializeField] private float debugLastDistance;
    [SerializeField] private float secondsSinceLastPacket;

    private UdpClient udpClient;
    private Thread receiveThread;
    private volatile bool running;

    // Shared state between background thread and main thread
    private readonly object lockObj = new object();
    private bool pendingVisible;
    private float pendingXOffset;
    private float pendingDistance;
    private bool hasNewData;
    private float lastPacketTime;

    private void Start()
    {
        udpClient = new UdpClient(listenPort);
        running = true;
        receiveThread = new Thread(ReceiveLoop);
        receiveThread.IsBackground = true;
        receiveThread.Start();

        Debug.Log($"[PersonUDP] Listening on port {listenPort}");
    }

    private void ReceiveLoop()
    {
        IPEndPoint anyEndpoint = new IPEndPoint(IPAddress.Any, 0);

        while (running)
        {
            try
            {
                byte[] data = udpClient.Receive(ref anyEndpoint);
                string json = Encoding.UTF8.GetString(data);

                // Simple manual parse - avoids JsonUtility needing an exact class match
                bool visible = json.Contains("\"movementDetected\": true") || json.Contains("\"personVisible\":true");
                float xOffset = ExtractFloat(json, "xOffset");
                float distance = ExtractFloat(json, "distance");

                lock (lockObj)
                {
                    pendingVisible = visible;
                    pendingXOffset = xOffset;
                    pendingDistance = distance;
                    hasNewData = true;
                    lastPacketTime = Time.realtimeSinceStartup;
                }
            }
            catch (SocketException)
            {
                // Thrown when socket closes on stop - expected, ignore
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PersonUDP] Receive error: " + e.Message);
            }
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
        while (end < json.Length && json[end] != ',' && json[end] != '}')
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

        lock (lockObj)
        {
            gotData = hasNewData;
            visible = pendingVisible;
            xOffset = pendingXOffset;
            distance = pendingDistance;
            hasNewData = false;
        }

        if (gotData && followController != null)
        {
            followController.personVisible = visible;
            followController.targetXOffset = xOffset;
            followController.targetDistance = distance;

            debugLastVisible = visible;
            debugLastXOffset = xOffset;
            debugLastDistance = distance;
        }

        secondsSinceLastPacket = Time.realtimeSinceStartup - lastPacketTime;

        // Safety - if we haven't heard from the Pi in a while, assume person is gone
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
        udpClient?.Close();
        if (receiveThread != null && receiveThread.IsAlive)
        {
            receiveThread.Join(500);
        }
    }
}