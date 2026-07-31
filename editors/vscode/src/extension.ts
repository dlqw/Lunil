import * as crypto from 'node:crypto';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import * as vscode from 'vscode';
import {
  CloseAction,
  ErrorAction,
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  State
} from 'vscode-languageclient/node';

let controller: LunilClientController | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  controller = new LunilClientController(context);
  context.subscriptions.push(controller);
  await controller.activate();
}

export async function deactivate(): Promise<void> {
  await controller?.stop();
  controller = undefined;
}

class LunilClientController implements vscode.Disposable {
  private readonly output = vscode.window.createOutputChannel('Lunil', { log: true });
  private readonly status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 20);
  private readonly hostDocuments = new HostDocumentProvider();
  private readonly disposables: vscode.Disposable[] = [];
  private client: LanguageClient | undefined;
  private restartTimer: NodeJS.Timeout | undefined;
  private stableTimer: NodeJS.Timeout | undefined;
  private restartAttempts = 0;
  private stopping = false;
  private disposed = false;

  public constructor(private readonly context: vscode.ExtensionContext) {
    this.status.name = 'Lunil';
    this.status.command = 'lunil.showOutput';
    this.setStatus('idle', 'Lunil: idle');
    this.status.show();
    this.disposables.push(
      this.output,
      this.status,
      vscode.workspace.registerTextDocumentContentProvider('lunil-host', this.hostDocuments),
      vscode.commands.registerCommand('lunil.restartServer', () => this.restart()),
      vscode.commands.registerCommand('lunil.clearCache', () => this.request('lunil/clearCache')),
      vscode.commands.registerCommand('lunil.reindexWorkspace', () => this.request('lunil/reindex')),
      vscode.commands.registerCommand('lunil.showOutput', () => this.output.show(true)),
      vscode.commands.registerCommand('lunil.showHostContract', () => this.showHostContract()),
      vscode.workspace.onDidChangeConfiguration(event => this.configurationChanged(event)),
      vscode.workspace.onDidGrantWorkspaceTrust(() => this.start())
    );
  }

  public async activate(): Promise<void> {
    if (!vscode.workspace.isTrusted) {
      this.setStatus('restricted', 'Lunil: disabled in Restricted Mode');
      this.output.appendLine('Lunil waits for Workspace Trust before starting executable workspace code.');
      return;
    }

    await this.start();
  }

  public async stop(): Promise<void> {
    this.stopping = true;
    this.clearTimers();
    const current = this.client;
    this.client = undefined;
    if (current !== undefined) {
      if (current.needsStop()) {
        await current.stop().catch(error => this.logError('stop', error));
      }
      await current.dispose().catch(error => this.logError('dispose', error));
    }
    this.setStatus('stopped', 'Lunil: stopped');
  }

  public dispose(): void {
    this.disposed = true;
    void this.stop();
    for (const disposable of this.disposables.splice(0)) {
      disposable.dispose();
    }
  }

  private async start(): Promise<void> {
    if (this.disposed || this.client !== undefined || !vscode.workspace.isTrusted) {
      return;
    }

    this.stopping = false;
    this.setStatus('starting', 'Lunil: starting');
    try {
      const executable = await this.resolveServer();
      const configuration = vscode.workspace.getConfiguration('lunil');
      const heapLimit = configuration.get<number>('server.gcHeapHardLimitPercent', 70);
      const serverOptions: ServerOptions = {
        command: executable,
        args: ['--stdio'],
        options: {
          cwd: firstWorkspaceDirectory(),
          env: {
            ...process.env,
            DOTNET_GCHeapHardLimitPercent: heapLimit.toString(16)
          }
        }
      };
      const watcher = vscode.workspace.createFileSystemWatcher('**/*.lua');
      const clientOptions: LanguageClientOptions = {
        documentSelector: [
          { language: 'lua', scheme: 'file' },
          { language: 'lua', scheme: 'untitled' }
        ],
        synchronize: {
          configurationSection: 'lunil',
          fileEvents: watcher
        },
        workspaceFolder: vscode.workspace.workspaceFolders?.[0],
        outputChannel: this.output,
        traceOutputChannel: this.output,
        initializationOptions: {
          extensionVersion: this.context.extension.packageJSON.version,
          platform: platformRid()
        },
        errorHandler: {
          error: () => ({ action: ErrorAction.Continue }),
          closed: () => ({ action: CloseAction.DoNotRestart })
        }
      };
      const client = new LanguageClient(
        'lunil',
        'Lunil Language Server',
        serverOptions,
        clientOptions
      );
      this.client = client;
      this.disposables.push(watcher, client.onDidChangeState(event => this.stateChanged(event.newState)));
      client.onNotification('lunil/indexProgress', progress => this.indexProgress(progress));
      await client.start();
      await client.setTrace(traceLevel(configuration.get<string>('server.trace', 'off')));
    } catch (error) {
      const failed = this.client;
      this.client = undefined;
      if (failed !== undefined) {
        await failed.dispose().catch(disposeError => this.logError('dispose failed start', disposeError));
      }
      this.logError('start', error);
      this.scheduleRestart();
    }
  }

  private async restart(): Promise<void> {
    this.restartAttempts = 0;
    await this.stop();
    this.stopping = false;
    await this.start();
  }

  private stateChanged(state: State): void {
    if (state === State.Running) {
      this.setStatus('running', 'Lunil: ready');
      if (this.stableTimer !== undefined) {
        clearTimeout(this.stableTimer);
      }
      this.stableTimer = setTimeout(() => { this.restartAttempts = 0; }, 30_000);
    } else if (state === State.Starting) {
      this.setStatus('starting', 'Lunil: starting');
    } else {
      this.setStatus('stopped', 'Lunil: stopped');
      const stopped = this.client;
      this.client = undefined;
      if (stopped !== undefined) {
        void stopped.dispose().catch(error => this.logError('dispose stopped client', error));
      }
      if (!this.stopping) {
        this.scheduleRestart();
      }
    }
  }

  private scheduleRestart(): void {
    if (this.disposed || this.stopping || !vscode.workspace.isTrusted) {
      return;
    }
    const maximum = vscode.workspace.getConfiguration('lunil')
      .get<number>('server.maximumRestartCount', 5);
    if (this.restartAttempts >= maximum) {
      this.setStatus('error', 'Lunil: server stopped; restart required');
      void vscode.window.showErrorMessage(
        'Lunil Language Server stopped repeatedly.',
        'Restart'
      ).then(selection => selection === 'Restart' ? this.restart() : undefined);
      return;
    }
    const delay = Math.min(30_000, 500 * (2 ** this.restartAttempts));
    this.restartAttempts++;
    this.setStatus('restarting', `Lunil: restarting in ${Math.ceil(delay / 1000)}s`);
    this.restartTimer = setTimeout(() => void this.start(), delay);
  }

  private async resolveServer(): Promise<string> {
    const configured = vscode.workspace.getConfiguration('lunil').get<string>('server.path', '').trim();
    const testPath = process.env['LUNIL_TEST_SERVER_PATH'];
    if (configured !== '') {
      if (!path.isAbsolute(configured)) {
        throw new Error('lunil.server.path must be absolute.');
      }
      await fs.access(configured);
      return configured;
    }
    if (testPath !== undefined && testPath !== '') {
      await fs.access(testPath);
      return testPath;
    }

    const executableName = process.platform === 'win32'
      ? 'lunil-language-server.exe'
      : 'lunil-language-server';
    const serverDirectory = this.context.asAbsolutePath(path.join('server', platformRid()));
    const executable = path.join(serverDirectory, executableName);
    await verifyChecksum(serverDirectory, executableName);
    if (process.platform !== 'win32') {
      await fs.chmod(executable, 0o755);
    }
    return executable;
  }

  private async request(method: string): Promise<unknown> {
    if (this.client === undefined || !this.client.isRunning()) {
      void vscode.window.showWarningMessage('Lunil Language Server is not running.');
      return undefined;
    }
    try {
      return await this.client.sendRequest(method);
    } catch (error) {
      this.logError(method, error);
      throw error;
    }
  }

  private async showHostContract(): Promise<void> {
    const result = await this.request('lunil/virtualHostDocument') as HostDocument | undefined;
    if (result === undefined) {
      return;
    }
    this.hostDocuments.update(result.text);
    const document = await vscode.workspace.openTextDocument(vscode.Uri.parse(result.uri));
    await vscode.languages.setTextDocumentLanguage(document, result.languageId);
    await vscode.window.showTextDocument(document, { preview: true });
  }

  private configurationChanged(event: vscode.ConfigurationChangeEvent): void {
    if (!event.affectsConfiguration('lunil')) {
      return;
    }
    if (event.affectsConfiguration('lunil.server.path') ||
        event.affectsConfiguration('lunil.server.gcHeapHardLimitPercent')) {
      void this.restart();
      return;
    }
    if (this.client?.isRunning() === true) {
      const settings = vscode.workspace.getConfiguration('lunil');
      void this.client.sendNotification('workspace/didChangeConfiguration', {
        settings: { lunil: configurationObject(settings) }
      });
    }
  }

  private indexProgress(value: unknown): void {
    const progress = value as { phase?: string; completed?: number; total?: number };
    const total = progress.total ?? 0;
    const completed = progress.completed ?? 0;
    this.setStatus('indexing', `Lunil: ${progress.phase ?? 'indexing'} ${completed}/${total}`);
  }

  private setStatus(kind: string, text: string): void {
    this.status.text = kind === 'running' ? '$(check) Lunil' : `$(pulse) ${text}`;
    this.status.tooltip = text;
  }

  private logError(operation: string, error: unknown): void {
    const message = error instanceof Error ? error.stack ?? error.message : String(error);
    this.output.appendLine(`[${new Date().toISOString()}] ${operation}: ${message}`);
  }

  private clearTimers(): void {
    if (this.restartTimer !== undefined) clearTimeout(this.restartTimer);
    if (this.stableTimer !== undefined) clearTimeout(this.stableTimer);
    this.restartTimer = undefined;
    this.stableTimer = undefined;
  }
}

class HostDocumentProvider implements vscode.TextDocumentContentProvider {
  private content = '-- No external host contract symbols are indexed.\n';
  private readonly changed = new vscode.EventEmitter<vscode.Uri>();
  public readonly onDidChange = this.changed.event;

  public provideTextDocumentContent(): string {
    return this.content;
  }

  public update(content: string): void {
    this.content = content;
    this.changed.fire(vscode.Uri.parse('lunil-host:/contract.lua'));
  }
}

interface HostDocument {
  readonly uri: string;
  readonly languageId: string;
  readonly text: string;
}

async function verifyChecksum(directory: string, executableName: string): Promise<void> {
  const manifestPath = path.join(directory, 'server.sha256');
  const manifest = (await fs.readFile(manifestPath, 'utf8')).trim().split(/\s+/u);
  if (manifest.length !== 2 || manifest[1] !== executableName || manifest[0] === undefined) {
    throw new Error(`Invalid bundled server checksum manifest: ${manifestPath}`);
  }
  const executable = await fs.readFile(path.join(directory, executableName));
  const actual = crypto.createHash('sha256').update(executable).digest('hex');
  if (actual.toLowerCase() !== manifest[0].toLowerCase()) {
    throw new Error(`Bundled Lunil Language Server checksum mismatch for ${executableName}.`);
  }
}

function platformRid(): string {
  const architecture = process.arch === 'x64' ? 'x64' : process.arch === 'arm64' ? 'arm64' : undefined;
  if (architecture === undefined) {
    throw new Error(`Unsupported architecture: ${process.arch}`);
  }
  const platform = process.platform === 'win32' ? 'win' :
    process.platform === 'darwin' ? 'osx' :
    process.platform === 'linux' ? 'linux' : undefined;
  if (platform === undefined) {
    throw new Error(`Unsupported platform: ${process.platform}`);
  }
  return `${platform}-${architecture}`;
}

function firstWorkspaceDirectory(): string | undefined {
  const folder = vscode.workspace.workspaceFolders?.[0];
  return folder?.uri.scheme === 'file' ? folder.uri.fsPath : undefined;
}

function traceLevel(value: string): import('vscode-jsonrpc').Trace {
  const protocol = require('vscode-jsonrpc') as typeof import('vscode-jsonrpc');
  return value === 'verbose' ? protocol.Trace.Verbose :
    value === 'messages' ? protocol.Trace.Messages : protocol.Trace.Off;
}

function configurationObject(configuration: vscode.WorkspaceConfiguration): Record<string, unknown> {
  return {
    hostContractPath: configuration.get<string>('hostContractPath', ''),
    hostContractJson: configuration.get<string>('hostContractJson', ''),
    server: {
      trace: configuration.get<string>('server.trace', 'off')
    }
  };
}
