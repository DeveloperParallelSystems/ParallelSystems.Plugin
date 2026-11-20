using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.LiveSession
{
    // Revit returns fresh managed Document wrappers across API calls. Use its
    // native-document Equals implementation, not CLR reference identity. Avoid
    // hashing invalid closed documents while pruning old entries.
    internal sealed class LiveDocumentRegistry<T> where T : class
    {
        private readonly List<KeyValuePair<T, string>> _entries = new List<KeyValuePair<T, string>>();
        private readonly Func<T, bool> _isValid;
        internal LiveDocumentRegistry(Func<T, bool> isValid) { _isValid = isValid; }
        internal string GetId(T document)
        {
            _entries.RemoveAll(entry => !_isValid(entry.Key));
            var existing = _entries.FirstOrDefault(entry => entry.Key.Equals(document));
            if (existing.Key != null) return existing.Value;
            string id = Guid.NewGuid().ToString("N");
            _entries.Add(new KeyValuePair<T, string>(document, id));
            return id;
        }
    }
}
