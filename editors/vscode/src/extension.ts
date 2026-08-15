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
  private readonly hostDocuments = new HostDocumentProvider(
    'lunil-host:/contract.lua',
    '-- No external host contract symbols are indexed.\n');
  private readonly builtinDocuments = new HostDocumentProvider('lunil-builtin:lua');
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
      vscode.workspace.registerTextDocumentContentProvider('lunil-builtin', this.builtinDocuments),
      vscode.commands.registerCommand('lunil.restartServer', () => this.restart()),
      vscode.commands.registerCommand('lunil.clearCache', () => this.clearCache()),
      vscode.commands.registerCommand('lunil.reindexWorkspace', () => this.reindexWorkspace()),
      vscode.commands.registerCommand('lunil.showOutput', () => this.output.show(true)),
      vscode.commands.registerCommand('lunil.showMenu', () => this.showMenu()),
      vscode.commands.registerCommand('lunil.showIndexStatus', () => this.showIndexStatus()),
      vscode.commands.registerCommand('lunil.showHostContract', () => this.showHostContract()),
      vscode.commands.registerCommand('lunil._suppressDiagnostic', (code: string) => this.suppressDiagnostic(code)),
      vscode.commands.registerCommand('lunil._openBuiltinLocation', (args: unknown) => this.openBuiltinLocation(args)),
      vscode.commands.registerCommand('lunil._openLocation', (args: unknown) => this.openLocation(args)),
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
          provideHover: async (document, position, token, next) => {
            const hover = await next(document, position, token);
            if (hover === undefined || hover === null) {
              return hover;
            }

            // Hover cards embed command links (definitions, the builtin library). VS Code
            // only renders command links in markdown that is marked trusted, and only
            // for the commands listed here.
            const trust = { enabledCommands: ['lunil._openLocation', 'lunil._openBuiltinLocation'] };
            const trusted = (contents: vscode.MarkdownString | vscode.MarkedString): vscode.MarkdownString => {
              if (typeof contents === 'string') {
                const wrapped = new vscode.MarkdownString(contents);
                wrapped.isTrusted = trust;
                return wrapped;
              }

              if (contents instanceof vscode.MarkdownString) {
                contents.isTrusted = trust;
                return contents;
              }

              const fromMarked = new vscode.MarkdownString(contents.value);
              fromMarked.isTrusted = trust;
              return fromMarked;
            };

            if (Array.isArray(hover.contents)) {
              hover.contents = hover.contents.map(trusted);
            } else {
              hover.contents = [trusted(hover.contents)];
            }

            return hover;
          },
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

  private async request(method: string, params?: unknown): Promise<unknown> {
    if (this.client === undefined || !this.client.isRunning()) {
      void vscode.window.showWarningMessage('Lunil Language Server is not running.');
      return undefined;
    }
    try {
      return await this.client.sendRequest(method, params);
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

  /** Opens a location inside the readonly builtin Lua library document. */
  private async openBuiltinLocation(args: unknown): Promise<void> {
    const location = args as { line?: number; character?: number } | undefined;
    const document = await this.openBuiltinDocument();
    if (document !== undefined && location?.line !== undefined) {
      const position = new vscode.Position(location.line, location.character ?? 0);
      await vscode.window.showTextDocument(document, { selection: new vscode.Range(position, position) });
    }
  }

  /** Opens a workspace location referenced from a hover card link. */
  private async openLocation(args: unknown): Promise<void> {
    const location = args as { uri?: string; line?: number; character?: number } | undefined;
    if (location?.uri === undefined || location.line === undefined) {
      return;
    }

    const uri = vscode.Uri.parse(location.uri);
    if (uri.scheme === 'lunil-builtin') {
      await this.openBuiltinLocation(location);
      return;
    }

    const document = await vscode.workspace.openTextDocument(uri);
    const position = new vscode.Position(location.line, location.character ?? 0);
    await vscode.window.showTextDocument(document, { selection: new vscode.Range(position, position) });
  }

  private async openBuiltinDocument(): Promise<vscode.TextDocument | undefined> {
    if (this.client === undefined || !this.client.isRunning()) {
      return undefined;
    }

    try {
      const source = await this.client.sendRequest('lunil/builtinSource') as
        { uri?: string; languageId?: string; text?: string } | undefined;
      if (source?.uri === undefined || source.text === undefined) {
        return undefined;
      }

      this.builtinDocuments.update(source.text);
      const document = await vscode.workspace.openTextDocument(vscode.Uri.parse(source.uri));
      await vscode.languages.setTextDocumentLanguage(document, source.languageId ?? 'lua');
      return document;
    } catch (error) {
      this.logError('openBuiltinDocument', error);
      return undefined;
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
      const pick = vscode.window.createQuickPick<IndexStatusItem>();
      pick.title = 'Lunil Index Status';
      pick.matchOnDescription = true;
      pick.matchOnDetail = true;
      pick.buttons = [reindexAllButton, refreshStatusButton];
      pick.busy = true;
      pick.show();
      pick.onDidHide(() => pick.dispose());
      pick.onDidTriggerButton(button => {
        if (button === reindexAllButton) {
          void this.runIndexStatusAction(pick, 'reindex');
        } else if (button === refreshStatusButton) {
          void this.refreshIndexStatusItems(pick);
        }
      });
      pick.onDidTriggerItemButton(event => {
        const item = event.item;
        if (event.button === retryFileButton) {
          void this.runIndexStatusAction(pick, 'retryFile', item);
        } else if (event.button === openFileButton && item.fileUri !== undefined) {
          void vscode.window.showTextDocument(item.fileUri, { preview: true });
        }
      });
      pick.onDidAccept(() => {
        const item = pick.selectedItems[0];
        if (item === undefined) {
          return;
        }
        if (item.action === 'reindex' || item.action === 'retryFailed') {
          void this.runIndexStatusAction(pick, item.action);
        } else if (item.fileUri !== undefined) {
          void vscode.window.showTextDocument(item.fileUri, { preview: true });
        }
      });
      await this.refreshIndexStatusItems(pick);
    } catch (error) {
      this.output.appendLine(`[${timestamp()}] showIndexStatus FAILED: ${error instanceof Error ? error.message : String(error)}`);
      this.logError('showIndexStatus', error);
    }
  }

  /** Runs an index-status action and reloads the picker contents afterwards. */
  private async runIndexStatusAction(
    pick: vscode.QuickPick<IndexStatusItem>,
    action: 'reindex' | 'retryFailed' | 'retryFile',
    item?: IndexStatusItem): Promise<void> {
    pick.busy = true;
    try {
      if (action === 'reindex') {
        await this.request('lunil/reindex');
        await this.refreshIndexedCount();
      } else if (action === 'retryFailed') {
        await this.request('lunil/reindex', { retryFailed: true });
      } else if (item?.fileUri !== undefined) {
        await this.request('lunil/reindex', { files: [item.fileUri.toString()] });
      }
      await this.refreshIndexStatusItems(pick);
    } catch {
      // request() already logged; keep the picker usable.
      pick.busy = false;
    }
  }

  private async refreshIndexStatusItems(pick: vscode.QuickPick<IndexStatusItem>): Promise<void> {
    pick.busy = true;
    try {
      const status = await this.client!.sendRequest('lunil/indexStatus') as IndexStatusResponse;
      this.output.appendLine(`[${timestamp()}] indexStatus result: ${JSON.stringify(status)}`);
      pick.placeholder = indexStatusSummary(status);
      pick.items = indexStatusItems(status);
    } finally {
      pick.busy = false;
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

interface IndexStatusResponse {
  readonly total?: number;
  readonly analyzed?: number;
  readonly succeeded?: number;
  readonly failed?: number;
  readonly inProgress?: number;
  readonly pending?: number;
  readonly failedFiles?: (string | { readonly uri?: string; readonly error?: string })[];
  readonly pendingFiles?: string[];
}

interface IndexStatusItem extends vscode.QuickPickItem {
  readonly action?: 'reindex' | 'retryFailed';
  readonly fileUri?: vscode.Uri;
}

const retryFileButton: vscode.QuickInputButton = {
  iconPath: new vscode.ThemeIcon('debug-restart'),
  tooltip: 'Retry analyzing this document'
};

const openFileButton: vscode.QuickInputButton = {
  iconPath: new vscode.ThemeIcon('go-to-file'),
  tooltip: 'Open document'
};

const reindexAllButton: vscode.QuickInputButton = {
  iconPath: new vscode.ThemeIcon('sync'),
  tooltip: 'Reindex workspace'
};

const refreshStatusButton: vscode.QuickInputButton = {
  iconPath: new vscode.ThemeIcon('refresh'),
  tooltip: 'Refresh index status'
};

function indexStatusSummary(status: IndexStatusResponse): string {
  return [
    `total:${status.total ?? 0}`,
    `succeeded:${status.succeeded ?? 0}`,
    `in-progress:${status.inProgress ?? 0}`,
    `failed:${status.failed ?? 0}`,
    `pending:${status.pending ?? 0}`
  ].join('  ');
}

function indexStatusItems(status: IndexStatusResponse): IndexStatusItem[] {
  const items: IndexStatusItem[] = [
    { label: '$(sync) Reindex Workspace', description: 'rebuild the index', action: 'reindex' }
  ];
  const failedCount = status.failed ?? 0;
  if (failedCount > 0) {
    items.push({
      label: `$(debug-restart) Retry Failed (${failedCount})`,
      description: 're-analyze failed documents',
      action: 'retryFailed'
    });
  }

  const failed = status.failedFiles ?? [];
  const pending = status.pendingFiles ?? [];
  if (failed.length === 0 && pending.length === 0 && (status.succeeded ?? 0) > 0) {
    items.push({ label: '$(check) All documents analyzed', detail: indexStatusSummary(status) });
  }
  if (failed.length > 0) {
    items.push({ label: `Failed (${failed.length})`, kind: vscode.QuickPickItemKind.Separator });
    for (const entry of failed.slice(0, 200)) {
      const uri = typeof entry === 'string' ? entry : entry.uri;
      if (uri === undefined) {
        continue;
      }
      const error = typeof entry === 'string' ? undefined : entry.error;
      items.push({
        ...fileStatusItem(uri, 'failed'),
        detail: error === undefined || error === '' ? 'Analysis failed for this document' : error,
        buttons: [retryFileButton, openFileButton]
      });
    }
    if (failed.length > 200) {
      items.push({ label: `... and ${failed.length - 200} more failed files`, kind: vscode.QuickPickItemKind.Separator });
    }
  }
  if (pending.length > 0) {
    items.push({ label: `Pending (${pending.length})`, kind: vscode.QuickPickItemKind.Separator });
    for (const file of pending.slice(0, 200)) {
      items.push({
        ...fileStatusItem(file, 'pending'),
        detail: 'Queued for analysis; retried automatically after edits or reindex',
        buttons: [openFileButton]
      });
    }
    if (pending.length > 200) {
      items.push({ label: `... and ${pending.length - 200} more pending files`, kind: vscode.QuickPickItemKind.Separator });
    }
  }
  return items;
}

function fileStatusItem(file: string, description: string): IndexStatusItem {
  const uri = vscode.Uri.parse(file);
  return {
    label: file.startsWith('file://') ? vscode.workspace.asRelativePath(uri, false) : file,
    description,
    fileUri: uri.scheme === 'file' ? uri : undefined
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
  private readonly documentUri: string;
  private content: string;
  private readonly changed = new vscode.EventEmitter<vscode.Uri>();
  public readonly onDidChange = this.changed.event;

  public constructor(documentUri: string, initialContent = '') {
    this.documentUri = documentUri;
    this.content = initialContent;
  }

  public provideTextDocumentContent(): string {
    return this.content;
  }

  public update(content: string): void {
    this.content = content;
    this.changed.fire(vscode.Uri.parse(this.documentUri));
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
