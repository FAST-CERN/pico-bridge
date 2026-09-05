"""Logical control messages sent over the existing TCP function channel."""

from __future__ import annotations

from typing import Any

CONTROL_FUNCTION_NAME = "BridgeControl"
CONTROL_VERSION = 1
CONTROL_CHANNEL_VIDEO = "video"
CONTROL_TYPE_SET_POLICY = "set_policy"
CONTROL_CHANNEL_TRACKING = "tracking"
CONTROL_TYPE_SET_MOTION = "set_motion"
CONTROL_TYPE_SET_BODY = "set_body"
CONTROL_TYPE_SET_MOUNT_CORRECTION = "set_mount_correction"


def build_control_message(channel: str, message_type: str, payload: dict[str, Any]) -> dict[str, Any]:
    return {
        "version": CONTROL_VERSION,
        "channel": channel,
        "type": message_type,
        "payload": payload,
    }


def build_video_policy_message(*, enabled: bool, source: str | None) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "enabled": bool(enabled),
        "auto_preview": bool(enabled),
    }
    if source is not None:
        payload["source"] = source
    return build_control_message(CONTROL_CHANNEL_VIDEO, CONTROL_TYPE_SET_POLICY, payload)


def build_motion_stream_message(*, enabled: bool) -> dict[str, Any]:
    """Toggle device-side motion-tracker streaming (mocap map t03)."""
    return build_control_message(
        CONTROL_CHANNEL_TRACKING,
        CONTROL_TYPE_SET_MOTION,
        {"enabled": bool(enabled)},
    )


def build_body_stream_message(*, enabled: bool, height_m: float = 1.75) -> dict[str, Any]:
    """Ask the device to switch to PICO body tracking (mocap map t07).

    The app starts/stops device body tracking on this message and enforces
    the arm-source mutex (body on implies motion-tracker streaming off).
    ``height_m`` scales the bone-length ratio table on the device.
    """
    return build_control_message(
        CONTROL_CHANNEL_TRACKING,
        CONTROL_TYPE_SET_BODY,
        {"enabled": bool(enabled), "height": float(height_m)},
    )


def build_mount_correction_message(
    *,
    enabled: bool,
    left: dict[str, float] | None,
    right: dict[str, float] | None,
) -> dict[str, Any]:
    """Push strapped-controller mount-correction params to the app (bodytrack-deploy t07).

    Per-side ``yaw``/``level`` in degrees; the app applies them as a fixed
    local rotation on the Wrist/Hand body joints and persists the values as
    its boot default. Sides left as ``None`` are omitted (device keeps its
    stored values for that side).
    """
    payload: dict[str, Any] = {"enabled": bool(enabled)}
    for side, params in (("left", left), ("right", right)):
        if params is not None:
            payload[side] = {
                "yaw": float(params.get("yaw", 0.0)),
                "level": float(params.get("level", 0.0)),
            }
    return build_control_message(
        CONTROL_CHANNEL_TRACKING,
        CONTROL_TYPE_SET_MOUNT_CORRECTION,
        payload,
    )
