using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json.Linq;

namespace ParallelSystemsPlugin.LiveSession
{
    // Resolve only assemblies already loaded by Revit's own Dynamo integration.
    // Shipping or loading a second DynamoCore/Python runtime would conflict with the host.
    internal static class DynamoReflection
    {
        internal static Type LoadedType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

        internal static object Get(object instance, string name)
        {
            if (instance == null) return null;
            var property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null) throw new NotSupportedException("Dynamo API is missing " + instance.GetType().FullName + "." + name);
            return property.GetValue(instance);
        }

        internal static object Optional(object instance, string name) => instance?.GetType()
            .GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) ?? instance?.GetType()
            .GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance);

        internal static object Call(object instance, string name, params object[] args)
        {
            var methods = instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == name && m.GetParameters().Length == args.Length)
                .Where(m => m.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(b => b))
                .ToArray();
            if (methods.Length != 1) throw new NotSupportedException("Unsupported Dynamo method: " + name);
            try { return methods[0].Invoke(instance, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }

        internal static object[] Items(object value) => value is IEnumerable items ? items.Cast<object>().ToArray() : new object[0];
        internal static string Text(object value) => value?.ToString();

        internal static object Model() => LoadedType("Dynamo.Applications.DynamoRevit")?
            .GetProperty("RevitDynamoModel", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        internal static void RequireDocument(object document)
        {
            var type = LoadedType("RevitServices.Persistence.DocumentManager");
            var manager = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var current = manager == null ? null : Get(manager, "CurrentDBDocument");
            if (current == null || !current.Equals(document))
                throw new InvalidOperationException("Dynamo is bound to a different document. Activate this model in Dynamo first.");
        }
    }

    // Bound graph results, and never serialize arbitrary Revit/.NET object properties.
    internal sealed class DynamoValueWriter
    {
        private int _remaining = 10000;
        private int _characters = 500000;
        internal bool Truncated { get; private set; }

        internal JToken Write(object value, int depth = 0)
        {
            if (--_remaining < 0 || depth > 16 || _characters <= 0) return Cut();
            if (value == null) return JValue.CreateNull();
            string type = value.GetType().FullName;
            if (type == "ProtoCore.Mirror.MirrorData")
            {
                if ((bool)DynamoReflection.Get(value, "IsNull")) return JValue.CreateNull();
                if ((bool)DynamoReflection.Get(value, "IsCollection"))
                    return Write(DynamoReflection.Call(value, "GetElements"), depth + 1);
                return Write(DynamoReflection.Get(value, "Data"), depth + 1);
            }
            if (value is string text)
            {
                int length = Math.Min(text.Length, Math.Min(_characters, 32000));
                _characters -= length;
                if (length < text.Length) { Truncated = true; return new JObject { ["text"] = text.Substring(0, length), ["truncated"] = true }; }
                return new JValue(text);
            }
            if (value is bool || value is byte || value is short || value is int || value is long ||
                value is float || value is double || value is decimal) return new JValue(value);
            if (type == "DesignScript.Builtin.Dictionary")
            {
                var keys = (IEnumerable)DynamoReflection.Get(value, "Keys");
                var values = ((IEnumerable)DynamoReflection.Get(value, "Values")).GetEnumerator();
                var result = new JObject();
                foreach (var key in keys)
                {
                    if (_remaining <= 0 || _characters <= 0) { result["$truncated"] = Cut(); break; }
                    if (!values.MoveNext()) break;
                    result[Key(key)] = Write(values.Current, depth + 1);
                }
                return result;
            }
            if (value is IDictionary dictionary)
            {
                var result = new JObject();
                foreach (DictionaryEntry pair in dictionary)
                {
                    if (_remaining <= 0 || _characters <= 0) { result["$truncated"] = Cut(); break; }
                    result[Key(pair.Key)] = Write(pair.Value, depth + 1);
                }
                return result;
            }
            if (value is IEnumerable sequence)
            {
                var result = new JArray();
                foreach (var item in sequence)
                {
                    if (_remaining <= 0 || _characters <= 0) { result.Add(Cut()); break; }
                    result.Add(Write(item, depth + 1));
                }
                return result;
            }
            return new JObject { ["type"] = type, ["display"] = Write(value.ToString(), depth + 1) };
        }

        private string Key(object key)
        {
            string text = Convert.ToString(key) ?? "";
            if (text.Length > 512) { Truncated = true; text = text.Substring(0, 512); }
            _characters -= text.Length;
            return text;
        }

        private JToken Cut() { Truncated = true; return new JObject { ["truncated"] = true }; }
    }
}
