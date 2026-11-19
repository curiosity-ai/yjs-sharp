// Generates the Yjs compatibility fixtures consumed by tests/YjsSharp.Tests/Compat.
//
//   cd tests/fixtures-generator && npm ci && node generate.mjs
//
// Output: tests/YjsSharp.Tests/Compat/Fixtures/*.json (committed, so CI does not need node).
// Everything is deterministic: fixed client ids, seeded randomness, no clocks in the output.

import * as fs from 'node:fs'
import * as path from 'node:path'
import { fileURLToPath } from 'node:url'
import * as Y from 'yjs'
import * as encoding from 'lib0/encoding'
import * as decoding from 'lib0/decoding'
import * as syncProtocol from 'y-protocols/sync'
import * as awarenessProtocol from 'y-protocols/awareness'
import * as authProtocol from 'y-protocols/auth'
import { Session, contentOf, resolve, b64, REMOTE } from './dsl.mjs'
import { scenarios } from './scenarios.mjs'

const here = path.dirname(fileURLToPath(import.meta.url))
const outDir = path.resolve(here, '../YjsSharp.Tests/Compat/Fixtures')
fs.mkdirSync(outDir, { recursive: true })

const yjsVersion = JSON.parse(fs.readFileSync(path.join(here, 'node_modules/yjs/package.json'), 'utf8')).version
const protocolsVersion = JSON.parse(fs.readFileSync(path.join(here, 'node_modules/y-protocols/package.json'), 'utf8')).version

/** @type {Map<string, any[]>} */
const files = new Map()
const emit = (file, entry) => {
  if (!files.has(file)) files.set(file, [])
  files.get(file).push(entry)
}

// ------------------------------------------------------------------------------- doc scenarios

const recordScenario = (scenario, session) => {
  const gc = scenario.gc !== false
  const peers = session.docs.map((doc, i) => {
    const { content, deltas } = contentOf(doc, scenario.roots)
    return {
      clientId: scenario.clientIds[i],
      updates: session.updates[i].map(u => ({ v1: b64(u.v1), v2: b64(u.v2) })),
      stateVector: b64(Y.encodeStateVector(doc)),
      update: b64(Y.encodeStateAsUpdate(doc)),
      updateV2: b64(Y.encodeStateAsUpdateV2(doc)),
      content,
      deltas
    }
  })

  const allV1 = session.updates.flat().map(u => u.v1)
  const allV2 = session.updates.flat().map(u => u.v2)
  const mergedV1 = Y.mergeUpdates(allV1)
  const mergedV2 = Y.mergeUpdatesV2(allV2)
  const fresh = new Y.Doc({ gc })
  fresh.clientID = 999999
  Y.applyUpdate(fresh, mergedV1)
  const freshV2 = new Y.Doc({ gc })
  freshV2.clientID = 999999
  Y.applyUpdateV2(freshV2, mergedV2)
  const merged = {
    v1: b64(mergedV1),
    v2: b64(mergedV2),
    stateVector: b64(Y.encodeStateVectorFromUpdate(mergedV1)),
    stateVectorV2: b64(Y.encodeStateVectorFromUpdateV2(mergedV2)),
    v1ToV2: b64(Y.convertUpdateFormatV1ToV2(mergedV1)),
    v2ToV1: b64(Y.convertUpdateFormatV2ToV1(mergedV2)),
    content: contentOf(fresh, scenario.roots).content,
    contentV2: contentOf(freshV2, scenario.roots).content
  }

  const peer0 = session.docs[0]
  const conversions = {
    v1ToV2: b64(Y.convertUpdateFormatV1ToV2(Y.encodeStateAsUpdate(peer0))),
    v2ToV1: b64(Y.convertUpdateFormatV2ToV1(Y.encodeStateAsUpdateV2(peer0)))
  }

  const diffs = session.checkpoints.map(c => ({
    id: c.id,
    peer: c.peer,
    stateVector: b64(c.stateVector),
    v1: b64(Y.diffUpdate(mergedV1, c.stateVector)),
    v2: b64(Y.diffUpdateV2(mergedV2, c.stateVector)),
    fromDoc: b64(Y.encodeStateAsUpdate(peer0, c.stateVector)),
    fromDocV2: b64(Y.encodeStateAsUpdateV2(peer0, c.stateVector))
  }))

  const snapshots = session.snapshots.map(s => {
    const entry = { id: s.id, peer: s.peer, v1: b64(s.v1), v2: b64(s.v2) }
    if (s.restored) {
      entry.restored = s.restored.content
      const doc = session.docs[s.peer]
      const deltas = {}
      for (const name of Object.keys(scenario.roots).sort()) {
        if (scenario.roots[name] === 'text') {
          deltas[name] = doc.getText(name).toDelta(s.snap).map(d => ({ ...d }))
        }
      }
      entry.deltas = JSON.parse(JSON.stringify(deltas))
    }
    return entry
  })

  const relPositions = session.relPositions.map(r => ({
    id: r.id,
    peer: r.peer,
    target: r.target,
    index: r.index,
    assoc: r.assoc,
    encoded: b64(r.encoded),
    json: JSON.parse(JSON.stringify(r.json)),
    resolved: session.docs.map(doc => {
      const abs = Y.createAbsolutePositionFromRelativePosition(Y.decodeRelativePosition(r.encoded), doc)
      if (abs === null) return null
      return { index: abs.index, assoc: abs.assoc, sameType: abs.type === resolve(doc, scenario.roots, r.target) }
    })
  }))

  return {
    name: scenario.name,
    description: scenario.description,
    gc,
    clientIds: scenario.clientIds,
    roots: scenario.roots,
    ops: scenario.ops,
    peers,
    merged,
    conversions,
    diffs,
    snapshots,
    relPositions
  }
}

for (const entry of scenarios) {
  let scenario, session
  if (entry.build) {
    ({ scenario, session } = entry.build())
  } else {
    scenario = entry.scenario
    session = new Session(scenario)
    session.run(scenario.ops)
  }
  emit(entry.file, recordScenario(scenario, session))
}

// --------------------------------------------------------------------------- sync protocol

const messageSync = 0
const messageAwareness = 1
const messageAuth = 2
const messageQueryAwareness = 3

/** What a y-websocket server / client does with an incoming sync frame: answer if there is something to say. */
const handleFrame = (doc, frame) => {
  const decoder = decoding.createDecoder(frame)
  const encoder = encoding.createEncoder()
  const type = decoding.readVarUint(decoder)
  if (type !== messageSync) throw new Error('only sync frames are handled here')
  encoding.writeVarUint(encoder, messageSync)
  const syncType = syncProtocol.readSyncMessage(decoder, encoder, doc, REMOTE)
  return { syncType, reply: encoding.length(encoder) > 1 ? encoding.toUint8Array(encoder) : null }
}

const frame = (write) => {
  const encoder = encoding.createEncoder()
  encoding.writeVarUint(encoder, messageSync)
  write(encoder)
  return encoding.toUint8Array(encoder)
}

const syncScenario = (scenario) => {
  const session = new Session(scenario)
  session.run(scenario.ops)
  const [a, b] = session.docs
  const exchange = []
  const send = (from, to, message) => {
    const { syncType, reply } = handleFrame(session.docs[to], message)
    exchange.push({ from, to, syncType, message: b64(message), reply: reply ? b64(reply) : null })
    return reply
  }
  // client a connects to server b (the y-websocket handshake): a sends step 1, b answers with
  // step 2 and its own step 1, a answers that with step 2.
  const step1A = frame(e => syncProtocol.writeSyncStep1(e, a))
  const step2B = send(0, 1, step1A)
  const step1B = frame(e => syncProtocol.writeSyncStep1(e, b))
  send(1, 0, step2B)
  const step2A = send(1, 0, step1B)
  send(0, 1, step2A)
  // then a keeps editing and forwards every update as a sync update frame
  const before = session.updates[0].length
  session.run(scenario.after || [])
  for (const u of session.updates[0].slice(before)) {
    send(0, 1, frame(e => syncProtocol.writeUpdate(e, u.v1)))
  }
  // a step 2 written against an explicit state vector, and one with none (the whole document)
  const explicitStep2 = frame(e => syncProtocol.writeSyncStep2(e, a, Y.encodeStateVector(new Y.Doc())))
  const fullStep2 = frame(e => syncProtocol.writeSyncStep2(e, a))
  return {
    name: scenario.name,
    description: scenario.description,
    gc: scenario.gc !== false,
    clientIds: scenario.clientIds,
    roots: scenario.roots,
    ops: scenario.ops,
    after: scenario.after || [],
    exchange,
    explicitStep2: b64(explicitStep2),
    fullStep2: b64(fullStep2),
    content: session.docs.map(d => contentOf(d, scenario.roots).content),
    stateVectors: session.docs.map(d => b64(Y.encodeStateVector(d)))
  }
}

emit('sync', syncScenario({
  name: 'sync-empty',
  description: 'Handshake between two empty documents',
  clientIds: [1, 2],
  roots: { t: 'text' },
  ops: []
}))
emit('sync', syncScenario({
  name: 'sync-client-server',
  description: 'Both sides have local edits before the handshake, then the client keeps editing',
  clientIds: [1, 2],
  roots: { t: 'text', m: 'map' },
  ops: [
    { op: 'textInsert', peer: 0, target: 't', index: 0, text: 'client says hi' },
    { op: 'mapSet', peer: 1, target: 'm', key: 'server', value: { started: true } },
    { op: 'textInsert', peer: 1, target: 't', index: 0, text: 'server text' }
  ],
  after: [
    { op: 'textInsert', peer: 0, target: 't', index: 0, text: '>> ' },
    { op: 'textFormat', peer: 0, target: 't', index: 0, length: 5, attrs: { bold: true } },
    { op: 'mapSet', peer: 0, target: 'm', key: 'client', value: 'connected' }
  ]
}))
emit('sync', syncScenario({
  name: 'sync-partial',
  description: 'The client already has part of the server state (a delta step 2 with a delete set)',
  clientIds: [3, 4],
  roots: { a: 'array' },
  ops: [
    { op: 'arrayPush', peer: 1, target: 'a', values: [1, 2, 3, 4] },
    { op: 'sync', from: 1, to: 0 },
    { op: 'arrayDelete', peer: 1, target: 'a', index: 1, length: 2 },
    { op: 'arrayPush', peer: 1, target: 'a', values: ['server'] },
    { op: 'arrayInsert', peer: 0, target: 'a', index: 0, values: ['client'] }
  ],
  after: [{ op: 'arrayDelete', peer: 0, target: 'a', index: 0, length: 1 }]
}))
emit('sync', syncScenario({
  name: 'sync-gc-disabled',
  description: 'Handshake between gc-disabled documents with deleted content',
  gc: false,
  clientIds: [5, 6],
  roots: { t: 'text' },
  ops: [
    { op: 'textInsert', peer: 0, target: 't', index: 0, text: 'deleted soon' },
    { op: 'textDelete', peer: 0, target: 't', index: 0, length: 8 },
    { op: 'textInsert', peer: 1, target: 't', index: 0, text: 'other' }
  ]
}))

// y-websocket framing of the other message types
{
  const encoder = encoding.createEncoder()
  encoding.writeVarUint(encoder, messageQueryAwareness)
  const query = encoding.toUint8Array(encoder)

  const auth = encoding.createEncoder()
  encoding.writeVarUint(auth, messageAuth)
  authProtocol.writePermissionDenied(auth, 'room is read-only ✋')
  const denied = encoding.toUint8Array(auth)

  const doc = new Y.Doc()
  doc.clientID = 11
  const aw = new awarenessProtocol.Awareness(doc)
  aw.setLocalState({ user: { name: 'Alice' } })
  const awEncoder = encoding.createEncoder()
  encoding.writeVarUint(awEncoder, messageAwareness)
  encoding.writeVarUint8Array(awEncoder, awarenessProtocol.encodeAwarenessUpdate(aw, [11]))
  const awarenessFrame = encoding.toUint8Array(awEncoder)
  aw.destroy()

  emit('messages', {
    name: 'framing',
    queryAwareness: b64(query),
    permissionDenied: b64(denied),
    permissionDeniedReason: 'room is read-only ✋',
    awareness: b64(awarenessFrame),
    awarenessClientId: 11,
    awarenessState: JSON.stringify({ user: { name: 'Alice' } })
  })
}

// ------------------------------------------------------------------------------- awareness

/**
 * Drives a JS Awareness through a list of local actions and records, for each one, the update event
 * and the encoded update a y-websocket provider would broadcast for it.
 */
const awarenessScenario = (name, description, clientId, actions) => {
  const doc = new Y.Doc()
  doc.clientID = clientId
  const aw = new awarenessProtocol.Awareness(doc)
  clearInterval(aw._checkInterval)
  const remoteDoc = new Y.Doc()
  remoteDoc.clientID = clientId + 1000
  const remote = new awarenessProtocol.Awareness(remoteDoc)
  clearInterval(remote._checkInterval)
  let lastUpdate = null
  let lastChange = null
  aw.on('update', (e, origin) => { lastUpdate = { ...e, origin } })
  aw.on('change', (e, origin) => { lastChange = { ...e, origin } })
  let remoteChange = null
  remote.on('change', (e, origin) => { remoteChange = { ...e, origin } })
  const steps = []
  for (const action of actions) {
    lastUpdate = null
    lastChange = null
    remoteChange = null
    switch (action.kind) {
      case 'set': aw.setLocalState(action.state); break
      case 'field': aw.setLocalStateField(action.field, action.value); break
      default: throw new Error(action.kind)
    }
    const changed = lastUpdate ? lastUpdate.added.concat(lastUpdate.updated, lastUpdate.removed) : []
    const encoded = awarenessProtocol.encodeAwarenessUpdate(aw, changed)
    awarenessProtocol.applyAwarenessUpdate(remote, encoded, 'remote')
    steps.push({
      action: { ...action, state: action.state === undefined ? undefined : JSON.stringify(action.state), value: action.value === undefined ? undefined : JSON.stringify(action.value) },
      update: lastUpdate && { added: lastUpdate.added, updated: lastUpdate.updated, removed: lastUpdate.removed },
      change: lastChange && { added: lastChange.added, updated: lastChange.updated, removed: lastChange.removed },
      encoded: b64(encoded),
      localState: aw.getLocalState() === null ? null : JSON.stringify(aw.getLocalState()),
      localClock: aw.meta.get(clientId).clock,
      remoteChange: remoteChange && { added: remoteChange.added, updated: remoteChange.updated, removed: remoteChange.removed },
      remoteStates: Object.fromEntries(Array.from(remote.getStates().entries()).map(([k, v]) => [String(k), JSON.stringify(v)])),
      remoteClocks: Object.fromEntries(Array.from(remote.meta.entries()).map(([k, v]) => [String(k), v.clock]))
    })
  }
  const all = awarenessProtocol.encodeAwarenessUpdate(aw, Array.from(aw.meta.keys()))
  aw.destroy()
  remote.destroy()
  return { name, description, clientId, steps, all: b64(all) }
}

emit('awareness', awarenessScenario('awareness-local-states', 'Setting, changing, re-setting and clearing the local state', 30, [
  { kind: 'set', state: { user: { name: 'Anonymous 50', color: '#30bced', colorLight: '#30bced33' } } },
  { kind: 'set', state: { user: { name: 'Anonymous 50', color: '#30bced', colorLight: '#30bced33' } } },
  { kind: 'field', field: 'cursor', value: { anchor: 3, head: 7 } },
  { kind: 'field', field: 'cursor', value: null },
  { kind: 'set', state: { x: 1.5, y: -2, z: 1e21, w: 0.000001, s: 'quote " backslash \\ newline \n tab \t ctrl \u0001 é 😀' } },
  { kind: 'set', state: null }
]))
emit('awareness', awarenessScenario('awareness-big-client', 'A client id above 2^31 is written as a var uint', 4294967295, [
  { kind: 'set', state: { n: [1, 2, 3], nested: { deep: { deeper: true } } } },
  { kind: 'set', state: {} }
]))

/** Raw awareness updates applied to a fresh awareness, with the resulting states, clocks and events. */
const writeAwarenessUpdate = (entries) => {
  const encoder = encoding.createEncoder()
  encoding.writeVarUint(encoder, entries.length)
  for (const [client, clock, json] of entries) {
    encoding.writeVarUint(encoder, client)
    encoding.writeVarUint(encoder, clock)
    encoding.writeVarString(encoder, json)
  }
  return encoding.toUint8Array(encoder)
}

{
  const localId = 1
  const doc = new Y.Doc()
  doc.clientID = localId
  const aw = new awarenessProtocol.Awareness(doc)
  clearInterval(aw._checkInterval)
  let update = null
  let change = null
  aw.on('update', (e, origin) => { update = { added: e.added, updated: e.updated, removed: e.removed, origin } })
  aw.on('change', (e, origin) => { change = { added: e.added, updated: e.updated, removed: e.removed, origin } })
  const inputs = [
    ['new client', [[2, 1, '{"a":1}']]],
    ['same clock is ignored', [[2, 1, '{"a":2}']]],
    ['newer clock, same state', [[2, 2, '{"a":1}']]],
    ['newer clock, other state', [[2, 3, '{"a":2}']]],
    ['older clock is ignored', [[2, 1, '{"a":3}']]],
    ['null with the same clock removes', [[2, 3, 'null']]],
    ['null again does nothing', [[2, 3, 'null']]],
    ['comes back', [[2, 4, '{ "back" : true }']]],
    ['several clients at once', [[3, 1, '{"c":3}'], [4, 0, '{"c":4}'], [2, 5, 'null']]],
    ['unknown client removed', [[5, 7, 'null']]],
    ['remote removes the local state', [[localId, 0, 'null']]],
    ['remote overrides the local state', [[localId, 5, '{"hijack":true}']]]
  ]
  const steps = []
  for (const [label, entries] of inputs) {
    update = null
    change = null
    const bytes = writeAwarenessUpdate(entries)
    awarenessProtocol.applyAwarenessUpdate(aw, bytes, 'remote-origin')
    steps.push({
      label,
      update: b64(bytes),
      updateEvent: update && { added: update.added, updated: update.updated, removed: update.removed },
      changeEvent: change && { added: change.added, updated: change.updated, removed: change.removed },
      states: Object.fromEntries(Array.from(aw.getStates().entries()).map(([k, v]) => [String(k), JSON.stringify(v)])),
      clocks: Object.fromEntries(Array.from(aw.meta.entries()).map(([k, v]) => [String(k), v.clock])),
      reencoded: b64(awarenessProtocol.encodeAwarenessUpdate(aw, Array.from(aw.meta.keys())))
    })
  }
  // removeAwarenessStates over a remote and the local client
  update = null
  awarenessProtocol.removeAwarenessStates(aw, [3, localId, 99], 'timeout')
  const afterRemove = {
    updateEvent: update && { added: update.added, updated: update.updated, removed: update.removed },
    states: Object.fromEntries(Array.from(aw.getStates().entries()).map(([k, v]) => [String(k), JSON.stringify(v)])),
    clocks: Object.fromEntries(Array.from(aw.meta.entries()).map(([k, v]) => [String(k), v.clock])),
    encoded: b64(awarenessProtocol.encodeAwarenessUpdate(aw, [3, localId]))
  }
  aw.destroy()
  emit('awareness-apply', { name: 'apply-sequence', localClientId: localId, steps, removeAwarenessStates: { clients: [3, localId, 99], ...afterRemove } })
}

{
  // modifyAwarenessUpdate and JSON.stringify canonicalisation
  const source = writeAwarenessUpdate([
    [7, 3, '{"user":{"name":"Mallory","color":"#ff0000"},"cursor":null}'],
    [8, 1, 'null'],
    [9, 2, '{ "user" : { "name" : "Bob" , "n" : 1.0, "e": 1E3, "u": "\\u00e9\\ud83d\\ude00", "big": 12345678901234567890 } }']
  ])
  const renamed = awarenessProtocol.modifyAwarenessUpdate(source, (state) => {
    if (state === null) return null
    return { ...state, user: { ...state.user, name: 'Server' } }
  })
  const identity = awarenessProtocol.modifyAwarenessUpdate(source, (state) => state)
  const json = [
    '{"a":1}', '{ "a" : [ 1 , 2 , { "b" : null } ] }', '{"n":1.0,"m":-0.0,"e":1e3,"f":1.5e-7,"g":123456789012345680000,"h":0.1}',
    '"\\u00e9\\u2028\\u2029\\u007f\\u0000"', '{"emoji":"😀","escaped":"\\ud83d\\ude00","lone":"\\ud800"}', 'true', '[]', '{}', '{"a":{"b":{"c":[[],{}]}}}', '-5e-324', '1.7976931348623157e308'
  ].map(input => ({ input, stringified: JSON.stringify(JSON.parse(input)) }))
  emit('awareness-modify', { name: 'modify', source: b64(source), renamed: b64(renamed), identity: b64(identity), json })
}

// ------------------------------------------------------------------------------------ write

for (const [file, entries] of files) {
  const out = { generator: { yjs: yjsVersion, 'y-protocols': protocolsVersion }, scenarios: entries }
  const compact = file === 'large' || file === 'random'
  fs.writeFileSync(path.join(outDir, file + '.json'), (compact ? JSON.stringify(out) : JSON.stringify(out, null, 1)) + '\n')
  console.log(`${file}.json: ${entries.length} scenario(s)`)
}
