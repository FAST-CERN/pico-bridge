from __future__ import annotations

import asyncio

from pico_bridge.control import CONTROL_FUNCTION_NAME
from pico_bridge.frame_store import FrameStore
from pico_bridge.runtime import PicoBridgeRuntime


def test_runtime_sends_motion_stream_control_message():
    async def run() -> list[tuple[str, object]]:
        runtime = PicoBridgeRuntime(
            host="0.0.0.0",
            port=63901,
            discovery=False,
            advertise_ip=None,
            video=None,
            video_enabled=False,
            video_frame_source=None,
            frame_store=FrameStore(),
            motion_enabled=True,
        )
        server = _FakeServer()
        runtime._server = server

        await runtime._send_motion_state()
        return server.messages

    messages = asyncio.run(run())

    assert messages == [
        (
            CONTROL_FUNCTION_NAME,
            {
                "version": 1,
                "channel": "tracking",
                "type": "set_motion",
                "payload": {"enabled": True},
            },
        )
    ]


def test_runtime_sends_video_policy_control_message():
    async def run() -> list[tuple[str, object]]:
        runtime = PicoBridgeRuntime(
            host="0.0.0.0",
            port=63901,
            discovery=False,
            advertise_ip=None,
            video="frames",
            video_enabled=False,
            video_frame_source=None,
            frame_store=FrameStore(),
        )
        server = _FakeServer()
        runtime._server = server

        await runtime._send_video_policy()
        return server.messages

    messages = asyncio.run(run())

    assert messages == [
        (
            CONTROL_FUNCTION_NAME,
            {
                "version": 1,
                "channel": "video",
                "type": "set_policy",
                "payload": {
                    "enabled": False,
                    "auto_preview": False,
                    "source": "frames",
                },
            },
        )
    ]


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


def test_runtime_body_mode_pushes_set_body_and_disables_motion():
    async def run() -> list[tuple[str, object]]:
        runtime = _make_runtime(arm_source="body", operator_height_m=1.82)
        server = _FakeServer()
        runtime._server = server

        await runtime._handle_client_connected()
        return server.messages

    messages = asyncio.run(run())
    tracking_messages = [m for m in messages if m[1]["channel"] == "tracking"]

    assert tracking_messages == [
        (
            CONTROL_FUNCTION_NAME,
            {
                "version": 1,
                "channel": "tracking",
                "type": "set_motion",
                "payload": {"enabled": False},
            },
        ),
        (
            CONTROL_FUNCTION_NAME,
            {
                "version": 1,
                "channel": "tracking",
                "type": "set_body",
                "payload": {"enabled": True, "height": 1.82},
            },
        ),
    ]


def test_runtime_tracker_mode_pushes_set_motion_only():
    async def run() -> list[tuple[str, object]]:
        runtime = _make_runtime(arm_source="tracker", motion_enabled=True)
        server = _FakeServer()
        runtime._server = server

        await runtime._handle_client_connected()
        return server.messages

    messages = asyncio.run(run())
    tracking_messages = [m for m in messages if m[1]["channel"] == "tracking"]

    assert tracking_messages == [
        (
            CONTROL_FUNCTION_NAME,
            {
                "version": 1,
                "channel": "tracking",
                "type": "set_motion",
                "payload": {"enabled": True},
            },
        )
    ]


def test_runtime_auto_mode_falls_back_to_body_without_valid_motion():
    async def run() -> list[tuple[str, object]]:
        clock = iter([1.0, 40.0, 40.0])
        runtime = _make_runtime(
            arm_source="auto",
            auto_fallback_s=15.0,
            clock=lambda: next(clock),
        )
        server = _FakeServer()
        runtime._server = server

        await runtime._handle_client_connected()
        assert server.messages[-1][1]["type"] == "set_motion"

        # tracker frames arrive but Motion sides stay invalid -> past the
        # fallback window the receiver asks the device for body tracking
        runtime._handle_tracking({"seq": 1, "timeStampNs": 1, "Motion": {
            "left": {"sn": 1, "p": "0,1,0,0,0,0,1", "valid": False},
            "right": {"sn": 2, "p": "0,1,0,0,0,0,1", "valid": False},
        }})
        await asyncio.sleep(0)  # let the scheduled fallback send run
        return server.messages

    messages = asyncio.run(run())

    set_body = [m for m in messages if m[1].get("type") == "set_body"]
    assert len(set_body) == 1
    assert set_body[0][1]["payload"] == {"enabled": True, "height": 1.75}


def test_runtime_auto_mode_stays_on_trackers_with_valid_motion():
    async def run() -> list[tuple[str, object]]:
        clock = iter([1.0, 1.0])
        runtime = _make_runtime(
            arm_source="auto",
            auto_fallback_s=15.0,
            clock=lambda: next(clock),
        )
        server = _FakeServer()
        runtime._server = server

        await runtime._handle_client_connected()

        runtime._handle_tracking({"seq": 1, "timeStampNs": 1, "Motion": {
            "left": {"sn": 1, "p": "0,1,0,0,0,0,1", "valid": True},
        }})
        # later frames, long after the fallback window, still no set_body
        runtime._handle_tracking({"seq": 2, "timeStampNs": 2, "Motion": {
            "left": {"sn": 1, "p": "0,1,0,0,0,0,1", "valid": False},
        }})
        await asyncio.sleep(0)
        return server.messages

    messages = asyncio.run(run())

    assert not [m for m in messages if m[1].get("type") == "set_body"]


def test_runtime_set_body_enabled_updates_state_and_sends():
    async def run() -> list[tuple[str, object]]:
        runtime = _make_runtime(arm_source="body", operator_height_m=1.9)
        server = _FakeServer()
        runtime._server = server

        await runtime.set_body_enabled(False)
        await runtime.set_body_enabled(True, height_m=1.66)
        return server.messages

    messages = asyncio.run(run())

    assert messages == [
        (
            CONTROL_FUNCTION_NAME,
            {
                "version": 1,
                "channel": "tracking",
                "type": "set_body",
                "payload": {"enabled": False, "height": 1.9},
            },
        ),
        (
            CONTROL_FUNCTION_NAME,
            {
                "version": 1,
                "channel": "tracking",
                "type": "set_body",
                "payload": {"enabled": True, "height": 1.66},
            },
        ),
    ]
