// Document scenarios. Every scenario is a script (see dsl.mjs) executed by real Yjs to produce the
// fixtures, and executed again by YjsSharp in the compatibility tests.

import * as Y from 'yjs'
import { Session, resolve } from './dsl.mjs'

const ins = (peer, target, index, text, attrs) => attrs ? { op: 'textInsert', peer, target, index, text, attrs } : { op: 'textInsert', peer, target, index, text }
const del = (peer, target, index, length) => ({ op: 'textDelete', peer, target, index, length })
const fmt = (peer, target, index, length, attrs) => ({ op: 'textFormat', peer, target, index, length, attrs })
const aIns = (peer, target, index, values) => ({ op: 'arrayInsert', peer, target, index, values })
const aPush = (peer, target, values) => ({ op: 'arrayPush', peer, target, values })
const aDel = (peer, target, index, length) => ({ op: 'arrayDelete', peer, target, index, length })
const mSet = (peer, target, key, value) => ({ op: 'mapSet', peer, target, key, value })
const mDel = (peer, target, key) => ({ op: 'mapDelete', peer, target, key })
const sync = (from, to) => ({ op: 'sync', from, to })
const syncAll = () => ({ op: 'syncAll' })
const tx = (peer, ops) => ({ op: 'transact', peer, ops })
const checkpoint = (peer, id) => ({ op: 'checkpoint', peer, id })
const snapshot = (peer, id) => ({ op: 'snapshot', peer, id })
const relPos = (peer, id, target, index, assoc = 0) => ({ op: 'relPos', peer, id, target, index, assoc })
const deliver = (from, to, indices, v2 = false) => v2 ? { op: 'deliver', from, to, indices, v2 } : { op: 'deliver', from, to, indices }

/** mulberry32 - a tiny deterministic PRNG */
export const prng = (seed) => {
  let a = seed >>> 0
  const next = () => {
    a = (a + 0x6D2B79F5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
  return {
    next,
    int: (n) => Math.floor(next() * n),
    pick: (arr) => arr[Math.floor(next() * arr.length)]
  }
}

const words = ['a', 'b', 'hello', ' ', 'world', 'ä', 'ß', '漢字', '😀', '👩‍👩‍👧', 'x\ny', 'é', '🇩🇪', 'Ω']

/**
 * Builds a random scenario by generating each operation against the live state of the peer it
 * runs on, so every operation is valid. The resulting op list is a plain script like any other.
 */
const randomScenario = (name, description, seed, clientIds, steps, gc = true, kinds = ['text', 'array', 'map']) => {
  const rng = prng(seed)
  const roots = { t: 'text', a: 'array', m: 'map' }
  const scenario = { name, description, gc, clientIds, roots, ops: [] }
  const session = new Session(scenario)
  const push = (op) => {
    scenario.ops.push(op)
    session.apply(op)
  }
  for (let i = 0; i < steps; i++) {
    const r = rng.next()
    if (r < 0.08) {
      const a = rng.int(clientIds.length)
      let b = rng.int(clientIds.length)
      if (b === a) b = (b + 1) % clientIds.length
      push(sync(a, b))
      continue
    }
    const peer = rng.int(clientIds.length)
    const doc = session.docs[peer]
    const kind = rng.pick(kinds)
    if (kind === 'text') {
      const t = doc.getText('t')
      const len = t.length
      const c = rng.next()
      if (c < 0.55 || len === 0) {
        const attrs = rng.next() < 0.2 ? { bold: true } : undefined
        push(ins(peer, 't', rng.int(len + 1), rng.pick(words) + rng.pick(words), attrs))
      } else if (c < 0.85) {
        const index = rng.int(len)
        push(del(peer, 't', index, 1 + rng.int(Math.min(4, len - index))))
      } else {
        const index = rng.int(len)
        const attrs = rng.pick([{ bold: true }, { italic: true }, { bold: null }, { color: '#ff0000' }])
        push(fmt(peer, 't', index, 1 + rng.int(Math.min(5, len - index)), attrs))
      }
    } else if (kind === 'array') {
      const a = doc.getArray('a')
      const len = a.length
      if (rng.next() < 0.65 || len === 0) {
        const value = rng.pick([rng.int(1000), 'v' + rng.int(100), true, null, { k: rng.int(10) }, [1, 2], 1.5, -7])
        push(aIns(peer, 'a', rng.int(len + 1), [value]))
      } else {
        const index = rng.int(len)
        push(aDel(peer, 'a', index, 1 + rng.int(Math.min(3, len - index))))
      }
    } else {
      const key = 'k' + rng.int(8)
      if (rng.next() < 0.75) {
        push(mSet(peer, 'm', key, rng.pick([rng.int(100), 'str' + rng.int(5), false, { nested: [rng.int(3)] }])))
      } else {
        push(mDel(peer, 'm', key))
      }
    }
  }
  push(syncAll())
  return { scenario, session }
}

/** @type {Array<{file:string, scenario?:any, build?:()=>{scenario:any, session:any}}>} */
export const scenarios = []

const add = (file, scenario) => scenarios.push({ file, scenario })
const addRandom = (file, built) => scenarios.push({ file, build: () => built })

// ---------------------------------------------------------------------------------------- text
add('text', {
  name: 'text-insert-basic',
  description: 'Single peer inserting at the start, middle and end of a Y.Text',
  clientIds: [1],
  roots: { t: 'text' },
  ops: [ins(0, 't', 0, 'world'), ins(0, 't', 0, 'hello '), ins(0, 't', 11, '!'), ins(0, 't', 5, ',')]
})

add('text', {
  name: 'text-insert-delete',
  description: 'Inserts and deletes, including a delete spanning several items',
  clientIds: [7],
  roots: { t: 'text' },
  ops: [ins(0, 't', 0, 'abcdef'), ins(0, 't', 3, 'XYZ'), del(0, 't', 2, 3), del(0, 't', 0, 1), ins(0, 't', 5, '123'), del(0, 't', 1, 6)]
})

add('text', {
  name: 'text-formatting',
  description: 'Formatted inserts, format ranges, removing formatting with null and overlapping formats',
  clientIds: [42],
  roots: { t: 'text' },
  ops: [
    ins(0, 't', 0, 'Hello world, this is formatted'),
    fmt(0, 't', 0, 5, { bold: true }),
    fmt(0, 't', 3, 6, { italic: true }),
    ins(0, 't', 6, 'new ', { underline: true }),
    fmt(0, 't', 0, 2, { bold: null }),
    fmt(0, 't', 10, 8, { color: '#ff00ff', size: 12 }),
    ins(0, 't', 30, ' end', { link: { href: 'https://example.com', target: '_blank' } }),
    fmt(0, 't', 0, 34, { italic: null })
  ]
})

add('text', {
  name: 'text-embeds',
  description: 'Object embeds with and without attributes between text',
  clientIds: [3],
  roots: { t: 'text' },
  ops: [
    ins(0, 't', 0, 'before after'),
    { op: 'textInsertEmbed', peer: 0, target: 't', index: 7, embed: { image: 'https://example.com/x.png', width: 100 } },
    { op: 'textInsertEmbed', peer: 0, target: 't', index: 0, embed: { formula: 'e=mc^2' }, attrs: { bold: true } },
    del(0, 't', 1, 2)
  ]
})

add('text', {
  name: 'text-apply-delta',
  description: 'Text.applyDelta with inserts, retains with attributes and deletes',
  clientIds: [11],
  roots: { t: 'text' },
  ops: [
    { op: 'textApplyDelta', peer: 0, target: 't', delta: [{ insert: 'Gandalf', attributes: { bold: true } }, { insert: ' the ' }, { insert: 'Grey', attributes: { color: '#cccccc' } }] },
    { op: 'textApplyDelta', peer: 0, target: 't', delta: [{ retain: 12 }, { insert: 'White', attributes: { color: '#ffffff' } }, { delete: 4 }] },
    { op: 'textApplyDelta', peer: 0, target: 't', delta: [{ retain: 7, attributes: { bold: null, italic: true } }, { retain: 5 }, { insert: '!' }] }
  ]
})

add('text', {
  name: 'text-unicode',
  description: 'Multi-byte characters, surrogate pairs, emoji ZWJ sequences, flags and combining marks',
  clientIds: [5],
  roots: { t: 'text' },
  ops: [
    ins(0, 't', 0, 'Grüße 漢字 😀'),
    ins(0, 't', 10, '👩‍👩‍👧'),
    ins(0, 't', 0, '🇩🇪 é '),
    del(0, 't', 0, 4),
    ins(0, 't', 2, '𝔘𝔫𝔦𝔠𝔬𝔡𝔢'),
    fmt(0, 't', 2, 14, { bold: true })
  ]
})

add('text', {
  name: 'text-surrogate-split',
  description: 'Edits landing in the middle of a surrogate pair (Yjs replaces broken halves with U+FFFD)',
  clientIds: [9],
  roots: { t: 'text' },
  ops: [ins(0, 't', 0, 'a😀b😃c'), ins(0, 't', 2, 'X'), del(0, 't', 6, 1)]
})

add('text', {
  name: 'text-concurrent-2peers',
  description: 'Two peers type concurrently at the same position, then sync',
  clientIds: [1, 2],
  roots: { t: 'text' },
  ops: [
    ins(0, 't', 0, 'shared'), sync(0, 1),
    ins(0, 't', 6, ' from one'), ins(1, 't', 6, ' from two'),
    del(0, 't', 0, 1), ins(1, 't', 0, 'S'),
    checkpoint(1, 'before-sync'),
    sync(0, 1), sync(1, 0)
  ]
})

add('text', {
  name: 'text-concurrent-3peers-formatting',
  description: 'Three peers concurrently insert, delete and format overlapping ranges',
  clientIds: [100, 200, 300],
  roots: { t: 'text' },
  ops: [
    ins(0, 't', 0, 'The quick brown fox jumps over the lazy dog'), syncAll(),
    fmt(0, 't', 4, 11, { bold: true }),
    fmt(1, 't', 10, 9, { italic: true }),
    del(2, 't', 10, 6), ins(2, 't', 10, 'red', { color: 'red' }),
    fmt(1, 't', 0, 3, { bold: true }),
    checkpoint(2, 'mid'),
    sync(0, 1), sync(2, 1), fmt(1, 't', 0, 20, { bold: null }),
    syncAll()
  ]
})

add('text', {
  name: 'text-out-of-order',
  description: 'Updates of one peer delivered to another in reverse order (pending structs and delete sets)',
  clientIds: [10, 20],
  roots: { t: 'text' },
  ops: [
    ins(0, 't', 0, 'one'), ins(0, 't', 3, ' two'), ins(0, 't', 7, ' three'), del(0, 't', 0, 4), fmt(0, 't', 0, 3, { bold: true }),
    deliver(0, 1, [4, 3, 2, 1, 0])
  ]
})

add('text', {
  name: 'text-out-of-order-v2',
  description: 'Reverse-order delivery of v2 updates',
  clientIds: [10, 20],
  roots: { t: 'text' },
  ops: [
    ins(0, 't', 0, 'alpha'), ins(0, 't', 5, ' beta'), del(0, 't', 2, 4), ins(0, 't', 0, '>'),
    deliver(0, 1, [3, 1, 2, 0], true)
  ]
})

add('text', {
  name: 'text-transaction',
  description: 'Several edits grouped in one transaction produce one update',
  clientIds: [77],
  roots: { t: 'text' },
  ops: [tx(0, [ins(0, 't', 0, 'abc'), ins(0, 't', 1, 'zz'), del(0, 't', 0, 1), fmt(0, 't', 0, 2, { bold: true })]), ins(0, 't', 4, 'end')]
})

add('text', {
  name: 'text-gc-false',
  description: 'Deletions with gc disabled keep the deleted content in the encoding',
  gc: false,
  clientIds: [8, 9],
  roots: { t: 'text' },
  ops: [ins(0, 't', 0, 'keep this text around'), del(0, 't', 5, 5), sync(0, 1), del(1, 't', 0, 5), ins(1, 't', 0, 'Keep '), sync(1, 0)]
})

// --------------------------------------------------------------------------------------- array
add('array', {
  name: 'array-basic',
  description: 'Insert, push and delete of every kind of Any value',
  clientIds: [1],
  roots: { a: 'array' },
  ops: [
    aPush(0, 'a', [1, 2, 3]),
    aIns(0, 'a', 1, ['string', true, false, null]),
    aPush(0, 'a', [{ key: 'value', n: 1, nested: { arr: [1, 'two', { three: 3 }] } }, [1, [2, [3]]]]),
    aIns(0, 'a', 0, [1.5, -42, 0, 2147483647, -2147483648, 2147483648, 9007199254740991, 0.1, 1e-7, 3.4028234663852886e+38, 1e300]),
    aPush(0, 'a', [{ $y: 'bytes', b64: 'AAEC/w==' }]),
    aDel(0, 'a', 3, 4),
    aIns(0, 'a', 2, ['ü漢😀'])
  ]
})

add('array', {
  name: 'array-concurrent-3peers',
  description: 'Three peers insert and delete concurrently',
  clientIds: [11, 22, 33],
  roots: { a: 'array' },
  ops: [
    aPush(0, 'a', ['x', 'y', 'z']), syncAll(),
    aIns(0, 'a', 1, [1, 2]), aIns(1, 'a', 1, [3, 4]), aIns(2, 'a', 1, [5]),
    aDel(0, 'a', 0, 1), aDel(1, 'a', 3, 2), aPush(2, 'a', ['end']),
    checkpoint(0, 'c0'), checkpoint(2, 'c2'),
    syncAll()
  ]
})

add('array', {
  name: 'array-out-of-order-v2',
  description: 'Array updates delivered out of order as v2',
  clientIds: [5, 6],
  roots: { a: 'array' },
  ops: [aPush(0, 'a', [1]), aPush(0, 'a', [2]), aIns(0, 'a', 0, [0]), aDel(0, 'a', 1, 1), aPush(0, 'a', [3, 4, 5]), deliver(0, 1, [2, 4, 0, 3, 1], true)]
})

// ----------------------------------------------------------------------------------------- map
add('map', {
  name: 'map-basic',
  description: 'Set, overwrite and delete keys with every kind of value',
  clientIds: [1],
  roots: { m: 'map' },
  ops: [
    mSet(0, 'm', 'number', 1), mSet(0, 'm', 'float', 0.25), mSet(0, 'm', 'string', 'hello'),
    mSet(0, 'm', 'bool', true), mSet(0, 'm', 'null', null), mSet(0, 'm', 'object', { a: [1, 2], b: { c: 'd' } }),
    mSet(0, 'm', 'array', [1, 'a', null]), mSet(0, 'm', 'bytes', { $y: 'bytes', b64: '3q2+7w==' }),
    mSet(0, 'm', 'number', 2), mDel(0, 'm', 'string'), mSet(0, 'm', 'ключ 🔑', 'unicode key')
  ]
})

add('map', {
  name: 'map-concurrent',
  description: 'Concurrent writes to the same key resolve by client id',
  clientIds: [50, 40, 60],
  roots: { m: 'map' },
  ops: [
    mSet(0, 'm', 'k', 'from 50'), mSet(1, 'm', 'k', 'from 40'), mSet(2, 'm', 'k', 'from 60'),
    mSet(0, 'm', 'a', 1), mSet(1, 'm', 'b', 2), mDel(2, 'm', 'k'), mSet(2, 'm', 'k2', 'x'),
    syncAll(), mDel(0, 'm', 'k'), mSet(1, 'm', 'k', 'again'), syncAll()
  ]
})

add('map', {
  name: 'map-clear',
  description: 'Y.Map.clear removes every key',
  clientIds: [3, 4],
  roots: { m: 'map' },
  ops: [mSet(0, 'm', 'a', 1), mSet(0, 'm', 'b', 2), sync(0, 1), { op: 'mapClear', peer: 1, target: 'm' }, mSet(0, 'm', 'c', 3), syncAll()]
})

// -------------------------------------------------------------------------------------- nested
add('nested', {
  name: 'nested-types',
  description: 'Maps containing arrays containing texts and maps',
  clientIds: [1, 2],
  roots: { m: 'map' },
  ops: [
    mSet(0, 'm', 'list', { $y: 'array' }),
    mSet(0, 'm', 'title', { $y: 'text' }),
    ins(0, ['m', 'title'], 0, 'Title', { bold: true }),
    aPush(0, ['m', 'list'], [{ $y: 'map' }, { $y: 'text' }, 'plain']),
    mSet(0, ['m', 'list', 0], 'name', 'first'),
    mSet(0, ['m', 'list', 0], 'tags', { $y: 'array' }),
    aPush(0, ['m', 'list', 0, 'tags'], ['a', 'b']),
    ins(0, ['m', 'list', 1], 0, 'nested text'),
    sync(0, 1),
    ins(1, ['m', 'list', 1], 7, 'ly typed'),
    aIns(1, ['m', 'list', 0, 'tags'], 1, ['c']),
    mSet(0, ['m', 'list', 0], 'name', 'renamed'),
    syncAll()
  ]
})

add('nested', {
  name: 'nested-array-root',
  description: 'An array root holding maps and arrays several levels deep',
  clientIds: [12],
  roots: { a: 'array' },
  ops: [
    aPush(0, 'a', [{ $y: 'map' }, { $y: 'array' }]),
    mSet(0, ['a', 0], 'deep', { $y: 'map' }),
    mSet(0, ['a', 0, 'deep'], 'deeper', { $y: 'array' }),
    aPush(0, ['a', 0, 'deep', 'deeper'], [{ $y: 'text' }]),
    ins(0, ['a', 0, 'deep', 'deeper', 0], 0, 'bottom'),
    aPush(0, ['a', 1], [1, { $y: 'map' }]),
    mSet(0, ['a', 1, 1], 'x', 'y')
  ]
})

add('nested', {
  name: 'nested-delete-gc',
  description: 'Deleting a type that has children garbage-collects the subtree',
  clientIds: [1, 2],
  roots: { m: 'map', a: 'array' },
  ops: [
    mSet(0, 'm', 'sub', { $y: 'map' }), mSet(0, ['m', 'sub'], 'text', { $y: 'text' }), ins(0, ['m', 'sub', 'text'], 0, 'gone soon'),
    aPush(0, 'a', [{ $y: 'array' }, 'keep']), aPush(0, ['a', 0], [1, 2, 3]),
    sync(0, 1),
    mDel(0, 'm', 'sub'), aDel(0, 'a', 0, 1),
    sync(0, 1)
  ]
})

add('nested', {
  name: 'nested-delete-no-gc',
  description: 'Deleting a type with children and gc disabled',
  gc: false,
  clientIds: [1, 2],
  roots: { m: 'map', a: 'array' },
  ops: [
    mSet(0, 'm', 'sub', { $y: 'map' }), mSet(0, ['m', 'sub'], 'text', { $y: 'text' }), ins(0, ['m', 'sub', 'text'], 0, 'still here'),
    aPush(0, 'a', [{ $y: 'array' }, 'keep']), aPush(0, ['a', 0], [1, 2, 3]),
    sync(0, 1),
    mDel(0, 'm', 'sub'), aDel(0, 'a', 0, 1),
    sync(0, 1)
  ]
})

// ----------------------------------------------------------------------------------------- xml
add('xml', {
  name: 'xml-basic',
  description: 'Elements, attributes and formatted xml text',
  clientIds: [1],
  roots: { x: 'xmlFragment' },
  ops: [
    { op: 'xmlInsert', peer: 0, target: 'x', index: 0, nodes: [{ $y: 'xmlElement', name: 'p' }, { $y: 'xmlElement', name: 'div' }] },
    { op: 'xmlInsert', peer: 0, target: ['x', 0], index: 0, nodes: [{ $y: 'xmlText' }] },
    ins(0, ['x', 0, 0], 0, 'Hello xml'),
    fmt(0, ['x', 0, 0], 0, 5, { bold: {} }),
    { op: 'xmlSetAttribute', peer: 0, target: ['x', 0], name: 'class', value: 'para' },
    { op: 'xmlSetAttribute', peer: 0, target: ['x', 1], name: 'id', value: 'main' },
    { op: 'xmlSetAttribute', peer: 0, target: ['x', 1], name: 'data-a', value: '1' },
    { op: 'xmlInsert', peer: 0, target: ['x', 1], index: 0, nodes: [{ $y: 'xmlElement', name: 'span' }, { $y: 'xmlText' }] },
    ins(0, ['x', 1, 1], 0, 'tail'),
    { op: 'xmlRemoveAttribute', peer: 0, target: ['x', 1], name: 'data-a' },
    { op: 'xmlInsert', peer: 0, target: 'x', index: 2, nodes: [{ $y: 'xmlText' }] },
    ins(0, ['x', 2], 0, 'root text')
  ]
})

add('xml', {
  name: 'xml-concurrent',
  description: 'Two peers edit an xml tree concurrently, including deleting elements',
  clientIds: [3, 4],
  roots: { x: 'xmlFragment' },
  ops: [
    { op: 'xmlInsert', peer: 0, target: 'x', index: 0, nodes: [{ $y: 'xmlElement', name: 'ul' }] },
    { op: 'xmlInsert', peer: 0, target: ['x', 0], index: 0, nodes: [{ $y: 'xmlElement', name: 'li' }, { $y: 'xmlElement', name: 'li' }] },
    sync(0, 1),
    { op: 'xmlInsert', peer: 1, target: ['x', 0], index: 1, nodes: [{ $y: 'xmlElement', name: 'li' }] },
    { op: 'xmlSetAttribute', peer: 1, target: ['x', 0], name: 'type', value: 'disc' },
    { op: 'xmlDelete', peer: 0, target: ['x', 0], index: 0, length: 1 },
    { op: 'xmlSetAttribute', peer: 0, target: ['x', 0], name: 'type', value: 'square' },
    syncAll()
  ]
})

// ------------------------------------------------------------------------------------- subdocs
add('subdocs', {
  name: 'subdoc-in-map',
  description: 'Subdocuments with guid, meta and autoLoad stored in a map',
  clientIds: [1, 2],
  roots: { m: 'map' },
  ops: [
    mSet(0, 'm', 'a', { $y: 'doc', guid: 'sub-a' }),
    mSet(0, 'm', 'b', { $y: 'doc', guid: 'sub-b', meta: { title: 'B' }, autoLoad: true }),
    sync(0, 1),
    mDel(1, 'm', 'a'),
    sync(1, 0)
  ]
})

add('subdocs', {
  name: 'subdoc-in-array',
  description: 'Subdocuments in an array next to plain values',
  clientIds: [21],
  roots: { a: 'array' },
  ops: [aPush(0, 'a', ['x', { $y: 'doc', guid: 'array-sub' }, 1])]
})

// ----------------------------------------------------------------------------------- snapshots
add('snapshots', {
  name: 'snapshot-text',
  description: 'Snapshots of a gc-disabled text taken between edits, restored with createDocFromSnapshot',
  gc: false,
  clientIds: [1, 2],
  roots: { t: 'text' },
  ops: [
    ins(0, 't', 0, 'version one'), snapshot(0, 's1'),
    del(0, 't', 8, 3), ins(0, 't', 8, 'two', { bold: true }), snapshot(0, 's2'),
    sync(0, 1), ins(1, 't', 0, 'B: '), sync(1, 0), del(0, 't', 0, 3), snapshot(0, 's3')
  ]
})

add('snapshots', {
  name: 'snapshot-map-array',
  description: 'Snapshots of a gc-disabled map and array',
  gc: false,
  clientIds: [5],
  roots: { m: 'map', a: 'array' },
  ops: [
    mSet(0, 'm', 'k', 1), aPush(0, 'a', [1, 2, 3]), snapshot(0, 's1'),
    mSet(0, 'm', 'k', 2), mSet(0, 'm', 'l', 'x'), aDel(0, 'a', 0, 2), snapshot(0, 's2'),
    mDel(0, 'm', 'k'), aPush(0, 'a', [4]), snapshot(0, 's3')
  ]
})

add('snapshots', {
  name: 'snapshot-gc',
  description: 'Snapshot encodings of a gc-enabled document',
  clientIds: [9, 10],
  roots: { t: 'text' },
  ops: [ins(0, 't', 0, 'abc'), snapshot(0, 'empty-ds'), del(0, 't', 1, 1), snapshot(0, 'with-ds'), sync(0, 1), ins(1, 't', 0, 'x'), snapshot(1, 'peer1')]
})

// -------------------------------------------------------------------------- relative positions
add('relpos', {
  name: 'relpos-text',
  description: 'Relative positions in a text resolved after concurrent edits on both peers',
  clientIds: [1, 2],
  roots: { t: 'text' },
  ops: [
    ins(0, 't', 0, 'hello world'), sync(0, 1),
    relPos(0, 'start', 't', 0), relPos(0, 'mid', 't', 5), relPos(0, 'mid-before', 't', 5, -1),
    relPos(0, 'end', 't', 11), relPos(0, 'end-before', 't', 11, -1),
    ins(1, 't', 0, '>> '), del(0, 't', 4, 3), ins(0, 't', 11 - 3, '!!'),
    syncAll()
  ]
})

add('relpos', {
  name: 'relpos-array-nested',
  description: 'Relative positions in an array and in a nested text',
  clientIds: [3, 4],
  roots: { a: 'array' },
  ops: [
    aPush(0, 'a', [1, 2, 3, { $y: 'text' }]), ins(0, ['a', 3], 0, 'inner'), sync(0, 1),
    relPos(0, 'arr-2', 'a', 2), relPos(0, 'arr-end', 'a', 4), relPos(0, 'nested', ['a', 3], 2),
    aDel(1, 'a', 1, 2), aIns(0, 'a', 0, ['new']), ins(1, ['a', 1], 0, 'XX'),
    syncAll()
  ]
})

// ---------------------------------------------------------------------------------------- misc
add('misc', {
  name: 'empty-doc',
  description: 'A document nobody wrote to',
  clientIds: [1],
  roots: { t: 'text', m: 'map' },
  ops: []
})

add('misc', {
  name: 'many-clients',
  description: 'Five peers with very different client ids (including large ones) edit the same text',
  clientIds: [1, 255, 256, 65536, 4294967295],
  roots: { t: 'text', m: 'map' },
  ops: [
    ins(0, 't', 0, 'a'), ins(1, 't', 0, 'b'), ins(2, 't', 0, 'c'), ins(3, 't', 0, 'd'), ins(4, 't', 0, 'e'),
    mSet(0, 'm', 'k', 0), mSet(1, 'm', 'k', 1), mSet(2, 'm', 'k', 2), mSet(3, 'm', 'k', 3), mSet(4, 'm', 'k', 4),
    checkpoint(2, 'c2'),
    syncAll(), del(4, 't', 1, 3), syncAll()
  ]
})

add('misc', {
  name: 'multiple-roots',
  description: 'Several root types of every kind in one document',
  clientIds: [31],
  roots: { t: 'text', a: 'array', m: 'map', x: 'xmlFragment', 'Ünïcödé root': 'text' },
  ops: [
    ins(0, 't', 0, 'text'), aPush(0, 'a', ['array']), mSet(0, 'm', 'map', true),
    { op: 'xmlInsert', peer: 0, target: 'x', index: 0, nodes: [{ $y: 'xmlElement', name: 'h1' }] },
    ins(0, 'Ünïcödé root', 0, 'root with a unicode name')
  ]
})

add('misc', {
  name: 'gc-merge-deleted',
  description: 'Content deleted by several peers then merged; merged update contains GC structs',
  clientIds: [1, 2, 3],
  roots: { t: 'text', a: 'array' },
  ops: [
    ins(0, 't', 0, 'aaaa'), ins(1, 't', 0, 'bbbb'), ins(2, 't', 0, 'cccc'),
    aPush(0, 'a', [1, 2]), aPush(1, 'a', [3, 4]),
    syncAll(), checkpoint(0, 'after-insert'),
    del(0, 't', 0, 6), aDel(2, 'a', 0, 3), del(1, 't', 4, 2),
    syncAll()
  ]
})

// -------------------------------------------------------------------------------------- random
addRandom('random', randomScenario('random-2peers-a', 'Random mixed edits, two peers', 1, [1, 2], 150))
addRandom('random', randomScenario('random-3peers-a', 'Random mixed edits, three peers', 2, [10, 20, 30], 200))
addRandom('random', randomScenario('random-3peers-b', 'Random mixed edits, three peers, gc disabled', 3, [5, 3, 1], 200, false))
addRandom('random', randomScenario('random-3peers-text', 'Random text-only edits with formatting, three peers', 4, [111, 222, 333], 250, true, ['text']))
addRandom('random', randomScenario('random-2peers-array', 'Random array-only edits, two peers', 5, [8, 9], 200, true, ['array']))
addRandom('random', randomScenario('random-3peers-map', 'Random map-only edits, three peers', 6, [7, 6, 5], 200, true, ['map']))
addRandom('large', randomScenario('large-text', 'A large document built by 2500 random edits of a single peer', 7, [123456], 1500, true, ['text']))
addRandom('large', randomScenario('large-mixed', 'A large mixed document built by three peers', 8, [1, 2, 3], 1500))

export { resolve, Y }
