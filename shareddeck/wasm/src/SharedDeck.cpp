// SharedDeck - shared cockpit for MSFS
// SPDX-License-Identifier: GPL-3.0
//
// Standalone WASM module that does all simulator I/O for SharedDeck:
//  - host:   discovers all L:vars, sends changed values (plus configured A:vars and the rigid body state)
//            as numbered frames and executes the inputs of the client
//  - client: applies the frames, keeps the aircraft frozen in place, detects the pilot's inputs
//            (L:var writes and key events) and verifies that the local state equals the host state
//
// The module talks to the SharedDeck bridge (JS, inside the aircraft) via CommBus, the bridge talks
// to the SharedDeck app via WebSocket and the app connects host and client over the network.

#include "SharedDeck.h"

#include <MSFS/MSFS_CommBus.h>

#include <cmath>
#include <cstdio>
#include <cstring>

#include "KeyEventNames.generated.h"

using json = nlohmann::json;

namespace sd {

namespace {

constexpr const char* TO_JS_EVENT = "SHAREDDECK_FROM_WASM";
constexpr const char* FROM_JS_EVENT = "SHAREDDECK_TO_WASM";
constexpr const char* ROLE_VARIABLE = "SHAREDDECK_ROLE";

constexpr size_t MAX_ENTRIES_PER_MESSAGE = 3000;
constexpr uint32_t HASH_EVERY_FRAMES = 30;
constexpr double PENDING_INPUT_TIMEOUT = 2.0;
constexpr double CONTESTED_WINDOW = 2.0;
constexpr uint32_t CONTESTED_CHANGES = 30;
constexpr uint32_t MISMATCH_STREAK_LIMIT = 3;
constexpr double MAX_EXTRAPOLATION = 0.25;
constexpr double KEYFRAME_RETRY = 3.0;
constexpr double OWN_KEY_ECHO_WINDOW = 1.0;
constexpr uint32_t CONTESTED_KEY_EVENTS = 60;
constexpr double EARTH_RADIUS_FEET = 20925646.0;
constexpr double DEG_PER_RAD = 57.29577951308232;

enum DataDefinition : SIMCONNECT_DATA_DEFINITION_ID { DEF_PHYSICS = 1, DEF_AVAR_BASE = 100 };

constexpr double LEVER_PICKUP_TOLERANCE = 0.04;
constexpr double LEVER_HOST_MOVE_THRESHOLD = 0.005;
constexpr double LEVER_CLIENT_GRACE = 0.2;
constexpr double TIME_SYNC_TOLERANCE = 90;
constexpr double TIME_SYNC_INTERVAL = 10;

/// Continuous axis events: only the latest value per frame is sent and they are never "contested".
bool isAxisEvent(const std::string& name) {
  return name.rfind("AXIS_", 0) == 0 || name.find("_AXIS_") != std::string::npos;
}
enum SystemEvent : SIMCONNECT_CLIENT_EVENT_ID { EVT_FRAME = 1, EVT_PAUSE = 2 };

SharedDeck* instance = nullptr;

bool sameBits(double a, double b) {
  return std::memcmp(&a, &b, sizeof(double)) == 0;
}

std::string formatDouble(double value) {
  char buffer[32];
  std::snprintf(buffer, sizeof(buffer), "%.17g", value);
  return buffer;
}

void replaceAll(std::string& text, const std::string& from, const std::string& to) {
  size_t pos = 0;
  while ((pos = text.find(from, pos)) != std::string::npos) {
    text.replace(pos, from.size(), to);
    pos += to.size();
  }
}

/// Glob match supporting '*' anywhere in the pattern.
bool globMatch(const char* pattern, const char* text) {
  while (*pattern) {
    if (*pattern == '*') {
      pattern++;
      if (!*pattern) {
        return true;
      }
      for (; *text; text++) {
        if (globMatch(pattern, text)) {
          return true;
        }
      }
      return false;
    }
    if (*pattern != *text) {
      return false;
    }
    pattern++;
    text++;
  }
  return *text == 0;
}

std::string keyEventName(ID32 event) {
  for (const auto& entry : kKeyEventNames) {
    if (entry.id == event) {
      return entry.name;
    }
  }
  return "#" + std::to_string(event);
}

bool keyEventId(const std::string& name, ID32& event) {
  if (!name.empty() && name[0] == '#') {
    event = static_cast<ID32>(std::strtoul(name.c_str() + 1, nullptr, 10));
    return true;
  }
  for (const auto& entry : kKeyEventNames) {
    if (name == entry.name) {
      event = entry.id;
      return true;
    }
  }
  return false;
}

void CALLBACK dispatch(SIMCONNECT_RECV* data, DWORD, void*) {
  if (!instance) {
    return;
  }
  switch (data->dwID) {
    case SIMCONNECT_RECV_ID_EVENT_FRAME: {
      const auto* frame = static_cast<SIMCONNECT_RECV_EVENT_FRAME*>(data);
      const double deltaTime = frame->fFrameRate > 0 ? 1.0 / frame->fFrameRate : 0;
      instance->onFrame(deltaTime);
      break;
    }
    case SIMCONNECT_RECV_ID_EVENT: {
      const auto* event = static_cast<SIMCONNECT_RECV_EVENT*>(data);
      if (event->uEventID == EVT_PAUSE) {
        instance->onPause(event->dwData);
      }
      break;
    }
    default:
      break;
  }
}

void keyHandler(ID32 event, UINT32 d0, UINT32 d1, UINT32 d2, UINT32 d3, UINT32 d4, PVOID) {
  if (instance) {
    instance->onKeyEvent(event, d0, d1, d2, d3, d4);
  }
}

void commBusHandler(const char* buffer, unsigned int size, void*) {
  if (instance && buffer && size > 0) {
    instance->onMessage(std::string(buffer, strnlen(buffer, size)));
  }
}

}  // namespace

// ---------------------------------------------------------------------------------------------------------------------
// lifecycle
// ---------------------------------------------------------------------------------------------------------------------

void SharedDeck::init() {
  instance = this;

  roleVariableId = register_named_variable(ROLE_VARIABLE);
  set_named_variable_value(roleVariableId, static_cast<double>(Role::Off));

  if (SUCCEEDED(SimConnect_Open(&simConnect, "SharedDeck", nullptr, 0, 0, 0))) {
    SimConnect_AddToDataDefinition(simConnect, DEF_PHYSICS, "PLANE LATITUDE", "degrees");
    SimConnect_AddToDataDefinition(simConnect, DEF_PHYSICS, "PLANE LONGITUDE", "degrees");
    SimConnect_AddToDataDefinition(simConnect, DEF_PHYSICS, "PLANE ALTITUDE", "feet");
    SimConnect_AddToDataDefinition(simConnect, DEF_PHYSICS, "PLANE PITCH DEGREES", "degrees");
    SimConnect_AddToDataDefinition(simConnect, DEF_PHYSICS, "PLANE BANK DEGREES", "degrees");
    SimConnect_AddToDataDefinition(simConnect, DEF_PHYSICS, "PLANE HEADING DEGREES TRUE", "degrees");
    SimConnect_AddToDataDefinition(simConnect, DEF_PHYSICS, "VELOCITY BODY X", "feet per second");
    SimConnect_AddToDataDefinition(simConnect, DEF_PHYSICS, "VELOCITY BODY Y", "feet per second");
    SimConnect_AddToDataDefinition(simConnect, DEF_PHYSICS, "VELOCITY BODY Z", "feet per second");
    SimConnect_AddToDataDefinition(simConnect, DEF_PHYSICS, "ROTATION VELOCITY BODY X", "radians per second");
    SimConnect_AddToDataDefinition(simConnect, DEF_PHYSICS, "ROTATION VELOCITY BODY Y", "radians per second");
    SimConnect_AddToDataDefinition(simConnect, DEF_PHYSICS, "ROTATION VELOCITY BODY Z", "radians per second");
    SimConnect_SubscribeToSystemEvent(simConnect, EVT_FRAME, "Frame");
    SimConnect_SubscribeToSystemEvent(simConnect, EVT_PAUSE, "Pause_EX1");
    SimConnect_CallDispatch(simConnect, dispatch, nullptr);
  } else {
    std::printf("SharedDeck: SimConnect_Open failed\n");
  }

  register_key_event_handler_EX1(keyHandler, nullptr);
  fsCommBusRegister(FROM_JS_EVENT, commBusHandler);

  send(json{{"t", "ready"}, {"v", 1}}.dump());
}

void SharedDeck::deinit() {
  if (active) {
    stopSession();
  }
  fsCommBusUnregister(FROM_JS_EVENT, commBusHandler);
  unregister_key_event_handler_EX1(keyHandler, nullptr);
  if (simConnect) {
    SimConnect_Close(simConnect);
    simConnect = 0;
  }
  instance = nullptr;
}

// ---------------------------------------------------------------------------------------------------------------------
// messaging
// ---------------------------------------------------------------------------------------------------------------------

void SharedDeck::send(const std::string& message) {
  bytesSent += message.size();
  fsCommBusCall(TO_JS_EVENT, message.c_str(), static_cast<unsigned int>(message.size() + 1), FsCommBusBroadcast_JS);
}

void SharedDeck::log(const std::string& message) {
  std::printf("SharedDeck: %s\n", message.c_str());
  send(json{{"t", "log"}, {"msg", message}}.dump());
}

void SharedDeck::onMessage(const std::string& text) {
  const json message = json::parse(text, nullptr, false);
  if (message.is_discarded() || !message.is_object() || !message.contains("t")) {
    log("ignored malformed message");
    return;
  }
  const std::string type = message["t"].get<std::string>();

  if (type == "ping") {
    send(json{{"t", "ready"}, {"v", 1}, {"role", static_cast<int>(role)}, {"active", active}}.dump());
  } else if (type == "config") {
    applyConfig(message);
  } else if (type == "start") {
    startSession();
  } else if (type == "stop") {
    stopSession();
  } else if (type == "keyframe") {
    if (role == Role::Host && active) {
      hostScanNames(true);
      hostSendAllNames();
      hostEmitFrame(true);
    }
  } else if (type == "names") {
    if (role == Role::Client) {
      clientApplyNames(message);
    }
  } else if (type == "frame") {
    if (role == Role::Client && active) {
      clientApplyFrame(message);
    }
  } else if (type == "input") {
    if (role == Role::Host && active) {
      hostApplyInput(message);
    }
  } else if (type == "pause") {
    const bool on = message.value("on", false);
    if (on != simPaused) {
      triggerKey(KEY_PAUSE_SET, on ? 1 : 0);
    }
  } else if (type == "audit") {
    runAudit(message);
  } else {
    log("unknown message type " + type);
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// configuration
// ---------------------------------------------------------------------------------------------------------------------

void SharedDeck::applyConfig(const json& config) {
  if (active) {
    stopSession();
  }

  role = static_cast<Role>(config.value("role", 0));
  strict = config.value("strict", true);
  rateHz = std::max(1.0, config.value("rateHz", 30.0));

  auto readList = [&config](const char* key) {
    std::vector<std::string> list;
    if (config.contains(key) && config[key].is_array()) {
      for (const auto& item : config[key]) {
        list.push_back(item.get<std::string>());
      }
    }
    return list;
  };
  lvarDeny = readList("lvarDeny");
  inputDeny = readList("inputDeny");
  keyDeny = readList("keyDeny");
  localAllow = readList("localAllow");

  lvarActions.clear();
  if (config.contains("lvarActions") && config["lvarActions"].is_array()) {
    for (const auto& item : config["lvarActions"]) {
      lvarActions[item.value("lvar", "")] = item.value("code", "");
    }
  }

  avars.clear();
  if (config.contains("avars") && config["avars"].is_array()) {
    for (const auto& item : config["avars"]) {
      AVar avar;
      avar.name = item.value("n", "");
      avar.unit = item.value("u", "number");
      avar.index = item.value("i", 0);
      avar.setter = item.value("set", "");
      avar.tolerance = item.value("tol", 0.0);
      avar.varEnum = get_aircraft_var_enum(avar.name.c_str());
      avar.unitEnum = get_units_enum(avar.unit.c_str());
      avar.declaredLocal = matchesAny(localAllow, "A:" + avar.name);
      if (avar.varEnum < 0) {
        log("unknown A:var " + avar.name);
        continue;
      }
      if (avar.setter == "@sc" && simConnect) {
        // written like FBW does it: through a SimConnect data definition
        avar.definition = DEF_AVAR_BASE + static_cast<SIMCONNECT_DATA_DEFINITION_ID>(avars.size());
        const std::string simVar = avar.index > 0 ? avar.name + ":" + std::to_string(avar.index) : avar.name;
        SimConnect_ClearDataDefinition(simConnect, avar.definition);
        SimConnect_AddToDataDefinition(simConnect, avar.definition, simVar.c_str(), avar.unit.c_str());
      }
      avars.push_back(avar);
    }
  }

  lvars.clear();
  lvarIndexByName.clear();
  nextScanId = 0;

  log("config applied: role " + std::to_string(static_cast<int>(role)) + ", " + std::to_string(avars.size()) + " A:vars");
}

bool SharedDeck::matchesAny(const std::vector<std::string>& patterns, const std::string& name) const {
  for (const auto& pattern : patterns) {
    if (globMatch(pattern.c_str(), name.c_str())) {
      return true;
    }
  }
  return false;
}

// ---------------------------------------------------------------------------------------------------------------------
// session
// ---------------------------------------------------------------------------------------------------------------------

void SharedDeck::startSession() {
  if (role == Role::Off) {
    log("cannot start without role");
    return;
  }

  active = true;
  time = 0;
  lastEmitTime = -1;
  lastSecondTick = 0;
  frameSeq = 0;
  framesSinceHash = 0;
  lastInputSeqApplied = 0;
  lastFrameSeq = 0;
  awaitingKeyframe = false;
  lastKeyframeRequest = -1;
  inputSeq = 0;
  hostAckedInputSeq = 0;
  transportMismatchStreak = 0;
  havePhysics = false;
  pendingLvarInputs.clear();
  pendingKeyInputs.clear();
  pendingAxisIndex.clear();

  setRoleVariable(role);
  lastTimeSent = -10;
  lastTimeCorrection = -100;
  for (int i = 0; i < 2; i++) {
    leverInputId[i] = register_named_variable(("A32NX_THROTTLE_MAPPING_INPUT:" + std::to_string(i + 1)).c_str());
    leverApplied[i] = get_named_variable_value(leverInputId[i]);
    leverOwner[i] = LeverOwner::Host;
    clientLeverOwner[i] = 0;
  }

  if (role == Role::Host) {
    hostScanNames(true);
    hostSendAllNames();
    hostEmitFrame(true);
  } else {
    setFreeze(true);
    clientRequestKeyframe("session start");
  }

  log("session started");
}

void SharedDeck::runLvarAction(LVar& lvar) {
  expectOwnKeyEvents(lvar.action);
  suppressKeyCapture = true;
  execute_calculator_code(lvar.action.c_str(), nullptr, nullptr, nullptr);
  suppressKeyCapture = false;
  lvar.actionToggled = !lvar.actionToggled;
}

void SharedDeck::stopSession() {
  if (role == Role::Client) {
    setFreeze(false);
    // FBW resumes with its own memory of these variables: undo our toggles so it toggles from the right state
    for (auto& lvar : lvars) {
      if (lvar.actionToggled) {
        runLvarAction(lvar);
      }
    }
  }
  active = false;
  setRoleVariable(Role::Off);
  log("session stopped");
}

void SharedDeck::setRoleVariable(Role value) {
  set_named_variable_value(roleVariableId, static_cast<double>(value));
}

void SharedDeck::setFreeze(bool frozen) {
  triggerKey(KEY_FREEZE_LATITUDE_LONGITUDE_SET, frozen ? 1 : 0);
  triggerKey(KEY_FREEZE_ALTITUDE_SET, frozen ? 1 : 0);
  triggerKey(KEY_FREEZE_ATTITUDE_SET, frozen ? 1 : 0);
}

void SharedDeck::expectOwnKeyEvents(const std::string& code) {
  // every (>K:NAME) or (>K:2:NAME) in the code produces one key event that must not be forwarded
  size_t pos = 0;
  while ((pos = code.find("(>K:", pos)) != std::string::npos) {
    pos += 4;
    const size_t end = code.find(')', pos);
    if (end == std::string::npos) {
      break;
    }
    std::string name = code.substr(pos, end - pos);
    const size_t colon = name.find(':');
    if (colon != std::string::npos) {
      name = name.substr(colon + 1);
    }
    OwnKeyExpectation& expectation = expectedOwnKeys[name];
    expectation.count = (time <= expectation.until ? expectation.count : 0) + 1;
    expectation.until = time + OWN_KEY_ECHO_WINDOW;
  }
}

void SharedDeck::triggerKey(ID32 event, UINT32 d0, UINT32 d1, UINT32 d2, UINT32 d3, UINT32 d4) {
  OwnKeyExpectation& expectation = expectedOwnKeys[keyEventName(event)];
  expectation.count = (time <= expectation.until ? expectation.count : 0) + 1;
  expectation.until = time + OWN_KEY_ECHO_WINDOW;
  suppressKeyCapture = true;
  trigger_key_event_EX1(event, d0, d1, d2, d3, d4);
  suppressKeyCapture = false;
}

void SharedDeck::onPause(DWORD pauseState) {
  // PAUSE_STATE_FLAG_ACTIVE_PAUSE (4) keeps the systems running, so only treat real pauses as paused
  simPaused = pauseState != 0 && pauseState != 4;
}

void SharedDeck::onFrame(double deltaTime) {
  time += deltaTime;
  if (!active) {
    return;
  }

  const bool secondTick = time - lastSecondTick >= 1.0;
  if (secondTick) {
    lastSecondTick = time;
  }

  if (role == Role::Host) {
    if (secondTick) {
      hostScanNames(false);
    }
    hostTrackLevers();
    if (lastEmitTime < 0 || time - lastEmitTime >= 1.0 / rateHz) {
      lastEmitTime = time;
      hostEmitFrame(false);
    }
    if (secondTick) {
      send(json{{"t", "stats"},
                {"role", "host"},
                {"lvars", lvars.size()},
                {"avars", avars.size()},
                {"seq", frameSeq},
                {"bytes", bytesSent},
                {"inputSeq", lastInputSeqApplied}}
               .dump());
    }
  } else if (role == Role::Client) {
    clientDetectInputs();
    clientFlushInputs();
    clientApplyPhysics(deltaTime);
    if (secondTick) {
      setFreeze(true);
      clientVerify();
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// host
// ---------------------------------------------------------------------------------------------------------------------

void SharedDeck::hostScanNames(bool force) {
  json newNames = json::array();
  const auto base = static_cast<uint32_t>(lvars.size());

  for (;; nextScanId++) {
    const char* name = get_name_of_named_variable(nextScanId);
    if (!name) {
      break;
    }
    const std::string varName = name;
    if (varName == ROLE_VARIABLE || matchesAny(lvarDeny, varName) || lvarIndexByName.count(varName)) {
      continue;
    }
    LVar lvar;
    lvar.name = varName;
    lvar.localId = nextScanId;
    lvar.value = get_named_variable_value(nextScanId);
    lvarIndexByName[varName] = static_cast<uint32_t>(lvars.size());
    lvars.push_back(lvar);
    newNames.push_back(varName);
  }

  // new variables are announced before any frame references them; their values follow with the next frame
  if (!newNames.empty() && !force) {
    send(json{{"t", "names"}, {"base", base}, {"names", newNames}}.dump());
    for (uint32_t i = base; i < lvars.size(); i++) {
      // force the value into the next delta
      lvars[i].value = std::nan("");
    }
  }
}

void SharedDeck::hostSendAllNames() {
  for (size_t base = 0; base < lvars.size() || base == 0; base += MAX_ENTRIES_PER_MESSAGE) {
    json names = json::array();
    for (size_t i = base; i < std::min(lvars.size(), base + MAX_ENTRIES_PER_MESSAGE); i++) {
      names.push_back(lvars[i].name);
    }
    send(json{{"t", "names"}, {"base", base}, {"names", names}, {"reset", base == 0}}.dump());
    if (lvars.empty()) {
      break;
    }
  }
}

void SharedDeck::readPhysics(PhysicsState& state) const {
  static const ENUM degrees = get_units_enum("degrees");
  static const ENUM feet = get_units_enum("feet");
  static const ENUM fps = get_units_enum("feet per second");
  static const ENUM rps = get_units_enum("radians per second");
  static const ENUM lat = get_aircraft_var_enum("PLANE LATITUDE");
  static const ENUM lon = get_aircraft_var_enum("PLANE LONGITUDE");
  static const ENUM alt = get_aircraft_var_enum("PLANE ALTITUDE");
  static const ENUM pitch = get_aircraft_var_enum("PLANE PITCH DEGREES");
  static const ENUM bank = get_aircraft_var_enum("PLANE BANK DEGREES");
  static const ENUM heading = get_aircraft_var_enum("PLANE HEADING DEGREES TRUE");
  static const ENUM vbx = get_aircraft_var_enum("VELOCITY BODY X");
  static const ENUM vby = get_aircraft_var_enum("VELOCITY BODY Y");
  static const ENUM vbz = get_aircraft_var_enum("VELOCITY BODY Z");
  static const ENUM rvx = get_aircraft_var_enum("ROTATION VELOCITY BODY X");
  static const ENUM rvy = get_aircraft_var_enum("ROTATION VELOCITY BODY Y");
  static const ENUM rvz = get_aircraft_var_enum("ROTATION VELOCITY BODY Z");

  state.latitude = aircraft_varget(lat, degrees, 0);
  state.longitude = aircraft_varget(lon, degrees, 0);
  state.altitude = aircraft_varget(alt, feet, 0);
  state.pitch = aircraft_varget(pitch, degrees, 0);
  state.bank = aircraft_varget(bank, degrees, 0);
  state.heading = aircraft_varget(heading, degrees, 0);
  state.velocityBodyX = aircraft_varget(vbx, fps, 0);
  state.velocityBodyY = aircraft_varget(vby, fps, 0);
  state.velocityBodyZ = aircraft_varget(vbz, fps, 0);
  state.rotationVelocityBodyX = aircraft_varget(rvx, rps, 0);
  state.rotationVelocityBodyY = aircraft_varget(rvy, rps, 0);
  state.rotationVelocityBodyZ = aircraft_varget(rvz, rps, 0);
}

void SharedDeck::hostEmitFrame(bool keyframe) {
  frameSeq++;

  // collect changed L:vars, split into several messages if needed
  std::vector<ByteWriter> parts(1);
  size_t entriesInPart = 0;
  for (uint32_t i = 0; i < lvars.size(); i++) {
    LVar& lvar = lvars[i];
    const double current = get_named_variable_value(lvar.localId);
    if (!keyframe && sameBits(current, lvar.value)) {
      continue;
    }
    lvar.value = current;
    if (entriesInPart == MAX_ENTRIES_PER_MESSAGE) {
      parts.emplace_back();
      entriesInPart = 0;
    }
    parts.back().varint(i);
    parts.back().f64(current);
    entriesInPart++;
  }

  ByteWriter avarWriter;
  for (uint32_t i = 0; i < avars.size(); i++) {
    AVar& avar = avars[i];
    const double current = readAVar(avar);
    if (!keyframe && avar.hasValue && sameBits(current, avar.value)) {
      continue;
    }
    avar.value = current;
    avar.hasValue = true;
    avarWriter.varint(i);
    avarWriter.f64(current);
  }

  PhysicsState state{};
  readPhysics(state);
  static const ENUM fps = get_units_enum("feet per second");
  static const ENUM vwx = get_aircraft_var_enum("VELOCITY WORLD X");
  static const ENUM vwy = get_aircraft_var_enum("VELOCITY WORLD Y");
  static const ENUM vwz = get_aircraft_var_enum("VELOCITY WORLD Z");
  ByteWriter physicsWriter;
  for (const double value : {state.latitude, state.longitude, state.altitude, state.pitch, state.bank, state.heading,
                             state.velocityBodyX, state.velocityBodyY, state.velocityBodyZ, state.rotationVelocityBodyX,
                             state.rotationVelocityBodyY, state.rotationVelocityBodyZ, aircraft_varget(vwx, fps, 0),
                             aircraft_varget(vwy, fps, 0), aircraft_varget(vwz, fps, 0)}) {
    physicsWriter.f64(value);
  }

  framesSinceHash++;
  const bool withHash = keyframe || framesSinceHash >= HASH_EVERY_FRAMES;
  if (withHash) {
    framesSinceHash = 0;
  }

  for (size_t part = 0; part < parts.size(); part++) {
    const bool last = part + 1 == parts.size();
    json message = {{"t", "frame"},
                    {"seq", frameSeq},
                    {"part", part},
                    {"parts", parts.size()},
                    {"key", keyframe},
                    {"count", lvars.size()},
                    {"l", base64Encode(parts[part].bytes)}};
    if (last) {
      message["a"] = base64Encode(avarWriter.bytes);
      message["p"] = base64Encode(physicsWriter.bytes);
      message["paused"] = simPaused;
      message["ack"] = lastInputSeqApplied;
      message["thr"] = json::array({static_cast<int>(leverOwner[0]), static_cast<int>(leverOwner[1])});
      if (time - lastTimeSent >= 1.0) {
        lastTimeSent = time;
        message["z"] = json::array({readCalculator("(E:ZULU YEAR, number)"), readCalculator("(E:ZULU DAY OF YEAR, number)"),
                                    readCalculator("(E:ZULU TIME, seconds)")});
      }
      if (withHash) {
        message["h"] = hostComputeHash();
      }
    }
    send(message.dump());
  }
}

std::string SharedDeck::hostComputeHash() const {
  StateHash hash;
  for (uint32_t i = 0; i < lvars.size(); i++) {
    hash.add(i, lvars[i].value);
  }
  for (uint32_t i = 0; i < avars.size(); i++) {
    hash.add(0x80000000u | i, avars[i].value);
  }
  return hash.hex();
}

void SharedDeck::hostApplyInput(const json& message) {
  if (message.contains("l")) {
    for (const auto& entry : message["l"]) {
      const std::string name = entry[0].get<std::string>();
      const double value = entry[1].get<double>();
      if (matchesAny(inputDeny, name)) {
        continue;
      }
      ID id;
      const auto it = lvarIndexByName.find(name);
      if (it != lvarIndexByName.end()) {
        id = lvars[it->second].localId;
      } else {
        id = register_named_variable(name.c_str());
      }
      set_named_variable_value(id, value);
    }
  }
  if (message.contains("k")) {
    for (const auto& entry : message["k"]) {
      ID32 event;
      const std::string name = entry[0].get<std::string>();
      if (matchesAny(keyDeny, name) || !keyEventId(name, event)) {
        continue;
      }
      if (hostApplyThrottleInput(name, entry[1].get<uint32_t>())) {
        continue;
      }
      triggerKey(event, entry[1].get<uint32_t>(), entry[2].get<uint32_t>(), entry[3].get<uint32_t>(), entry[4].get<uint32_t>(),
                 entry[5].get<uint32_t>());
    }
  }
  lastInputSeqApplied = std::max(lastInputSeqApplied, message.value("seq", 0u));
}

void SharedDeck::hostTrackLevers() {
  // FBW keeps the raw input of each thrust lever axis; if it moves without us, the captain moved his lever
  for (int i = 0; i < 2; i++) {
    // FBW may process an injected co-pilot input only in the next frame
    if (time - leverClientInputTime[i] < LEVER_CLIENT_GRACE) {
      continue;
    }
    const double input = get_named_variable_value(leverInputId[i]);
    if (std::fabs(input - leverApplied[i]) > LEVER_HOST_MOVE_THRESHOLD) {
      leverApplied[i] = input;
      leverOwner[i] = LeverOwner::Host;
    }
  }
}

bool SharedDeck::hostApplyThrottleInput(const std::string& name, UINT32 d0) {
  bool levers[2];
  if (name == "THROTTLE_SET" || name == "THROTTLE_AXIS_SET_EX1" || name == "AXIS_THROTTLE_SET") {
    levers[0] = levers[1] = true;
  } else if (name == "THROTTLE1_SET" || name == "THROTTLE1_AXIS_SET_EX1" || name == "AXIS_THROTTLE1_SET") {
    levers[0] = true;
    levers[1] = false;
  } else if (name == "THROTTLE2_SET" || name == "THROTTLE2_AXIS_SET_EX1" || name == "AXIS_THROTTLE2_SET") {
    levers[0] = false;
    levers[1] = true;
  } else {
    return false;
  }

  // soft takeover: the co-pilot's lever only takes over once it reaches the current lever position
  const double raw = static_cast<int32_t>(d0) / 16384.0;
  static const char* perLeverEvent[2] = {"THROTTLE1_AXIS_SET_EX1", "THROTTLE2_AXIS_SET_EX1"};
  for (int i = 0; i < 2; i++) {
    if (!levers[i]) {
      continue;
    }
    if (leverOwner[i] != LeverOwner::Client) {
      const double current = get_named_variable_value(leverInputId[i]);
      if (std::fabs(raw - current) > LEVER_PICKUP_TOLERANCE) {
        if (time - lastPickupHint > 5) {
          lastPickupHint = time;
          log("Schubhebel " + std::to_string(i + 1) + ": Hebel des Co-Piloten noch nicht auf aktueller Stellung");
        }
        continue;
      }
      leverOwner[i] = LeverOwner::Client;
    }
    leverApplied[i] = raw;
    leverClientInputTime[i] = time;
    ID32 event;
    if (keyEventId(perLeverEvent[i], event)) {
      triggerKey(event, d0);
    }
  }
  return true;
}

// ---------------------------------------------------------------------------------------------------------------------
// client
// ---------------------------------------------------------------------------------------------------------------------

double SharedDeck::readCalculator(const char* code) {
  FLOAT64 value = 0;
  execute_calculator_code(code, &value, nullptr, nullptr);
  return value;
}

void SharedDeck::clientSyncTime(const json& zulu) {
  if (!zulu.is_array() || zulu.size() < 3 || time - lastTimeCorrection < TIME_SYNC_INTERVAL) {
    return;
  }
  const double year = zulu[0].get<double>();
  const double day = zulu[1].get<double>();
  const double seconds = zulu[2].get<double>();
  if (readCalculator("(E:ZULU YEAR, number)") == year && readCalculator("(E:ZULU DAY OF YEAR, number)") == day &&
      std::fabs(readCalculator("(E:ZULU TIME, seconds)") - seconds) < TIME_SYNC_TOLERANCE) {
    return;
  }
  lastTimeCorrection = time;
  ID32 event;
  if (keyEventId("ZULU_YEAR_SET", event)) {
    triggerKey(event, static_cast<UINT32>(year));
  }
  if (keyEventId("ZULU_DAY_SET", event)) {
    triggerKey(event, static_cast<UINT32>(day));
  }
  if (keyEventId("ZULU_HOURS_SET", event)) {
    triggerKey(event, static_cast<UINT32>(seconds / 3600));
  }
  if (keyEventId("ZULU_MINUTES_SET", event)) {
    triggerKey(event, static_cast<UINT32>(std::fmod(seconds, 3600) / 60));
  }
  log("Simulatorzeit an den Host angeglichen");
}

void SharedDeck::clientRequestKeyframe(const std::string& reason) {
  // one request is enough while the keyframe is on its way, repeat only if it got lost
  if (awaitingKeyframe && lastKeyframeRequest >= 0 && time - lastKeyframeRequest < KEYFRAME_RETRY) {
    return;
  }
  awaitingKeyframe = true;
  lastKeyframeRequest = time;
  send(json{{"t", "needKeyframe"}, {"reason", reason}}.dump());
}

void SharedDeck::clientApplyNames(const json& message) {
  const auto base = message.value("base", 0u);
  if (message.value("reset", false)) {
    lvars.clear();
    lvarIndexByName.clear();
  }
  if (base != lvars.size()) {
    clientRequestKeyframe("name table gap");
    return;
  }
  for (const auto& item : message["names"]) {
    LVar lvar;
    lvar.name = item.get<std::string>();
    lvar.localId = register_named_variable(lvar.name.c_str());
    lvar.value = get_named_variable_value(lvar.localId);
    lvar.lastLocal = lvar.value;
    lvar.declaredLocal = matchesAny(localAllow, lvar.name);
    lvar.inputDenied = matchesAny(inputDeny, lvar.name);
    const auto action = lvarActions.find(lvar.name);
    if (action != lvarActions.end()) {
      // the local value matches the local simulator state, so changes are counted from here
      lvar.action = action->second;
      lvar.actionLast = lvar.value;
    }
    lvarIndexByName[lvar.name] = static_cast<uint32_t>(lvars.size());
    lvars.push_back(lvar);
  }
}

void SharedDeck::clientApplyFrame(const json& message) {
  const auto seq = message.value("seq", 0u);
  const auto part = message.value("part", 0u);
  const auto parts = message.value("parts", 1u);
  const bool keyframe = message.value("key", false);

  if (part == 0) {
    if (keyframe) {
      awaitingKeyframe = false;
    } else if (awaitingKeyframe) {
      return;
    } else if (seq != lastFrameSeq + 1) {
      clientRequestKeyframe("frame gap " + std::to_string(lastFrameSeq) + " -> " + std::to_string(seq));
      return;
    }
  } else if (awaitingKeyframe) {
    return;
  }

  if (message.value("count", 0u) > lvars.size()) {
    clientRequestKeyframe("unknown variables");
    return;
  }

  std::vector<uint8_t> bytes;
  if (base64Decode(message.value("l", ""), bytes)) {
    ByteReader reader(bytes);
    uint32_t index;
    double value;
    while (!reader.atEnd() && reader.varint(index) && reader.f64(value)) {
      if (index >= lvars.size()) {
        clientRequestKeyframe("variable index out of range");
        return;
      }
      LVar& lvar = lvars[index];
      lvar.value = value;
      // pending pilot inputs and declared local variables keep their local value
      if (lvar.pendingInputSeq == 0 && !lvar.declaredLocal) {
        set_named_variable_value(lvar.localId, value);
        lvar.lastLocal = value;
      }
      if (!lvar.action.empty() && !sameBits(value, lvar.actionLast)) {
        lvar.actionLast = value;
        runLvarAction(lvar);
      }
    }
  }

  if (part + 1 < parts) {
    return;
  }

  // the last part carries everything except the L:vars
  lastFrameSeq = seq;
  hostAckedInputSeq = std::max(hostAckedInputSeq, message.value("ack", 0u));

  if (base64Decode(message.value("a", ""), bytes)) {
    ByteReader reader(bytes);
    uint32_t index;
    double value;
    while (!reader.atEnd() && reader.varint(index) && reader.f64(value)) {
      if (index < avars.size()) {
        AVar& avar = avars[index];
        const bool changed = !avar.hasValue || !sameBits(avar.value, value);
        avar.value = value;
        avar.hasValue = true;
        if (changed && !avar.declaredLocal) {
          writeAVar(avar, value);
        }
      }
    }
  }

  if (base64Decode(message.value("p", ""), bytes) && bytes.size() >= 15 * 8) {
    ByteReader reader(bytes);
    double values[15];
    for (double& value : values) {
      reader.f64(value);
    }
    physics = {values[0], values[1], values[2], values[3], values[4], values[5],
               values[6], values[7], values[8], values[9], values[10], values[11]};
    worldVelocityEast = values[12];
    worldVelocityUp = values[13];
    worldVelocityNorth = values[14];
    physicsAge = 0;
    havePhysics = true;
  }

  if (message.contains("thr") && message["thr"].is_array() && message["thr"].size() == 2) {
    clientLeverOwner[0] = message["thr"][0].get<int>();
    clientLeverOwner[1] = message["thr"][1].get<int>();
  }
  if (message.contains("z")) {
    clientSyncTime(message["z"]);
  }

  const bool hostPaused = message.value("paused", false);
  if (hostPaused != simPaused) {
    triggerKey(KEY_PAUSE_SET, hostPaused ? 1 : 0);
  }

  if (message.contains("h")) {
    const std::string expected = message["h"].get<std::string>();
    const std::string actual = clientComputeMirrorHash();
    if (expected == actual) {
      transportMismatchStreak = 0;
    } else {
      transportMismatchStreak++;
      send(json{{"t", "verify"}, {"kind", "transport"}, {"seq", seq}, {"ok", false}, {"streak", transportMismatchStreak}}.dump());
      clientRequestKeyframe("state hash mismatch");
    }
  }
}

std::string SharedDeck::clientComputeMirrorHash() const {
  StateHash hash;
  for (uint32_t i = 0; i < lvars.size(); i++) {
    hash.add(i, lvars[i].value);
  }
  for (uint32_t i = 0; i < avars.size(); i++) {
    hash.add(0x80000000u | i, avars[i].value);
  }
  return hash.hex();
}

void SharedDeck::clientDetectInputs() {
  for (auto& lvar : lvars) {
    if (lvar.declaredLocal) {
      continue;
    }
    const double current = get_named_variable_value(lvar.localId);

    // the host has processed our input: from now on the host value applies again
    if (lvar.pendingInputSeq != 0 && (hostAckedInputSeq >= lvar.pendingInputSeq || time - lvar.pendingSince > PENDING_INPUT_TIMEOUT)) {
      lvar.pendingInputSeq = 0;
      if (!sameBits(current, lvar.value)) {
        set_named_variable_value(lvar.localId, lvar.value);
        lvar.lastLocal = lvar.value;
        continue;
      }
    }

    if (sameBits(current, lvar.lastLocal)) {
      continue;
    }
    lvar.lastLocal = current;

    if (time - lvar.windowStart > CONTESTED_WINDOW) {
      lvar.windowStart = time;
      lvar.windowChanges = 0;
    }
    lvar.windowChanges++;
    if (!lvar.contested && lvar.windowChanges > CONTESTED_CHANGES) {
      // something on this PC keeps writing this variable: it is no pilot input
      lvar.contested = true;
      lvar.pendingInputSeq = 0;
      send(json{{"t", "contested"}, {"name", lvar.name}}.dump());
    }
    if (lvar.contested || lvar.inputDenied) {
      continue;
    }

    pendingLvarInputs.emplace_back(lvar.name, current);
    lvar.pendingInputSeq = inputSeq + 1;
    lvar.pendingSince = time;
  }
}

void SharedDeck::onKeyEvent(ID32 event, UINT32 d0, UINT32 d1, UINT32 d2, UINT32 d3, UINT32 d4) {
  if (role != Role::Client || !active) {
    return;
  }
  if (suppressKeyCapture) {
    // delivered synchronously: the expected echo has arrived
    const auto own = expectedOwnKeys.find(keyEventName(event));
    if (own != expectedOwnKeys.end() && own->second.count > 0) {
      own->second.count--;
    }
    return;
  }

  // the F/O sidestick has its own variables in the FBW fork (same sign convention as the captain's)
  if (event == KEY_AXIS_ELEVATOR_SET || event == KEY_ELEVATOR_SET) {
    queueLvarInput("A32NX_SIDESTICK_FO_POSITION_Y", -static_cast<int32_t>(d0) / 16384.0);
    return;
  }
  if (event == KEY_AXIS_AILERONS_SET || event == KEY_AILERON_SET) {
    queueLvarInput("A32NX_SIDESTICK_FO_POSITION_X", -static_cast<int32_t>(d0) / 16384.0);
    return;
  }

  const std::string name = keyEventName(event);
  if (matchesAny(keyDeny, name)) {
    return;
  }

  // echo of a key event this module triggered itself (setters, freeze, pause), possibly delivered later
  const auto own = expectedOwnKeys.find(name);
  if (own != expectedOwnKeys.end() && own->second.count > 0 && time <= own->second.until) {
    own->second.count--;
    return;
  }

  // something on this PC fires this event continuously: it is no pilot input
  KeyActivity& activity = keyActivity[event];
  if (time - activity.windowStart > CONTESTED_WINDOW) {
    activity.windowStart = time;
    activity.count = 0;
  }
  activity.count++;
  if (activity.suppressed) {
    return;
  }
  if (activity.count > CONTESTED_KEY_EVENTS && !isAxisEvent(name)) {
    activity.suppressed = true;
    send(json{{"t", "contested"}, {"name", "K:" + name}}.dump());
    return;
  }

  // axis events arrive many times per frame: only the latest value of each axis is sent
  if (isAxisEvent(name)) {
    const auto it = pendingAxisIndex.find(event);
    if (it != pendingAxisIndex.end()) {
      pendingKeyInputs[it->second] = {event, d0, d1, d2, d3, d4};
      return;
    }
    pendingAxisIndex[event] = pendingKeyInputs.size();
  }
  pendingKeyInputs.push_back({event, d0, d1, d2, d3, d4});
}

void SharedDeck::queueLvarInput(const std::string& name, double value) {
  // continuous inputs (sidestick) bypass the change detection, so they are never classified as contested
  const ID id = register_named_variable(name.c_str());
  set_named_variable_value(id, value);
  const auto it = lvarIndexByName.find(name);
  if (it != lvarIndexByName.end()) {
    LVar& lvar = lvars[it->second];
    lvar.lastLocal = value;
    lvar.pendingInputSeq = inputSeq + 1;
    lvar.pendingSince = time;
  }
  for (auto& entry : pendingLvarInputs) {
    if (entry.first == name) {
      entry.second = value;
      return;
    }
  }
  pendingLvarInputs.emplace_back(name, value);
}

void SharedDeck::clientFlushInputs() {
  if (pendingLvarInputs.empty() && pendingKeyInputs.empty()) {
    return;
  }
  inputSeq++;

  json lvarInputs = json::array();
  for (const auto& [name, value] : pendingLvarInputs) {
    lvarInputs.push_back(json::array({name, value}));
  }
  json keyInputs = json::array();
  for (const auto& entry : pendingKeyInputs) {
    keyInputs.push_back(json::array({keyEventName(entry[0]), entry[1], entry[2], entry[3], entry[4], entry[5]}));
  }
  send(json{{"t", "input"}, {"seq", inputSeq}, {"l", lvarInputs}, {"k", keyInputs}}.dump());

  inputsSentTotal += static_cast<uint32_t>(pendingLvarInputs.size() + pendingKeyInputs.size());
  pendingLvarInputs.clear();
  pendingKeyInputs.clear();
  pendingAxisIndex.clear();
}

void SharedDeck::clientApplyPhysics(double deltaTime) {
  if (!havePhysics || !simConnect) {
    return;
  }
  // dead reckoning between two host frames, so the motion stays smooth at any frame rate
  physicsAge = std::min(physicsAge + deltaTime, MAX_EXTRAPOLATION);
  PhysicsState state = physics;
  const double latitudeRad = state.latitude / DEG_PER_RAD;
  state.latitude += worldVelocityNorth * physicsAge / EARTH_RADIUS_FEET * DEG_PER_RAD;
  state.longitude += worldVelocityEast * physicsAge / (EARTH_RADIUS_FEET * std::max(0.01, std::cos(latitudeRad))) * DEG_PER_RAD;
  state.altitude += worldVelocityUp * physicsAge;

  SimConnect_SetDataOnSimObject(simConnect, DEF_PHYSICS, SIMCONNECT_OBJECT_ID_USER, 0, 0, sizeof(state), &state);
}

void SharedDeck::clientVerify() {
  json lvarMismatches = json::array();
  json contested = json::array();
  json avarMismatches = json::array();

  for (auto& lvar : lvars) {
    if (lvar.declaredLocal) {
      continue;
    }
    if (lvar.contested) {
      contested.push_back(lvar.name);
      continue;
    }
    if (lvar.pendingInputSeq != 0) {
      continue;
    }
    const double current = get_named_variable_value(lvar.localId);
    if (sameBits(current, lvar.value)) {
      lvar.mismatchStreak = 0;
      continue;
    }
    // self-heal first, report only if the value does not stick
    lvar.mismatchStreak++;
    set_named_variable_value(lvar.localId, lvar.value);
    lvar.lastLocal = lvar.value;
    if (lvar.mismatchStreak >= MISMATCH_STREAK_LIMIT) {
      lvarMismatches.push_back(lvar.name);
    }
  }

  json advisory = json::array();
  for (auto& avar : avars) {
    if (avar.declaredLocal || !avar.hasValue) {
      continue;
    }
    const double current = readAVar(avar);
    const double allowed = std::max(avar.tolerance, 1e-9 * std::fabs(avar.value));
    if (std::fabs(current - avar.value) <= allowed) {
      avar.mismatchStreak = 0;
      continue;
    }
    // variables without setter (e.g. weather) can't be corrected: reported as a hint only
    if (avar.setter.empty()) {
      advisory.push_back(json::array({avar.name + ":" + std::to_string(avar.index), current, avar.value}));
      continue;
    }
    avar.mismatchStreak++;
    writeAVar(avar, avar.value);
    if (avar.mismatchStreak >= MISMATCH_STREAK_LIMIT) {
      avarMismatches.push_back(json::array({avar.name + ":" + std::to_string(avar.index), current, avar.value}));
    }
  }

  send(json{{"t", "verify"},
            {"kind", "local"},
            {"seq", lastFrameSeq},
            {"ok", lvarMismatches.empty() && avarMismatches.empty() && (contested.empty() || !strict)},
            {"transportStreak", transportMismatchStreak},
            {"lvarMismatches", lvarMismatches},
            {"avarMismatches", avarMismatches},
            {"contested", contested},
            {"advisory", advisory},
            {"levers", json::array({clientLeverOwner[0], clientLeverOwner[1]})},
            {"lvars", lvars.size()},
            {"inputsSent", inputsSentTotal}}
           .dump());
}

// ---------------------------------------------------------------------------------------------------------------------
// A:vars and audit
// ---------------------------------------------------------------------------------------------------------------------

double SharedDeck::readAVar(const AVar& avar) const {
  return aircraft_varget(avar.varEnum, avar.unitEnum, avar.index);
}

void SharedDeck::writeAVar(const AVar& avar, double value) {
  if (avar.setter.empty()) {
    return;
  }
  if (avar.setter == "@sc") {
    if (simConnect) {
      double data = value;
      SimConnect_SetDataOnSimObject(simConnect, avar.definition, SIMCONNECT_OBJECT_ID_USER, 0, 0, sizeof(data), &data);
    }
    return;
  }
  std::string code = avar.setter;
  replaceAll(code, "{v}", formatDouble(value));
  replaceAll(code, "{i}", std::to_string(avar.index));
  if (role == Role::Client) {
    expectOwnKeyEvents(code);
  }
  suppressKeyCapture = true;
  execute_calculator_code(code.c_str(), nullptr, nullptr, nullptr);
  suppressKeyCapture = false;
}

void SharedDeck::runAudit(const json& message) {
  json values = json::array();
  if (message.contains("vars")) {
    for (const auto& item : message["vars"]) {
      const std::string name = item.value("n", "");
      const ENUM varEnum = get_aircraft_var_enum(name.c_str());
      const ENUM unitEnum = get_units_enum(item.value("u", "number").c_str());
      values.push_back(varEnum < 0 ? json(nullptr) : json(aircraft_varget(varEnum, unitEnum, item.value("i", 0))));
    }
  }
  send(json{{"t", "audit"}, {"id", message.value("id", 0)}, {"values", values}}.dump());
}

}  // namespace sd

// ---------------------------------------------------------------------------------------------------------------------
// module entry points
// ---------------------------------------------------------------------------------------------------------------------

static sd::SharedDeck sharedDeck;

extern "C" MSFS_CALLBACK void module_init(void) {
  sharedDeck.init();
}

extern "C" MSFS_CALLBACK void module_deinit(void) {
  sharedDeck.deinit();
}
