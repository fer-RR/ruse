# Control API (WebSocket)

Ruse exposes a loopback-only control API for local apps. Read the URL and
token from `%APPDATA%\Ruse\control.json`:

```json
{
  "url": "http://127.0.0.1:47100",
  "token": "...",
  "pid": 1234,
  "apiVersion": 1
}
```

The token changes each launch. The port is `ControlApiPort` (default 47100);
if it is busy Ruse picks a free port and the discovery file reflects it.
To disable the API, turn off **Settings > Behavior > Control API**.
The same setting is stored as `"ControlApiEnabled"` in
`%APPDATA%\Ruse\settings.json`.

## Connect

```text
ws://127.0.0.1:<port>/api/v1/events?token=<token>
```

The query token is convenient for browsers, which cannot set an
`Authorization` header on WebSocket. Native clients may use
`Authorization: Bearer <token>` instead.

The server sends:

```json
{"type":"hello","apiVersion":1}
```

## Find nodes and parameters

```json
{"type":"get-nodes","id":1}
```

```json
{
  "type": "nodes",
  "id": 1,
  "nodes": [{
    "id": "abc123",
    "controlId": "compressor",
    "kind": "modifier",
    "title": "Compressor",
    "processors": [{
      "index": 0,
      "typeId": "compressor",
      "parameters": [{
        "index": 0,
        "name": "Threshold",
        "min": -60,
        "max": 0,
        "value": -18,
        "normalized": 0.7
      }]
    }]
  }]
}
```

Use `node.controlId` (recommended), `node.id`, or a unique `node.title`, plus
`processors[].index` and `parameters[].index`.

`controlId` is generated once and persisted with sessions and presets, so it is
stable across restarts. `id` is only stable for the running session. Set or
change it with `{"type":"set-node","nodeId":"compressor","controlId":"my-compressor"}`.

## Control

```json
{"type":"set-parameter","id":2,"nodeId":"abc123","processor":0,"parameter":0,"value":-30}
{"type":"set-parameter","id":3,"nodeId":"abc123","processor":0,"parameter":0,"normalized":0.5}
{"type":"set-node","id":4,"nodeId":"compressor","gain":0.7,"muted":false}
{"type":"set-node","id":5,"nodeId":"compressor","enabled":false}
{"type":"set-node","id":6,"nodeId":"compressor","controlId":"my-compressor"}
{"type":"transport","id":7,"running":true}
```

`set-node` accepts `gain`, `muted`, `solo` for sources/outputs and `enabled`
for effect bypass. Replies are:

```json
{"type":"ack","id":2}
{"type":"error","id":2,"message":"Effect or parameter index is out of range."}
```

After changes the server also broadcasts throttled graph events:

```json
{"type":"graph","version":42,"running":true,"nodes":[...]}
```

## Tiny Node.js example

```js
import fs from 'node:fs';
import path from 'node:path';

const info = JSON.parse(fs.readFileSync(
  path.join(process.env.APPDATA, 'Ruse', 'control.json'), 'utf8'));

const ws = new WebSocket(
  info.url.replace('http', 'ws') + '/api/v1/events?token=' +
  encodeURIComponent(info.token));

ws.onmessage = event => console.log(JSON.parse(event.data));
ws.onopen = () => ws.send(JSON.stringify({ type: 'get-nodes', id: 1 }));
```

Use `controlId` for stable external references. Re-read nodes after graph changes.
