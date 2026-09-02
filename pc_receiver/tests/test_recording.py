from __future__ import annotations

import json

import numpy as np

from pico_bridge.recording import RECORDING_FORMAT, RECORDING_VERSION, TrackingRecorder


def test_tracking_recorder_writes_metadata_and_frames(tmp_path):
    path = tmp_path / "tracking.jsonl"

    with TrackingRecorder(path) as recorder:
        recorder.record_tracking({"timeStampNs": 42, "Head": {"pose": [1, 2, 3, 0, 0, 0, 1]}})

    lines = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]

    assert lines[0]["type"] == "metadata"
    assert lines[0]["format"] == RECORDING_FORMAT
    assert lines[0]["version"] == RECORDING_VERSION
    assert lines[1] == {
        "type": "tracking",
        "seq": 1,
        "recorded_at_ns": lines[1]["recorded_at_ns"],
        "payload": {"timeStampNs": 42, "Head": {"pose": [1, 2, 3, 0, 0, 0, 1]}},
    }
    assert recorder.frame_count == 1


def test_tracking_recorder_accepts_directory_path(tmp_path):
    with TrackingRecorder(tmp_path) as recorder:
        path = recorder.path

    assert path.parent == tmp_path
    assert path.name.startswith("tracking_")
    assert path.suffix == ".jsonl"


def test_recording_roundtrip_preserves_motion_trackers(tmp_path):
    from pico_bridge.frames import PicoFrame

    path = tmp_path / "tracking.jsonl"
    payload = {
        "timeStampNs": 42,
        "Motion": {
            "poseSpace": "pico_tracker_local",
            "left": {"sn": 12345678901, "p": "0.1,0.2,0.3,0,0,0,1", "valid": 1},
            "right": {"sn": 98765432109, "p": "0.4,0.5,0.6,0.1,0.2,0.3,0.9", "valid": 0},
        },
    }

    with TrackingRecorder(path) as recorder:
        recorder.record_tracking(payload)

    lines = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
    assert lines[1]["payload"] == payload

    frame = PicoFrame.from_tracking_payload(lines[1]["payload"], seq=1, receive_time_s=2.0)
    assert frame.trackers.active is True
    assert frame.trackers.left is not None
    assert frame.trackers.left.sn == 12345678901
    assert frame.trackers.left.valid is True
    np.testing.assert_allclose(frame.trackers.left.pose.position, [0.1, 0.2, 0.3])
    assert frame.trackers.right is not None
    assert frame.trackers.right.valid is False
