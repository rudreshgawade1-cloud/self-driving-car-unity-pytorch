"""Shared preprocessing. Training AND live driving must both use these functions."""
import cv2
import numpy as np

# Rows of the 320x160 DashCam frame to keep (removes sky and the car hood)
CROP_TOP, CROP_BOTTOM = 56, 134
OUT_W, OUT_H = 200, 66


def preprocess_uint8(img_rgb):
    """img_rgb: 160x320x3 uint8 RGB frame. Returns 66x200x3 uint8 (cropped + resized)."""
    crop = img_rgb[CROP_TOP:CROP_BOTTOM, :, :]
    return cv2.resize(crop, (OUT_W, OUT_H), interpolation=cv2.INTER_AREA)


def to_tensor_array(img_uint8):
    """66x200x3 uint8 -> 3x66x200 float32 in [-1, 1]."""
    x = img_uint8.astype(np.float32) / 127.5 - 1.0
    return np.transpose(x, (2, 0, 1))
