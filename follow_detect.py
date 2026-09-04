import cv2
import socket
import json
import time
import numpy as np

UNITY_IP = "192.168.0.219"
UNITY_PORT = 5005
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

cap = cv2.VideoCapture('http://127.0.0.1:8080/stream', cv2.CAP_FFMPEG)

FRAME_WIDTH = 320
MIN_CONTOUR_AREA = 500  # ignore tiny noise blobs - tune this based on testing

backSub = cv2.createBackgroundSubtractorMOG2(history=200, varThreshold=40, detectShadows=False)

while True:
    ret, frame = cap.read()
    if not ret:
        time.sleep(0.1)
        continue

    h, w = frame.shape[:2]
    small = cv2.resize(frame, (FRAME_WIDTH, int(h * FRAME_WIDTH / w)))
    gray = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY)
    blurred = cv2.GaussianBlur(gray, (5, 5), 0)

    fgMask = backSub.apply(blurred)
    fgMask = cv2.dilate(fgMask, None, iterations=2)

    contours, _ = cv2.findContours(fgMask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

    valid = [c for c in contours if cv2.contourArea(c) > MIN_CONTOUR_AREA]

    if valid:
        largest = max(valid, key=cv2.contourArea)
        x, y, bw, bh = cv2.boundingRect(largest)

        box_center_x = x + bw / 2
        x_offset = (box_center_x / small.shape[1]) * 2 - 1

        box_height_ratio = bh / small.shape[0]
        distance = max(0.0, 1.0 - (box_height_ratio / 0.8))

        message = {
            "movementDetected": True,
            "xOffset": round(float(x_offset), 3),
            "distance": round(float(distance), 3)
        }
    else:
        message = {"movementDetected": False, "xOffset": 0.0, "distance": 0.0}

    payload = json.dumps(message).encode('utf-8')
    sock.sendto(payload, (UNITY_IP, UNITY_PORT))
    print(message)
    time.sleep(0.1)
