using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace JasperFx.Core
{
    public class LightweightCache<TKey, TValue> : IEnumerable<TValue> where TKey: notnull
    {
        private ImHashMap<TKey, TValue> _values = ImHashMap<TKey, TValue>.Empty;

        private Func<TKey, TValue> _onMissing = delegate (TKey key) {
                                                                        var message = $"Key '{key}' could not be found";
                                                                        throw new KeyNotFoundException(message);
        };

        public LightweightCache()
            : this(new Dictionary<TKey, TValue>())
        {
        }

        public LightweightCache(Func<TKey, TValue> onMissing)
            : this(new Dictionary<TKey, TValue>(), onMissing)
        {
        }

        public LightweightCache(IDictionary<TKey, TValue> dictionary, Func<TKey, TValue> onMissing)
            : this(dictionary)
        {
            _onMissing = onMissing;
        }

        public LightweightCache(IDictionary<TKey, TValue> dictionary)
        {
            foreach (var pair in dictionary)
            {
                _values = _values.AddOrUpdate(pair.Key, pair.Value);
            }
        }

        /// <summary>
        /// Provide a function to create missing values
        /// </summary>
        public Func<TKey, TValue> OnMissing
        {
            set => _onMissing = value;
        }

        public int Count => _values.Count();

        /// <summary>
        /// Access the value by key. This will create a new item if one does not already exist
        /// </summary>
        /// <param name="key"></param>
        public TValue this[TKey key]
        {
            get
            {
                if (!_values.TryFind(key, out TValue? value))
                {
                    value = _onMissing(key);

                    if (value != null)
                    {
                        _values = _values.AddOrUpdate(key, value);
                    }
                }

                return value;
            }
            set => _values = _values.AddOrUpdate(key, value);
        }

        public IEnumerable<TKey> Keys() => _values.Enumerate().Select(x => x.Key);

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable<TValue>)this).GetEnumerator();
        }

        public IEnumerator<TValue> GetEnumerator()
        {
            return _values.Enumerate().Select(x => x.Value).GetEnumerator();
        }

        /// <summary>
        ///     Guarantees that the Cache has the default value for a given key.
        ///     If it does not already exist, it's created.
        /// </summary>
        /// <param name="key"></param>
        public void FillDefault(TKey key)
        {
            Fill(key, _onMissing(key));
        }

        /// <summary>
        /// Fills in a value for this key *if* it does not already exist
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        public void Fill(TKey key, TValue value)
        {
            if (_values.Contains(key))
            {
                return;
            }

            _values = _values.AddOrUpdate(key, value);
        }

        /// <summary>
        /// Return the item if it exists
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public bool TryFind(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            value = default;

            if (_values.TryFind(key, out value))
            {
                return true;
            }

            return false;
        }

        public bool Contains(TKey key)
        {
            return _values.Contains(key);
        }

        /// <summary>
        /// Remove a single item from this cache
        /// </summary>
        /// <param name="key"></param>
        public void Remove(TKey key)
        {
            _values = _values.Remove(key);
        }

        /// <summary>
        /// Remove all items from this cache
        /// </summary>
        public void Clear()
        {
            _values = ImHashMap<TKey, TValue>.Empty;
        }

        /// <summary>
        /// Carry out an action against a member of this cache
        /// </summary>
        /// <param name="key"></param>
        /// <param name="action"></param>
        [Obsolete("This will be removed in future versions. Favor TryFind()")]
        public void WithValue(TKey key, Action<TValue> action)
        {
            action(this[key]);
        }
    }
}