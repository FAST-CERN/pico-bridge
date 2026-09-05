"""Async runtime internals for the public PICO bridge API."""

from __future__ import annotations

import asyncio
import logging
import time
from dataclasses import dataclass
from typing import Any, Callable

from .camera_request import CameraRequest
from .control import (
    CONTROL_FUNCTION_NAME,
    build_body_stream_message,
    build_mount_correction_message,
    build_motion_stream_message,
    build_video_policy_message,
)
from .discovery import UdpBroadcaster
from .frame_store import FrameStore
from .tcp_server import PicoBridgeServer
from .webrtc_sender import ExternalVideoFrameSource, WebRtcVideoSender

log = logging.getLogger("pico_bridge.runtime")

RawTrackingCallback = Callable[[dict[str, Any]], None]

ARM_SOURCES = ("tracker", "body", "auto")


@dataclass(frozen=True)
class RuntimeStatus:
    connected: bool
    device_sn: str
    video_enabled: bool
    video_running: bool
    video_source: str | None


class PicoBridgeRuntime:
    """Owns the async transport objects for one in-process bridge."""

    def __init__(
        self,
        *,
        host: str,
        port: int,
        discovery: bool,
        advertise_ip: str | None,
        video: str | None,
        video_enabled: bool,
        video_frame_source: ExternalVideoFrameSource | None,
        motion_enabled: bool = False,
        arm_source: str = "tracker",
        operator_height_m: float = 1.75,
        mount_correction: dict[str, Any] | None = None,
        auto_fallback_s: float = 15.0,
        clock: Callable[[], float] | None = None,
        frame_store: FrameStore,
        print_tracking: bool = False,
        on_raw_tracking: RawTrackingCallback | None = None,
        on_started: Callable[[], None] | None = None,
    ):
        if arm_source not in ARM_SOURCES:
            raise ValueError(f"arm_source must be one of {ARM_SOURCES}, got {arm_source!r}")
        self._host = host
        self._port = port
        self._discovery_enabled = discovery
        self._advertise_ip = advertise_ip
        self._video_source = video
        self._video_enabled = bool(video_enabled)
        self._video_frame_source = video_frame_source
        self._motion_enabled = bool(motion_enabled)
        self._arm_source = str(arm_source)
        self._operator_height_m = float(operator_height_m)
        self._mount_correction: dict[str, Any] | None = (
            dict(mount_correction) if mount_correction is not None else None
        )
        self._auto_fallback_s = float(auto_fallback_s)
        self._clock = clock or time.monotonic
        self._body_enabled = self._arm_source == "body"
        self._connected_at: float | None = None
        self._auto_fell_back = False
        self._auto_saw_valid_motion = False
        self._frame_store = frame_store
        self._print_tracking = print_tracking
        self._on_raw_tracking = on_raw_tracking
        self._on_started = on_started
        self._stop_event: asyncio.Event | None = None
        self._stop_requested = False
        self._server: PicoBridgeServer | None = None
        self._broadcaster: UdpBroadcaster | None = None
        self._webrtc_sender: WebRtcVideoSender | None = None

    async def run(self) -> None:
        self._stop_event = asyncio.Event()
        if self._stop_requested:
            self._stop_event.set()

        async def server_send_function(name: str, value: Any) -> None:
            if self._server is not None:
                await self._server.send_function(name, value)

        self._server = PicoBridgeServer(
            host=self._host,
            port=self._port,
            on_tracking=self._handle_tracking,
            on_function=self._handle_function,
            on_camera_request=self._handle_camera_request,
            on_camera_stop=self._handle_camera_stop,
            on_client_connected=self._handle_client_connected,
        )
        if self._video_source is not None:
            self._webrtc_sender = WebRtcVideoSender(
                server_send_function,
                source=self._video_source,
                frame_source=self._video_frame_source,
            )

        self._broadcaster = UdpBroadcaster(
            tcp_port=self._port,
            advertise_ip=self._advertise_ip,
        )

        try:
            await self._server.start()
            if self._discovery_enabled:
                await self._broadcaster.start()
        except Exception:
            await self.stop()
            raise

        log.info("PICO bridge runtime listening on %s:%d", self._host, self._port)
        if self._on_started is not None:
            self._on_started()
        try:
            await self._stop_event.wait()
        finally:
            await self.stop()

    async def stop(self) -> None:
        webrtc_sender = self._webrtc_sender
        broadcaster = self._broadcaster
        server = self._server
        self._webrtc_sender = None
        self._broadcaster = None
        self._server = None

        if webrtc_sender is not None:
            await webrtc_sender.stop()
        if broadcaster is not None:
            await broadcaster.stop()
        if server is not None:
            await server.stop()

    def request_stop(self) -> None:
        self._stop_requested = True
        if self._stop_event is not None:
            self._stop_event.set()

    def status(self) -> RuntimeStatus:
        server = self._server
        webrtc_sender = self._webrtc_sender
        return RuntimeStatus(
            connected=False if server is None else server.connected,
            device_sn="" if server is None else server.device_sn,
            video_enabled=self._video_enabled,
            video_running=False if webrtc_sender is None else webrtc_sender.is_running,
            video_source=self._video_source,
        )

    async def set_video_enabled(self, enabled: bool) -> None:
        self._video_enabled = bool(enabled)
        if not self._video_enabled and self._webrtc_sender is not None:
            await self._webrtc_sender.stop()
        await self._send_video_policy()

    async def set_motion_enabled(self, enabled: bool) -> None:
        self._motion_enabled = bool(enabled)
        await self._send_motion_state()

    async def set_body_enabled(self, enabled: bool, height_m: float | None = None) -> None:
        """Switch the device to PICO body tracking (mocap map t07)."""
        self._body_enabled = bool(enabled)
        if height_m is not None:
            self._operator_height_m = float(height_m)
        await self._send_body_state()

    async def set_mount_correction(self, params: dict[str, Any] | None) -> None:
        """Push strapped-controller mount-correction params to the app (t07).

        ``None`` clears the configured state (subsequent connects push
        nothing, preserving values tuned in-headset and persisted on the
        device). Only pushes when a device is connected.
        """
        self._mount_correction = dict(params) if params is not None else None
        await self._send_mount_correction()

    async def _send_motion_state(self) -> None:
        server = self._server
        if server is None or not server.connected:
            return
        await server.send_function(
            CONTROL_FUNCTION_NAME,
            build_motion_stream_message(enabled=self._effective_motion_enabled),
        )

    async def _send_body_state(self) -> None:
        server = self._server
        if server is None or not server.connected:
            return
        await server.send_function(
            CONTROL_FUNCTION_NAME,
            build_body_stream_message(enabled=self._body_enabled, height_m=self._operator_height_m),
        )

    async def _send_mount_correction(self) -> None:
        server = self._server
        if server is None or not server.connected or self._mount_correction is None:
            return
        params = self._mount_correction
        await server.send_function(
            CONTROL_FUNCTION_NAME,
            build_mount_correction_message(
                enabled=bool(params.get("enabled", True)),
                left=params.get("left"),
                right=params.get("right"),
            ),
        )

    @property
    def _effective_motion_enabled(self) -> bool:
        # Arm-source modes (t07): tracker defers to the motion flag, auto
        # asks for trackers up front (fallback watches the stream), body
        # explicitly disables motion (device-side mutex mirrors it).
        if self._arm_source == "tracker":
            return self._motion_enabled
        return self._arm_source == "auto"

    def _handle_tracking(self, data: dict[str, Any]) -> None:
        frame = self._frame_store.append_payload(data)
        if self._on_raw_tracking is not None:
            self._on_raw_tracking(data)
        if self._print_tracking:
            print(f"[{frame.seq:>6}] {frame.summary()}", flush=True)
        self._maybe_auto_fallback(data)

    def _maybe_auto_fallback(self, data: dict[str, Any]) -> None:
        """arm_source=auto: sticky fallback to body tracking (t07).

        If no valid motion-tracker side is seen within the fallback window
        after connecting (trackers off / out of FOV / worn over gloves), ask
        the device for body tracking instead. Sticky by design — no
        oscillation back once fallen back.
        """
        if self._arm_source != "auto" or self._auto_fell_back or self._connected_at is None:
            return

        motion = data.get("Motion")
        if isinstance(motion, dict):
            for side in ("left", "right"):
                state = motion.get(side)
                if isinstance(state, dict) and state.get("valid"):
                    self._auto_saw_valid_motion = True
        if self._auto_saw_valid_motion:
            return

        if self._clock() - self._connected_at < self._auto_fallback_s:
            return

        self._auto_fell_back = True
        self._body_enabled = True
        log.info(
            "arm_source=auto: no valid motion tracker within %.1fs of connect — requesting body tracking",
            self._auto_fallback_s,
        )
        self._schedule_sender_task(self._send_body_state(), "auto-fallback to body tracking")

    def _handle_function(self, name: str, value: Any) -> None:
        sender = self._webrtc_sender
        if sender is not None:
            if name == "WebRtcAnswer":
                self._schedule_sender_task(sender.handle_answer(value), "handle WebRTC answer")
                return
            if name == "WebRtcIceCandidate":
                self._schedule_sender_task(sender.handle_ice_candidate(value), "handle WebRTC ICE candidate")
                return
        log.info("function: %s = %s", name, value)

    async def _handle_client_connected(self) -> None:
        self._connected_at = self._clock()
        self._auto_fell_back = False
        self._auto_saw_valid_motion = False
        await self._send_video_policy()
        await self._send_motion_state()
        if self._arm_source == "body":
            await self._send_body_state()
        await self._send_mount_correction()

    async def _send_video_policy(self) -> None:
        server = self._server
        if server is None or not server.connected:
            return
        await server.send_function(
            CONTROL_FUNCTION_NAME,
            build_video_policy_message(enabled=self._video_enabled, source=self._video_source),
        )

    @staticmethod
    def _schedule_sender_task(coro: Any, label: str) -> asyncio.Task:
        task = asyncio.create_task(coro)

        def log_failure(done: asyncio.Task) -> None:
            try:
                done.result()
            except asyncio.CancelledError:
                pass
            except Exception:
                log.exception("failed to %s", label)

        task.add_done_callback(log_failure)
        return task

    def _handle_camera_request(self, req: CameraRequest) -> None:
        sender = self._webrtc_sender
        if sender is None:
            log.info("WebRTC camera request ignored because video is disabled")
            return
        if not self._video_enabled:
            log.info("WebRTC camera request ignored because video is disabled by PC policy")
            return

        async def start_video() -> None:
            try:
                await sender.start(req)
            except Exception:
                log.exception("failed to start WebRTC video sender")

        self._schedule_sender_task(start_video(), "start WebRTC video sender")

    def _handle_camera_stop(self) -> asyncio.Task | None:
        sender = self._webrtc_sender
        if sender is None:
            return None

        async def stop_video() -> None:
            try:
                await sender.stop()
            except Exception:
                log.exception("failed to stop WebRTC video sender")

        return self._schedule_sender_task(stop_video(), "stop WebRTC video sender")
