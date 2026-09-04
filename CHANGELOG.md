# Changelog

## [0.2.4] - 2026-09-04

### Added

- Arm-source mode selection (mocap map t07): `PicoBridge(arm_source=...)` /
  CLI `--arm-source {tracker,body,auto}` (default `tracker`). `body` pushes
  `BridgeControl tracking/set_body` on connect (device starts PICO body
  tracking with bone lengths from `--operator-height`, motion streaming off);
  `auto` requests trackers first and stickily falls back to body tracking if
  no valid tracker side appears within the fallback window (default 15 s).
- `PicoBridge.set_body_enabled(enabled, height_m=None)` runtime toggle.

## [0.2.3] - 2026-09-03

- Motion-tracker collection on device (side-first `Motion` wire, SN
  auto-binding, `BridgeControl tracking/set_motion` toggle); receiver
  requires/forwards `motion_enabled`.

## [0.2.2] - 2026-09-02

### Added

- `PicoFrame.trackers` (`MotionFrame`): parses the `Motion` field sent by the
  PICO bridge app (motion trackers bound to left/right). Exposes per-side
  `TrackerState` (SN, pose, PICO validity flag). Recording/replay carries the
  field verbatim. Old apps without `Motion` yield an inactive frame.

## [0.2.1] - 2026-05-22

- Added head-gaze UI following for the headset panel.
- Added receiver tracking recording with `--record`.
- Fixed tracking pose semantics and packet resync handling.
- Fixed the XR origin floor offset.
- Documented receiver architecture support.

## [0.2.0] - 2026-05-11

- Added pushed PC video frame streaming.
- Added PC video policy controls and camera extras.
- Updated tracking UI label casing.
- Documented ARM RealSense dependency behavior.

## [0.1.0] - 2026-05-03

- Published the initial PICO 4 / PICO 4 Ultra APK.
- Added headset, controller, hand, body, and Motion Tracker streaming to PC.
- Added the PC receiver Python SDK and CLI.
- Added optional PC camera video return to the headset.
