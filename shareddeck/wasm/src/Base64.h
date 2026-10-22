// SharedDeck - shared cockpit for MSFS
// SPDX-License-Identifier: GPL-3.0

#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace sd {

/// Little binary writer used for the delta encoding: varint indices followed by raw IEEE-754 doubles.
class ByteWriter {
 public:
  void varint(uint32_t value) {
    while (value >= 0x80) {
      bytes.push_back(static_cast<uint8_t>(value | 0x80));
      value >>= 7;
    }
    bytes.push_back(static_cast<uint8_t>(value));
  }

  void f64(double value) {
    uint64_t bits;
    static_assert(sizeof(bits) == sizeof(value));
    __builtin_memcpy(&bits, &value, sizeof(bits));
    for (int i = 0; i < 8; i++) {
      bytes.push_back(static_cast<uint8_t>(bits >> (8 * i)));
    }
  }

  [[nodiscard]] bool empty() const { return bytes.empty(); }
  void clear() { bytes.clear(); }

  std::vector<uint8_t> bytes;
};

class ByteReader {
 public:
  explicit ByteReader(const std::vector<uint8_t>& data) : data(data) {}

  [[nodiscard]] bool atEnd() const { return pos >= data.size(); }

  bool varint(uint32_t& out) {
    out = 0;
    for (int shift = 0; shift < 35; shift += 7) {
      if (pos >= data.size()) {
        return false;
      }
      const uint8_t b = data[pos++];
      out |= static_cast<uint32_t>(b & 0x7F) << shift;
      if ((b & 0x80) == 0) {
        return true;
      }
    }
    return false;
  }

  bool f64(double& out) {
    if (pos + 8 > data.size()) {
      return false;
    }
    uint64_t bits = 0;
    for (int i = 0; i < 8; i++) {
      bits |= static_cast<uint64_t>(data[pos + i]) << (8 * i);
    }
    pos += 8;
    __builtin_memcpy(&out, &bits, sizeof(out));
    return true;
  }

 private:
  const std::vector<uint8_t>& data;
  size_t pos = 0;
};

inline std::string base64Encode(const std::vector<uint8_t>& in) {
  static const char* table = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  std::string out;
  out.reserve(((in.size() + 2) / 3) * 4);
  size_t i = 0;
  for (; i + 2 < in.size(); i += 3) {
    const uint32_t n = (in[i] << 16) | (in[i + 1] << 8) | in[i + 2];
    out.push_back(table[(n >> 18) & 63]);
    out.push_back(table[(n >> 12) & 63]);
    out.push_back(table[(n >> 6) & 63]);
    out.push_back(table[n & 63]);
  }
  if (i < in.size()) {
    uint32_t n = in[i] << 16;
    if (i + 1 < in.size()) {
      n |= in[i + 1] << 8;
    }
    out.push_back(table[(n >> 18) & 63]);
    out.push_back(table[(n >> 12) & 63]);
    out.push_back(i + 1 < in.size() ? table[(n >> 6) & 63] : '=');
    out.push_back('=');
  }
  return out;
}

inline bool base64Decode(const std::string& in, std::vector<uint8_t>& out) {
  auto value = [](char c) -> int {
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+') return 62;
    if (c == '/') return 63;
    return -1;
  };
  out.clear();
  out.reserve(in.size() / 4 * 3);
  uint32_t buffer = 0;
  int bits = 0;
  for (const char c : in) {
    if (c == '=') {
      break;
    }
    const int v = value(c);
    if (v < 0) {
      return false;
    }
    buffer = (buffer << 6) | static_cast<uint32_t>(v);
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      out.push_back(static_cast<uint8_t>((buffer >> bits) & 0xFF));
    }
  }
  return true;
}

/// FNV-1a 64 bit over (index, value bits) pairs, evaluated in canonical index order on both sides.
class StateHash {
 public:
  void add(uint32_t index, double value) {
    uint64_t bits;
    __builtin_memcpy(&bits, &value, sizeof(bits));
    mix(index);
    mix(bits);
  }

  [[nodiscard]] std::string hex() const {
    static const char* digits = "0123456789abcdef";
    std::string out(16, '0');
    for (int i = 0; i < 16; i++) {
      out[15 - i] = digits[(hash >> (4 * i)) & 0xF];
    }
    return out;
  }

 private:
  void mix(uint64_t value) {
    for (int i = 0; i < 8; i++) {
      hash ^= (value >> (8 * i)) & 0xFF;
      hash *= 0x100000001b3ULL;
    }
  }

  uint64_t hash = 0xcbf29ce484222325ULL;
};

}  // namespace sd
