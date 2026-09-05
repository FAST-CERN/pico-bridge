"""Bodytrack-deploy t07: BridgeControl tracking/set_mount_correction push path.

The strapped-controller mount correction moves to the Unity app side; the
receiver injects per-side yaw/level parameters (degrees) from a JSON file.
Strictly opt-in: unconfigured receivers must NOT push (an empty push would
clobber in-headset-tuned values persisted on the device, t08).
"""

from __future__ import annotations

import asyncio
import json
import socket
import time

import pytest

from pico_bridge.bridge import PicoBridge
from pico_bridge.control import (
    CONTROL_FUNCTION_NAME,
    build_mount_correction_message,
)
from pico_bridge.frame_store import FrameStore
from pico_bridge.protocol import (
    CMD,
    HEAD_PC_TO_VR,
    PacketParser,
)
from pico_bridge.runtime import PicoBridgeRuntime

_PARAMS = {
    "enabled": True,
    "left": {"yaw": 15.0, "level": -4.0},
    "right": {"yaw": -27.0, "level": 3.0},
}


class _FakeServer:
    connected = True

    def __init__(self) -> None:
        self.messages: list[tuple[str, object]] = []

    async def send_function(self, name: str, value: object) -> None:
        self.messages.append((name, value))


def _make_runtime(**overrides: object) -> PicoBridgeRuntime:
    kwargs: dict[str, object] = {
        "host": "0.0.0.0",
        "port": 63901,
        "discovery": False,
        "advertise_ip": None,
        "video": None,
        "video_enabled": False,
        "video_frame_source": None,
        "frame_store": FrameStore(),
    }
    kwargs.update(overrides)
    return PicoBridgeRuntime(**kwargs)


def test_build_mount_correction_message_envelope():
    message = build_mount_correction_message(
        enabled=True,
        left={"yaw": 15.0, "level": -4.0},
        right={"yaw": -27.0, "level": 3.0},
    )
    assert message == {
        "version": 1,
        "channel": "tracking",
        "type": "set_mount_correction",
        "payload": {
            "enabled": True,
            "left": {"yaw": 15.0, "level": -4.0},
            "right": {"yaw": -27.0, "level": 3.0},
        },
    }


def test_runtime_pushes_mount_correction_on_connect_when_configured():
    async def run() -> list[tuple[str, object]]:
        runtime = _make_runtime(arm_source="body", mount_correction=dict(_PARAMS))
        server = _FakeServer()
        runtime._server = server

        await runtime._handle_client_connected()
        return server.messages

    messages = asyncio.run(run())
    tracking = [m for m in messages if m[1]["channel"] == "tracking"]

    assert tracking[-1] == (
        CONTROL_FUNCTION_NAME,
        {
            "version": 1,
            "channel": "tracking",
            "type": "set_mount_correction",
            "payload": dict(_PARAMS),
        },
    )


def test_runtime_does_not_push_mount_correction_by_default():
    async def run() -> list[tuple[str, object]]:
        runtime = _make_runtime(arm_source="body")
        server = _FakeServer()
        runtime._server = server

        await runtime._handle_client_connected()
        return server.messages

    messages = asyncio.run(run())
    assert not [m for m in messages if m[1].get("type") == "set_mount_correction"]


def test_runtime_set_mount_correction_updates_state_and_sends():
    async def run() -> tuple[list[dict], list[dict]]:
        runtime = _make_runtime()
        server = _FakeServer()
        runtime._server = server

        await runtime.set_mount_correction({"enabled": True, "left": {"yaw": 5.0, "level": 0.0}})
        sent = [m[1]["payload"] for m in server.messages if m[1].get("type") == "set_mount_correction"]

        await runtime.set_mount_correction(None)  # explicit disable clears state
        server.messages.clear()
        await runtime._handle_client_connected()
        after_clear = [
            m[1]["payload"] for m in server.messages if m[1].get("type") == "set_mount_correction"
        ]
        return sent, after_clear

    sent, after_clear = asyncio.run(run())

    assert sent == [
        {"enabled": True, "left": {"yaw": 5.0, "level": 0.0}},
    ]
    assert after_clear == []


def test_load_mount_correction_reads_json(tmp_path):
    from pico_bridge.cli import load_mount_correction

    path = tmp_path / "mount.json"
    path.write_text(json.dumps(_PARAMS), encoding="utf-8")

    assert load_mount_correction(str(path)) == _PARAMS


def test_load_mount_correction_rejects_malformed(tmp_path):
    from pico_bridge.cli import load_mount_correction

    bad = tmp_path / "bad.json"
    bad.write_text(json.dumps({"left": {"yaw": "not-a-number"}}), encoding="utf-8")
    with pytest.raises(SystemExit):
        load_mount_correction(str(bad))


def test_loopback_pushes_mount_correction_over_real_tcp():
    port = _free_port()
    bridge = PicoBridge(
        host="127.0.0.1",
        port=port,
        discovery=False,
        arm_source="body",
        operator_height_m=1.66,
        mount_correction=dict(_PARAMS),
        start_timeout=5.0,
    )
    bridge.start()
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=5.0) as sock:
            messages = _read_control_messages(sock, deadline_s=1.5)
    finally:
        bridge.close()

    tracking = [m for m in messages if m.get("channel") == "tracking"]
    assert [(m["type"], m["payload"]) for m in tracking] == [
        ("set_motion", {"enabled": False}),
        ("set_body", {"enabled": True, "height": 1.66}),
        ("set_mount_correction", dict(_PARAMS)),
    ], f"unexpected tracking control sequence: {tracking}"



def _free_port() -> int:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return int(probe.getsockname()[1])


def _read_control_messages(sock: socket.socket, deadline_s: float) -> list[dict]:
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
