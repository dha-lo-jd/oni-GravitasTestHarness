using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Gravitas.TestHarness.Core;

/// <summary>
/// POST /query. Two forms:
/// <code>{"path": "GameClock.Instance.cycle"}</code> reads a static path ("Type.StaticMember.member[0]...").
/// <code>{"type": "Building", "select": ["name","transform.position"], "limit": 50, "include_inactive": false}</code>
/// lists the components of that type in the scene and reads the given member paths from each.
/// </summary>
public static class Query
{
    public static object Run(JObject body)
    {
        if (body == null) throw new ArgumentException("body is required");
        if (body["path"] != null)
            return new { path = (string)body["path"], value = Reflect.ToJson(Reflect.ReadStaticPath((string)body["path"])) };

        var typeName = (string)body["type"];
        if (string.IsNullOrEmpty(typeName)) throw new ArgumentException("either 'path' or 'type' is required");
        var type = Reflect.FindType(typeName) ?? throw new ArgumentException("Type not found: " + typeName);
        if (!typeof(UnityEngine.Object).IsAssignableFrom(type))
            throw new ArgumentException(type.FullName + " is not a UnityEngine.Object; use 'path' with a static entry point instead");

        var limit = body["limit"] != null ? (int)body["limit"] : 100;
        var select = (body["select"] as JArray)?.Select(t => (string)t).ToList();
        var includeInactive = body["include_inactive"] != null && (bool)body["include_inactive"];

        var found = UnityEngine.Object.FindObjectsByType(type, includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        var items = new JArray();
        foreach (var obj in found.Take(limit))
        {
            var item = new JObject { ["id"] = obj.GetInstanceID() };
            if (select == null)
            {
                item["name"] = obj.name;
                if (obj is Component c) item["position"] = Reflect.ToJson(c.transform.position);
            }
            else
            {
                foreach (var path in select)
                {
                    try { item[path] = Reflect.ToJson(Reflect.ReadPath(obj, path)); }
                    catch (Exception e) { item[path] = "<error: " + (e.InnerException ?? e).Message + ">"; }
                }
            }
            items.Add(item);
        }
        return new { type = type.FullName, count = found.Length, returned = items.Count, items };
    }
}
