# PICO ArUco marker telemetry

The Unity client registers `PXR_Enterprise.SetMarkerInfoCallback` after the
Enterprise service is bound and only after the PICO runtime origin agrees with
the Unity `XROrigin` mode. Registration uses:

- `Floor` with `cameraYOffsetMeters = 0` when both origins are floor-relative.
- `Device` with the live `XROrigin.CameraYOffset` when both origins are device-relative.
- No registration while the two origin modes disagree or remain unavailable.

The callback is registered once per Enterprise bind generation. The SDK wrapper
does not expose an unregister operation, so switching origin mode after
registration is unsupported; rebind or restart the app before changing between
Floor and Device experiments.

## Wire fields

Every tracking packet contains `aruco_marker_status_v1`. It records registration
state/result, origin modes, the exact camera Y offset passed to the SDK, callback
history state, and the latest callback/count metadata.

`aruco_markers_v1` is sparse: it is attached only when that output sink emits one
new callback. Each event records callback sequence, receive timestamp, bind
generation, sink name, consumer sequence gaps, truncation, and up to eight
markers. Each marker contains:

- PICO marker ID, `markerType`, and `validFlag`.
- Raw SDK source timestamp plus a finite-value flag.
- Position and quaternion after PICO's Unity callback conversion.
- Numeric validity and quaternion norm.

The fixed PC SDK state JSON buffer is 16,352 bytes. The eight-marker cap leaves
head, controllers, IMU, tracking-origin, and LBE metadata sufficient space. The
event reports both the SDK-reported and serialized counts, and explicitly marks
truncation.

The callback history is a 64-event replay window shared by independent output
sinks. TCP direct and legacy encoders use one `tcp_tracking` cursor, so switching
between them cannot duplicate an event. Local camera recording has its own
cursor. TCP reconnect policy is `resume_cursor`: unseen callbacks still in the
history are emitted after reconnection; an eviction is distinguishable from a
real consumer miss through the event sequence-gap fields.

## Semantics that remain intentionally unclaimed

PICO SDK 3.4 does not document the callback timestamp unit/clock domain, exact
pose direction, or a per-marker confidence value in this API. The telemetry
therefore labels those semantics as undocumented, records
`confidence_available = false`, and does not guess a transform direction.
Establish axes, scale, pose direction, and Floor/Device Y behavior with a
measured-marker experiment before using the pose for calibration.

Use a PICO-supported marker pattern for the first hardware test. A generic
OpenCV ArUco dictionary is not proof of compatibility with the Enterprise
tracking service.
