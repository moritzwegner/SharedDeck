using System.Diagnostics;
using System.Text.Json.Nodes;
using SharedDeck.Core.Transport;

namespace SharedDeck.Core;

public enum SessionRole
{
    None,
    Host,
    Client,
}

public enum SessionState
{
    Idle,
    WaitingForCoPilot,
    Connecting,
    Synchronizing,
    Live,
    Halted,
    LinkLost,
}

/// <summary>Immutable snapshot for the UI.</summary>
public sealed record SessionStatus(
    SessionRole Role,
    SessionState State,
    string Reason,
    bool BridgeConnected,
    string Aircraft,
    bool PeerConnected,
    double RttMs,
    double FramesPerSecond,
    double KilobytesPerSecondOut,
    double KilobytesPerSecondIn,
    int LVarCount,
    long InputsSent,
    bool LastVerifyOk,
    IReadOnlyList<string> Mismatches,
    IReadOnlyList<string> Contested,
    IReadOnlyList<string> Hints,
    string LeverOwners);

/// <summary>One A:var that differs between host and client in an audit.</summary>
public sealed record AuditDifference(string Variable, double? Host, double? Client);

/// <summary>
/// Connects the local bridge (simulator) with the other pilot's app and decides when the sync is
/// broken. The host simulator is the only source of truth; the client only mirrors it and sends
/// the pilot's inputs.
/// </summary>
public sealed class SessionController : IDisposable
{
    private const int TransportStreakLimit = 3;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);

    private readonly SharedDeckConfig localConfig;
    private readonly IMessageChannel bridge;
    private readonly Func<IPeerConnector> peerFactory;
    private readonly object gate = new();
    private readonly Timer watchdog;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private SharedDeckConfig config;
    private IPeerConnector? peer;
    private SessionRole role = SessionRole.None;
    private SessionState state = SessionState.Idle;
    private string reason = "";
    private string aircraft = "";
    private string bridgeVersion = "";
    private bool wasmReady;
    private double rttMs;
    private int lvarCount;
    private long inputsSent;
    private bool lastVerifyOk;
    private List<string> mismatches = new();
    private List<string> hints = new();
    private string leverOwners = "";
    private readonly SortedSet<string> contested = new();
    private double lastFrameAt = -1;
    private int framesThisSecond;
    private double framesPerSecond;
    private long lastBytesOut;
    private long lastBytesIn;
    private double kbOut;
    private double kbIn;
    private double lastRateAt;
    private int auditId;
    private JsonArray? pendingAuditLocal;
    private JsonArray? pendingAuditRemote;

    public SessionController(SharedDeckConfig config, IMessageChannel bridge, Func<IPeerConnector> peerFactory)
    {
        localConfig = config;
        this.config = config;
        this.bridge = bridge;
        this.peerFactory = peerFactory;
        bridge.MessageReceived += OnBridgeMessage;
        bridge.Connected += () =>
        {
            Log("Simulator-Bridge verbunden");
            SendModeToBridge();
            if (state is SessionState.Synchronizing or SessionState.Live)
            {
                // aircraft reloaded during a session: configure the module again
                StartLocalSession();
            }
            Changed();
        };
        bridge.Disconnected += why =>
        {
            Log($"Simulator-Bridge getrennt: {why}");
            wasmReady = false;
            // a stale build would be reported to the host after an aircraft change
            bridgeVersion = "";
            aircraft = "";
            if (state is SessionState.Live or SessionState.Synchronizing)
            {
                Halt("Verbindung zum eigenen Simulator verloren");
            }
            Changed();
        };
        watchdog = new Timer(_ => Tick(), null, 500, 500);
    }

    public event Action? StatusChanged;
    public event Action<string>? LogMessage;
    public event Action<IReadOnlyList<AuditDifference>>? AuditCompleted;

    public SessionStatus Status
    {
        get
        {
            lock (gate)
            {
                return new SessionStatus(role, state, reason, bridge.IsConnected, aircraft, peer?.IsConnected == true, rttMs,
                    framesPerSecond, kbOut, kbIn, lvarCount, inputsSent, lastVerifyOk, mismatches.ToList(), contested.ToList(),
                    hints.ToList(), leverOwners);
            }
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // user actions
    // -----------------------------------------------------------------------------------------------------------------

    public void StartHost()
    {
        lock (gate)
        {
            if (role != SessionRole.None)
            {
                return;
            }
            role = SessionRole.Host;
            config = localConfig;
            peer = peerFactory();
            AttachPeer(peer);
            peer.Listen(config.PeerPort);
            SetState(SessionState.WaitingForCoPilot, $"Warte auf Co-Pilot (Port {config.PeerPort})");
        }
    }

    public async Task ConnectAsync(string host, CancellationToken token = default)
    {
        IPeerConnector connector;
        lock (gate)
        {
            if (role == SessionRole.Host)
            {
                return;
            }
            // without its own simulator the host could only reject us, with a misleading reason
            var notReady = LocalSimulatorProblem();
            if (notReady != null)
            {
                SetState(SessionState.Idle, $"Nicht verbunden: {notReady}");
                return;
            }
            role = SessionRole.Client;
            peer?.Dispose();
            peer = peerFactory();
            connector = peer;
            AttachPeer(connector);
            SetState(SessionState.Connecting, $"Verbinde mit {host} …");
        }

        try
        {
            await connector.ConnectAsync(host, localConfig.PeerPort, token);
        }
        catch (Exception e)
        {
            lock (gate)
            {
                role = SessionRole.None;
                SetState(SessionState.Idle, $"Verbindung fehlgeschlagen: {e.Message}");
            }
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (bridge.IsConnected)
            {
                bridge.Send(Protocol.Wasm(new JsonObject { ["t"] = "stop" }));
                bridge.Send(Protocol.Envelope("mode", new JsonObject { ["role"] = "off" }));
            }
            peer?.Dispose();
            peer = null;
            role = SessionRole.None;
            config = localConfig;
            contested.Clear();
            mismatches.Clear();
            SetState(SessionState.Idle, "Getrennt");
        }
    }

    /// <summary>After a halt: full resynchronisation and continue.</summary>
    public void Resume()
    {
        lock (gate)
        {
            if (state != SessionState.Halted)
            {
                return;
            }
            if (role == SessionRole.Host)
            {
                ResumeAsHost();
                peer?.Send(Protocol.Envelope("resume"));
            }
            else if (role == SessionRole.Client)
            {
                peer?.Send(Protocol.Envelope("resume"));
            }
        }
    }

    /// <summary>Compares the audit A:vars of both simulators (client only).</summary>
    public void RunAudit()
    {
        lock (gate)
        {
            if (role != SessionRole.Client || peer?.IsConnected != true)
            {
                return;
            }
            auditId++;
            pendingAuditLocal = null;
            pendingAuditRemote = null;
            peer.Send(Protocol.Envelope("auditRequest", new JsonObject { ["id"] = auditId }));
            bridge.Send(Protocol.Wasm(AuditMessage(auditId)));
        }
    }

    /// <summary>Declares the currently contested variables as local and saves the configuration (host).</summary>
    public IReadOnlyList<string> ContestedVariables
    {
        get
        {
            lock (gate)
            {
                return contested.ToList();
            }
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // bridge
    // -----------------------------------------------------------------------------------------------------------------

    private void OnBridgeMessage(string text)
    {
        var message = Protocol.Parse(text);
        if (message is null)
        {
            return;
        }
        lock (gate)
        {
            switch (message["ch"]?.GetValue<string>())
            {
                case "bridge":
                    aircraft = message["aircraft"]?.GetValue<string>() ?? "";
                    bridgeVersion = message["version"]?.GetValue<string>() ?? "";
                    Log($"Bridge {bridgeVersion} im Flugzeug „{aircraft}“");
                    // the module's own ready message is usually sent before the bridge listens
                    bridge.Send(Protocol.Wasm(new JsonObject { ["t"] = "ping" }));
                    Changed();
                    break;
                case "wasm":
                    OnWasmMessage(message["d"]?.GetValue<string>() ?? "");
                    break;
                case "eb":
                    ForwardEventBus(message, fromBridge: true);
                    break;
                case "h":
                    if (role == SessionRole.Client && state == SessionState.Live)
                    {
                        var name = message["n"]?.GetValue<string>() ?? "";
                        if (!Protocol.Matches(config.HEventDeny, name))
                        {
                            peer?.Send(text);
                        }
                    }
                    break;
            }
        }
    }

    private void OnWasmMessage(string payload)
    {
        var message = Protocol.Parse(payload);
        var type = message?["t"]?.GetValue<string>();
        switch (type)
        {
            case "ready":
                wasmReady = true;
                Log("SharedDeck-Modul bereit");
                break;
            case "log":
                Log($"Modul: {message!["msg"]?.GetValue<string>()}");
                break;
            case "stats":
                lvarCount = message!["lvars"]?.GetValue<int>() ?? lvarCount;
                Changed();
                break;
            case "names":
            case "frame":
                if (role == SessionRole.Host && peer?.IsConnected == true && state == SessionState.Live)
                {
                    peer.Send(Protocol.Wasm(payload));
                    if (type == "frame")
                    {
                        framesThisSecond++;
                    }
                }
                break;
            case "input":
                if (role == SessionRole.Client && state == SessionState.Live)
                {
                    inputsSent += (message!["l"] as JsonArray)?.Count ?? 0;
                    inputsSent += (message["k"] as JsonArray)?.Count ?? 0;
                    peer?.Send(Protocol.Wasm(payload));
                }
                break;
            case "needKeyframe":
                if (role == SessionRole.Client)
                {
                    Log($"Neusynchronisierung angefordert: {message!["reason"]?.GetValue<string>()}");
                    peer?.Send(Protocol.Wasm(payload));
                }
                break;
            case "verify":
                if (role == SessionRole.Client)
                {
                    OnVerify(message!);
                }
                break;
            case "contested":
                if (role == SessionRole.Client)
                {
                    var name = message!["name"]?.GetValue<string>() ?? "";
                    if (contested.Add(name))
                    {
                        Log($"Wird auf diesem PC lokal beschrieben: {name}");
                    }
                    Changed();
                }
                break;
            case "audit":
                if (role == SessionRole.Host)
                {
                    peer?.Send(Protocol.Envelope("auditResult", new JsonObject
                    {
                        ["id"] = message!["id"]?.GetValue<int>() ?? 0,
                        ["values"] = message["values"]?.DeepClone(),
                    }));
                }
                else if (role == SessionRole.Client && message!["id"]?.GetValue<int>() == auditId)
                {
                    pendingAuditLocal = message["values"] as JsonArray;
                    CompleteAuditIfReady();
                }
                break;
        }
    }

    private void ForwardEventBus(JsonObject message, bool fromBridge)
    {
        if (!fromBridge)
        {
            // from the other app: inject into the local simulator
            if (state == SessionState.Live)
            {
                bridge.Send(message.ToJsonString());
            }
            return;
        }
        if (state != SessionState.Live || peer?.IsConnected != true || message["pkg"]?["data"] is not JsonArray entries)
        {
            return;
        }
        // host mirrors everything except denied topics, the client only sends explicitly allowed topics
        var keep = new JsonArray();
        foreach (var entry in entries)
        {
            var topic = entry?["topic"]?.GetValue<string>() ?? "";
            var forward = role == SessionRole.Host
                ? !Protocol.Matches(config.EbDenyHostToClient, topic)
                : Protocol.Matches(config.EbAllowClientToHost, topic);
            if (forward)
            {
                keep.Add(entry!.DeepClone());
            }
        }
        if (keep.Count == 0)
        {
            return;
        }
        var package = (JsonObject)message["pkg"]!.DeepClone();
        package["data"] = keep;
        peer.Send(Protocol.Envelope("eb", new JsonObject { ["k"] = message["k"]?.DeepClone(), ["pkg"] = package }));
    }

    private void SendModeToBridge()
    {
        var mode = role switch
        {
            SessionRole.Host when state is SessionState.Live or SessionState.Synchronizing or SessionState.Halted => "host",
            SessionRole.Client when state is SessionState.Live or SessionState.Synchronizing or SessionState.Halted or SessionState.LinkLost => "client",
            _ => "off",
        };
        bridge.Send(Protocol.Envelope("mode", new JsonObject { ["role"] = mode }));
    }

    private void StartLocalSession()
    {
        var wasmRole = role == SessionRole.Host ? Protocol.RoleHost : Protocol.RoleClient;
        bridge.Send(Protocol.Wasm(config.WasmConfig(wasmRole)));
        bridge.Send(Protocol.Wasm(new JsonObject { ["t"] = "start" }));
        SendModeToBridge();
        if (role == SessionRole.Host)
        {
            // let all instruments publish their current EventBus state again for the new client
            bridge.Send(Protocol.Envelope("resync"));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // peer
    // -----------------------------------------------------------------------------------------------------------------

    private void AttachPeer(IPeerConnector connector)
    {
        connector.MessageReceived += text => OnPeerMessage(connector, text);
        connector.Connected += () => OnPeerConnected(connector);
        connector.Disconnected += why => OnPeerDisconnected(connector, why);
    }

    private void OnPeerConnected(IPeerConnector connector)
    {
        lock (gate)
        {
            if (!ReferenceEquals(peer, connector))
            {
                return;
            }
            if (role == SessionRole.Client)
            {
                connector.Send(Protocol.Envelope("hello", new JsonObject
                {
                    ["version"] = Protocol.Version,
                    ["bridgeVersion"] = bridgeVersion,
                    ["aircraft"] = aircraft,
                }));
                Log($"Mit Host verbunden, warte auf Freigabe (eigener Flugzeug-Build „{bridgeVersion}“)");
            }
            else
            {
                Log("Co-Pilot verbindet sich …");
            }
            Changed();
        }
    }

    private void OnPeerDisconnected(IPeerConnector connector, string why)
    {
        lock (gate)
        {
            if (!ReferenceEquals(peer, connector))
            {
                return;
            }
            Log($"Verbindung zur Gegenstelle getrennt: {why}");
            if (role == SessionRole.Client && state != SessionState.Idle)
            {
                if (bridge.IsConnected)
                {
                    bridge.Send(Protocol.Wasm(new JsonObject { ["t"] = "pause", ["on"] = true }));
                }
                SetState(SessionState.LinkLost, "Verbindung zum Host verloren – Simulator pausiert");
            }
            else if (role == SessionRole.Host && state != SessionState.Idle)
            {
                if (config.PauseHostOnLinkLoss && bridge.IsConnected)
                {
                    bridge.Send(Protocol.Wasm(new JsonObject { ["t"] = "pause", ["on"] = true }));
                }
                SetState(SessionState.WaitingForCoPilot, "Co-Pilot getrennt – warte auf erneute Verbindung");
            }
        }
    }

    private void OnPeerMessage(IPeerConnector connector, string text)
    {
        var message = Protocol.Parse(text);
        if (message is null)
        {
            return;
        }
        lock (gate)
        {
            if (!ReferenceEquals(peer, connector))
            {
                return;
            }
            switch (message["ch"]?.GetValue<string>())
            {
                case "hello":
                    OnHello(connector, message);
                    break;
                case "welcome":
                    OnWelcome(message);
                    break;
                case "reject":
                    Log($"Host hat abgelehnt: {message["reason"]?.GetValue<string>()}");
                    connector.Dispose();
                    peer = null;
                    role = SessionRole.None;
                    SetState(SessionState.Idle, $"Abgelehnt: {message["reason"]?.GetValue<string>()}");
                    break;
                case "wasm":
                    OnPeerWasm(message);
                    break;
                case "eb":
                    ForwardEventBus(message, fromBridge: false);
                    break;
                case "h":
                    if (role == SessionRole.Host && state == SessionState.Live)
                    {
                        bridge.Send(text);
                    }
                    break;
                case "halt":
                    PauseLocal(true);
                    SetState(SessionState.Halted, $"Not-Halt: {message["reason"]?.GetValue<string>()}");
                    break;
                case "resume":
                    if (role == SessionRole.Host)
                    {
                        // requested by the client: resume here and confirm
                        ResumeAsHost();
                        connector.Send(Protocol.Envelope("resume"));
                    }
                    else
                    {
                        mismatches.Clear();
                        lastFrameAt = clock.Elapsed.TotalSeconds;
                        SetState(SessionState.Synchronizing, "Neusynchronisierung nach Halt");
                    }
                    break;
                case "ping":
                    connector.Send(Protocol.Envelope("pong", new JsonObject { ["t"] = message["t"]?.DeepClone() }));
                    break;
                case "pong":
                    rttMs = clock.Elapsed.TotalMilliseconds - (message["t"]?.GetValue<double>() ?? 0);
                    break;
                case "verifyReport":
                    if (role == SessionRole.Host)
                    {
                        lastVerifyOk = message["ok"]?.GetValue<bool>() ?? false;
                        mismatches = (message["mismatches"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? new();
                        contested.Clear();
                        foreach (var name in (message["contested"] as JsonArray)?.Select(n => n!.GetValue<string>()) ?? [])
                        {
                            contested.Add(name);
                        }
                        hints = (message["hints"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? new();
                        leverOwners = message["levers"]?.GetValue<string>() ?? "";
                        Changed();
                    }
                    break;
                case "auditRequest":
                    if (role == SessionRole.Host)
                    {
                        bridge.Send(Protocol.Wasm(AuditMessage(message["id"]?.GetValue<int>() ?? 0)));
                    }
                    break;
                case "auditResult":
                    if (role == SessionRole.Client && message["id"]?.GetValue<int>() == auditId)
                    {
                        pendingAuditRemote = message["values"] as JsonArray;
                        CompleteAuditIfReady();
                    }
                    break;
            }
        }
    }

    private void OnHello(IPeerConnector connector, JsonObject message)
    {
        if (role != SessionRole.Host)
        {
            return;
        }
        var copilotBuild = message["bridgeVersion"]?.GetValue<string>() ?? "";
        Log($"Co-Pilot meldet Flugzeug-Build „{(copilotBuild.Length > 0 ? copilotBuild : "–")}“, eigener Build „{bridgeVersion}“");
        string? rejection = null;
        if (message["version"]?.GetValue<int>() != Protocol.Version)
        {
            rejection = "unterschiedliche SharedDeck-Versionen";
        }
        else if (LocalSimulatorProblem() is { } hostProblem)
        {
            rejection = $"beim Host: {hostProblem}";
        }
        else if (copilotBuild.Length == 0)
        {
            rejection = "beim Co-Piloten ist der Simulator nicht mit SharedDeck verbunden (MSFS mit dem SharedDeck-A32NX starten, " +
                        "unter „Simulator“ muss „verbunden“ stehen)";
        }
        else if (copilotBuild != bridgeVersion)
        {
            rejection = DescribeBuildMismatch(bridgeVersion, copilotBuild);
        }
        if (rejection != null)
        {
            Log($"Co-Pilot abgelehnt: {rejection}");
            connector.Send(Protocol.Envelope("reject", new JsonObject { ["reason"] = rejection }));
            return;
        }

        connector.Send(Protocol.Envelope("welcome", new JsonObject
        {
            ["version"] = Protocol.Version,
            ["settings"] = config.SyncSettings(),
        }));
        mismatches.Clear();
        contested.Clear();
        SetState(SessionState.Live, "Co-Pilot verbunden");
        StartLocalSession();
        PauseLocal(false);
    }

    /// <summary>Why this simulator can't take part, or null if it is ready.</summary>
    private string? LocalSimulatorProblem()
    {
        if (!bridge.IsConnected)
        {
            return "der Simulator ist nicht mit SharedDeck verbunden (MSFS mit dem SharedDeck-A32NX starten und einen Flug laden)";
        }
        if (!wasmReady)
        {
            return "das SharedDeck-Modul antwortet nicht (Ordner shareddeck-module im Community-Ordner? MSFS danach neu gestartet?)";
        }
        return null;
    }

    /// <summary>Both aircraft builds in plain words, so it is clear who has to install what.</summary>
    public static string DescribeBuildMismatch(string hostBuild, string copilotBuild)
    {
        static string Describe(string build) =>
            build.Contains("shareddeck", StringComparison.OrdinalIgnoreCase) ? $"SharedDeck-A32NX {build}"
            : build == "unknown" ? "Flugzeugversion nicht lesbar"
            : $"normaler FlyByWire A32NX {build}";

        return $"unterschiedliche Flugzeuge – Host: {Describe(hostBuild)}, Co-Pilot: {Describe(copilotBuild)}. " +
               "Beide müssen den SharedDeck-A32NX aus demselben Release installieren (SharedDeck-Setup.exe mit den ZIP-Teilen).";
    }

    private void OnWelcome(JsonObject message)
    {
        if (role != SessionRole.Client)
        {
            return;
        }
        if (!bridge.IsConnected || !wasmReady)
        {
            SetState(SessionState.Idle, "Eigener Simulator nicht bereit (Flugzeug geladen? SharedDeck-Modul installiert?)");
            peer?.Dispose();
            peer = null;
            role = SessionRole.None;
            return;
        }
        // the host decides what is synchronized; the local file stays untouched
        config = SharedDeckConfig.CreateDefault();
        config.PeerPort = localConfig.PeerPort;
        config.ApplySyncSettings((JsonObject)message["settings"]!);
        mismatches.Clear();
        contested.Clear();
        lastFrameAt = clock.Elapsed.TotalSeconds;
        SetState(SessionState.Synchronizing, "Übernehme Zustand des Hosts …");
        StartLocalSession();
    }

    private void OnPeerWasm(JsonObject message)
    {
        var payload = message["d"]?.GetValue<string>() ?? "";
        if (role == SessionRole.Client)
        {
            // names and frames go straight into the module
            if (state is SessionState.Synchronizing or SessionState.Live or SessionState.Halted)
            {
                bridge.Send(Protocol.Wasm(payload));
                if (payload.Contains("\"t\":\"frame\""))
                {
                    lastFrameAt = clock.Elapsed.TotalSeconds;
                    framesThisSecond++;
                }
            }
            return;
        }

        var inner = Protocol.Parse(payload);
        switch (inner?["t"]?.GetValue<string>())
        {
            case "input":
                if (state == SessionState.Live)
                {
                    bridge.Send(Protocol.Wasm(payload));
                }
                break;
            case "needKeyframe":
                Log($"Co-Pilot fordert Neusynchronisierung an: {inner["reason"]?.GetValue<string>()}");
                bridge.Send(Protocol.Wasm(new JsonObject { ["t"] = "keyframe" }));
                bridge.Send(Protocol.Envelope("resync"));
                break;
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // verification and halt (client decides, host follows)
    // -----------------------------------------------------------------------------------------------------------------

    private void OnVerify(JsonObject message)
    {
        if (state is not (SessionState.Synchronizing or SessionState.Live))
        {
            return;
        }
        var kind = message["kind"]?.GetValue<string>();
        if (kind == "transport")
        {
            var streak = message["streak"]?.GetValue<int>() ?? 0;
            Log($"Zustands-Prüfsumme weicht ab (Frame {message["seq"]}), Neusynchronisierung #{streak}");
            if (streak >= TransportStreakLimit)
            {
                Halt($"Zustand weicht nach {streak} Neusynchronisierungen weiterhin ab");
            }
            return;
        }

        var ok = message["ok"]?.GetValue<bool>() ?? false;
        var lvarMismatches = (message["lvarMismatches"] as JsonArray)?.Select(n => n!.GetValue<string>()) ?? [];
        var avarMismatches = (message["avarMismatches"] as JsonArray)?.Select(n => $"{n![0]} (lokal {n[1]}, Host {n[2]})") ?? [];
        mismatches = lvarMismatches.Concat(avarMismatches).ToList();
        foreach (var name in (message["contested"] as JsonArray)?.Select(n => n!.GetValue<string>()) ?? [])
        {
            contested.Add(name);
        }
        lastVerifyOk = ok;
        lvarCount = message["lvars"]?.GetValue<int>() ?? lvarCount;
        hints = (message["advisory"] as JsonArray)?
            .Select(n => $"Wetter/Umgebung abweichend: {n![0]} (hier {n[1]}, Host {n[2]})").ToList() ?? new();
        leverOwners = LeverOwnerText(message["levers"] as JsonArray);

        peer?.Send(Protocol.Envelope("verifyReport", new JsonObject
        {
            ["ok"] = ok,
            ["mismatches"] = new JsonArray(mismatches.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["contested"] = new JsonArray(contested.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["hints"] = new JsonArray(hints.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["levers"] = leverOwners,
        }));

        if (ok)
        {
            if (state == SessionState.Synchronizing)
            {
                SetState(SessionState.Live, "Synchron");
            }
            Changed();
            return;
        }

        var details = string.Join(", ", mismatches.Concat(contested.Select(c => c + " (lokal beschrieben)")).Take(8));
        if (config.Strict)
        {
            Halt($"Abweichung: {details}");
        }
        else
        {
            if (state == SessionState.Synchronizing)
            {
                SetState(SessionState.Live, "Synchron (Abstimmungsmodus)");
            }
            Log($"Abweichung (Abstimmungsmodus, kein Halt): {details}");
            Changed();
        }
    }

    private static string LeverOwnerText(JsonArray? owners)
    {
        if (owners is not { Count: 2 })
        {
            return "";
        }
        static string Owner(JsonNode? node) => node?.GetValue<int>() == 1 ? "Co-Pilot" : "Kapitän";
        var first = Owner(owners[0]);
        var second = Owner(owners[1]);
        return first == second ? first : $"1: {first}, 2: {second}";
    }

    private void Halt(string why)
    {
        if (state == SessionState.Halted)
        {
            return;
        }
        Log($"NOT-HALT: {why}");
        PauseLocal(true);
        peer?.Send(Protocol.Envelope("halt", new JsonObject { ["reason"] = why }));
        SetState(SessionState.Halted, why);
    }

    private void ResumeAsHost()
    {
        bridge.Send(Protocol.Wasm(new JsonObject { ["t"] = "keyframe" }));
        bridge.Send(Protocol.Envelope("resync"));
        PauseLocal(false);
        mismatches.Clear();
        SetState(SessionState.Live, "Fortgesetzt nach Neusynchronisierung");
    }

    private void PauseLocal(bool on)
    {
        if (bridge.IsConnected)
        {
            bridge.Send(Protocol.Wasm(new JsonObject { ["t"] = "pause", ["on"] = on }));
        }
    }

    private void Tick()
    {
        lock (gate)
        {
            var now = clock.Elapsed.TotalSeconds;
            if (now - lastRateAt >= 1)
            {
                var elapsed = now - lastRateAt;
                lastRateAt = now;
                framesPerSecond = framesThisSecond / elapsed;
                framesThisSecond = 0;
                var outBytes = peer?.BytesSent ?? 0;
                var inBytes = peer?.BytesReceived ?? 0;
                kbOut = Math.Max(0, outBytes - lastBytesOut) / 1024.0 / elapsed;
                kbIn = Math.Max(0, inBytes - lastBytesIn) / 1024.0 / elapsed;
                lastBytesOut = outBytes;
                lastBytesIn = inBytes;
                if (peer?.IsConnected == true)
                {
                    peer.Send(Protocol.Envelope("ping", new JsonObject { ["t"] = clock.Elapsed.TotalMilliseconds }));
                }
                Changed();
            }

            if (role == SessionRole.Client && state is SessionState.Live or SessionState.Synchronizing &&
                lastFrameAt >= 0 && now - lastFrameAt > FrameTimeout.TotalSeconds)
            {
                Halt("Keine Daten vom Host seit mehr als 3 Sekunden");
            }
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // audit
    // -----------------------------------------------------------------------------------------------------------------

    private JsonObject AuditMessage(int id) => new()
    {
        ["t"] = "audit",
        ["id"] = id,
        ["vars"] = new JsonArray(SharedDeckConfig.Expand(config.AuditAVars.Concat(config.AVars))
            .Select(a => (JsonNode)new JsonObject { ["n"] = a.Name, ["u"] = a.Unit, ["i"] = a.Index }).ToArray()),
    };

    private void CompleteAuditIfReady()
    {
        if (pendingAuditLocal is null || pendingAuditRemote is null)
        {
            return;
        }
        var vars = SharedDeckConfig.Expand(config.AuditAVars.Concat(config.AVars)).ToList();
        var differences = new List<AuditDifference>();
        for (var i = 0; i < vars.Count; i++)
        {
            var host = i < pendingAuditRemote.Count ? pendingAuditRemote[i]?.GetValue<double>() : null;
            var local = i < pendingAuditLocal.Count ? pendingAuditLocal[i]?.GetValue<double>() : null;
            var tolerance = Math.Max(vars[i].Tolerance, 1e-6 * Math.Abs(host ?? 0));
            if (host is null || local is null || Math.Abs(host.Value - local.Value) > tolerance)
            {
                differences.Add(new AuditDifference($"{vars[i].Name}:{vars[i].Index} ({vars[i].Unit})", host, local));
            }
        }
        Log($"A:Var-Audit: {differences.Count} von {vars.Count} Variablen weichen ab");
        var handler = AuditCompleted;
        Task.Run(() => handler?.Invoke(differences));
    }

    // -----------------------------------------------------------------------------------------------------------------

    private void SetState(SessionState newState, string why)
    {
        state = newState;
        reason = why;
        Log(why);
        if (bridge.IsConnected)
        {
            SendModeToBridge();
        }
        Changed();
    }

    private void Log(string message)
    {
        var handler = LogMessage;
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        Task.Run(() => handler?.Invoke(line));
    }

    private void Changed()
    {
        var handler = StatusChanged;
        Task.Run(() => handler?.Invoke());
    }

    public void Dispose()
    {
        watchdog.Dispose();
        peer?.Dispose();
    }
}
