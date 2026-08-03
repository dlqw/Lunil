import * as crypto from 'node:crypto';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';

export interface ChecksumRetryOptions {
  readonly maximumWaitMilliseconds?: number;
  readonly initialDelayMilliseconds?: number;
  readonly maximumDelayMilliseconds?: number;
  readonly sleep?: (milliseconds: number) => Promise<void>;
}

const defaultMaximumWaitMilliseconds = 60_000;
const defaultInitialDelayMilliseconds = 50;
const defaultMaximumDelayMilliseconds = 1_000;

export async function verifyChecksum(
  directory: string,
  executableName: string,
  options: ChecksumRetryOptions = {}): Promise<void> {
  const maximumWait = options.maximumWaitMilliseconds ?? defaultMaximumWaitMilliseconds;
  const maximumDelay = options.maximumDelayMilliseconds ?? defaultMaximumDelayMilliseconds;
  let retryDelay = options.initialDelayMilliseconds ?? defaultInitialDelayMilliseconds;
  const sleep = options.sleep ?? defaultSleep;
  const deadline = Date.now() + Math.max(0, maximumWait);

  for (;;) {
    try {
      await verifyChecksumOnce(directory, executableName);
      return;
    } catch (error) {
      const remaining = deadline - Date.now();
      if (!isMissingFileError(error) || remaining <= 0) {
        throw error;
      }

      const delay = Math.min(Math.max(1, retryDelay), Math.max(1, maximumDelay), remaining);
      await sleep(delay);
      retryDelay = Math.min(Math.max(1, maximumDelay), delay * 2);
    }
  }
}

async function verifyChecksumOnce(directory: string, executableName: string): Promise<void> {
  const manifestPath = path.join(directory, 'server.sha256');
  const manifestText = await fs.readFile(manifestPath, 'utf8');
  const entry = manifestText.split(/\r?\n/u)
    .map(line => line.trim().split(/\s+/u))
    .find(parts => parts.length === 2 && parts[1] === executableName);
  if (entry === undefined || entry[0] === undefined) {
    throw new Error(`Bundled server checksum manifest has no entry for ${executableName}: ${manifestPath}`);
  }

  const executable = await fs.readFile(path.join(directory, executableName));
  const actual = crypto.createHash('sha256').update(executable).digest('hex');
  if (actual.toLowerCase() !== entry[0].toLowerCase()) {
    throw new Error(`Bundled Lunil server checksum mismatch for ${executableName}.`);
  }
}

function isMissingFileError(error: unknown): boolean {
  return error instanceof Error && 'code' in error && error.code === 'ENOENT';
}

async function defaultSleep(milliseconds: number): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, milliseconds));
}
