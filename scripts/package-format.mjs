import { closeSync, openSync, readSync } from "node:fs";

// Every RID the release workflow is allowed to ship. A package must contain
// binaries that really match its RID, so the layout is checked at pack time.
const RID_LAYOUT = {
  "win-x64": { kind: "pe", machine: 0x8664, ext: ".zip", exe: "kl.exe", core: "kl-core.exe" },
  "win-x86": { kind: "pe", machine: 0x014c, ext: ".zip", exe: "kl.exe", core: "kl-core.exe" },
  "win-arm64": { kind: "pe", machine: 0xaa64, ext: ".zip", exe: "kl.exe", core: "kl-core.exe" },
  "linux-x64": { kind: "elf", machine: 0x003e, ext: ".tar.gz", exe: "kl", core: "kl-core" },
  "linux-arm64": { kind: "elf", machine: 0x00b7, ext: ".tar.gz", exe: "kl", core: "kl-core" },
  "osx-x64": { kind: "macho", cpu: 0x01000007, ext: ".tar.gz", exe: "kl", core: "kl-core" },
  "osx-arm64": { kind: "macho", cpu: 0x0100000c, ext: ".tar.gz", exe: "kl", core: "kl-core" },
};

export function layoutFor(rid) {
  const layout = RID_LAYOUT[rid];
  if (!layout) throw new Error(`unsupported rid: ${rid}`);
  return layout;
}

function readBytes(file, length, position = 0) {
  const fd = openSync(file, "r");
  try {
    const buffer = Buffer.alloc(length);
    return buffer.subarray(0, readSync(fd, buffer, 0, length, position));
  } finally {
    closeSync(fd);
  }
}

function peMachine(file) {
  const head = readBytes(file, 0x40);
  if (head.length < 0x40 || head[0] !== 0x4d || head[1] !== 0x5a) return null;
  const peOffset = head.readUInt32LE(0x3c);
  const pe = readBytes(file, 6, peOffset);
  if (pe.length < 6 || pe.toString("latin1", 0, 4) !== "PE\0\0") return null;
  return pe.readUInt16LE(4);
}

function elfMachine(file) {
  const head = readBytes(file, 0x14);
  if (head.length < 0x14) return null;
  if (head[0] !== 0x7f || head[1] !== 0x45 || head[2] !== 0x4c || head[3] !== 0x46) return null;
  return head.readUInt16LE(0x12);
}

function machoCpu(file) {
  const head = readBytes(file, 4096);
  if (head.length < 8) return null;
  const magic = head.readUInt32LE(0);
  // Thin binaries: cpu type follows the magic in the same byte order.
  if (magic === 0xfeedfacf || magic === 0xfeedface) return new Set([head.readUInt32LE(4)]);
  if (magic === 0xcffaedfe || magic === 0xcefaedfe) return new Set([head.readUInt32BE(4)]);
  // Universal binary: collect every slice cpu type.
  const fat = magic === 0xbebafeca ? 20 : magic === 0xbfbafeca ? 32 : 0;
  if (fat === 0) return null;
  const cpus = new Set();
  const count = head.readUInt32BE(4);
  for (let index = 0; index < count; index += 1) {
    const at = 8 + index * fat;
    if (at + 4 > head.length) break;
    cpus.add(head.readUInt32BE(at));
  }
  return cpus;
}

export function assertBinaryFormat(file, rid) {
  const layout = layoutFor(rid);
  const actual =
    layout.kind === "pe" ? peMachine(file) : layout.kind === "elf" ? elfMachine(file) : machoCpu(file);
  const expected = layout.kind === "macho" ? layout.cpu : layout.machine;
  if (actual === null) throw new Error(`${file}: not a ${layout.kind} binary`);
  if (layout.kind === "macho" ? !actual.has(expected) : actual !== expected) {
    const found =
      layout.kind === "macho"
        ? [...actual].map((cpu) => `0x${cpu.toString(16)}`).join(",")
        : `0x${actual.toString(16)}`;
    throw new Error(`${file}: expected ${rid} (0x${expected.toString(16)}), found ${found}`);
  }
}
