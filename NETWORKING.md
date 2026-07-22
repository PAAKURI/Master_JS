# PAAKURI networking

The existing AI and host-authoritative P2P modes remain available. A headless dedicated server now runs the same `GameManager`, `Player`, map collision, bullet, hit, and parry code as the playable project.

## Run modes

```powershell
# AI
godot --path . -- --paakuri-ai

# P2P host/client
godot --path . -- --paakuri-host=24872
godot --path . -- --paakuri-join=127.0.0.1:24872

# One dedicated process hosts one 1v1 match
godot --headless --path . -- --paakuri-dedicated=24872
godot --path . -- --paakuri-join=127.0.0.1:24872
```

Both dedicated clients press READY to start. After a match, both press REMATCH READY. A disconnect returns the remaining client to the connected lobby so a replacement can join.

## Transport and simulation

- Channel 0: 60 Hz input batches, `UnreliableOrdered`. Each packet repeats up to eight unacknowledged frames, so jump, fire, and parry survive an isolated packet loss without head-of-line blocking.
- Channel 1: 30 Hz authoritative snapshots, `UnreliableOrdered`.
- Channel 2: reliable slot assignment, protocol/map compatibility, lobby READY, scene start, rematch, and disconnect recovery.
- The local client player predicts movement immediately and reconciles against the server ACK. Remote players render 100 ms behind with interpolation and at most 100 ms of extrapolation.
- Projectile fire is compensated by half of ENet RTT, capped at 100 ms. A ray cast preserves wall, hit, and parry checks during the compensated segment.

`ProtocolVersion` is 5 and `MapCatalogVersion` is 1. The server sends only `MapId` and `RoundId`; each client chooses its own map/background palette. Collision polygons are derived from the shared map catalog and cached locally after the first load.

## Verification

Close running editor/game instances so Godot can replace the C# assembly, then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\smoke-network.ps1
```

The smoke check covers the protocol codec, AI startup, a two-client dedicated match startup, P2P startup, and Godot runtime errors.
