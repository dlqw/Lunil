import * as assert from 'node:assert/strict';
import * as crypto from 'node:crypto';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { verifyChecksum } from '../../src/checksum';

describe('bundled server checksum', () => {
  const temporaryDirectories: string[] = [];

  afterEach(async () => {
    await Promise.all(temporaryDirectories.splice(0).map(directory =>
      fs.rm(directory, { recursive: true, force: true })));
  });

  it('waits for a temporarily missing extraction payload', async () => {
    const directory = await createTemporaryDirectory();
    const executableName = 'lunil-language-server.exe';
    const executable = Buffer.from('server payload');
    await fs.writeFile(path.join(directory, executableName), executable);
    const manifest = `${crypto.createHash('sha256').update(executable).digest('hex')}  ${executableName}\n`;
    const publication = setTimeout(() => {
      void fs.writeFile(path.join(directory, 'server.sha256'), manifest);
    }, 20);

    try {
      await verifyChecksum(directory, executableName, {
        maximumWaitMilliseconds: 500,
        initialDelayMilliseconds: 5,
        maximumDelayMilliseconds: 20,
      });
    } finally {
      clearTimeout(publication);
    }
  });

  it('keeps a checksum mismatch as an immediate hard failure', async () => {
    const directory = await createTemporaryDirectory();
    const executableName = 'lunil-language-server.exe';
    await fs.writeFile(path.join(directory, executableName), 'server payload');
    await fs.writeFile(path.join(directory, 'server.sha256'), `${'0'.repeat(64)}  ${executableName}\n`);
    let sleeps = 0;

    await assert.rejects(
      verifyChecksum(directory, executableName, {
        maximumWaitMilliseconds: 1_000,
        sleep: async () => { sleeps++; },
      }),
      /checksum mismatch/u);
    assert.equal(sleeps, 0);
  });

  it('fails after the bounded wait when the payload never appears', async () => {
    const directory = await createTemporaryDirectory();

    await assert.rejects(
      verifyChecksum(directory, 'lunil-language-server.exe', {
        maximumWaitMilliseconds: 15,
        initialDelayMilliseconds: 2,
        maximumDelayMilliseconds: 4,
      }),
      (error: unknown) => error instanceof Error && 'code' in error && error.code === 'ENOENT');
  });

  async function createTemporaryDirectory(): Promise<string> {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'lunil-checksum-'));
    temporaryDirectories.push(directory);
    return directory;
  }
});
