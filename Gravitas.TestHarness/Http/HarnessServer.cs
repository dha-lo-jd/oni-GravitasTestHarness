using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Gravitas.TestHarness.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Gravitas.TestHarness.Http;

/// <summary>
/// Localhost-only JSON HTTP server. Requests are received on worker threads and the
/// handler is executed on the Unity main thread.
/// </summary>
internal sealed class HarnessServer
{
    private sealed class Route
    {
        public string Method;
        public Func<JObject, object> Handler;
        public System.Action AfterResponse;
    }

    private readonly Dictionary<string, Route> _routes = new Dictionary<string, Route>();
    private HttpListener _listener;
    private Thread _thread;

    public void Map(string method, string path, Func<JObject, object> handler, System.Action afterResponse = null)
    {
        _routes[path] = new Route { Method = method, Handler = handler, AfterResponse = afterResponse };
    }

    public void Start(int port)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        _listener.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Gravitas.TestHarness.Http" };
        _thread.Start();
        Debug.Log("[Gravitas.TestHarness] Listening on http://127.0.0.1:" + port + "/");
    }

    private void Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (Exception) { break; }
            // One worker per request so a slow handler never blocks other requests.
            ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        int status = 200;
        string json;
        System.Action after = null;
        try
        {
            var path = ctx.Request.Url.AbsolutePath;
            if (!_routes.TryGetValue(path, out var route))
            {
                status = 404;
                json = Error("Not found: " + path, null);
            }
            else if (!string.Equals(route.Method, ctx.Request.HttpMethod, StringComparison.OrdinalIgnoreCase))
            {
                status = 405;
                json = Error("Method not allowed: " + ctx.Request.HttpMethod + " (expected " + route.Method + ")", null);
            }
            else
            {
                JObject body = null;
                if (ctx.Request.HasEntityBody)
                {
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    {
                        var text = reader.ReadToEnd();
                        if (!string.IsNullOrWhiteSpace(text)) body = JObject.Parse(text);
                    }
                }
                var data = MainThreadDispatcher.Invoke(() => route.Handler(body));
                if (data is Pending pending)
                {
                    if (!pending.Done.Wait(pending.TimeoutMs))
                    {
                        if (pending.OnTimeout != null) MainThreadDispatcher.Post(pending.OnTimeout);
                        throw new TimeoutException("Operation did not finish within " + pending.TimeoutMs + " ms.");
                    }
                    if (pending.Error != null) throw pending.Error;
                    data = pending.Result;
                }
                json = JsonConvert.SerializeObject(new { ok = true, data });
                after = route.AfterResponse;
            }
        }
        catch (Exception e)
        {
            status = 500;
            var inner = e.InnerException ?? e;
            json = Error(inner.Message, inner.ToString());
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Gravitas.TestHarness] Failed to write response: " + e.Message);
        }

        if (after != null) MainThreadDispatcher.Post(after);
    }

    private static string Error(string message, string stackTrace)
    {
        return JsonConvert.SerializeObject(new { ok = false, error = new { message, stackTrace } });
    }
}
