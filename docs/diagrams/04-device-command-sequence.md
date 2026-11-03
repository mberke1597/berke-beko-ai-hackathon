# 04 — Device command (app, remote or AI)

All three sources use the same path, so every change is recorded with its `source`. A remote change shortly after an AI change counts as implicit feedback.

```mermaid
sequenceDiagram
  autonumber
  actor M as Member
  participant UI as Home app / virtual remote
  participant API as DevicesController
  participant C as IHomeWhizClient
  participant MHW as Mock HomeWhiz API
  participant S as DeviceSimulator
  participant DB as MongoDB device_events
  participant H as DeviceHub
  participant LR as PatternLearner

  M->>UI: setpoint 24 (source remote)
  UI->>API: POST /api/devices/{id}/commands
  API->>API: check capabilities and bounds
  API->>C: send command
  C->>MHW: POST /api/mock/homewhiz/appliances/{id}/commands
  MHW->>S: apply command
  S-->>MHW: new state
  MHW-->>C: 202 accepted + state
  C-->>API: state
  API->>DB: device_event (before, after, source, member, context)
  API->>H: DeviceStateChanged
  H-->>UI: every member's screen updates
  API-->>UI: 202
  LR->>DB: hourly read
  LR->>LR: AI change followed by remote change → override delta
```

Reading uses only GET: the app and the tools pull `homes`, `rooms` and `appliances` from the mock HomeWhiz through the same client.
