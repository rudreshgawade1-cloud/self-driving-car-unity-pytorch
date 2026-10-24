"""Assemble recorded dashcam JPEGs into an MP4 (H.264 via imageio-ffmpeg if present,
else mp4v). Usage: python make_video.py <folder_with_f*.jpg> <out.mp4> [fps]"""
import glob
import os
import sys

import cv2

src, out = sys.argv[1], sys.argv[2]
fps = float(sys.argv[3]) if len(sys.argv) > 3 else 20.0
files = sorted(glob.glob(os.path.join(src, "f*.jpg")))
if not files:
    sys.exit("no frames found in " + src)
img = cv2.imread(files[0])
h, w = img.shape[:2]
fourcc = cv2.VideoWriter_fourcc(*"mp4v")
vw = cv2.VideoWriter(out, fourcc, fps, (w, h))
for f in files:
    vw.write(cv2.imread(f))
vw.release()
print(f"{out}: {len(files)} frames, {w}x{h}, {len(files)/fps:.1f}s")
