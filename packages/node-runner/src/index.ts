#!/usr/bin/env node

import { spawn, exec } from "node:child_process";
import { createConnection } from "node:net";
import { exit } from "node:process";
import { promisify } from "node:util";
import { ArgumentParser, BooleanOptionalAction } from "argparse";
import { readPackageUp } from "read-package-up";
import path from "node:path";
import fs from "node:fs/promises";
import { fileURLToPath } from "node:url";

const execAsync = promisify(exec);

const parser = new ArgumentParser({
  description: "Unity MCP runner. Either spawn Unity (-unityPath, -projectPath) or connect to an already-running Unity (-connectPort).",
});
parser.add_argument("-unityPath", { type: String, required: false });
parser.add_argument("-projectPath", { type: String, required: false });
parser.add_argument("-connectPort", {
  type: Number,
  required: false,
  help: "Connect to Unity already running with -mcp -mcpPort <port> (e.g. from Hub). No spawn.",
});
parser.add_argument("-connectHost", {
  type: String,
  required: false,
  default: "127.0.0.1",
  help: "Host for -connectPort (default: 127.0.0.1)",
});
parser.add_argument("-dev", { type: Boolean, action: BooleanOptionalAction, required: false });
const args = parser.parse_args();

const connectPort = args.connectPort as number | undefined;
const connectHost = (args.connectHost as string) || "127.0.0.1";
const devMode = args.dev;

if (connectPort != null) {
  // Connect mode: pipe stdio to existing Unity MCP server (TCP). No spawn, no manifest edit.
  runConnectMode(connectHost, connectPort, devMode);
  // Process stays alive; exit is called from socket "close" / "error" handlers.
  return;
}

// Spawn mode: require -unityPath and -projectPath
if (!args.unityPath || !args.projectPath) {
  console.error("Either -connectPort or both -unityPath and -projectPath are required.");
  exit(1);
}

const unityPath = args.unityPath as string;
const projectPath = args.projectPath as string;

let log: fs.FileHandle | undefined = undefined;

if (devMode) {
  log = await fs.open(path.join(projectPath, "mcp.log"), "w");
}

if (!(await fs.stat(projectPath).catch(() => false))) {
  console.error("Unity project path is not valid");
  exit(1);
}

const lockFile = path.join(projectPath, "Temp", "UnityLockFile");

if (await fs.stat(lockFile).catch(() => false)) {
  if (process.platform === "darwin" || process.platform === "linux") {
    try {
      await execAsync(`lsof "${lockFile}"`);
      console.error("Unity project is already open");
      exit(1);
    } catch {
      await log?.write(`Lock file exists but not open by any process, proceeding...\n`);
    }
  } else {
    try {
      await fs.unlink(lockFile);
      await log?.write(`Lock file existed but was successfully deleted, Unity not running, proceeding...\n`);
    } catch {
      console.error("Unity project is already open");
      exit(1);
    }
  }
}

const currentDir = path.dirname(fileURLToPath(import.meta.url));
const packageData = await readPackageUp({
  cwd: currentDir,
});

let packageUrl;

if (devMode) {
  const unityPackagePath = path.resolve(path.dirname(packageData!.path), "..", "unity");
  packageUrl = `file:${unityPackagePath}`;
} else {
  packageUrl = `https://github.com/nurture-tech/unity-mcp.git?path=packages/unity#v${packageData!.packageJson.version}`;
}

await log?.write(`Package URL: ${packageUrl}\n`);

const packageJsonPath = path.join(projectPath, "Packages", "manifest.json");
const packageJson = JSON.parse(await fs.readFile(packageJsonPath, "utf8"));
packageJson.dependencies["is.nurture.mcp"] = packageUrl;
await fs.writeFile(packageJsonPath, JSON.stringify(packageJson, null, 2));

const unityArgs = ["-projectPath", projectPath, "-mcp", "-logFile", "-"];

const separatorIndex = process.argv.indexOf("--");
if (separatorIndex !== -1) {
  unityArgs.push(...process.argv.slice(separatorIndex + 1));
}

const env = {
  ...process.env,
};

if (process.platform === "win32") {
  const userProfile = process.env.USERPROFILE || "C:\\Users\\Default";
  env.PROGRAMDATA = process.env.PROGRAMDATA || "C:\\ProgramData";
  env.ALLUSERSPROFILE = process.env.ALLUSERSPROFILE || "C:\\ProgramData";
  env.SYSTEMROOT = process.env.SYSTEMROOT || "C:\\Windows";
  env.LOCALAPPDATA = process.env.LOCALAPPDATA || path.join(userProfile, "AppData", "Local");
}

const proc = spawn(unityPath, unityArgs, { env });

try {
  const code = await pipeStdioToProcess(proc, log);
  exit(code ?? 0);
} finally {
  log?.close();
}

async function runConnectMode(host: string, port: number, devMode: boolean): Promise<void> {
  const log: fs.FileHandle | undefined = devMode ? await fs.open(path.join(process.cwd(), "mcp.log"), "w") : undefined;

  const socket = createConnection({ host, port }, () => {
    process.stdin.pipe(socket as NodeJS.WritableStream, { end: true });
  });

  let buffer = "";
  socket.on("data", (data: Buffer) => {
    buffer += data.toString();
    const lines = buffer.split("\n");
    buffer = lines.pop() || "";
    for (const line of lines) {
      if (line.startsWith("{")) {
        process.stdout.write(line + "\n");
        log?.write(line + "\n").catch(() => {});
      }
    }
  });

  socket.on("close", (hadError) => {
    log?.close().catch(() => {});
    exit(hadError ? 1 : 0);
  });
  socket.on("error", (err) => {
    console.error(err.message);
    log?.close().catch(() => {});
    exit(1);
  });
}

function pipeStdioToProcess(proc: ReturnType<typeof spawn>, log: fs.FileHandle | undefined): Promise<number | null> {
  return new Promise((resolve, reject) => {
    let buffer = "";

    process.stdin.on("data", async (data) => {
      await log?.write(data.toString());
      proc.stdin?.write(data);
    });
    proc.stdout?.on("data", async (data) => {
      buffer += data.toString();
      const lines = buffer.split("\n");
      buffer = lines.pop() || "";
      for (const line of lines) {
        if (line.startsWith("{")) {
          process.stdout.write(line + "\n");
          await log?.write(line + "\n");
        }
      }
    });
    proc.on("exit", (code) => {
      resolve(code);
    });
    proc.on("error", (err) => {
      reject(err.message);
    });
  });
}
