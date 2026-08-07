using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace PubTransfer
{
    // A thin, total reader over JavaScriptSerializer's object graph.
    //
    // Total in the sense that asking for something that is not there returns a
    // default rather than throwing: every value read from the page is
    // range-checked by its caller anyway, and a missing field and a wrong field
    // should fail in the same place, with the caller's message, rather than as
    // a cast exception halfway down.
    public static class Json
    {
        private static readonly JavaScriptSerializer Serializer = MakeSerializer();

        private static JavaScriptSerializer MakeSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            // The bodies that carry file bytes never come through here — they
            // arrive as a stream — so a deep-nesting guard costs nothing.
            serializer.RecursionLimit = 32;
            return serializer;
        }

        public sealed class Node
        {
            private readonly object value;

            public Node(object value)
            {
                this.value = value;
            }

            public object Raw { get { return value; } }

            public bool IsObject { get { return value is IDictionary<string, object>; } }

            public Node Get(string key)
            {
                IDictionary<string, object> map = value as IDictionary<string, object>;
                object found;
                if (map != null && map.TryGetValue(key, out found))
                {
                    return new Node(found);
                }
                return new Node(null);
            }

            public string String(string key)
            {
                object found = Get(key).value;
                return found as string;
            }

            public string AsString()
            {
                return value as string;
            }

            public double Number(string key)
            {
                object found = Get(key).value;
                if (found == null)
                {
                    return double.NaN;
                }
                try
                {
                    return Convert.ToDouble(found, CultureInfo.InvariantCulture);
                }
                catch
                {
                    return double.NaN;
                }
            }

            public bool Bool(string key, bool fallback)
            {
                object found = Get(key).value;
                if (found is bool)
                {
                    return (bool)found;
                }
                return fallback;
            }

            public List<Node> Array(string key)
            {
                List<Node> items = new List<Node>();
                IEnumerable list = Get(key).value as IEnumerable;
                if (list == null || list is string)
                {
                    return items;
                }
                foreach (object item in list)
                {
                    items.Add(new Node(item));
                }
                return items;
            }

            public List<string> Strings(string key)
            {
                List<string> items = new List<string>();
                foreach (Node node in Array(key))
                {
                    string text = node.AsString();
                    if (text != null)
                    {
                        items.Add(text);
                    }
                }
                return items;
            }
        }

        public static Node Parse(string json)
        {
            try
            {
                return new Node(Serializer.DeserializeObject(json));
            }
            catch (Exception)
            {
                throw new HostActionException("bad-json", "要求を読み取れません。");
            }
        }

        public static string Write(object value)
        {
            return Serializer.Serialize(value);
        }
    }
}
