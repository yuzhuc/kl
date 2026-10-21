import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

const PROTOCOL_RS = "crates/kl-protocol/src/lib.rs";
const METHODS_SCHEMA = "schemas/protocol/v1/methods.schema.json";

// schema 里少数根名推不出 Rust 方法名（历史命名不一致），补 schema 时应统一
const SCHEMA_ROOT_OWNERS = {
  setDefaultModel: "config.set_default_model",
  defaultModel: "config.set_default_model",
  providerKey: "provider.set_api_key",
  providerEnabled: "provider.set_enabled",
  pluginId: "plugin.install",
  permissions: "permissions.get",
  agentConfig: "agent.config.get",
  review: "review.run",
};

// 还没有 schema 定义的方法；R1-9 收口时补齐 schema 后从这里删除
const UNSCHEMATIZED_METHODS = [
  "session.resume",
  "session.list",
  "session.fork",
  "turn.cancel",
  "tool.approval.respond",
  "audit.tail",
  "config.get",
  "skill.list",
  "skill.get",
  "mcp.list",
  "plugin.reload",
  "plugin.enable",
  "plugin.disable",
];

function rustBlock(text, header) {
  const start = text.indexOf(header);
  if (start < 0) return null;
  const end = text.indexOf("\n}", start);
  return text.slice(start, end < 0 ? text.length : end);
}

export function extractRustMethods(text) {
  const block = rustBlock(text, "pub mod methods {");
  if (block === null) return null;
  return [...block.matchAll(/pub const (\w+): &str = "([^"]+)";/g)].map((match) => match[2]);
}

export function extractRustEvents(text) {
  const block = rustBlock(text, "pub enum CoreEvent {");
  if (block === null) return null;
  return [...block.matchAll(/#\[serde\(rename = "([^"]+)"\)\]/g)].map((match) => match[1]);
}

export function extractSchemaRoots(schema) {
  return Object.keys(schema.$defs ?? {})
    .filter((key) => key.endsWith("Params") || key.endsWith("Result"))
    .map((key) => key.replace(/(Params|Result)$/, ""));
}

export function extractSchemaEvents(schema) {
  const notification = schema.$defs?.eventNotification;
  return notification?.properties?.params?.properties?.event?.properties?.type?.enum ?? [];
}

// "provider.set_api_key" -> "providerSetApiKey"，"kl.handshake" -> "handshake"
export function schemaRootFor(method) {
  const bare = method.startsWith("kl.") ? method.slice(3) : method;
  return bare
    .split(".")
    .map((segment, index) => {
      const camel = segment
        .split("_")
        .filter(Boolean)
        .map((part, index) => (index === 0 ? part : part[0].toUpperCase() + part.slice(1)))
        .join("");
      return index === 0 ? camel : camel[0].toUpperCase() + camel.slice(1);
    })
    .join("");
}

function schemaCoversMethod(method, schemaRoots) {
  if (schemaRoots.has(schemaRootFor(method))) return true;
  return Object.entries(SCHEMA_ROOT_OWNERS).some(
    ([schemaRoot, owner]) => owner === method && schemaRoots.has(schemaRoot),
  );
}

function schemaRootHasOwner(schemaRoot, rustMethods) {
  if (rustMethods.some((method) => method !== "event" && schemaRootFor(method) === schemaRoot)) return true;
  const owner = SCHEMA_ROOT_OWNERS[schemaRoot];
  return owner !== undefined && rustMethods.includes(owner);
}

export function checkProtocol(read) {
  const failures = [];
  const rustText = read(PROTOCOL_RS);
  const schemaText = read(METHODS_SCHEMA);
  if (rustText === undefined) {
    return { failures: [{ id: "rust-missing", detail: `${PROTOCOL_RS}: file is missing` }] };
  }
  if (schemaText === undefined) {
    return { failures: [{ id: "schema-missing", detail: `${METHODS_SCHEMA}: file is missing` }] };
  }

  let schema;
  try {
    schema = JSON.parse(schemaText);
  } catch (error) {
    return { failures: [{ id: "schema-invalid", detail: `${METHODS_SCHEMA}: invalid JSON (${error.message})` }] };
  }

  const rustMethods = extractRustMethods(rustText);
  const rustEvents = extractRustEvents(rustText);
  if (rustMethods === null || rustEvents === null) {
    return {
      failures: [{ id: "rust-unparsable", detail: `${PROTOCOL_RS}: expected a methods module and a CoreEvent enum` }],
    };
  }

  const schemaRoots = new Set(extractSchemaRoots(schema));
  const schemaEvents = new Set(extractSchemaEvents(schema));

  for (const method of rustMethods) {
    if (method === "event") continue;
    if (UNSCHEMATIZED_METHODS.includes(method)) continue;
    if (!schemaCoversMethod(method, schemaRoots)) {
      failures.push({ id: "method-unschematized", detail: `method ${method} has no entry in ${METHODS_SCHEMA}` });
    }
  }

  for (const method of UNSCHEMATIZED_METHODS) {
    if (!rustMethods.includes(method)) {
      failures.push({
        id: "stale-unschematized",
        detail: `${method} is listed as unschematized but no longer exists in ${PROTOCOL_RS}`,
      });
    }
  }

  for (const schemaRoot of schemaRoots) {
    if (!schemaRootHasOwner(schemaRoot, rustMethods)) {
      failures.push({
        id: "method-missing-in-rust",
        detail: `${METHODS_SCHEMA} documents ${schemaRoot} but ${PROTOCOL_RS} has no such method`,
      });
    }
  }

  for (const event of rustEvents) {
    if (!schemaEvents.has(event)) {
      failures.push({ id: "event-unschematized", detail: `event ${event} is missing from ${METHODS_SCHEMA}` });
    }
  }
  for (const event of schemaEvents) {
    if (!rustEvents.includes(event)) {
      failures.push({ id: "event-missing-in-rust", detail: `event ${event} is missing from ${PROTOCOL_RS}` });
    }
  }

  return { failures };
}

function readFromDisk(file) {
  const full = path.join(root, file);
  return existsSync(full) ? readFileSync(full, "utf8") : undefined;
}

function main() {
  const { failures } = checkProtocol(readFromDisk);
  for (const failure of failures) {
    console.error(`protocol check failed: ${failure.id}`);
    console.error(`  - ${failure.detail}`);
  }
  if (failures.length > 0) {
    console.error(`protocol check: ${failures.length} mismatches`);
    process.exit(1);
  }
  console.log("protocol check: Rust methods and events match the schema");
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) main();
