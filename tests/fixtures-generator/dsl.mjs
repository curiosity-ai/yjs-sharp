// Operation scripts shared by the fixture generator (generate.mjs) and the live round-trip runner
// (runner.mjs). The C# side (tests/YjsSharp.Tests/Compat/ScenarioRunner.cs) interprets the very same
// scripts, which is what makes a byte-for-byte comparison of the two implementations possible.
//
// A target is either a root name ("t") or a path whose first element is a root name and whose
// following elements are map keys (strings) or array / xml child indices (numbers).
//
// Values are plain JSON ("Any"), except objects carrying a "$y" key:
//   { "$y": "text" | "array" | "map" | "xmlText" | "xmlFragment" }   an empty, prelim shared type
//   { "$y": "xmlElement", "name": "p" }                              an empty xml element
//   { "$y": "bytes", "b64": "..." }                                  a Uint8Array
//   { "$y": "doc", "guid": "...", "meta": any?, "autoLoad": bool? }  a subdocument

import * as Y from 'yjs'

/** Origin used when applying updates that came from another peer, so they are not logged. */
export const REMOTE = 'remote'

export const b64 = (u8) => Buffer.from(u8).toString('base64')
export const unb64 = (s) => new Uint8Array(Buffer.from(s, 'base64'))

/**
 * @param {string} kind
 * @param {Y.Doc} doc
 * @param {string} name
 */
export const getRoot = (doc, name, kind) => {
  switch (kind) {
    case 'text': return doc.getText(name)
    case 'array': return doc.getArray(name)
    case 'map': return doc.getMap(name)
    case 'xmlFragment': return doc.getXmlFragment(name)
    default: throw new Error('unknown root kind ' + kind)
  }
}

/**
 * @param {Y.Doc} doc
 * @param {Record<string,string>} roots
 * @param {string|Array<string|number>} target
 */
export const resolve = (doc, roots, target) => {
  const path = typeof target === 'string' ? [target] : target
  let cur = getRoot(doc, /** @type {string} */ (path[0]), roots[/** @type {string} */ (path[0])])
  for (let i = 1; i < path.length; i++) {
    const step = path[i]
    if (cur instanceof Y.Map) {
      cur = cur.get(/** @type {string} */ (step))
    } else if (cur instanceof Y.Array) {
      cur = cur.get(/** @type {number} */ (step))
    } else if (cur instanceof Y.XmlFragment || cur instanceof Y.XmlElement) {
      cur = cur.get(/** @type {number} */ (step))
    } else {
      throw new Error('cannot step into ' + String(cur) + ' with ' + step)
    }
  }
  return cur
}

/** Turns a script value into a value Yjs accepts. */
export const toYValue = (v) => {
  if (v === null || typeof v !== 'object') {
    return v
  }
  if (Array.isArray(v)) {
    return v.map(toYValue)
  }
  if (typeof v.$y === 'string') {
    switch (v.$y) {
      case 'text': return new Y.Text()
      case 'array': return new Y.Array()
      case 'map': return new Y.Map()
      case 'xmlText': return new Y.XmlText()
      case 'xmlFragment': return new Y.XmlFragment()
      case 'xmlElement': return new Y.XmlElement(v.name)
      case 'bytes': return unb64(v.b64)
      case 'doc': {
        const opts = { guid: v.guid }
        if (v.meta !== undefined) opts.meta = v.meta
        if (v.autoLoad !== undefined) opts.autoLoad = v.autoLoad
        return new Y.Doc(opts)
      }
      default: throw new Error('unknown $y ' + v.$y)
    }
  }
  const o = {}
  for (const k of Object.keys(v)) {
    o[k] = toYValue(v[k])
  }
  return o
}

/** Turns a Yjs value into canonical JSON (map keys sorted, bytes / docs tagged). */
export const toPlain = (v) => {
  if (v === undefined || v === null) {
    return null
  }
  if (v instanceof Y.XmlText || v instanceof Y.XmlElement || v instanceof Y.XmlFragment || v instanceof Y.XmlHook) {
    return v.toString()
  }
  if (v instanceof Y.Text) {
    return v.toString()
  }
  if (v instanceof Y.Array) {
    return v.toArray().map(toPlain)
  }
  if (v instanceof Y.Map) {
    const o = {}
    for (const k of Array.from(v.keys()).sort()) {
      o[k] = toPlain(v.get(k))
    }
    return o
  }
  if (v instanceof Y.Doc) {
    return { $doc: v.guid }
  }
  if (v instanceof Uint8Array) {
    return { $bytes: b64(v) }
  }
  if (Array.isArray(v)) {
    return v.map(toPlain)
  }
  if (typeof v === 'object') {
    const o = {}
    for (const k of Object.keys(v).sort()) {
      o[k] = toPlain(v[k])
    }
    return o
  }
  return v
}

export const deltaToPlain = (delta) => delta.map(d => {
  const o = {}
  if (d.insert !== undefined) o.insert = toPlain(d.insert)
  if (d.delete !== undefined) o.delete = d.delete
  if (d.retain !== undefined) o.retain = d.retain
  if (d.attributes !== undefined) o.attributes = toPlain(d.attributes)
  return o
})

export const contentOf = (doc, roots, snapshot) => {
  const content = {}
  const deltas = {}
  for (const name of Object.keys(roots).sort()) {
    const root = getRoot(doc, name, roots[name])
    content[name] = toPlain(root)
    if (roots[name] === 'text') {
      deltas[name] = deltaToPlain(snapshot ? root.toDelta(snapshot) : root.toDelta())
    }
  }
  return { content, deltas }
}

/**
 * Applies one operation to a single document. `sync`-style operations that involve several peers are
 * handled by the Session below.
 */
export const applyDocOp = (doc, roots, op) => {
  switch (op.op) {
    case 'transact':
      doc.transact(() => {
        for (const inner of op.ops) applyDocOp(doc, roots, inner)
      })
      return
    case 'textInsert':
      resolve(doc, roots, op.target).insert(op.index, op.text, op.attrs ? toYValue(op.attrs) : undefined)
      return
    case 'textDelete':
      resolve(doc, roots, op.target).delete(op.index, op.length)
      return
    case 'textFormat':
      resolve(doc, roots, op.target).format(op.index, op.length, toYValue(op.attrs))
      return
    case 'textInsertEmbed':
      resolve(doc, roots, op.target).insertEmbed(op.index, toYValue(op.embed), op.attrs ? toYValue(op.attrs) : undefined)
      return
    case 'textApplyDelta':
      resolve(doc, roots, op.target).applyDelta(op.delta.map(d => {
        const o = { ...d }
        if (o.insert !== undefined) o.insert = toYValue(o.insert)
        if (o.attributes !== undefined) o.attributes = toYValue(o.attributes)
        return o
      }))
      return
    case 'arrayInsert':
      resolve(doc, roots, op.target).insert(op.index, op.values.map(toYValue))
      return
    case 'arrayPush':
      resolve(doc, roots, op.target).push(op.values.map(toYValue))
      return
    case 'arrayDelete':
      resolve(doc, roots, op.target).delete(op.index, op.length)
      return
    case 'mapSet':
      resolve(doc, roots, op.target).set(op.key, toYValue(op.value))
      return
    case 'mapDelete':
      resolve(doc, roots, op.target).delete(op.key)
      return
    case 'mapClear':
      resolve(doc, roots, op.target).clear()
      return
    case 'xmlInsert':
      resolve(doc, roots, op.target).insert(op.index, op.nodes.map(toYValue))
      return
    case 'xmlDelete':
      resolve(doc, roots, op.target).delete(op.index, op.length)
      return
    case 'xmlSetAttribute':
      resolve(doc, roots, op.target).setAttribute(op.name, op.value)
      return
    case 'xmlRemoveAttribute':
      resolve(doc, roots, op.target).removeAttribute(op.name)
      return
    default:
      throw new Error('unknown op ' + op.op)
  }
}

/** A set of peers that execute a scenario script and record everything the fixtures need. */
export class Session {
  constructor (scenario) {
    this.scenario = scenario
    this.roots = scenario.roots
    this.gc = scenario.gc !== false
    this.docs = scenario.clientIds.map(id => {
      const doc = new Y.Doc({ gc: this.gc })
      doc.clientID = id
      return doc
    })
    /** local update events per peer: { v1, v2 } */
    this.updates = this.docs.map(() => [])
    this.docs.forEach((doc, i) => {
      doc.on('update', (u, origin) => {
        if (origin !== REMOTE) this.updates[i].push({ v1: u })
      })
      doc.on('updateV2', (u, origin) => {
        if (origin !== REMOTE) this.updates[i][this.updates[i].length - 1].v2 = u
      })
    })
    this.checkpoints = []
    this.snapshots = []
    this.relPositions = []
  }

  run (ops) {
    for (const op of ops) this.apply(op)
  }

  apply (op) {
    switch (op.op) {
      case 'sync': {
        const from = this.docs[op.from]
        const to = this.docs[op.to]
        Y.applyUpdate(to, Y.encodeStateAsUpdate(from, Y.encodeStateVector(to)), REMOTE)
        return
      }
      case 'syncAll': {
        for (let pass = 0; pass < 2; pass++) {
          for (let a = 0; a < this.docs.length; a++) {
            for (let b = 0; b < this.docs.length; b++) {
              if (a !== b) this.apply({ op: 'sync', from: a, to: b })
            }
          }
        }
        return
      }
      case 'deliver': {
        const to = this.docs[op.to]
        for (const index of op.indices) {
          const u = this.updates[op.from][index]
          if (op.v2) {
            Y.applyUpdateV2(to, u.v2, REMOTE)
          } else {
            Y.applyUpdate(to, u.v1, REMOTE)
          }
        }
        return
      }
      case 'checkpoint':
        this.checkpoints.push({ id: op.id, peer: op.peer, stateVector: Y.encodeStateVector(this.docs[op.peer]) })
        return
      case 'snapshot': {
        const doc = this.docs[op.peer]
        const snap = Y.snapshot(doc)
        const entry = { id: op.id, peer: op.peer, v1: Y.encodeSnapshot(snap), v2: Y.encodeSnapshotV2(snap), snap }
        if (!this.gc) {
          const restored = Y.createDocFromSnapshot(doc, snap)
          entry.restored = contentOf(restored, this.roots)
        }
        this.snapshots.push(entry)
        return
      }
      case 'relPos': {
        const type = resolve(this.docs[op.peer], this.roots, op.target)
        const rpos = Y.createRelativePositionFromTypeIndex(type, op.index, op.assoc || 0)
        this.relPositions.push({ id: op.id, peer: op.peer, target: op.target, index: op.index, assoc: op.assoc || 0, encoded: Y.encodeRelativePosition(rpos), json: Y.relativePositionToJSON(rpos), rpos })
        return
      }
      default:
        applyDocOp(this.docs[op.peer], this.roots, op)
    }
  }
}
