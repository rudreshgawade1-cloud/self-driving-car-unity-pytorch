"""Live steering server. Unity sends camera frames, this returns the network's steering.

Run (in the venv):   python drive_server.py
Then press Play in Unity and press G. Stop the server with Ctrl+C.
Protocol (little-endian): Unity -> [uint32 length][JPEG bytes]   Python -> [float32 steering]
"""
import argparse
import os
import socket
import struct
import time

import cv2
import numpy as np

from preprocess import preprocess_uint8, to_tensor_array

HERE = os.path.dirname(os.path.abspath(__file__))


def recv_exact(conn, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = conn.recv(n - len(buf))
        if not chunk:
            return None
        buf.extend(chunk)
    return bytes(buf)


def serve(conn, predict):
    """Handle one Unity connection until it closes. predict(rgb_uint8_160x320x3) -> float."""
    n_frames, t0, lat = 0, time.time(), 0.0
    while True:
        hdr = recv_exact(conn, 4)
        if hdr is None:
            break
        (n,) = struct.unpack("<I", hdr)
        if n == 0 or n > 5_000_000:
            print("Bad frame size", n, "- closing connection")
            break
        data = recv_exact(conn, n)
        if data is None:
            break
        t1 = time.time()
        img = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_COLOR)
        if img is None:
            steer = 0.0
        else:
            rgb = cv2.cvtColor(img, cv2.COLOR_BGR2RGB)
            if rgb.shape[:2] != (160, 320):
                rgb = cv2.resize(rgb, (320, 160), interpolation=cv2.INTER_AREA)
            steer = float(max(-1.0, min(1.0, predict(rgb))))
        conn.sendall(struct.pack("<f", steer))
        lat += time.time() - t1
        n_frames += 1
        if n_frames % 100 == 0:
            dt = time.time() - t0
            print(f"{n_frames / dt:5.1f} frames/s | {1000 * lat / 100:4.1f} ms per frame | last steering {steer:+.2f}")
            t0, lat, n_frames = time.time(), 0.0, 0


def load_predictor(model_path):
    import torch
    from model import SteeringNet

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print("Device:", device)
    ckpt = torch.load(model_path, map_location=device, weights_only=True)
    model = SteeringNet().to(device)
    model.load_state_dict(ckpt["model"])
    model.eval()
    print(f"Loaded {model_path} (val MSE {ckpt.get('val_mse', float('nan')):.4f}, val MAE {ckpt.get('val_mae', float('nan')):.4f})")

    @torch.no_grad()
    def predict(rgb):
        x = torch.from_numpy(to_tensor_array(preprocess_uint8(rgb))).unsqueeze(0).to(device)
        return model(x).item()

    predict(np.zeros((160, 320, 3), np.uint8))   # warm-up
    return predict


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default=os.path.join(HERE, "best_model.pt"))
    ap.add_argument("--port", type=int, default=5005)
    args = ap.parse_args()

    predict = load_predictor(args.model)
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", args.port))
    srv.listen(1)
    print(f"Waiting for Unity on 127.0.0.1:{args.port} ... (press Play in Unity)")
    try:
        while True:
            conn, addr = srv.accept()
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            print("Unity connected")
            try:
                serve(conn, predict)
            except (ConnectionError, OSError) as e:
                print("Connection error:", e)
            finally:
                conn.close()
                print("Unity disconnected. Waiting again...")
    except KeyboardInterrupt:
        print("\nStopped.")


if __name__ == "__main__":
    main()
