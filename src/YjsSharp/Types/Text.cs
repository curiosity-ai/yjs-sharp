using System;
using System.Collections.Generic;
using YjsSharp.Encoding;
using YjsSharp.Structs;
using GC = YjsSharp.Structs.GC;
using YjsSharp.Updates;

namespace YjsSharp;

/// <summary>The kind of change reported in the <c>ychange</c> attribute of <see cref="TextBase.ToDelta"/> with snapshots.</summary>
public static class YChangeKind
{
    /// <summary>Content that is not visible in the newer snapshot.</summary>
    public const string Removed = "removed";

    /// <summary>Content that is not visible in the older snapshot.</summary>
    public const string Added = "added";
}

internal sealed class ItemTextListPosition
{
    public Item?                       Left;
    public Item?                       Right;
    public int                         Index;
    public OrderedMap<string, object?> CurrentAttributes;

    public ItemTextListPosition(Item? left, Item? right, int index, OrderedMap<string, object?> currentAttributes)
    {
        Left              = left;
        Right             = right;
        Index             = index;
        CurrentAttributes = currentAttributes;
    }

    public void Forward()
    {
        if (Right == null)
        {
            throw new InvalidOperationException("Unexpected case: cannot move past the end of the text");
        }
        if (Right.ContentField is ContentFormat cf)
        {
            if (!Right.Deleted)
            {
                TextBase.UpdateCurrentAttributes(CurrentAttributes, cf);
            }
        }
        else if (!Right.Deleted)
        {
            Index += (int)Right.Length;
        }
        Left  = Right;
        Right = Right.Right;
    }
}

/// <summary>Event of a <see cref="Text"/> or <see cref="XmlText"/>.</summary>
public class TextEvent : YEvent
{
    internal TextEvent(TextBase target, Transaction transaction, HashSet<string?> subs) : base(target, transaction)
    {
        var keys = new HashSet<string>();
        foreach (var sub in subs)
        {
            if (sub == null)
            {
                ChildListChanged = true;
            }
            else
            {
                keys.Add(sub);
            }
        }
        KeysChanged = keys;
    }

    private static bool IsJsObject(object? v) => v is not (string or bool) && !AnyValues.IsNumber(v);

    /// <summary>Whether the content changed.</summary>
    public bool ChildListChanged { get; }

    /// <summary>The attributes (of the text type itself) that changed.</summary>
    public ISet<string> KeysChanged { get; }

    /// <summary>The text that changed.</summary>
    public new TextBase Target => (TextBase)base.Target;

    /// <inheritdoc />
    public override YChanges Changes => _changes ??= new YChanges(new HashSet<Item>(), new HashSet<Item>(), Delta, Keys);

    /// <summary>The change as a rich text delta.</summary>
    public override IReadOnlyList<Delta> Delta
    {
        get
        {
            if (_delta == null)
            {
                var y     = Target.Doc!;
                var delta = new List<Delta>();
                y.TransactInternal<object?>(transaction =>
                {
                    var     currentAttributes = new OrderedMap<string, object?>();
                    var     oldAttributes     = new OrderedMap<string, object?>();
                    var     item              = Target._start;
                    string? action            = null;
                    var     attributes        = new OrderedMap<string, object?>();
                    object  insert            = string.Empty;
                    var     insertBuilder     = new System.Text.StringBuilder();
                    var     insertIsString    = true;
                    var     retain            = 0;
                    var     deleteLen         = 0;
                    void AddOp()
                    {
                        if (action != null)
                        {
                            Delta? op = null;
                            switch (action)
                            {
                                case "delete":
                                    if (deleteLen > 0)
                                    {
                                        op = YjsSharp.Delta.Deleted(deleteLen);
                                    }
                                    deleteLen = 0;
                                    break;
                                case "insert":
                                    if ((!insertIsString && IsJsObject(insert)) || (insertIsString && insertBuilder.Length > 0))
                                    {
                                        Dictionary<string, object?>? attrs = null;
                                        if (currentAttributes.Count > 0)
                                        {
                                            attrs = new Dictionary<string, object?>();
                                            foreach (var kv in currentAttributes)
                                            {
                                                if (kv.Value != null)
                                                {
                                                    attrs[kv.Key] = kv.Value;
                                                }
                                            }
                                        }
                                        op = YjsSharp.Delta.Inserted(insertIsString ? insertBuilder.ToString() : insert, attrs);
                                    }
                                    insertBuilder.Clear();
                                    insertIsString = true;
                                    insert         = string.Empty;
                                    break;
                                case "retain":
                                    if (retain > 0)
                                    {
                                        Dictionary<string, object?>? attrs = null;
                                        if (attributes.Count > 0)
                                        {
                                            attrs = new Dictionary<string, object?>();
                                            foreach (var kv in attributes)
                                            {
                                                attrs[kv.Key] = kv.Value;
                                            }
                                        }
                                        op = YjsSharp.Delta.Retained(retain, attrs);
                                    }
                                    retain = 0;
                                    break;
                            }
                            if (op != null)
                            {
                                delta.Add(op);
                            }
                            action = null;
                        }
                    }
                    while (item != null)
                    {
                        switch (item.ContentField)
                        {
                            case ContentType:
                            case ContentEmbed:
                                if (Adds(item))
                                {
                                    if (!Deletes(item))
                                    {
                                        AddOp();
                                        action         = "insert";
                                        insert         = item.ContentField.GetContent()[0]!;
                                        insertIsString = false;
                                        AddOp();
                                    }
                                }
                                else if (Deletes(item))
                                {
                                    if (action != "delete")
                                    {
                                        AddOp();
                                        action = "delete";
                                    }
                                    deleteLen += 1;
                                }
                                else if (!item.Deleted)
                                {
                                    if (action != "retain")
                                    {
                                        AddOp();
                                        action = "retain";
                                    }
                                    retain += 1;
                                }
                                break;
                            case ContentString cs:
                                if (Adds(item))
                                {
                                    if (!Deletes(item))
                                    {
                                        if (action != "insert")
                                        {
                                            AddOp();
                                            action = "insert";
                                        }
                                        insertBuilder.Append(cs.Str);
                                    }
                                }
                                else if (Deletes(item))
                                {
                                    if (action != "delete")
                                    {
                                        AddOp();
                                        action = "delete";
                                    }
                                    deleteLen += (int)item.Length;
                                }
                                else if (!item.Deleted)
                                {
                                    if (action != "retain")
                                    {
                                        AddOp();
                                        action = "retain";
                                    }
                                    retain += (int)item.Length;
                                }
                                break;
                            case ContentFormat cf:
                                {
                                    var key   = cf.KeyField;
                                    var value = cf.ValueField;
                                    if (Adds(item))
                                    {
                                        if (!Deletes(item))
                                        {
                                            var curVal = currentAttributes.TryGetValue(key, out var cv) ? cv : null;
                                            if (!AnyValues.EqualAttrs(curVal, value))
                                            {
                                                if (action == "retain")
                                                {
                                                    AddOp();
                                                }
                                                var oldVal = oldAttributes.TryGetValue(key, out var ov) ? ov : null;
                                                if (AnyValues.EqualAttrs(value, oldVal))
                                                {
                                                    attributes.Remove(key);
                                                }
                                                else
                                                {
                                                    attributes[key] = value;
                                                }
                                            }
                                            else if (value != null)
                                            {
                                                item.Delete(transaction);
                                            }
                                        }
                                    }
                                    else if (Deletes(item))
                                    {
                                        oldAttributes[key] = value;
                                        var curVal = currentAttributes.TryGetValue(key, out var cv) ? cv : null;
                                        if (!AnyValues.EqualAttrs(curVal, value))
                                        {
                                            if (action == "retain")
                                            {
                                                AddOp();
                                            }
                                            attributes[key] = curVal;
                                        }
                                    }
                                    else if (!item.Deleted)
                                    {
                                        oldAttributes[key] = value;
                                        if (attributes.TryGetValue(key, out var attr))
                                        {
                                            if (!AnyValues.EqualAttrs(attr, value))
                                            {
                                                if (action == "retain")
                                                {
                                                    AddOp();
                                                }
                                                if (value == null)
                                                {
                                                    attributes.Remove(key);
                                                }
                                                else
                                                {
                                                    attributes[key] = value;
                                                }
                                            }
                                            else if (attr != null)
                                            {
                                                item.Delete(transaction);
                                            }
                                        }
                                    }
                                    if (!item.Deleted)
                                    {
                                        if (action == "insert")
                                        {
                                            AddOp();
                                        }
                                        TextBase.UpdateCurrentAttributes(currentAttributes, cf);
                                    }
                                    break;
                                }
                        }
                        item = item.Right;
                    }
                    AddOp();
                    while (delta.Count > 0)
                    {
                        var lastOp = delta[delta.Count - 1];
                        if (lastOp.Retain != null && lastOp.Attributes == null)
                        {
                            delta.RemoveAt(delta.Count - 1);
                        }
                        else
                        {
                            break;
                        }
                    }
                    return null;
                }, null, true);
                _delta = delta;
            }
            return _delta;
        }
    }
}

/// <summary>Shared rich text: the implementation behind <see cref="Text"/> and <see cref="XmlText"/>.</summary>
public abstract class TextBase : AbstractType
{
    internal List<Action>? _pending;

    internal TextBase(string? initial)
    {
        _pending = new List<Action>();
        if (initial != null)
        {
            _pending.Add(() => Insert(0, initial));
        }
        _searchMarker = new List<ArraySearchMarker>();
    }

    /// <summary>The length of the text in UTF-16 code units (embeds count as one).</summary>
    public int Length => _length;

    internal override void Integrate(Doc y, Item? item)
    {
        base.Integrate(y, item);
        var pending = _pending!;
        _pending = null;
        foreach (var f in pending)
        {
            f();
        }
    }

    internal virtual TextEvent CreateEvent(Transaction transaction, HashSet<string?> parentSubs) => new TextEvent(this, transaction, parentSubs);

    internal override void CallObserver(Transaction transaction, HashSet<string?> parentSubs)
    {
        base.CallObserver(transaction, parentSubs);
        var ev = CreateEvent(transaction, parentSubs);
        CallTypeObservers(this, transaction, ev);
        if (!transaction._local && _hasFormatting)
        {
            transaction._needFormattingCleanup = true;
        }
    }

    /// <summary>The plain text (embeds and formatting are ignored).</summary>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        for (var n = _start; n != null; n = n.Right)
        {
            if (!n.Deleted && n.Countable && n.ContentField is ContentString cs)
            {
                sb.Append(cs.Str);
            }
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public override object? ToJson() => ToString();

    // ---- static helpers (ports of YText.js module functions) ----

    internal static void UpdateCurrentAttributes(OrderedMap<string, object?> currentAttributes, ContentFormat format)
    {
        if (format.ValueField == null)
        {
            currentAttributes.Remove(format.KeyField);
        }
        else
        {
            currentAttributes[format.KeyField] = format.ValueField;
        }
    }

    private static ItemTextListPosition FindNextPosition(Transaction transaction, ItemTextListPosition pos, int count)
    {
        while (pos.Right != null && count > 0)
        {
            if (pos.Right.ContentField is ContentFormat cf)
            {
                if (!pos.Right.Deleted)
                {
                    UpdateCurrentAttributes(pos.CurrentAttributes, cf);
                }
            }
            else if (!pos.Right.Deleted)
            {
                if (count < pos.Right.Length)
                {
                    StructStore.GetItemCleanStart(transaction, new ID(pos.Right.Id.Client, pos.Right.Id.Clock + (uint)count));
                }
                pos.Index += (int)pos.Right.Length;
                count     -= (int)pos.Right.Length;
            }
            pos.Left  = pos.Right;
            pos.Right = pos.Right.Right;
        }
        return pos;
    }

    private static ItemTextListPosition FindPosition(Transaction transaction, AbstractType parent, int index, bool useSearchMarker)
    {
        var currentAttributes = new OrderedMap<string, object?>();
        var marker            = useSearchMarker ? FindMarker(parent, index) : null;
        if (marker != null)
        {
            var pos = new ItemTextListPosition(marker.P.Left, marker.P, marker.Index, currentAttributes);
            return FindNextPosition(transaction, pos, index - marker.Index);
        }
        var pos2 = new ItemTextListPosition(null, parent._start, 0, currentAttributes);
        return FindNextPosition(transaction, pos2, index);
    }

    private static object? GetOrNull(IReadOnlyDictionary<string, object?> attributes, string key)
    {
        return attributes.TryGetValue(key, out var v) && v is not Undefined ? v : null;
    }

    private static void InsertNegatedAttributes(Transaction transaction, AbstractType parent, ItemTextListPosition currPos, OrderedMap<string, object?> negatedAttributes)
    {
        while (currPos.Right != null
               && (currPos.Right.Deleted
                   || (currPos.Right.ContentField is ContentFormat cf
                       && AnyValues.EqualAttrs(negatedAttributes.TryGetValue(cf.KeyField, out var nv) ? nv : Undefined.Value, cf.ValueField))))
        {
            if (!currPos.Right.Deleted)
            {
                negatedAttributes.Remove(((ContentFormat)currPos.Right.ContentField).KeyField);
            }
            currPos.Forward();
        }
        var doc         = transaction.Doc;
        var ownClientId = doc.ClientId;
        foreach (var kv in negatedAttributes.ToList())
        {
            var left       = currPos.Left;
            var right      = currPos.Right;
            var nextFormat = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, parent, null, new ContentFormat(kv.Key, kv.Value));
            nextFormat.Integrate(transaction, 0);
            currPos.Right = nextFormat;
            currPos.Forward();
        }
    }

    private static void MinimizeAttributeChanges(ItemTextListPosition currPos, IReadOnlyDictionary<string, object?> attributes)
    {
        while (true)
        {
            if (currPos.Right == null)
            {
                break;
            }
            if (currPos.Right.Deleted
                || (currPos.Right.ContentField is ContentFormat cf && AnyValues.EqualAttrs(GetOrNull(attributes, cf.KeyField), cf.ValueField)))
            {
                // keep going
            }
            else
            {
                break;
            }
            currPos.Forward();
        }
    }

    private static OrderedMap<string, object?> InsertAttributes(Transaction transaction, AbstractType parent, ItemTextListPosition currPos, IReadOnlyDictionary<string, object?> attributes)
    {
        var doc               = transaction.Doc;
        var ownClientId       = doc.ClientId;
        var negatedAttributes = new OrderedMap<string, object?>();
        foreach (var kv in AnyValues.JsKeyOrder(attributes))
        {
            var key        = kv.Key;
            var val        = kv.Value;
            if (val is Undefined)
            {
                continue;
            }
            var currentVal = currPos.CurrentAttributes.TryGetValue(key, out var cv) ? cv : null;
            if (!AnyValues.EqualAttrs(currentVal, val))
            {
                negatedAttributes[key] = currentVal;
                var left  = currPos.Left;
                var right = currPos.Right;
                currPos.Right = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, parent, null, new ContentFormat(key, val));
                currPos.Right.Integrate(transaction, 0);
                currPos.Forward();
            }
        }
        return negatedAttributes;
    }

    private static void InsertText(Transaction transaction, AbstractType parent, ItemTextListPosition currPos, object text, IReadOnlyDictionary<string, object?> attributes0)
    {
        var attributes = new OrderedMapDictionary(attributes0);
        foreach (var key in currPos.CurrentAttributes.Keys)
        {
            if (!attributes.TryGetValue(key, out var v) || v is Undefined)
            {
                attributes[key] = null;
            }
        }
        var doc         = transaction.Doc;
        var ownClientId = doc.ClientId;
        MinimizeAttributeChanges(currPos, attributes);
        var negatedAttributes = InsertAttributes(transaction, parent, currPos, attributes);
        ItemContent content = text switch
        {
            string s          => new ContentString(s),
            AbstractType type => new ContentType(type),
            _                 => new ContentEmbed(text),
        };
        if (text is AbstractType at && at._doc != null)
        {
            throw new InvalidOperationException("This type was already integrated into a document. Insert a new instance or a clone instead.");
        }
        var left  = currPos.Left;
        var right = currPos.Right;
        var index = currPos.Index;
        if (parent._searchMarker != null)
        {
            UpdateMarkerChanges(parent._searchMarker, currPos.Index, (int)content.Length);
        }
        right = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, parent, null, content);
        right.Integrate(transaction, 0);
        currPos.Right = right;
        currPos.Index = index;
        currPos.Forward();
        InsertNegatedAttributes(transaction, parent, currPos, negatedAttributes);
    }

    private static void FormatText(Transaction transaction, AbstractType parent, ItemTextListPosition currPos, int length, IReadOnlyDictionary<string, object?> attributes)
    {
        var doc         = transaction.Doc;
        var ownClientId = doc.ClientId;
        MinimizeAttributeChanges(currPos, attributes);
        var negatedAttributes = InsertAttributes(transaction, parent, currPos, attributes);
        while (currPos.Right != null
               && (length > 0 || (negatedAttributes.Count > 0 && (currPos.Right.Deleted || currPos.Right.ContentField is ContentFormat))))
        {
            if (!currPos.Right.Deleted)
            {
                if (currPos.Right.ContentField is ContentFormat cf)
                {
                    var key   = cf.KeyField;
                    var value = cf.ValueField;
                    if (attributes.TryGetValue(key, out var attr) && attr is not Undefined)
                    {
                        if (AnyValues.EqualAttrs(attr, value))
                        {
                            negatedAttributes.Remove(key);
                        }
                        else
                        {
                            if (length == 0)
                            {
                                break;
                            }
                            negatedAttributes[key] = value;
                        }
                        currPos.Right.Delete(transaction);
                    }
                    else
                    {
                        currPos.CurrentAttributes[key] = value;
                    }
                }
                else
                {
                    if (length < currPos.Right.Length)
                    {
                        StructStore.GetItemCleanStart(transaction, new ID(currPos.Right.Id.Client, currPos.Right.Id.Clock + (uint)length));
                    }
                    length -= (int)currPos.Right.Length;
                }
            }
            currPos.Forward();
        }
        if (length > 0)
        {
            var newlines = new string('\n', length);
            currPos.Right = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), currPos.Left, currPos.Left?.LastId, currPos.Right, currPos.Right?.Id, parent, null, new ContentString(newlines));
            currPos.Right.Integrate(transaction, 0);
            currPos.Forward();
        }
        InsertNegatedAttributes(transaction, parent, currPos, negatedAttributes);
    }

    private static int CleanupFormattingGap(Transaction transaction, Item start, Item? curr, OrderedMap<string, object?> startAttributes, OrderedMap<string, object?> currAttributes)
    {
        Item? end        = start;
        var   endFormats = new Dictionary<string, ContentFormat>();
        while (end != null && (!end.Countable || end.Deleted))
        {
            if (!end.Deleted && end.ContentField is ContentFormat cf)
            {
                endFormats[cf.KeyField] = cf;
            }
            end = end.Right;
        }
        var   cleanups    = 0;
        var   reachedCurr = false;
        Item? s           = start;
        while (s != end)
        {
            if (curr == s)
            {
                reachedCurr = true;
            }
            if (!s!.Deleted)
            {
                if (s.ContentField is ContentFormat cf)
                {
                    var key            = cf.KeyField;
                    var value          = cf.ValueField;
                    var startAttrValue = startAttributes.TryGetValue(key, out var sv) ? sv : null;
                    if (!endFormats.TryGetValue(key, out var ef) || ef != cf || AnyValues.StrictEquals(startAttrValue, value))
                    {
                        s.Delete(transaction);
                        cleanups++;
                        var currVal = currAttributes.TryGetValue(key, out var cav) ? cav : null;
                        if (!reachedCurr && AnyValues.StrictEquals(currVal, value) && !AnyValues.StrictEquals(startAttrValue, value))
                        {
                            if (startAttrValue == null)
                            {
                                currAttributes.Remove(key);
                            }
                            else
                            {
                                currAttributes[key] = startAttrValue;
                            }
                        }
                    }
                    if (!reachedCurr && !s.Deleted)
                    {
                        UpdateCurrentAttributes(currAttributes, cf);
                    }
                }
            }
            s = s.Right;
        }
        return cleanups;
    }

    private static void CleanupContextlessFormattingGap(Transaction transaction, Item? item)
    {
        while (item != null && item.Right != null && (item.Right.Deleted || !item.Right.Countable))
        {
            item = item.Right;
        }
        var attrs = new HashSet<string>();
        while (item != null && (item.Deleted || !item.Countable))
        {
            if (!item.Deleted && item.ContentField is ContentFormat cf)
            {
                var key = cf.KeyField;
                if (attrs.Contains(key))
                {
                    item.Delete(transaction);
                }
                else
                {
                    attrs.Add(key);
                }
            }
            item = item.Left;
        }
    }

    /// <summary>Removes redundant formatting items. Returns the number of removed items.</summary>
    public int CleanupFormatting()
    {
        return CleanupYTextFormatting(this);
    }

    internal static int CleanupYTextFormatting(AbstractType type)
    {
        var res = 0;
        type.RequireDoc().TransactInternal<object?>(transaction =>
        {
            var start             = type._start;
            var end               = type._start;
            var startAttributes   = new OrderedMap<string, object?>();
            var currentAttributes = new OrderedMap<string, object?>();
            while (end != null)
            {
                if (!end.Deleted)
                {
                    if (end.ContentField is ContentFormat cf)
                    {
                        UpdateCurrentAttributes(currentAttributes, cf);
                    }
                    else
                    {
                        res += CleanupFormattingGap(transaction, start!, end, startAttributes, currentAttributes);
                        startAttributes = Copy(currentAttributes);
                        start           = end;
                    }
                }
                end = end.Right;
            }
            return null;
        }, null, true);
        return res;
    }

    private static OrderedMap<string, object?> Copy(OrderedMap<string, object?> m)
    {
        var c = new OrderedMap<string, object?>();
        foreach (var kv in m)
        {
            c[kv.Key] = kv.Value;
        }
        return c;
    }

    internal static void CleanupYTextAfterTransaction(Transaction transaction)
    {
        var needFullCleanup = new OrderedSet<AbstractType>();
        var doc             = transaction.Doc;
        foreach (var kv in transaction._afterState)
        {
            var client     = kv.Key;
            var afterClock = kv.Value;
            var clock      = transaction.GetBeforeState(client);
            if (afterClock == clock)
            {
                continue;
            }
            StructStore.IterateStructs(transaction, doc.Store.Clients[client], clock, afterClock, item =>
            {
                if (!item.Deleted && item is Item it && it.ContentField is ContentFormat)
                {
                    needFullCleanup.Add(it.Parent!);
                }
            });
        }
        doc.TransactInternal<object?>(t =>
        {
            StructStore.IterateDeletedStructs(transaction, transaction._deleteSet, str =>
            {
                if (str is GC || str is not Item item || !item.Parent!._hasFormatting || needFullCleanup.Contains(item.Parent))
                {
                    return;
                }
                var parent = item.Parent;
                if (item.ContentField is ContentFormat)
                {
                    needFullCleanup.Add(parent);
                }
                else
                {
                    CleanupContextlessFormattingGap(t, item);
                }
            });
            foreach (var yText in needFullCleanup)
            {
                CleanupYTextFormatting(yText);
            }
            return null;
        }, null, true);
    }

    private static ItemTextListPosition DeleteText(Transaction transaction, ItemTextListPosition currPos, int length)
    {
        var startLength = length;
        var startAttrs  = Copy(currPos.CurrentAttributes);
        var start       = currPos.Right;
        while (length > 0 && currPos.Right != null)
        {
            if (!currPos.Right.Deleted)
            {
                switch (currPos.Right.ContentField)
                {
                    case ContentType:
                    case ContentEmbed:
                    case ContentString:
                        if (length < currPos.Right.Length)
                        {
                            StructStore.GetItemCleanStart(transaction, new ID(currPos.Right.Id.Client, currPos.Right.Id.Clock + (uint)length));
                        }
                        length -= (int)currPos.Right.Length;
                        currPos.Right.Delete(transaction);
                        break;
                }
            }
            currPos.Forward();
        }
        if (start != null)
        {
            CleanupFormattingGap(transaction, start, currPos.Right, startAttrs, currPos.CurrentAttributes);
        }
        var parent = (currPos.Left ?? currPos.Right)?.Parent;
        if (parent?._searchMarker != null)
        {
            UpdateMarkerChanges(parent._searchMarker, currPos.Index, -startLength + length);
        }
        return currPos;
    }

    // ---- public API ----

    /// <summary>Applies a rich text delta (Quill format).</summary>
    public void ApplyDelta(IEnumerable<Delta> delta, bool sanitize = true) => ApplyDelta(null, delta, sanitize);

    /// <summary>Applies a rich text delta within the given transaction.</summary>
    public void ApplyDelta(Transaction? txn, IEnumerable<Delta> delta, bool sanitize = true)
    {
        var ops = new List<Delta>(delta);
        if (_doc != null)
        {
            Transact(txn, transaction =>
            {
                var currPos = new ItemTextListPosition(null, _start, 0, new OrderedMap<string, object?>());
                for (var i = 0; i < ops.Count; i++)
                {
                    var op    = ops[i];
                    var attrs = AsReadOnly(op.Attributes);
                    if (op.Retain == null && op.Delete == null)
                    {
                        var ins = op.Insert;
                        if (!sanitize && ins is string s && i == ops.Count - 1 && currPos.Right == null && s.EndsWith("\n", StringComparison.Ordinal))
                        {
                            ins = s.Substring(0, s.Length - 1);
                        }
                        if (ins is not string str || str.Length > 0)
                        {
                            InsertText(transaction, this, currPos, ins!, attrs);
                        }
                    }
                    else if (op.Retain != null)
                    {
                        FormatText(transaction, this, currPos, op.Retain.Value, attrs);
                    }
                    else if (op.Delete != null)
                    {
                        DeleteText(transaction, currPos, op.Delete.Value);
                    }
                }
            });
        }
        else
        {
            _pending!.Add(() => ApplyDelta(ops, sanitize));
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyAttributes = new Dictionary<string, object?>();

    private static IReadOnlyDictionary<string, object?> AsReadOnly(IDictionary<string, object?>? attrs)
    {
        if (attrs == null)
        {
            return EmptyAttributes;
        }
        if (attrs is IReadOnlyDictionary<string, object?> ro)
        {
            return ro;
        }
        return new Dictionary<string, object?>(attrs);
    }

    /// <summary>
    /// Converts the content to a rich text delta. With snapshots, content that differs between them is marked with a
    /// <c>ychange</c> attribute (<c>{ type: "removed" }</c> / <c>{ type: "added" }</c>, or what
    /// <paramref name="computeYChange"/> returns).
    /// </summary>
    public IReadOnlyList<Delta> ToDelta(Snapshot? snapshot = null, Snapshot? prevSnapshot = null, Func<string, ID, IDictionary<string, object?>>? computeYChange = null)
    {
        var ops               = new List<Delta>();
        var currentAttributes = new OrderedMap<string, object?>();
        var doc               = RequireDoc();
        var str               = new System.Text.StringBuilder();
        var n                 = _start;
        void PackStr()
        {
            if (str.Length > 0)
            {
                Dictionary<string, object?>? attributes = null;
                if (currentAttributes.Count > 0)
                {
                    attributes = new Dictionary<string, object?>();
                    foreach (var kv in currentAttributes)
                    {
                        attributes[kv.Key] = kv.Value;
                    }
                }
                ops.Add(YjsSharp.Delta.Inserted(str.ToString(), attributes));
                str.Clear();
            }
        }
        bool YChangeMatches(string type, ulong client)
        {
            if (!currentAttributes.TryGetValue("ychange", out var cur) || cur is not IDictionary<string, object?> d)
            {
                return false;
            }
            d.TryGetValue("user", out var user);
            d.TryGetValue("type", out var t);
            return AnyValues.StrictEquals(user, (double)client) && AnyValues.StrictEquals(t, type);
        }
        void ComputeDelta()
        {
            while (n != null)
            {
                if (Snapshot.IsVisible(n, snapshot) || (prevSnapshot != null && Snapshot.IsVisible(n, prevSnapshot)))
                {
                    switch (n.ContentField)
                    {
                        case ContentString cs:
                            {
                                var hasCur = currentAttributes.ContainsKey("ychange");
                                if (snapshot != null && !Snapshot.IsVisible(n, snapshot))
                                {
                                    if (!YChangeMatches(YChangeKind.Removed, n.Id.Client))
                                    {
                                        PackStr();
                                        currentAttributes["ychange"] = computeYChange != null ? computeYChange(YChangeKind.Removed, n.Id) : new Dictionary<string, object?> { ["type"] = YChangeKind.Removed };
                                    }
                                }
                                else if (prevSnapshot != null && !Snapshot.IsVisible(n, prevSnapshot))
                                {
                                    if (!YChangeMatches(YChangeKind.Added, n.Id.Client))
                                    {
                                        PackStr();
                                        currentAttributes["ychange"] = computeYChange != null ? computeYChange(YChangeKind.Added, n.Id) : new Dictionary<string, object?> { ["type"] = YChangeKind.Added };
                                    }
                                }
                                else if (hasCur)
                                {
                                    PackStr();
                                    currentAttributes.Remove("ychange");
                                }
                                str.Append(cs.Str);
                                break;
                            }
                        case ContentType:
                        case ContentEmbed:
                            {
                                PackStr();
                                Dictionary<string, object?>? attrs = null;
                                if (currentAttributes.Count > 0)
                                {
                                    attrs = new Dictionary<string, object?>();
                                    foreach (var kv in currentAttributes)
                                    {
                                        attrs[kv.Key] = kv.Value;
                                    }
                                }
                                ops.Add(YjsSharp.Delta.Inserted(n.ContentField.GetContent()[0], attrs));
                                break;
                            }
                        case ContentFormat cf:
                            if (Snapshot.IsVisible(n, snapshot))
                            {
                                PackStr();
                                UpdateCurrentAttributes(currentAttributes, cf);
                            }
                            break;
                    }
                }
                n = n.Right;
            }
            PackStr();
        }
        if (snapshot != null || prevSnapshot != null)
        {
            doc.TransactInternal<object?>(transaction =>
            {
                if (snapshot != null)
                {
                    Snapshot.SplitSnapshotAffectedStructs(transaction, snapshot);
                }
                if (prevSnapshot != null)
                {
                    Snapshot.SplitSnapshotAffectedStructs(transaction, prevSnapshot);
                }
                ComputeDelta();
                return null;
            }, "cleanup", true);
        }
        else
        {
            ComputeDelta();
        }
        return ops;
    }

    /// <summary>Inserts text at <paramref name="index"/> with optional formatting attributes.</summary>
    public void Insert(int index, string text, IDictionary<string, object?>? attributes = null) => Insert(null, index, text, attributes);

    /// <summary>Inserts text within the given transaction.</summary>
    public void Insert(Transaction? txn, int index, string text, IDictionary<string, object?>? attributes = null)
    {
        if (text.Length <= 0)
        {
            return;
        }
        if (_doc != null)
        {
            Transact(txn, transaction =>
            {
                CheckIndex(index);
                var pos = FindPosition(transaction, this, index, attributes == null);
                IReadOnlyDictionary<string, object?> attrs;
                if (attributes == null)
                {
                    var a = new Dictionary<string, object?>();
                    foreach (var kv in pos.CurrentAttributes)
                    {
                        a[kv.Key] = kv.Value;
                    }
                    attrs = a;
                }
                else
                {
                    attrs = AsReadOnly(attributes);
                }
                InsertText(transaction, this, pos, text, attrs);
            });
        }
        else
        {
            _pending!.Add(() => Insert(index, text, attributes));
        }
    }

    private void CheckIndex(int index)
    {
        if (index < 0 || index > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Index out of range");
        }
    }

    /// <summary>Inserts an embed (an Any value, or a shared type) at <paramref name="index"/>.</summary>
    public void InsertEmbed(int index, object embed, IDictionary<string, object?>? attributes = null) => InsertEmbed(null, index, embed, attributes);

    /// <summary>Inserts an embed within the given transaction.</summary>
    public void InsertEmbed(Transaction? txn, int index, object embed, IDictionary<string, object?>? attributes = null)
    {
        if (_doc != null)
        {
            Transact(txn, transaction =>
            {
                CheckIndex(index);
                var pos = FindPosition(transaction, this, index, attributes == null);
                InsertText(transaction, this, pos, embed, AsReadOnly(attributes));
            });
        }
        else
        {
            _pending!.Add(() => InsertEmbed(index, embed, attributes ?? new Dictionary<string, object?>()));
        }
    }

    /// <summary>Deletes <paramref name="length"/> code units starting at <paramref name="index"/>.</summary>
    public void Delete(int index, int length) => Delete(null, index, length);

    /// <summary>Deletes text within the given transaction.</summary>
    public void Delete(Transaction? txn, int index, int length)
    {
        if (length == 0)
        {
            return;
        }
        if (_doc != null)
        {
            Transact(txn, transaction =>
            {
                if (index < 0 || length < 0 || index + length > _length)
                {
                    throw new ArgumentOutOfRangeException(nameof(length), "Index out of range");
                }
                DeleteText(transaction, FindPosition(transaction, this, index, true), length);
            });
        }
        else
        {
            _pending!.Add(() => Delete(index, length));
        }
    }

    /// <summary>Removes a range (alias of <see cref="Delete(int, int)"/>).</summary>
    public void RemoveRange(int index, int length) => Delete(index, length);

    /// <summary>Applies formatting attributes to a range (null values remove an attribute).</summary>
    public void Format(int index, int length, IDictionary<string, object?> attributes) => Format(null, index, length, attributes);

    /// <summary>Applies formatting within the given transaction.</summary>
    public void Format(Transaction? txn, int index, int length, IDictionary<string, object?> attributes)
    {
        if (length == 0)
        {
            return;
        }
        if (_doc != null)
        {
            Transact(txn, transaction =>
            {
                var pos = FindPosition(transaction, this, index, false);
                if (pos.Right == null)
                {
                    return;
                }
                FormatText(transaction, this, pos, length, AsReadOnly(attributes));
            });
        }
        else
        {
            _pending!.Add(() => Format(index, length, attributes));
        }
    }

    /// <summary>Removes an attribute of the text type itself (not a formatting attribute).</summary>
    public void RemoveAttribute(string attributeName) => RemoveAttribute(null, attributeName);

    /// <summary>Removes an attribute within the given transaction.</summary>
    public void RemoveAttribute(Transaction? txn, string attributeName)
    {
        if (_doc != null)
        {
            Transact(txn, transaction => TypeMapDelete(transaction, this, attributeName));
        }
        else
        {
            _pending!.Add(() => RemoveAttribute(attributeName));
        }
    }

    /// <summary>Sets an attribute of the text type itself (not a formatting attribute).</summary>
    public void SetAttribute(string attributeName, object? attributeValue) => SetAttribute(null, attributeName, attributeValue);

    /// <summary>Sets an attribute within the given transaction.</summary>
    public void SetAttribute(Transaction? txn, string attributeName, object? attributeValue)
    {
        if (_doc != null)
        {
            Transact(txn, transaction => TypeMapSet(transaction, this, attributeName, attributeValue));
        }
        else
        {
            _pending!.Add(() => SetAttribute(attributeName, attributeValue));
        }
    }

    /// <summary>The value of an attribute of the text type itself.</summary>
    public object? GetAttribute(string attributeName) => TypeMapGet(attributeName);

    /// <summary>All attributes of the text type itself.</summary>
    public IDictionary<string, object?> GetAttributes() => TypeMapGetAll();

    /// <summary>The attributes visible in a snapshot.</summary>
    public IDictionary<string, object?> GetAttributes(Snapshot snapshot) => TypeMapGetAllSnapshot(snapshot);
}

/// <summary>A read-only dictionary view that also supports adding keys, preserving insertion order.</summary>
internal sealed class OrderedMapDictionary : IReadOnlyDictionary<string, object?>
{
    private readonly OrderedMap<string, object?> _map = new OrderedMap<string, object?>();

    public OrderedMapDictionary(IReadOnlyDictionary<string, object?> source)
    {
        foreach (var kv in AnyValues.JsKeyOrder(source))
        {
            _map[kv.Key] = kv.Value;
        }
    }

    public object? this[string key]
    {
        get => _map[key];
        set => _map[key] = value;
    }

    public IEnumerable<string> Keys => _map.Keys;

    public IEnumerable<object?> Values => _map.Values;

    public int Count => _map.Count;

    public bool ContainsKey(string key) => _map.ContainsKey(key);

    public bool TryGetValue(string key, out object? value) => _map.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _map.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Shared rich text (Y.Text): text with formatting attributes and embeds.</summary>
public sealed class Text : TextBase
{
    internal const uint TypeRefId = 2;

    /// <summary>Creates a text that is not attached to a document yet, optionally with initial content.</summary>
    public Text() : base(null)
    {
    }

    /// <summary>Creates a text that is not attached to a document yet, with initial content.</summary>
    public Text(string? initial) : base(initial)
    {
    }

    internal override AbstractType InternalCopy() => new Text();

    /// <inheritdoc />
    public override AbstractType Clone()
    {
        var text = new Text();
        text.ApplyDelta(ToDelta());
        return text;
    }

    /// <summary>Creates an unattached copy of the text.</summary>
    public Text CloneText() => (Text)Clone();

    /// <summary>Registers a handler for changes of this text. Dispose the result to unsubscribe.</summary>
    public IDisposable Observe(Action<TextEvent> handler) => _eH.Add((e, _) => handler((TextEvent)e));

    /// <summary>Registers a handler for changes of this text. Dispose the result to unsubscribe.</summary>
    public IDisposable Observe(Action<TextEvent, Transaction> handler) => _eH.Add((e, t) => handler((TextEvent)e, t));

    internal override void WriteTypeRef(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(TypeRefId);
    }
}
