using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SharedDeck.Core;

/// <summary>An A:var that is copied from host to client, optionally for a range of indices.</summary>
public sealed class AVarEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("unit")] public string Unit { get; set; } = "number";
    [JsonPropertyName("index")] public int Index { get; set; }
    /// <summary>Inclusive index range; when set, one entry per index is synchronized.</summary>
    [JsonPropertyName("indexTo")] public int? IndexTo { get; set; }
    /// <summary>RPN code writing the value on the client. {v} = value, {i} = index. Empty = read/verify only.</summary>
    [JsonPropertyName("set")] public string Setter { get; set; } = "";
    [JsonPropertyName("tolerance")] public double Tolerance { get; set; }
}

/// <summary>All settings of SharedDeck. The host's sync settings are sent to the client on connect.</summary>
public sealed class SharedDeckConfig
{
    /// <summary>Raised whenever the default sync lists change, so older files are upgraded on load.</summary>
    public const int CurrentVersion = 3;

    [JsonPropertyName("configVersion")] public int ConfigVersion { get; set; }
    [JsonPropertyName("peerPort")] public int PeerPort { get; set; } = 51530;
    [JsonPropertyName("bridgePort")] public int BridgePort { get; set; } = 8391;
    [JsonPropertyName("lastHostAddress")] public string LastHostAddress { get; set; } = "";
    [JsonPropertyName("rateHz")] public double RateHz { get; set; } = 30;
    /// <summary>Strict: any deviation not declared as local pauses both sims. Off: deviations are only reported (tuning).</summary>
    [JsonPropertyName("strict")] public bool Strict { get; set; } = true;
    [JsonPropertyName("pauseHostOnLinkLoss")] public bool PauseHostOnLinkLoss { get; set; } = true;

    /// <summary>L:vars that are never synchronized (glob patterns).</summary>
    [JsonPropertyName("lvarDeny")] public List<string> LvarDeny { get; set; } = new();
    /// <summary>L:vars the client never forwards as pilot input.</summary>
    [JsonPropertyName("inputDeny")] public List<string> InputDeny { get; set; } = new();
    /// <summary>Key events (names without KEY_) the client never forwards.</summary>
    [JsonPropertyName("keyDeny")] public List<string> KeyDeny { get; set; } = new();
    /// <summary>Variables that may differ on purpose (e.g. seat position). L:vars by name, A:vars as "A:NAME".</summary>
    [JsonPropertyName("localAllow")] public List<string> LocalAllow { get; set; } = new();
    /// <summary>EventBus topics of the host that are not mirrored to the client.</summary>
    [JsonPropertyName("ebDenyHostToClient")] public List<string> EbDenyHostToClient { get; set; } = new();
    /// <summary>EventBus topics of the client that are forwarded to the host.</summary>
    [JsonPropertyName("ebAllowClientToHost")] public List<string> EbAllowClientToHost { get; set; } = new();
    /// <summary>H events of the client that are not forwarded.</summary>
    [JsonPropertyName("hEventDeny")] public List<string> HEventDeny { get; set; } = new();
    [JsonPropertyName("avars")] public List<AVarEntry> AVars { get; set; } = new();
    /// <summary>A:vars that are only compared on demand (audit), to find what is still missing in "avars".</summary>
    [JsonPropertyName("auditAVars")] public List<AVarEntry> AuditAVars { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static SharedDeckConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = CreateDefault();
            defaults.Save(path);
            return defaults;
        }
        var loaded = JsonSerializer.Deserialize<SharedDeckConfig>(File.ReadAllText(path), JsonOptions) ?? CreateDefault();
        if (loaded.ConfigVersion < CurrentVersion)
        {
            loaded = Upgrade(loaded);
            loaded.Save(path);
        }
        return loaded;
    }

    /// <summary>New default lists, keeping the user's own settings and variables declared local.</summary>
    public static SharedDeckConfig Upgrade(SharedDeckConfig old)
    {
        var upgraded = CreateDefault();
        upgraded.PeerPort = old.PeerPort;
        upgraded.BridgePort = old.BridgePort;
        upgraded.LastHostAddress = old.LastHostAddress;
        upgraded.RateHz = old.RateHz;
        upgraded.Strict = old.Strict;
        upgraded.PauseHostOnLinkLoss = old.PauseHostOnLinkLoss;
        foreach (var name in old.LocalAllow.Where(n => !upgraded.LocalAllow.Contains(n)))
        {
            upgraded.LocalAllow.Add(name);
        }
        return upgraded;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>Expands index ranges into single entries in a stable order (host and client must agree on it).</summary>
    public static IEnumerable<AVarEntry> Expand(IEnumerable<AVarEntry> entries)
    {
        foreach (var entry in entries)
        {
            var to = entry.IndexTo ?? entry.Index;
            for (var i = entry.Index; i <= to; i++)
            {
                yield return new AVarEntry { Name = entry.Name, Unit = entry.Unit, Index = i, Setter = entry.Setter, Tolerance = entry.Tolerance };
            }
        }
    }

    /// <summary>The settings both sides must share, sent from host to client.</summary>
    public JsonObject SyncSettings() => new()
    {
        ["rateHz"] = RateHz,
        ["strict"] = Strict,
        ["lvarDeny"] = ToArray(LvarDeny),
        ["inputDeny"] = ToArray(InputDeny),
        ["keyDeny"] = ToArray(KeyDeny),
        ["localAllow"] = ToArray(LocalAllow),
        ["ebDenyHostToClient"] = ToArray(EbDenyHostToClient),
        ["ebAllowClientToHost"] = ToArray(EbAllowClientToHost),
        ["hEventDeny"] = ToArray(HEventDeny),
        ["avars"] = JsonSerializer.SerializeToNode(AVars),
        ["auditAVars"] = JsonSerializer.SerializeToNode(AuditAVars),
    };

    /// <summary>Applies the host's settings received on connect (local network settings are kept).</summary>
    public void ApplySyncSettings(JsonObject settings)
    {
        RateHz = settings["rateHz"]?.GetValue<double>() ?? RateHz;
        Strict = settings["strict"]?.GetValue<bool>() ?? Strict;
        LvarDeny = FromArray(settings["lvarDeny"]);
        InputDeny = FromArray(settings["inputDeny"]);
        KeyDeny = FromArray(settings["keyDeny"]);
        LocalAllow = FromArray(settings["localAllow"]);
        EbDenyHostToClient = FromArray(settings["ebDenyHostToClient"]);
        EbAllowClientToHost = FromArray(settings["ebAllowClientToHost"]);
        HEventDeny = FromArray(settings["hEventDeny"]);
        AVars = settings["avars"].Deserialize<List<AVarEntry>>() ?? new();
        AuditAVars = settings["auditAVars"].Deserialize<List<AVarEntry>>() ?? new();
    }

    /// <summary>The configuration message for the WASM module.</summary>
    public JsonObject WasmConfig(int role) => new()
    {
        ["t"] = "config",
        ["role"] = role,
        ["strict"] = Strict,
        ["rateHz"] = RateHz,
        ["lvarDeny"] = ToArray(LvarDeny),
        ["inputDeny"] = ToArray(InputDeny),
        ["keyDeny"] = ToArray(KeyDeny),
        ["localAllow"] = ToArray(LocalAllow),
        ["avars"] = new JsonArray(Expand(AVars).Select(a => (JsonNode)new JsonObject
        {
            ["n"] = a.Name, ["u"] = a.Unit, ["i"] = a.Index, ["set"] = a.Setter, ["tol"] = a.Tolerance,
        }).ToArray()),
    };

    private static JsonArray ToArray(IEnumerable<string> items) => new(items.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());

    private static List<string> FromArray(JsonNode? node) =>
        node is JsonArray array ? array.Select(n => n!.GetValue<string>()).ToList() : new();

    public static SharedDeckConfig CreateDefault() => new()
    {
        ConfigVersion = CurrentVersion,
        LvarDeny =
        {
            "SHAREDDECK_*",
        },
        LocalAllow =
        {
            // each pilot has their own seat, camera and tablet
            "FBW_PILOT_SEAT",
            "A32NX_PILOT_AVATAR_VISIBLE_*",
            "A32NX_EFB_BRIGHTNESS",
            "A32NX_EFB_USING_AUTOBRIGHTNESS",
            "A32NX_EFB_USING_METRIC_UNIT",
            "A32NX_EFB_POWER",
            // display brightness of this PC
            "A32NX_AMBIENT_BRIGHTNESS",
            "XMLVAR_FirstUpdateTime",
            "XMLVAR_Interacting*",
            "XMLVAR_InteractingWith*",
            "XMLVAR_*Hidden*",
        },
        KeyDeny =
        {
            "VIEW*", "*CAMERA*", "EYEPOINT*", "CHASE*", "ZOOM*", "PANEL_*", "*_PANEL", "PAN_*",
            "PAUSE*", "SIM_RATE*", "SLEW*", "FREEZE*", "SITUATION_*", "CAPTURE_*", "SOUND_*", "TOOLTIP*",
            "FLIGHT_MAP", "KNEEBOARD*", "HELP*", "SELECT_*", "NEXT_SUB_VIEW", "PREV_SUB_VIEW", "ZULU_*",
        },
        EbDenyHostToClient =
        {
            "event_bus",
        },
        EbAllowClientToHost =
        {
            // flight plan edits/uplinks and failures from the co-pilot's flyPad
            "flightPlanRemoteClient_rpcCommand",
            "SHAREDDECK_FAILURE_COMMAND",
        },
        AVars =
        {
            // everything FBW writes into the simulator itself, set on the client the same way (SimConnect)
            new() { Name = "FUEL TANK CENTER QUANTITY", Unit = "gallons", Setter = "@sc", Tolerance = 0.001 },
            new() { Name = "FUEL TANK LEFT MAIN QUANTITY", Unit = "gallons", Setter = "@sc", Tolerance = 0.001 },
            new() { Name = "FUEL TANK LEFT AUX QUANTITY", Unit = "gallons", Setter = "@sc", Tolerance = 0.001 },
            new() { Name = "FUEL TANK RIGHT MAIN QUANTITY", Unit = "gallons", Setter = "@sc", Tolerance = 0.001 },
            new() { Name = "FUEL TANK RIGHT AUX QUANTITY", Unit = "gallons", Setter = "@sc", Tolerance = 0.001 },
            new() { Name = "PAYLOAD STATION WEIGHT", Unit = "pounds", Index = 1, IndexTo = 8, Setter = "@sc", Tolerance = 0.01 },
            new() { Name = "AILERON POSITION", Unit = "position", Setter = "@sc", Tolerance = 1e-4 },
            new() { Name = "ELEVATOR POSITION", Unit = "position", Setter = "@sc", Tolerance = 1e-4 },
            new() { Name = "RUDDER POSITION", Unit = "position", Setter = "@sc", Tolerance = 1e-4 },
            new() { Name = "TRAILING EDGE FLAPS LEFT PERCENT", Unit = "percent", Setter = "@sc", Tolerance = 0.01 },
            new() { Name = "TRAILING EDGE FLAPS RIGHT PERCENT", Unit = "percent", Setter = "@sc", Tolerance = 0.01 },
            new() { Name = "LEADING EDGE FLAPS LEFT PERCENT", Unit = "percent", Setter = "@sc", Tolerance = 0.01 },
            new() { Name = "LEADING EDGE FLAPS RIGHT PERCENT", Unit = "percent", Setter = "@sc", Tolerance = 0.01 },
            new() { Name = "FLAPS HANDLE INDEX", Unit = "number", Setter = "@sc" },
            new() { Name = "GEAR CENTER POSITION", Unit = "percent over 100", Setter = "@sc", Tolerance = 1e-4 },
            new() { Name = "GEAR LEFT POSITION", Unit = "percent over 100", Setter = "@sc", Tolerance = 1e-4 },
            new() { Name = "GEAR RIGHT POSITION", Unit = "percent over 100", Setter = "@sc", Tolerance = 1e-4 },
            new() { Name = "GEAR HANDLE POSITION", Unit = "percent over 100", Setter = "@sc", Tolerance = 1e-4 },
            new() { Name = "ELEVATOR TRIM POSITION", Unit = "degrees", Setter = "@sc", Tolerance = 1e-4 },
            new() { Name = "RUDDER TRIM PCT", Unit = "percent over 100", Setter = "@sc", Tolerance = 1e-6 },
            new() { Name = "SPOILERS HANDLE POSITION", Unit = "percent", Setter = "@sc", Tolerance = 0.001 },
            new() { Name = "GENERAL ENG THROTTLE LEVER POSITION", Unit = "percent", Index = 1, IndexTo = 2, Setter = "@sc", Tolerance = 0.001 },
            new() { Name = "TURB ENG CORRECTED N1", Unit = "percent", Index = 1, IndexTo = 2, Setter = "@sc", Tolerance = 0.01 },
            new() { Name = "TURB ENG CORRECTED N2", Unit = "percent", Index = 1, IndexTo = 2, Setter = "@sc", Tolerance = 0.01 },
            new() { Name = "GENERAL ENG OIL TEMPERATURE", Unit = "celsius", Index = 1, IndexTo = 2, Setter = "@sc", Tolerance = 0.01 },
            // native state FBW changes through key events
            // the STD flag first: while it is set, the simulator overrides the setting with 1013.25
            new() { Name = "KOHLSMAN SETTING STD", Unit = "bool", Index = 1, IndexTo = 2, Setter = "@sc" },
            new() { Name = "KOHLSMAN SETTING STD", Unit = "bool", Index = 4, Setter = "@sc" },
            new() { Name = "KOHLSMAN SETTING MB", Unit = "millibars", Index = 1, IndexTo = 2, Setter = "{i} {v} 16 * near (>K:2:KOHLSMAN_SET)", Tolerance = 0.07 },
            new() { Name = "KOHLSMAN SETTING MB", Unit = "millibars", Index = 4, Setter = "{i} {v} 16 * near (>K:2:KOHLSMAN_SET)", Tolerance = 0.07 },
            new() { Name = "LIGHT POTENTIOMETER", Unit = "percent", Index = 1, IndexTo = 100, Setter = "{v} {i} (>K:2:LIGHT_POTENTIOMETER_SET)", Tolerance = 0.5 },
            new() { Name = "CIRCUIT SWITCH ON", Unit = "bool", Index = 1, IndexTo = 120, Setter = "(A:CIRCUIT SWITCH ON:{i}, bool) {v} != if{ {i} (>K:ELECTRICAL_CIRCUIT_TOGGLE) }" },
            new() { Name = "COM ACTIVE FREQUENCY", Unit = "hertz", Index = 1, Setter = "{v} near (>K:COM_RADIO_SET_HZ)" },
            new() { Name = "COM ACTIVE FREQUENCY", Unit = "hertz", Index = 2, Setter = "{v} near (>K:COM2_RADIO_SET_HZ)" },
            new() { Name = "COM ACTIVE FREQUENCY", Unit = "hertz", Index = 3, Setter = "{v} near (>K:COM3_RADIO_SET_HZ)" },
            new() { Name = "NAV ACTIVE FREQUENCY", Unit = "hertz", Index = 1, Setter = "{v} near (>K:NAV1_RADIO_SET_HZ)" },
            new() { Name = "NAV ACTIVE FREQUENCY", Unit = "hertz", Index = 2, Setter = "{v} near (>K:NAV2_RADIO_SET_HZ)" },
            new() { Name = "TRANSPONDER CODE", Unit = "bco16", Index = 1, Setter = "{v} near (>K:XPNDR_SET)" },
            // doors and hatches: the goal is set, the simulator animates towards it
            new() { Name = "INTERACTIVE POINT GOAL", Unit = "percent over 100", Index = 0, IndexTo = 9, Setter = "{v} (>A:INTERACTIVE POINT GOAL:{i}, percent over 100)", Tolerance = 0.01 },
            // weather can't be set by an add-on: compared only and shown as a hint, never a halt
            new() { Name = "AMBIENT WIND VELOCITY", Unit = "knots", Tolerance = 5 },
            new() { Name = "AMBIENT TEMPERATURE", Unit = "celsius", Tolerance = 2 },
            new() { Name = "SEA LEVEL PRESSURE", Unit = "millibars", Tolerance = 2 },
            new() { Name = "AMBIENT VISIBILITY", Unit = "meters", Tolerance = 2000 },
            new() { Name = "AMBIENT PRECIP STATE", Unit = "number" },
        },
        AuditAVars =
        {
            new() { Name = "AIRSPEED INDICATED", Unit = "knots" },
            new() { Name = "AIRSPEED TRUE", Unit = "knots" },
            new() { Name = "INDICATED ALTITUDE", Unit = "feet" },
            new() { Name = "PLANE ALTITUDE", Unit = "feet" },
            new() { Name = "FUEL TOTAL QUANTITY WEIGHT", Unit = "pounds" },
            new() { Name = "TOTAL WEIGHT", Unit = "pounds" },
            new() { Name = "GENERAL ENG COMBUSTION", Unit = "bool", Index = 1, IndexTo = 2 },
            new() { Name = "TURB ENG N1", Unit = "percent", Index = 1, IndexTo = 2 },
            new() { Name = "GEAR ANIMATION POSITION", Unit = "percent", Index = 0, IndexTo = 2 },
            new() { Name = "BRAKE PARKING POSITION", Unit = "bool" },
            new() { Name = "INTERACTIVE POINT OPEN", Unit = "percent over 100", Index = 0, IndexTo = 9 },
            new() { Name = "LIGHT LANDING ON", Unit = "bool" },
            new() { Name = "LIGHT BEACON ON", Unit = "bool" },
            new() { Name = "LIGHT NAV ON", Unit = "bool" },
            new() { Name = "LIGHT STROBE ON", Unit = "bool" },
            new() { Name = "LIGHT TAXI ON", Unit = "bool" },
            new() { Name = "LIGHT LOGO ON", Unit = "bool" },
            new() { Name = "LIGHT WING ON", Unit = "bool" },
            new() { Name = "APU PCT RPM", Unit = "percent" },
            new() { Name = "ADF ACTIVE FREQUENCY", Unit = "hertz", Index = 1 },
            new() { Name = "AMBIENT WIND DIRECTION", Unit = "degrees" },
        },
    };
}
