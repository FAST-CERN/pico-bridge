"""Off-device loopback: receiver arm-source pushes against a mock headset.

Speaks the real wire protocol over a real TCP socket (no headset needed) and
asserts the BridgeControl sequence a device would receive on connect for each
arm_source mode (mocap map t07).
"""

from __future__ import annotations

import json
import socket
import time

from pico_bridge.bridge import PicoBridge
from pico_bridge.protocol import (
    CMD,
    HEAD_PC_TO_VR,
    PacketParser,
    pack,
)


def _free_port() -> int:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return int(probe.getsockname()[1])


def _read_control_messages(sock: socket.socket, deadline_s: float) -> list[dict]:
    """Collect BridgeControl (FROM_CONTROLLER_COMMON_FUNCTION) values."""
    parser = PacketParser(accept_head=HEAD_PC_TO_VR)
    messages: list[dict] = []
    deadline = time.monotonic() + deadline_s
    sock.settimeout(0.1)
    while time.monotonic() < deadline:
        try:
            chunk = sock.recv(65536)
        except socket.timeout:
            continue
        if not chunk:
            break
        for pkt in parser.feed(chunk):
            if pkt.cmd != CMD.FROM_CONTROLLER_COMMON_FUNCTION:
                continue
            envelope = json.loads(pkt.data.decode("utf-8"))
            if envelope.get("functionName") == "BridgeControl":
                messages.append(envelope["value"])
    return messages


def _tracking_messages(messages: list[dict]) -> list[dict]:
    return [m for m in messages if m.get("channel") == "tracking"]


def test_body_mode_loopback_pushes_set_body_over_real_tcp():
    port = _free_port()
    bridge = PicoBridge(
        host="127.0.0.1",
        port=port,
        discovery=False,
        arm_source="body",
        operator_height_m=1.66,
        start_timeout=5.0,
    )
    bridge.start()
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=5.0) as sock:
            messages = _read_control_messages(sock, deadline_s=1.5)
    finally:
        bridge.close()

    tracking = _tracking_messages(messages)
    assert [(m["type"], m["payload"]) for m in tracking] == [
        ("set_motion", {"enabled": False}),
        ("set_body", {"enabled": True, "height": 1.66}),
    ], f"unexpected tracking control sequence: {tracking}"


def test_tracker_mode_loopback_pushes_set_motion_over_real_tcp():
    port = _free_port()
    bridge = PicoBridge(
        host="127.0.0.1",
        port=port,
        discovery=False,
        arm_source="tracker",
        motion_enabled=True,
        start_timeout=5.0,
    )
    bridge.start()
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=5.0) as sock:
            messages = _read_control_messages(sock, deadline_s=1.5)
    finally:
        bridge.close()

    tracking = _tracking_messages(messages)
    assert [(m["type"], m["payload"]) for m in tracking] == [
        ("set_motion", {"enabled": True}),
    ], f"unexpected tracking control sequence: {tracking}"


def test_auto_mode_loopback_falls_back_without_valid_motion():
    port = _free_port()
    bridge = PicoBridge(
        host="127.0.0.1",
        port=port,
        discovery=False,
        arm_source="auto",
        auto_fallback_s=0.5,
        start_timeout=5.0,
    )
    bridge.start()
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=5.0) as sock:
            # mock device streams invalid trackers, never a valid side
            frame = json.dumps({
                "timeStampNs": int(time.time() * 1e9),
                "Motion": {
                    "left": {"sn": 1, "p": "0,1,0,0,0,0,1", "valid": False},
                    "right": {"sn": 2, "p": "0,1,0,0,0,0,1", "valid": False},
                },
            }).encode()
            deadline = time.monotonic() + 2.5
            sock.settimeout(0.1)
            messages: list[dict] = []
            parser = PacketParser(accept_head=HEAD_PC_TO_VR)
            sent_at = time.monotonic()
            while time.monotonic() < deadline:
                try:
                    chunk = sock.recv(65536)
                    if chunk:
                        for pkt in parser.feed(chunk):
                            if pkt.cmd == CMD.FROM_CONTROLLER_COMMON_FUNCTION:
                                envelope = json.loads(pkt.data.decode("utf-8"))
                                if envelope.get("functionName") == "BridgeControl":
                                    messages.append(envelope["value"])
                except socket.timeout:
                    pass
                if time.monotonic() - sent_at > 0.1:  # keep feeding frames
                    sock.sendall(pack(CMD.TO_CONTROLLER_FUNCTION, json.dumps(
                        {"functionName": "Tracking", "value": json.loads(frame)}).encode(),
                        pc_to_vr=False))
                    sent_at = time.monotonic()
    finally:
        bridge.close()

    tracking = _tracking_messages(messages)
    types = [m["type"] for m in tracking]
    assert types == ["set_motion", "set_body"], f"expected fallback sequence, got: {tracking}"
    assert tracking[0]["payload"] == {"enabled": True}
    assert tracking[1]["payload"] == {"enabled": True, "height": 1.75}
