import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';

export async function run(): Promise<void> {
  const extension = vscode.extensions.getExtension('dlqw.lunil-lua');
  assert.ok(extension, 'The Lunil extension is installed in the extension test host.');
  await extension.activate();

  const commands = await vscode.commands.getCommands(true);
  for (const command of [
    'lunil.restartServer',
    'lunil.clearCache',
    'lunil.reindexWorkspace',
    'lunil.showOutput',
    'lunil.showMenu',
    'lunil.showHostContract'
  ]) {
    assert.ok(commands.includes(command), `${command} must be registered.`);
  }

  const packageJson = JSON.parse(fs.readFileSync(
    path.join(extension.extensionPath, 'package.json'), 'utf8')) as {
      contributes?: {
        snippets?: { path: string }[];
        walkthroughs?: { steps: { media: { markdown: string } }[] }[];
      };
    };
  const snippets = packageJson.contributes?.snippets ?? [];
  assert.equal(snippets.length, 1, 'The Lua snippet library must be contributed.');
  assert.ok(
    fs.existsSync(path.join(extension.extensionPath, snippets[0]!.path)),
    'The snippet file must ship with the extension.');
  const walkthrough = packageJson.contributes?.walkthroughs?.[0];
  assert.ok(walkthrough, 'The getting-started walkthrough must be contributed.');
  assert.ok(walkthrough.steps.length >= 5, 'The walkthrough must cover the core flows.');
  for (const step of walkthrough.steps) {
    assert.ok(
      fs.existsSync(path.join(extension.extensionPath, step.media.markdown)),
      `Walkthrough media ${step.media.markdown} must ship with the extension.`);
  }

  const document = await vscode.workspace.openTextDocument(
    vscode.Uri.joinPath(vscode.workspace.workspaceFolders![0]!.uri, 'sample.lua')
  );
  assert.equal(document.languageId, 'lua');
  if (process.env['LUNIL_EXPECT_CRASH_RECOVERY'] === '1') {
    await new Promise(resolve => setTimeout(resolve, 2_000));
  }
  let reindex: { modules: number } | undefined;
  for (let attempt = 0; attempt < 50; attempt++) {
    reindex = await vscode.commands.executeCommand<{ modules: number }>('lunil.reindexWorkspace');
    if (reindex !== undefined) {
      break;
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  assert.ok(reindex, 'The language server must recover and accept requests.');
  assert.ok(reindex.modules >= 1);
  let completion: vscode.CompletionList | undefined;
  for (let attempt = 0; attempt < 50; attempt++) {
    completion = await vscode.commands.executeCommand<vscode.CompletionList>(
      'vscode.executeCompletionItemProvider',
      document.uri,
      new vscode.Position(1, 7)
    );
    if (completion.items.some(item => item.label === 'local' || item.label === 'value')) {
      break;
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  assert.ok(completion);
  assert.ok(completion.items.some(item => item.label === 'local' || item.label === 'value'));
  const cleared = await vscode.commands.executeCommand<{ cleared: boolean }>('lunil.clearCache');
  assert.equal(cleared.cleared, true);

  const hostContractPath = vscode.Uri.joinPath(
    vscode.workspace.workspaceFolders![0]!.uri,
    'host-contract.json'
  ).fsPath;
  const configuration = vscode.workspace.getConfiguration('lunil');
  await configuration.update(
    'hostContractPath',
    hostContractPath,
    vscode.ConfigurationTarget.Workspace
  );
  reindex = await vscode.commands.executeCommand<{ modules: number }>('lunil.reindexWorkspace');
  assert.ok(reindex, 'The language server must accept requests after settings invalidation.');
  await vscode.commands.executeCommand('lunil.showHostContract');
  const hostDocument = vscode.workspace.textDocuments.find(
    candidate => candidate.uri.scheme === 'lunil-host'
  );
  assert.ok(hostDocument, 'The virtual host contract document must be opened.');
  assert.match(hostDocument.getText(), /game\.run/);
  await configuration.update(
    'hostContractPath',
    undefined,
    vscode.ConfigurationTarget.Workspace
  );
  assert.ok(
    process.memoryUsage().heapUsed <= 100 * 1024 * 1024,
    `Extension host retained JS heap must remain within 100 MiB, observed ${process.memoryUsage().heapUsed}.`
  );
}
