using System;
using System.Collections.Generic;
using YjsSharp.Structs;

namespace YjsSharp;

/// <summary>
/// Maps client ids and deletions to users (Yjs PermanentUserData), stored in the document itself so every peer can
/// attribute changes, e.g. to render the <c>ychange</c> attributes of <see cref="TextBase.ToDelta"/> with a user.
/// </summary>
public sealed class PermanentUserData
{
    private readonly YMap                          _yusers;
    private readonly Dictionary<ulong, string>     _clients = new Dictionary<ulong, string>();
    private readonly Dictionary<string, DeleteSet> _dss     = new Dictionary<string, DeleteSet>();
    private readonly List<Action>                  _deferred = new List<Action>();

    /// <summary>Reads (and keeps reading) the user mapping stored in <paramref name="storeType"/> (default: the root map "users").</summary>
    public PermanentUserData(Doc doc, YMap? storeType = null)
    {
        Doc     = doc;
        _yusers = storeType ?? doc.GetMap("users");
        _yusers.Observe(e =>
        {
            foreach (var userDescription in e.KeysChanged)
            {
                if (_yusers.Get(userDescription) is YMap user)
                {
                    InitUser(user, userDescription);
                }
            }
        });
        foreach (var kv in _yusers)
        {
            if (kv.Value is YMap user)
            {
                InitUser(user, kv.Key);
            }
        }
        doc.AfterAllTransactions += (_, _) => RunDeferred();
    }

    /// <summary>The document.</summary>
    public Doc Doc { get; }

    private static ulong ToClient(object? v)
    {
        return v switch
        {
            long l   => (ulong)l,
            ulong u  => u,
            int i    => (ulong)i,
            double d => (ulong)d,
            _        => Convert.ToUInt64(v, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private void InitUser(YMap user, string userDescription)
    {
        if (user.Get("ds") is not YArray ds || user.Get("ids") is not YArray ids)
        {
            return;
        }
        ds.Observe(e =>
        {
            foreach (var item in e.Changes.Added)
            {
                foreach (var encodedDs in item.Content.GetContent())
                {
                    if (encodedDs is byte[] bytes)
                    {
                        _dss.TryGetValue(userDescription, out var existing);
                        _dss[userDescription] = DeleteSet.Merge(new[] { existing ?? new DeleteSet(), DeleteSet.Decode(bytes) });
                    }
                }
            }
        });
        var all = new List<DeleteSet>();
        foreach (var encodedDs in ds)
        {
            if (encodedDs is byte[] bytes)
            {
                all.Add(DeleteSet.Decode(bytes));
            }
        }
        _dss[userDescription] = DeleteSet.Merge(all);
        ids.Observe(e =>
        {
            foreach (var item in e.Changes.Added)
            {
                foreach (var clientId in item.Content.GetContent())
                {
                    _clients[ToClient(clientId)] = userDescription;
                }
            }
        });
        foreach (var clientId in ids)
        {
            _clients[ToClient(clientId)] = userDescription;
        }
    }

    private void RunDeferred()
    {
        while (_deferred.Count > 0)
        {
            var actions = _deferred.ToArray();
            _deferred.Clear();
            foreach (var a in actions)
            {
                a();
            }
        }
    }

    /// <summary>
    /// Records that <paramref name="clientId"/> belongs to <paramref name="userDescription"/> and, from now on, the
    /// local deletions of <paramref name="doc"/> (that pass <paramref name="filter"/>) as made by that user.
    /// </summary>
    public void SetUserMapping(Doc doc, ulong clientId, string userDescription, Func<Transaction, DeleteSet, bool>? filter = null)
    {
        filter ??= (_, _) => true;
        var users = _yusers;
        var user  = users.Get(userDescription) as YMap;
        if (user == null)
        {
            user = new YMap();
            user.Set("ids", new YArray());
            user.Set("ds", new YArray());
            users.Set(userDescription, user);
        }
        ((YArray)user.Get("ids")!).Push((long)clientId);
        users.Observe(_ => _deferred.Add(() =>
        {
            var userOverwrite = users.Get(userDescription) as YMap;
            if (userOverwrite != null && userOverwrite != user)
            {
                user = userOverwrite;
                foreach (var kv in _clients)
                {
                    if (kv.Value == userDescription)
                    {
                        ((YArray)user.Get("ids")!).Push((long)kv.Key);
                    }
                }
                if (_dss.TryGetValue(userDescription, out var ds))
                {
                    ((YArray)user.Get("ds")!).Push(ds.Encode());
                }
            }
        }));
        doc.AfterTransaction += (_, e) =>
        {
            var transaction = e.Transaction;
            _deferred.Add(() =>
            {
                var yds = (YArray)user.Get("ds")!;
                var ds  = transaction.DeleteSet;
                if (transaction.Local && !ds.IsEmpty && filter(transaction, ds))
                {
                    yds.Push(ds.Encode());
                }
            });
        };
    }

    /// <summary>The user a client id was mapped to, or null.</summary>
    public string? GetUserByClientId(ulong clientId) => _clients.TryGetValue(clientId, out var u) ? u : null;

    /// <summary>The user who deleted the element with the given id, or null.</summary>
    public string? GetUserByDeletedId(ID id)
    {
        foreach (var kv in _dss)
        {
            if (kv.Value.IsDeleted(id))
            {
                return kv.Key;
            }
        }
        return null;
    }
}
