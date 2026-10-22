// SharedDeck - shared cockpit for MSFS
// SPDX-License-Identifier: GPL-3.0

#pragma once

#include <MSFS/Legacy/gauges.h>
#include <MSFS/MSFS.h>
#include <SimConnect.h>

#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

#include "Base64.h"
#include "nlohmann/json.hpp"

namespace sd {

/// Values of L:SHAREDDECK_ROLE. The FBW fork only checks for CLIENT.
enum class Role : int { Off = 0, Client = 1, Host = 2 };

struct LVar {
  std::string name;
  ID localId = -1;
  // host: value last sent to the client; client: mirror of the host value
  double value = 0;
  // client: last value seen or written locally, used to detect pilot inputs
  double lastLocal = 0;
  // client: the input with this sequence number has not been acknowledged by the host yet
  uint32_t pendingInputSeq = 0;
  double pendingSince = 0;
  // client: local change counter for detecting variables that local code keeps writing
  uint32_t windowChanges = 0;
  double windowStart = 0;
  bool contested = false;
  bool declaredLocal = false;
  bool inputDenied = false;
  uint32_t mismatchStreak = 0;
  // client: RPN code to run whenever the host changes this variable (what FBW does on the host side)
  std::string action;
  double actionLast = 0;
  bool actionToggled = false;  // odd number of runs during this session, undone when the session ends
};

struct AVar {
  std::string name;
  std::string unit;
  int index = 0;
  ENUM varEnum = -1;
  ENUM unitEnum = -1;
  std::string setter;  // RPN template with {v} and {i}, "@sc" for SimConnect, empty = only compared (advisory)
  SIMCONNECT_DATA_DEFINITION_ID definition = 0;  // for "@sc"
  double tolerance = 0;
  double value = 0;  // host: last sent; client: mirror
  bool hasValue = false;
  bool declaredLocal = false;
  uint32_t mismatchStreak = 0;
};

/// Rigid body state copied from host to client every frame.
struct PhysicsState {
  double latitude;
  double longitude;
  double altitude;
  double pitch;
  double bank;
  double heading;
  double velocityBodyX;
  double velocityBodyY;
  double velocityBodyZ;
  double rotationVelocityBodyX;
  double rotationVelocityBodyY;
  double rotationVelocityBodyZ;
};

class SharedDeck {
 public:
  void init();
  void deinit();

  void onFrame(double deltaTime);
  void onPause(DWORD pauseState);
  void onKeyEvent(ID32 event, UINT32 d0, UINT32 d1, UINT32 d2, UINT32 d3, UINT32 d4);
  void onMessage(const std::string& json);

 private:
  // --- messaging ---
  void send(const std::string& json);
  void log(const std::string& message);

  // --- configuration ---
  void applyConfig(const nlohmann::json& config);
  bool matchesAny(const std::vector<std::string>& patterns, const std::string& name) const;

  // --- session ---
  void startSession();
  void stopSession();
  void setRoleVariable(Role value);
  void setFreeze(bool frozen);
  void triggerKey(ID32 event, UINT32 d0 = 0, UINT32 d1 = 0, UINT32 d2 = 0, UINT32 d3 = 0, UINT32 d4 = 0);
  void expectOwnKeyEvents(const std::string& code);

  // --- host ---
  void hostScanNames(bool force);
  void hostEmitFrame(bool keyframe);
  void hostSendAllNames();
  void hostApplyInput(const nlohmann::json& message);
  std::string hostComputeHash() const;
  void readPhysics(PhysicsState& state) const;

  // --- client ---
  void clientApplyNames(const nlohmann::json& message);
  void clientApplyFrame(const nlohmann::json& message);
  void clientDetectInputs();
  void clientFlushInputs();
  void queueLvarInput(const std::string& name, double value);
  void clientApplyPhysics(double deltaTime);
  void clientVerify();
  void clientRequestKeyframe(const std::string& reason);
  std::string clientComputeMirrorHash() const;

  // --- audit ---
  void runAudit(const nlohmann::json& message);

  double readAVar(const AVar& avar) const;
  void writeAVar(const AVar& avar, double value);

  HANDLE simConnect = 0;
  ID roleVariableId = -1;

  Role role = Role::Off;
  bool active = false;
  bool strict = true;
  double rateHz = 30;
  std::vector<std::string> lvarDeny;
  std::vector<std::string> inputDeny;
  std::vector<std::string> keyDeny;
  std::vector<std::string> localAllow;
  std::unordered_map<std::string, std::string> lvarActions;
  void runLvarAction(LVar& lvar);

  double time = 0;
  double lastEmitTime = -1;
  double lastSecondTick = 0;
  bool simPaused = false;
  bool suppressKeyCapture = false;

  std::vector<LVar> lvars;
  std::vector<AVar> avars;
  std::unordered_map<std::string, uint32_t> lvarIndexByName;

  // host
  ID nextScanId = 0;
  uint32_t frameSeq = 0;
  uint32_t lastInputSeqApplied = 0;
  uint32_t framesSinceHash = 0;
  uint64_t bytesSent = 0;

  // client
  uint32_t lastFrameSeq = 0;
  bool awaitingKeyframe = true;
  double lastKeyframeRequest = -1;
  uint32_t inputSeq = 0;
  uint32_t hostAckedInputSeq = 0;
  uint32_t transportMismatchStreak = 0;
  bool havePhysics = false;
  PhysicsState physics{};
  double physicsAge = 0;
  double worldVelocityNorth = 0;
  double worldVelocityEast = 0;
  double worldVelocityUp = 0;
  std::vector<std::pair<std::string, double>> pendingLvarInputs;
  std::vector<std::vector<uint32_t>> pendingKeyInputs;  // event, d0..d4
  std::unordered_map<ID32, size_t> pendingAxisIndex;
  uint32_t inputsSentTotal = 0;

  struct OwnKeyExpectation {
    uint32_t count = 0;
    double until = 0;
  };
  std::unordered_map<std::string, OwnKeyExpectation> expectedOwnKeys;

  // host: which pilot moves each thrust lever (soft takeover)
  enum class LeverOwner : int { Host = 0, Client = 1 };
  LeverOwner leverOwner[2] = {LeverOwner::Host, LeverOwner::Host};
  double leverApplied[2] = {0, 0};
  double leverClientInputTime[2] = {-10, -10};
  ID leverInputId[2] = {-1, -1};
  double lastPickupHint = -10;
  void hostTrackLevers();
  bool hostApplyThrottleInput(const std::string& name, UINT32 d0);

  // client: lever owners reported by the host
  int clientLeverOwner[2] = {0, 0};

  // simulation time sync (host -> client)
  double lastTimeSent = -10;
  double lastTimeCorrection = -100;
  void clientSyncTime(const nlohmann::json& zulu);
  static double readCalculator(const char* code);

  struct KeyActivity {
    double windowStart = 0;
    uint32_t count = 0;
    bool suppressed = false;
  };
  std::unordered_map<ID32, KeyActivity> keyActivity;
};

}  // namespace sd
