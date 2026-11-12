using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Connascence
{
    /// <summary>
    /// Marks a collection whose Equals compares contents. FunctionalLint's FP0014 matches it by name and accepts these
    /// collections inside records, because a record holding one is equal to a copy of itself.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class ValueEqualityAttribute : Attribute { }

    /// <summary>
    /// An immutable list that equals any list with the same items in the same order. Use it in records instead of
    /// ImmutableArray or a list, which compare by reference. <c>default</c> is the empty list. The CLI reads and writes it
    /// as a JSON array.
    /// </summary>
    [ValueEquality]
    public readonly struct ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
    {
        private readonly ImmutableArray<T> _items;

        public ValueList(IEnumerable<T> items) { _items = items.ToImmutableArray(); }

        /// <summary>The items. Never a default ImmutableArray, so it's always safe to use.</summary>
        public ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

        public int Count => Items.Length;
        public int Length => Items.Length;
        public T this[int index] => Items[index];

        public bool Equals(ValueList<T> other) => Items.SequenceEqual(other.Items);
        public override bool Equals(object? obj) => obj is ValueList<T> other && Equals(other);
        public override int GetHashCode() =>
            Items.Aggregate(17, (hash, item) => unchecked(hash * 31 + (item is null ? 0 : EqualityComparer<T>.Default.GetHashCode(item))));

        public static bool operator ==(ValueList<T> left, ValueList<T> right) => left.Equals(right);
        public static bool operator !=(ValueList<T> left, ValueList<T> right) => !left.Equals(right);

        public static implicit operator ValueList<T>(ImmutableArray<T> items) => new ValueList<T>(items.IsDefault ? ImmutableArray<T>.Empty : items);

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => "[" + string.Join(", ", Items) + "]";
    }

    public static class ValueList
    {
        public static ValueList<T> Of<T>(params T[] items) => new ValueList<T>(items);

        public static ValueList<T> ToValueList<T>(this IEnumerable<T> items) => new ValueList<T>(items);
    }

    /// <summary>
    /// An immutable map that equals any map with the same keys and values, whatever their order. It keeps the order its
    /// entries were given in, so a config file is written back in the order it was read. <c>default</c> is the empty map.
    /// Keys are compared ordinally; the first entry wins if a key repeats. Look values up with TryGetValue: there is no
    /// indexer, because one would have to throw or invent a value for a missing key. The CLI reads and writes it as a JSON object.
    /// </summary>
    [ValueEquality]
    public readonly struct ValueMap<TValue> : IReadOnlyCollection<KeyValuePair<string, TValue>>, IEquatable<ValueMap<TValue>>
    {
        private readonly ImmutableArray<KeyValuePair<string, TValue>> _entries;
        private readonly ImmutableDictionary<string, TValue>? _lookup;

        public ValueMap(IEnumerable<KeyValuePair<string, TValue>> entries)
        {
            var given = entries.ToImmutableArray();
            _entries = given.GroupBy(e => e.Key, StringComparer.Ordinal).Select(g => g.First()).ToImmutableArray();
            _lookup = given.GroupBy(e => e.Key, StringComparer.Ordinal).ToImmutableDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        }

        /// <summary>The entries, in the order they were given.</summary>
        public ImmutableArray<KeyValuePair<string, TValue>> Entries => _entries.IsDefault ? ImmutableArray<KeyValuePair<string, TValue>>.Empty : _entries;

        private ImmutableDictionary<string, TValue> Lookup => _lookup ?? ImmutableDictionary<string, TValue>.Empty;

        public int Count => Entries.Length;
        public IEnumerable<string> Keys => Entries.Select(e => e.Key);
        public IEnumerable<TValue> Values => Entries.Select(e => e.Value);
        public bool ContainsKey(string key) => Lookup.ContainsKey(key);
        public bool TryGetValue(string key, out TValue value) => Lookup.TryGetValue(key, out value!);

        public bool Equals(ValueMap<TValue> other)
        {
            var mine = Lookup;
            return Count == other.Count && other.Entries.All(e => mine.TryGetValue(e.Key, out var v) && EqualityComparer<TValue>.Default.Equals(v, e.Value));
        }

        public override bool Equals(object? obj) => obj is ValueMap<TValue> other && Equals(other);

        // Order-independent, like Equals: XOR of each entry's hash.
        public override int GetHashCode() =>
            Entries.Aggregate(0, (hash, e) => hash ^ unchecked(StringComparer.Ordinal.GetHashCode(e.Key) * 31 +
                                                              (e.Value is null ? 0 : EqualityComparer<TValue>.Default.GetHashCode(e.Value))));

        public static bool operator ==(ValueMap<TValue> left, ValueMap<TValue> right) => left.Equals(right);
        public static bool operator !=(ValueMap<TValue> left, ValueMap<TValue> right) => !left.Equals(right);

        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, TValue>>)Entries).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public static class ValueMap
    {
        public static ValueMap<TValue> Of<TValue>(params (string Key, TValue Value)[] entries) =>
            new ValueMap<TValue>(entries.Select(e => new KeyValuePair<string, TValue>(e.Key, e.Value)));

        public static ValueMap<TValue> ToValueMap<TValue>(this IEnumerable<KeyValuePair<string, TValue>> entries) => new ValueMap<TValue>(entries);
    }
}
