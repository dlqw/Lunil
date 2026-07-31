import * as path from 'node:path';
import { runTests } from '@vscode/test-electron';

async function main(): Promise<void> {
  const extensionDevelopmentPath = path.resolve(__dirname, '..', '..');
  const extensionTestsPath = path.resolve(__dirname, 'suite', 'index');
  const testWorkspace = path.resolve(extensionDevelopmentPath, 'test', 'fixture');
  const options = {
    extensionDevelopmentPath,
    extensionTestsPath,
    extensionTestsEnv: process.env,
    launchArgs: [testWorkspace, '--disable-extensions'],
    version: process.env['VSCODE_TEST_VERSION'] ?? 'stable'
  } as Parameters<typeof runTests>[0];
  if (process.env['VSCODE_EXECUTABLE_PATH'] !== undefined) {
    options.vscodeExecutablePath = process.env['VSCODE_EXECUTABLE_PATH'];
  }
  await runTests(options);
}

void main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
