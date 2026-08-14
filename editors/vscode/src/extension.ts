import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { verifyChecksum } from './checksum';
import {
  CloseAction,
  ErrorAction,
  LanguageClient,
  LanguageClientOptions,
  ProgressType,
  ServerOptions,
  State
} from 'vscode-languageclient/node';

let controller: LunilClientController | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  controller = new LunilClientController(context);
  context.subscriptions.push(controller);
  await controller.activate();
  context.subscriptions.push(
    vscode.debug.registerDebugAdapterDescriptorFactory(
      'lunil',
      new LunilDebugAdapterFactory(context)));
}

export async function deactivate(): Promise<void> {
  await controller?.stop();
  controller = undefined;
}

class LunilDebugAdapterFactory implements vscode.DebugAdapterDescriptorFactory {
  public constructor(private readonly context: vscode.ExtensionContext) {}

  public async createDebugAdapterDescriptor(
    session: vscode.DebugSession): Promise<vscode.DebugAdapterDescriptor> {
    const executableName = process.platform === 'win32'
      ? 'lunil-debug-adapter.exe'
      : 'lunil-debug-adapter';
    const serverDirectory = this.context.asAbsolutePath(path.join('server', platformRid()));
    const executable = path.join(serverDirectory, executableName);
    await verifyChecksum(serverDirectory, executableName);
    if (process.platform !== 'win32') {
      await fs.chmod(executable, 0o755);
    }

    const args = ['--stdio'];
    if (session.configuration.request === 'attach' &&
        typeof session.configuration.debugPipe === 'string') {
      args.push('--pipe', session.configuration.debugPipe);
    }

    return new vscode.DebugAdapterExecutable(executable, args);
  }
}

type ServerState =
  | 'restricted'
  | 'starting'
  | 'running'
  | 'indexing'
  | 'restarting'
  | 'stopped'
  | 'error';

interface IndexProgressPayload {
  readonly phase?: string;
  readonly completed?: number;
  readonly total?: number;
  readonly succeeded?: number;
  readonly failed?: number;
  readonly inProgress?: number;
  readonly pending?: number;
}

const indexProgressThrottleMs = 200;

class LunilClientController implements vscode.Disposable {
  private readonly output = vscode.window.createOutputChannel('Lunil', { log: true });
  private readonly status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 20);
  private readonly hostDocuments = new HostDocumentProvider();
  /** One watcher for the controller lifetime; restarting the server must not stack duplicates. */
  private readonly watcher = vscode.workspace.createFileSystemWatcher('**/*.lua');
  private readonly disposables: vscode.Disposable[] = [];
  private client: LanguageClient | undefined;
  private restartTimer: NodeJS.Timeout | undefined;
  private stableTimer: NodeJS.Timeout | undefined;
  private restartAttempts = 0;
  private stopping = false;
  private disposed = false;
  private state: ServerState = 'stopped';
  private stateText = 'idle';
  private lastIndexDetail = '';
  private lastIndexUpdate = 0;

  public constructor(private readonly context: vscode.ExtensionContext) {
    this.status.name = 'Lunil';
    this.status.command = 'lunil.showMenu';
    this.setStatus('stopped', 'idle');
    this.status.show();
    this.disposables.push(
      this.output,
      this.status,
      this.watcher,
      vscode.workspace.registerTextDocumentContentProvider('lunil-host', this.hostDocuments),
      vscode.commands.registerCommand('lunil.restartServer', () => this.restart()),
      vscode.commands.registerCommand('lunil.clearCache', () => this.clearCache()),
      vscode.commands.registerCommand('lunil.reindexWorkspace', () => this.reindexWorkspace()),
      vscode.commands.registerCommand('lunil.showOutput', () => this.output.show(true)),
      vscode.commands.registerCommand('lunil.showMenu', () => this.showMenu()),
      vscode.commands.registerCommand('lunil.showIndexStatus', () => this.showIndexStatus()),
      vscode.commands.registerCommand('lunil.showHostContract', () => this.showHostContract()),
      vscode.commands.registerCommand('lunil._suppressDiagnostic', (code: string) => this.suppressDiagnostic(code)),
      vscode.workspace.onDidChangeConfiguration(event => this.configurationChanged(event)),
      vscode.workspace.onDidGrantWorkspaceTrust(() => this.start())
    );
  }

  public async activate(): Promise<void> {
    const folders = vscode.workspace.workspaceFolders?.map(f => f.uri.toString()) ?? [];
    this.output.appendLine(`[${timestamp()}] activate: trusted=${vscode.workspace.isTrusted} folders=[${folders.join(', ')}]`);
    if (!vscode.workspace.isTrusted) {
      this.setStatus('restricted', 'disabled in Restricted Mode');
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
    this.setStatus('stopped', 'stopped');
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
      this.output.appendLine(`[${timestamp()}] start: skipped (disposed=${this.disposed} clientExists=${this.client !== undefined} trusted=${vscode.workspace.isTrusted})`);
      return;
    }

    this.stopping = false;
    this.setStatus('starting', 'starting');
    try {
      const executable = await this.resolveServer();
      this.output.appendLine(`[${timestamp()}] start: server = ${executable}`);
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
      const clientOptions: LanguageClientOptions = {
        documentSelector: [
          { language: 'lua', scheme: 'file' },
          { language: 'lua', scheme: 'untitled' }
        ],
        synchronize: {
          configurationSection: 'lunil',
          fileEvents: this.watcher
        },
        workspaceFolder: vscode.workspace.workspaceFolders?.[0],
        outputChannel: this.output,
        traceOutputChannel: this.output,
        initializationOptions: {
          extensionVersion: this.context.extension.packageJSON.version,
          platform: platformRid()
        },
        middleware: {
          provideCodeActions: async (document, range, context, token, next) => {
            const provided = await next(document, range, context, token);
            const items: (vscode.CodeAction | vscode.Command)[] =
              provided === undefined || provided === null
                ? []
                : Array.isArray(provided)
                  ? [...provided]
                  : [...provided];
            for (const diagnostic of context.diagnostics) {
              const code = typeof diagnostic.code === 'string' ? diagnostic.code : undefined;
              if (code === undefined || !/^LUA\d+$/.test(code)) {
                continue;
              }
              const suppressed = vscode.workspace.getConfiguration('lunil')
                .get<string[]>('server.suppressedDiagnosticCodes', []);
              if (suppressed.includes(code)) {
                continue;
              }
              items.push({
                title: `Suppress ${code} in this workspace (lunil.server.suppressedDiagnosticCodes)`,
                kind: vscode.CodeActionKind.QuickFix,
                command: { title: `Suppress ${code}`, command: 'lunil._suppressDiagnostic', arguments: [code] }
              } as vscode.CodeAction);
            }
            return items;
          }
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
      this.disposables.push(
        client.onDidChangeState(event => this.stateChanged(event.newState)),
        client.onProgress(new ProgressType<LunilWorkDoneProgress>(), 'lunil-workspace-index',
          progress => this.workDoneProgress(progress)));
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
    this.output.appendLine(`[${timestamp()}] stateChanged: ${state}`);
    if (state === State.Running) {
      this.setStatus('running', 'ready');
      this.output.appendLine(`[${timestamp()}] server running (Lunil: ready)`);
      if (this.stableTimer !== undefined) {
        clearTimeout(this.stableTimer);
      }
      this.stableTimer = setTimeout(() => { this.restartAttempts = 0; }, 30_000);
    } else if (state === State.Starting) {
      this.setStatus('starting', 'starting');
    } else {
      this.setStatus('stopped', 'stopped');
      this.output.appendLine(`[${timestamp()}] server stopped (stopping=${this.stopping})`);
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
      this.setStatus('error', 'server stopped; restart required');
      void vscode.window.showErrorMessage(
        'Lunil Language Server stopped repeatedly.',
        'Restart'
      ).then(selection => selection === 'Restart' ? this.restart() : undefined);
      return;
    }
    const delay = Math.min(30_000, 500 * (2 ** this.restartAttempts));
    this.restartAttempts++;
    this.setStatus('restarting', `restarting in ${Math.ceil(delay / 1000)}s`);
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

  private async clearCache(): Promise<void> {
    const result = await this.request('lunil/clearCache') as { cleared?: boolean } | undefined;
    if (result === undefined) {
      return;
    }
    vscode.window.setStatusBarMessage(
      result.cleared === true
        ? 'Lunil: analysis cache cleared.'
        : 'Lunil: no analysis cache to clear.',
      4_000);
  }

  private async reindexWorkspace(): Promise<void> {
    const result = await this.request('lunil/reindex') as { modules?: number } | undefined;
    if (result === undefined) {
      return;
    }
    vscode.window.setStatusBarMessage(
      `Lunil: reindexed ${result.modules ?? 0} modules.`,
      4_000);
    await this.refreshIndexedCount();
  }

  /** Adds a Lunil diagnostic code to the workspace suppression setting. */
  private async suppressDiagnostic(code: string): Promise<void> {
    const configuration = vscode.workspace.getConfiguration('lunil');
    const current = configuration.get<string[]>('server.suppressedDiagnosticCodes', []);
    if (current.includes(code)) {
      vscode.window.setStatusBarMessage(`Lunil: ${code} is already suppressed.`, 4_000);
      return;
    }
    const target = vscode.workspace.workspaceFolders !== undefined
      ? vscode.ConfigurationTarget.Workspace
      : vscode.ConfigurationTarget.Global;
    await configuration.update('server.suppressedDiagnosticCodes', [...current, code], target);
    vscode.window.setStatusBarMessage(`Lunil: ${code} suppressed.`, 4_000);
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

  private async showMenu(): Promise<void> {
    interface MenuAction extends vscode.QuickPickItem {
      readonly run: () => unknown;
    }
    const actions: MenuAction[] = [
      {
        label: '$(list-ordered) Show Index Status',
        description: `${this.stateText}${this.lastIndexDetail === '' ? '' : ` · ${this.lastIndexDetail}`}`,
        run: () => vscode.commands.executeCommand('lunil.showIndexStatus')
      },
      { label: '$(sync) Reindex Workspace', run: () => vscode.commands.executeCommand('lunil.reindexWorkspace') },
      { label: '$(refresh) Restart Language Server', run: () => vscode.commands.executeCommand('lunil.restartServer') },
      { label: '$(file-code) Show Virtual Host Contract', run: () => vscode.commands.executeCommand('lunil.showHostContract') },
      { label: '$(trash) Clear Analysis Cache', run: () => vscode.commands.executeCommand('lunil.clearCache') },
      { label: '$(output) Show Output', run: () => vscode.commands.executeCommand('lunil.showOutput') },
      {
        label: '$(settings-gear) Open Settings',
        run: () => vscode.commands.executeCommand('workbench.action.openSettings', '@ext:dlqw.lunil-lua')
      }
    ];
    const pick = await vscode.window.showQuickPick(actions, {
      placeHolder: `Lunil Language Server — ${this.stateText}`
    });
    if (pick !== undefined) {
      void pick.run();
    }
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

  private workDoneProgress(value: LunilWorkDoneProgress): void {
    if (value.kind === 'end') {
      this.indexingFinished();
    }
  }

  private indexingFinished(): void {
    if (this.state === 'indexing') {
      this.setStatus('running', 'ready');
      this.lastIndexDetail = '';
    }
    void this.refreshIndexedCount();
  }

  /** Ready-state status text: workspace folder and indexed module count. */
  private async refreshIndexedCount(): Promise<void> {
    if (this.client === undefined || !this.client.isRunning() || this.state !== 'running') {
      return;
    }
    const showCount = vscode.workspace.getConfiguration('lunil')
      .get<boolean>('statusBar.showModuleCount', true);
    try {
      const status = await this.client.sendRequest('lunil/indexStatus') as { total?: number } | undefined;
      const folderName = vscode.workspace.workspaceFolders?.[0]?.name;
      const count = status?.total ?? 0;
      const parts: string[] = [];
      if (folderName !== undefined && folderName !== '') {
        parts.push(folderName);
      }
      if (showCount) {
        parts.push(`${count} module${count === 1 ? '' : 's'}`);
      }
      this.stateText = parts.length > 0 ? parts.join(' · ') : 'ready';
      this.status.text = statusIcon('running', this.stateText);
      this.status.tooltip = `Lunil: ready · ${folderName ?? 'no folder'} · ${count} indexed` +
        (this.lastIndexDetail === '' ? '' : ` · ${this.lastIndexDetail}`);
    } catch {
      // Index status is best-effort; keep the plain ready text.
    }
  }

  private indexProgress(value: unknown): void {
    const progress = value as IndexProgressPayload;
    const total = progress.total ?? 0;
    const completed = progress.completed ?? 0;
    const detail = [
      `succeeded:${progress.succeeded ?? 0}`,
      `in-progress:${progress.inProgress ?? 0}`,
      `failed:${progress.failed ?? 0}`,
      `pending:${progress.pending ?? 0}`
    ].join(' ');
    const summary = `${completed}/${total} (${detail})`;
    this.lastIndexDetail = summary;
    if (total > 0 && completed >= total) {
      this.output.appendLine(`[${timestamp()}] indexProgress: phase=${progress.phase ?? '?'} ${summary}`);
      this.indexingFinished();
      return;
    }

    const now = Date.now();
    if (now - this.lastIndexUpdate < indexProgressThrottleMs) {
      return;
    }
    this.lastIndexUpdate = now;
    this.output.appendLine(`[${timestamp()}] indexProgress: phase=${progress.phase ?? '?'} ${summary}`);
    this.state = 'indexing';
    this.stateText = progress.phase ?? 'indexing';
    this.status.text = `$(sync~spin) Lunil ${Math.floor(100 * completed / Math.max(total, 1))}%`;
    this.status.tooltip = `Lunil: ${this.stateText} ${summary}`;
  }

  private async showIndexStatus(): Promise<void> {
    this.output.appendLine(`[${timestamp()}] showIndexStatus: client=${this.client !== undefined ? 'exists' : 'none'} running=${this.client?.isRunning() ?? false}`);
    if (this.client === undefined || !this.client.isRunning()) {
      void vscode.window.showWarningMessage('Lunil Language Server is not running.');
      return;
    }
    try {
      const status = await this.client.sendRequest('lunil/indexStatus') as {
        total?: number; analyzed?: number; succeeded?: number; failed?: number;
        inProgress?: number; pending?: number; failedFiles?: string[]; pendingFiles?: string[];
      };
      this.output.appendLine(`[${timestamp()}] indexStatus result: ${JSON.stringify(status)}`);
      const summary = [
        `total:${status.total ?? 0}`,
        `succeeded:${status.succeeded ?? 0}`,
        `in-progress:${status.inProgress ?? 0}`,
        `failed:${status.failed ?? 0}`,
        `pending:${status.pending ?? 0}`
      ].join('  ');
      const failed = status.failedFiles ?? [];
      const pending = status.pendingFiles ?? [];
      const items: IndexStatusItem[] = [];
      if (failed.length > 0) {
        items.push({ label: `$(error) Failed (${failed.length})`, kind: vscode.QuickPickItemKind.Separator });
        for (const file of failed.slice(0, 50)) {
          items.push(indexStatusItem(file, 'failed', 'Analysis threw for this document'));
        }
        if (failed.length > 50) {
          items.push({ label: `... and ${failed.length - 50} more failed files`, kind: vscode.QuickPickItemKind.Separator });
        }
      }
      if (pending.length > 0) {
        items.push({ label: `$(clock) Pending (${pending.length})`, kind: vscode.QuickPickItemKind.Separator });
        for (const file of pending.slice(0, 50)) {
          items.push(indexStatusItem(file, 'pending', 'Not yet analyzed'));
        }
        if (pending.length > 50) {
          items.push({ label: `... and ${pending.length - 50} more pending files`, kind: vscode.QuickPickItemKind.Separator });
        }
      }
      if (items.length === 0) {
        items.push({ label: '$(check) All documents analyzed', detail: summary });
      }
      const pick = await vscode.window.showQuickPick(items, {
        placeHolder: summary,
        matchOnDescription: true,
        matchOnDetail: true
      });
      if (pick?.uri !== undefined) {
        await vscode.window.showTextDocument(pick.uri, { preview: true });
      }
    } catch (error) {
      this.output.appendLine(`[${timestamp()}] showIndexStatus FAILED: ${error instanceof Error ? error.message : String(error)}`);
      this.logError('showIndexStatus', error);
    }
  }

  private setStatus(kind: ServerState, text: string): void {
    this.state = kind;
    this.stateText = text;
    this.status.text = statusIcon(kind, text);
    this.status.tooltip = `Lunil: ${text}`;
    this.status.backgroundColor = kind === 'error'
      ? new vscode.ThemeColor('statusBarItem.errorBackground')
      : undefined;
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

interface LunilWorkDoneProgress {
  readonly kind?: 'begin' | 'report' | 'end';
  readonly title?: string;
  readonly message?: string;
  readonly percentage?: number;
}

interface IndexStatusItem extends vscode.QuickPickItem {
  readonly uri?: vscode.Uri;
}

function indexStatusItem(file: string, description: string, detail: string): IndexStatusItem {
  const uri = vscode.Uri.parse(file);
  return {
    label: file.startsWith('file://') ? vscode.workspace.asRelativePath(uri, false) : file,
    description,
    detail,
    uri: uri.scheme === 'file' ? uri : undefined
  };
}

function statusIcon(kind: ServerState, text: string): string {
  const icon = kind === 'running' ? '$(check)'
    : kind === 'indexing' ? '$(sync~spin)'
    : kind === 'starting' || kind === 'restarting' ? '$(loading~spin)'
    : kind === 'error' ? '$(error)'
    : kind === 'restricted' ? '$(shield)'
    : '$(circle-slash)';
  return `${icon} Lunil${text === 'ready' ? '' : `: ${text}`}`;
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
      trace: configuration.get<string>('server.trace', 'off'),
      suppressedDiagnosticCodes: configuration.get<string[]>('server.suppressedDiagnosticCodes', [])
    }
  };
}

function timestamp(): string {
  return new Date().toISOString();
}
