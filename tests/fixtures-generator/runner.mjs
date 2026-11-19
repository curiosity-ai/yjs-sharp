// Live round-trip runner for the YjsSharp compatibility tests.
//
// Reads one JSON command per line on stdin and answers one JSON line on stdout, so the C# tests can
// keep real Yjs documents alive and feed them updates produced by YjsSharp (and the other way round).
//
//   { "cmd": "newDoc", "doc": "a", "clientId": 1, "gc": true }
//   { "cmd": "apply", "doc": "a", "update": "<base64>", "v2": false }
//   { "cmd": "op", "doc": "a", "roots": { "t": "text" }, "op": { ...script op, see dsl.mjs... } }
//        -> { "updates": ["<base64 v1>", ...] }   the updates the local transaction(s) emitted
//   { "cmd": "stateVector", "doc": "a" }
//   { "cmd": "encode", "doc": "a", "stateVector": "<base64>"?, "v2": false }
//   { "cmd": "content", "doc": "a", "roots": { ... } }   -> { "content": ..., "deltas": ... }
//   { "cmd": "merge", "updates": ["<base64>", ...], "v2": false }
//   { "cmd": "exit" }
//
// Every answer is { "ok": true, "result": ... } or { "ok": false, "error": "..." }.

import * as readline from 'node:readline'
import * as Y from 'yjs'
import { applyDocOp, contentOf, b64, unb64, REMOTE } from './dsl.mjs'

/** @type {Map<string, { doc: Y.Doc, pending: Uint8Array[] }>} */
const docs = new Map()

const get = (name) => {
  const entry = docs.get(name)
  if (!entry) throw new Error('unknown doc ' + name)
  return entry
}

const handle = (msg) => {
  switch (msg.cmd) {
    case 'newDoc': {
      const doc = new Y.Doc({ gc: msg.gc !== false })
      doc.clientID = msg.clientId
      const entry = { doc, pending: [] }
      doc.on('update', (u, origin) => {
        if (origin !== REMOTE) entry.pending.push(u)
      })
      docs.set(msg.doc, entry)
      return null
    }
    case 'apply': {
      const { doc } = get(msg.doc)
      if (msg.v2) {
        Y.applyUpdateV2(doc, unb64(msg.update), REMOTE)
      } else {
        Y.applyUpdate(doc, unb64(msg.update), REMOTE)
      }
      return null
    }
    case 'op': {
      const entry = get(msg.doc)
      entry.pending = []
      applyDocOp(entry.doc, msg.roots, msg.op)
      const updates = entry.pending.map(b64)
      entry.pending = []
      return { updates }
    }
    case 'stateVector':
      return b64(Y.encodeStateVector(get(msg.doc).doc))
    case 'encode': {
      const { doc } = get(msg.doc)
      const sv = msg.stateVector ? unb64(msg.stateVector) : undefined
      return b64(msg.v2 ? Y.encodeStateAsUpdateV2(doc, sv) : Y.encodeStateAsUpdate(doc, sv))
    }
    case 'content':
      return contentOf(get(msg.doc).doc, msg.roots)
    case 'merge': {
      const updates = msg.updates.map(unb64)
      return b64(msg.v2 ? Y.mergeUpdatesV2(updates) : Y.mergeUpdates(updates))
    }
    case 'exit':
      process.exit(0)
      return null
    default:
      throw new Error('unknown command ' + msg.cmd)
  }
}

const rl = readline.createInterface({ input: process.stdin, terminal: false })
rl.on('line', (line) => {
  if (line.trim() === '') return
  let answer
  try {
    answer = { ok: true, result: handle(JSON.parse(line)) }
  } catch (err) {
    answer = { ok: false, error: String(err && err.stack ? err.stack : err) }
  }
  process.stdout.write(JSON.stringify(answer) + '\n')
})
rl.on('close', () => process.exit(0))
