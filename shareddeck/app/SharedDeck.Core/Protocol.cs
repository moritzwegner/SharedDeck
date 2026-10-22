using System.Text.Json.Nodes;

namespace SharedDeck.Core;

/// <summary>
/// Message envelopes. Every message (bridge &lt;-&gt; app and app &lt;-&gt; app) is a JSON object with a channel "ch":
/// <list type="bullet">
/// <item>wasm  - a message of the WASM module, passed through unchanged as string "d"</item>
/// <item>eb    - an EventBus sync package "pkg" of listener channel "k"</item>
/// <item>h     - an H event "n"</item>
/// <item>bridge, mode, resync - bridge control</item>
/// <item>hello, welcome, reject, halt, resume, ping, pong, auditRequest, auditResult - between the apps</item>
/// </list>
/// </summary>
public static class Protocol
{
    public const int Version = 1;

    public const int RoleClient = 1;
    public const int RoleHost = 2;

    public static string Envelope(string channel, JsonObject? fields = null)
    {
        var message = fields ?? new JsonObject();
        message["ch"] = channel;
        return message.ToJsonString();
    }

    public static string Wasm(string payload) => new JsonObject { ["ch"] = "wasm", ["d"] = payload }.ToJsonString();

    public static string Wasm(JsonObject payload) => Wasm(payload.ToJsonString());

    public static JsonObject? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Glob match with '*' wildcards, same semantics as in the WASM module.</summary>
    public static bool Matches(IEnumerable<string> patterns, string name) => patterns.Any(p => Glob(p, 0, name, 0));

    private static bool Glob(string pattern, int p, string text, int t)
    {
        while (p < pattern.Length)
        {
            if (pattern[p] == '*')
            {
                p++;
                if (p == pattern.Length)
                {
                    return true;
                }
                for (; t <= text.Length; t++)
                {
                    if (Glob(pattern, p, text, t))
                    {
                        return true;
                    }
                }
                return false;
            }
            if (t >= text.Length || pattern[p] != text[t])
            {
                return false;
            }
            p++;
            t++;
        }
        return t == text.Length;
    }
}
