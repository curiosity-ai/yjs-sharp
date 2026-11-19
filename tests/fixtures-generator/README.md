# Yjs compatibility fixture generator

Generates the fixtures in `tests/YjsSharp.Tests/Compat/Fixtures/` with the real Yjs implementation
(versions pinned exactly in `package.json`: yjs 13.6.33, y-protocols 1.0.7), and hosts the live
round-trip runner the C# tests talk to.

```bash
cd tests/fixtures-generator
npm ci              # node 18+
node generate.mjs   # rewrites ../YjsSharp.Tests/Compat/Fixtures/*.json
```

- `scenarios.mjs` - the document scenarios: operation scripts (fixed client ids, seeded randomness).
- `dsl.mjs` - the interpreter for those scripts. `tests/YjsSharp.Tests/Compat/ScenarioRunner.cs` is its C# twin;
  a new operation has to be added to both.
- `generate.mjs` - runs every scenario and records updates (v1 and v2), state vectors, merged and diffed
  updates, conversions, snapshots, relative positions and the resulting content; plus the sync-protocol
  handshakes, the y-websocket framing and awareness updates.
- `runner.mjs` - a line-based JSON server over stdin/stdout keeping live Yjs documents for the round-trip and
  fuzz tests (`LiveRoundTripTests`). Those tests are inconclusive when node or `node_modules` is missing.

The fixtures are committed, so building and testing YjsSharp does not need node.
