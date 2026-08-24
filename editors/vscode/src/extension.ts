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
  readonly excluded?: number;
}

const indexProgressThrottleMs = 200;

/** Localized short label for a server progress phase; unknown names pass through. */
function indexPhaseLabel(phase: string | undefined): string {
  const strings = t();
  switch (phase) {
    case 'Loading': return strings.indexPhaseLoading;
    case 'Declarations': return strings.indexPhaseDeclarations;
    case 'Discovery': return strings.indexPhaseDiscovery;
    case 'Resolution': return strings.indexPhaseResolution;
    case 'Analysis': return strings.indexPhaseAnalysis;
    case 'Indexing': return strings.indexPhaseIndexing;
    case 'CacheMaintenance': return strings.indexPhaseCacheMaintenance;
    default: return phase ?? strings.indexing;
  }
}

class LunilClientController implements vscode.Disposable {
  private readonly output = vscode.window.createOutputChannel('Lunil', { log: true });
  private readonly status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 20);
  private readonly hostDocuments = new HostDocumentProvider(
    'lunil-host:/contract.lua',
    '-- No external host contract symbols are indexed.\n');
  private readonly builtinDocuments = new BuiltinDocumentProvider(
    (name: string) => this.fetchBuiltinSource(name));
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
    this.setStatus('stopped', t().statusIdle);
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
      vscode.commands.registerCommand('lunil.searchEverywhere', () => this.searchEverywhere()),
      vscode.commands.registerCommand('lunil.classHierarchy', () => this.classHierarchy()),
      vscode.commands.registerCommand('lunil.findUsages', () => this.findUsages()),
      vscode.commands.registerCommand('lunil._suppressDiagnostic', (code: string) => this.suppressDiagnostic(code)),
      vscode.commands.registerCommand('lunil._openBuiltinLocation', (args: unknown) => this.openBuiltinLocation(args)),
      vscode.commands.registerCommand('lunil._openLocation', (args: unknown) => this.openLocation(args)),
      vscode.workspace.onDidChangeConfiguration(event => this.configurationChanged(event)),
      vscode.workspace.onDidGrantWorkspaceTrust(() => this.start()),
      // VS Code may open builtin/host virtual documents with a plaintext language
      // (its own openers bypass our commands); re-tag them so Lua features apply.
      vscode.workspace.onDidOpenTextDocument(document => ensureVirtualDocumentLanguage(document))
    );
  }

  public async activate(): Promise<void> {
    const folders = vscode.workspace.workspaceFolders?.map(f => f.uri.toString()) ?? [];
    this.output.appendLine(`[${timestamp()}] activate: trusted=${vscode.workspace.isTrusted} folders=[${folders.join(', ')}]`);
    if (!vscode.workspace.isTrusted) {
      this.setStatus('restricted', t().statusRestricted);
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
    this.setStatus('stopped', t().statusStopped);
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
    this.setStatus('starting', t().statusStarting);
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
          { language: 'lua', scheme: 'untitled' },
          { language: 'lua', scheme: 'lunil-builtin' },
          { language: 'lua', scheme: 'lunil-host' }
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
          platform: platformRid(),
          locale: resolveLocaleTag()
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
      this.setStatus('running', t().statusReady);
      this.output.appendLine(`[${timestamp()}] server running (Lunil: ready)`);
      if (this.stableTimer !== undefined) {
        clearTimeout(this.stableTimer);
      }
      this.stableTimer = setTimeout(() => { this.restartAttempts = 0; }, 30_000);
    } else if (state === State.Starting) {
      this.setStatus('starting', t().statusStarting);
    } else {
      this.setStatus('stopped', t().statusStopped);
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
      this.setStatus('error', t().statusRestartRequired);
      void vscode.window.showErrorMessage(
        t().serverStoppedRepeatedly,
        t().restartButton
      ).then(selection => selection === t().restartButton ? this.restart() : undefined);
      return;
    }
    const delay = Math.min(30_000, 500 * (2 ** this.restartAttempts));
    this.restartAttempts++;
    this.setStatus('restarting', t().restartingIn(Math.ceil(delay / 1000)));
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
      void vscode.window.showWarningMessage(t().serverNotRunning);
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
      result.cleared === true ? t().cacheCleared : t().noCacheToClear,
      4_000);
  }

  private async reindexWorkspace(): Promise<void> {
    const result = await this.request('lunil/reindex') as { modules?: number } | undefined;
    if (result === undefined) {
      return;
    }
    vscode.window.setStatusBarMessage(
      t().reindexed(result.modules ?? 0),
      4_000);
    await this.refreshIndexedCount();
  }

  /** Adds a Lunil diagnostic code to the workspace suppression setting. */
  private async suppressDiagnostic(code: string): Promise<void> {
    const configuration = vscode.workspace.getConfiguration('lunil');
    const current = configuration.get<string[]>('server.suppressedDiagnosticCodes', []);
    if (current.includes(code)) {
      vscode.window.setStatusBarMessage(t().alreadySuppressed(code), 4_000);
      return;
    }
    const target = vscode.workspace.workspaceFolders !== undefined
      ? vscode.ConfigurationTarget.Workspace
      : vscode.ConfigurationTarget.Global;
    await configuration.update('server.suppressedDiagnosticCodes', [...current, code], target);
    vscode.window.setStatusBarMessage(t().suppressed(code), 4_000);
  }

  /** Opens a location inside a readonly builtin Lua library page. */
  private async openBuiltinLocation(args: unknown): Promise<void> {
    const location = args as { document?: string; line?: number; character?: number } | undefined;
    const document = await this.openBuiltinDocument(location?.document ?? 'base');
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
      await this.openBuiltinLocation({
        document: builtinDocumentName(uri),
        line: location.line,
        character: location.character
      });
      return;
    }

    const document = await vscode.workspace.openTextDocument(uri);
    const position = new vscode.Position(location.line, location.character ?? 0);
    await vscode.window.showTextDocument(document, { selection: new vscode.Range(position, position) });
  }

  /** A searchable location item for the pickers below. */
  private static locationItem(prefix: string, label: string, description: string | undefined, uri: string, range: { start: { line: number; character: number } }) {
    return {
      label: `${prefix} ${label}`,
      description,
      uri,
      line: range.start.line,
      character: range.start.character
    };
  }

  /**
   * Rider-style symbol search: a fuzzy QuickPick fed by the server's
   * workspace-symbol index, debounced while typing.
   */
  private async searchEverywhere(): Promise<void> {
    if (this.client === undefined || !this.client.isRunning()) {
      void vscode.window.showWarningMessage(t().serverNotRunning);
      return;
    }

    interface SymbolHit {
      name: string;
      kind: number;
      containerName?: string;
      location: { uri: string; range: { start: { line: number; character: number } } };
    }

    const strings = t();
    const pick = vscode.window.createQuickPick<ReturnType<typeof LunilClientController.locationItem>>();
    pick.title = strings.searchTitle;
    pick.placeholder = strings.searchPlaceholder;
    pick.matchOnDescription = true;
    pick.matchOnDetail = true;
    pick.ignoreFocusOut = false;

    let sequence = 0;
    let debounce: ReturnType<typeof setTimeout> | undefined;
    pick.onDidChangeValue(value => {
      if (debounce !== undefined) {
        clearTimeout(debounce);
      }
      const query = value.trim();
      if (query === '') {
        sequence++;
        pick.items = [];
        return;
      }

      const current = ++sequence;
      debounce = setTimeout(async () => {
        pick.busy = true;
        try {
          const hits = await this.client!.sendRequest('workspace/symbol', { query }) as SymbolHit[] | null;
          if (current !== sequence) {
            return;
          }

          pick.items = (hits ?? []).slice(0, 200).map(hit => LunilClientController.locationItem(
            hit.kind === 12 ? '$(symbol-function)' : '$(symbol-field)',
            hit.name,
            hit.containerName,
            hit.location.uri,
            hit.location.range));
          if (pick.items.length === 0) {
            pick.items = [{ label: strings.searchEmpty, uri: '', line: 0, character: 0, description: undefined }];
          }
        } catch (error) {
          this.logError('searchEverywhere', error);
        } finally {
          pick.busy = false;
        }
      }, 200);
    });

    pick.onDidAccept(() => {
      const item = pick.activeItems[0];
      if (item !== undefined && item.uri !== '') {
        void this.openLocation({ uri: item.uri, line: item.line, character: item.character });
        pick.hide();
      }
    });
    pick.onDidHide(() => pick.dispose());
    pick.show();
  }

  /**
   * The class hierarchy at the cursor: base classes and derived classes through
   * annotation declarations and runtime/factory edges, each entry navigable.
   */
  private async classHierarchy(): Promise<void> {
    const editor = vscode.window.activeTextEditor;
    if (editor === undefined || this.client === undefined || !this.client.isRunning()) {
      void vscode.window.showWarningMessage(t().serverNotRunning);
      return;
    }

    interface HierarchyEntry {
      name: string;
      moduleName?: string;
      location?: { uri: string; range: { start: { line: number; character: number } } } | null;
    }

    const strings = t();
    try {
      const position = editor.selection.active;
      const hierarchy = await this.client.sendRequest('lunil/classHierarchy', {
        textDocument: { uri: editor.document.uri.toString() },
        position: { line: position.line, character: position.character }
      }) as { name: string; bases?: HierarchyEntry[]; derived?: HierarchyEntry[] } | null;
      if (hierarchy === null) {
        void vscode.window.showInformationMessage(strings.hierarchyNoClass);
        return;
      }

      const entry = (item: HierarchyEntry, prefix: string) => {
        const location = item.location;
        return location === undefined || location === null
          ? { label: `${prefix} ${item.name}`, description: item.moduleName, uri: '', line: 0, character: 0 }
          : LunilClientController.locationItem(prefix, item.name, item.moduleName, location.uri, location.range);
      };

      const items: ReturnType<typeof LunilClientController.locationItem>[] = [
        ...(hierarchy.bases ?? []).map(item => entry(item, '$(arrow-up)')),
        ...(hierarchy.derived ?? []).map(item => entry(item, '$(arrow-down)'))
      ].map(item => item);
      const pick = vscode.window.createQuickPick<ReturnType<typeof LunilClientController.locationItem>>();
      pick.title = `${strings.hierarchyTitle}: ${hierarchy.name}`;
      pick.placeholder = strings.hierarchyTitle;
      pick.matchOnDescription = true;
      // Section headers separate bases from derived classes.
      const bases = hierarchy.bases ?? [];
      pick.items = bases.length > 0 && (hierarchy.derived ?? []).length > 0
        ? [
            { label: strings.hierarchyBases, kind: vscode.QuickPickItemKind.Separator } as never,
            ...bases.map(item => entry(item, '$(arrow-up)')),
            { label: strings.hierarchyDerived, kind: vscode.QuickPickItemKind.Separator } as never,
            ...(hierarchy.derived ?? []).map(item => entry(item, '$(arrow-down)'))
          ]
        : items;
      pick.onDidAccept(() => {
        const item = pick.activeItems[0];
        if (item !== undefined && item.uri !== '') {
          void this.openLocation({ uri: item.uri, line: item.line, character: item.character });
          pick.hide();
        }
      });
      pick.onDidHide(() => pick.dispose());
      pick.show();
    } catch (error) {
      this.logError('classHierarchy', error);
    }
  }

  /** All references to the symbol under the cursor, listed in a searchable picker. */
  private async findUsages(): Promise<void> {
    const editor = vscode.window.activeTextEditor;
    if (editor === undefined || this.client === undefined || !this.client.isRunning()) {
      void vscode.window.showWarningMessage(t().serverNotRunning);
      return;
    }

    const strings = t();
    try {
      const position = editor.selection.active;
      const references = await this.client.sendRequest('textDocument/references', {
        textDocument: { uri: editor.document.uri.toString() },
        position: { line: position.line, character: position.character },
        context: { includeDeclaration: true }
      }) as { uri: string; range: { start: { line: number; character: number } } }[] | null;
      if (references === null || references.length === 0) {
        void vscode.window.showInformationMessage(strings.usagesNoReferences);
        return;
      }

      const pick = vscode.window.createQuickPick<ReturnType<typeof LunilClientController.locationItem>>();
      pick.title = `${strings.usagesTitle} (${references.length})`;
      pick.matchOnDescription = true;
      pick.items = references.map(reference => LunilClientController.locationItem(
        '$(references)', vscode.workspace.asRelativePath(vscode.Uri.parse(reference.uri), false),
        undefined, reference.uri, reference.range));
      pick.onDidAccept(() => {
        const item = pick.activeItems[0];
        if (item !== undefined) {
          void this.openLocation({ uri: item.uri, line: item.line, character: item.character });
          pick.hide();
        }
      });
      pick.onDidHide(() => pick.dispose());
      pick.show();
    } catch (error) {
      this.logError('findUsages', error);
    }
  }

  /** Fetches one builtin library page (`base`, `math`, ...) from the server. */
  private async fetchBuiltinSource(name: string): Promise<string> {
    if (this.client === undefined || !this.client.isRunning()) {
      return '';
    }

    try {
      const source = await this.client.sendRequest('lunil/builtinSource', { document: name }) as
        { text?: string } | undefined;
      return source?.text ?? '';
    } catch {
      return '';
    }
  }

  private async openBuiltinDocument(name: string): Promise<vscode.TextDocument | undefined> {
    const text = await this.fetchBuiltinSource(name);
    if (text === '') {
      return undefined;
    }

    this.builtinDocuments.update(name, text);
    const document = await vscode.workspace.openTextDocument(
      vscode.Uri.parse(`lunil-builtin:${name}.lua`));
    if (document.languageId !== 'lua') {
      await vscode.languages.setTextDocumentLanguage(document, 'lua');
    }
    return document;
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
        label: `$(list-ordered) ${t().menuIndexStatus}`,
        description: `${this.stateText}${this.lastIndexDetail === '' ? '' : ` · ${this.lastIndexDetail}`}`,
        run: () => vscode.commands.executeCommand('lunil.showIndexStatus')
      },
      { label: `$(sync) ${t().menuReindex}`, run: () => vscode.commands.executeCommand('lunil.reindexWorkspace') },
      { label: `$(refresh) ${t().menuRestart}`, run: () => vscode.commands.executeCommand('lunil.restartServer') },
      { label: `$(file-code) ${t().menuHostContract}`, run: () => vscode.commands.executeCommand('lunil.showHostContract') },
      { label: `$(trash) ${t().menuClearCache}`, run: () => vscode.commands.executeCommand('lunil.clearCache') },
      { label: `$(output) ${t().menuOutput}`, run: () => vscode.commands.executeCommand('lunil.showOutput') },
      {
        label: `$(settings-gear) ${t().menuSettings}`,
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
      this.setStatus('running', t().statusReady);
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
        parts.push(t().modules(count));
      }
      this.stateText = parts.length > 0 ? parts.join(' · ') : t().statusReady;
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
    const phase = progress.phase;
    const detail = [
      `succeeded:${progress.succeeded ?? 0}`,
      `in-progress:${progress.inProgress ?? 0}`,
      `failed:${progress.failed ?? 0}`,
      `pending:${progress.pending ?? 0}`
    ].join(' ');
    const summary = `${completed}/${total} (${detail})`;
    this.lastIndexDetail = summary;
    // Only the terminal phase completes indexing: intermediate phases (loading,
    // declarations, discovery, ...) each reach their own n/total on the way.
    if (phase === 'Completed') {
      this.output.appendLine(`[${timestamp()}] indexProgress: phase=${phase ?? '?'} ${summary}`);
      this.indexingFinished();
      return;
    }

    const now = Date.now();
    if (now - this.lastIndexUpdate < indexProgressThrottleMs) {
      return;
    }
    this.lastIndexUpdate = now;
    this.output.appendLine(`[${timestamp()}] indexProgress: phase=${phase ?? '?'} ${summary}`);
    this.state = 'indexing';
    this.stateText = indexPhaseLabel(phase);
    const percent = total > 0 ? Math.floor(100 * completed / total) : 0;
    const counts = total > 0 ? `${completed}/${total}` : `${completed}`;
    this.status.text = `$(sync~spin) Lunil: ${this.stateText} ${counts}`;
    this.status.tooltip = `Lunil: ${this.stateText} ${counts}` +
      (total > 0 ? ` (${percent}%)` : '') + ` · ${summary}` +
      (progress.excluded === undefined || progress.excluded === 0 ? '' : ` · excluded:${progress.excluded}`);
  }

  private async showIndexStatus(): Promise<void> {
    this.output.appendLine(`[${timestamp()}] showIndexStatus: client=${this.client !== undefined ? 'exists' : 'none'} running=${this.client?.isRunning() ?? false}`);
    if (this.client === undefined || !this.client.isRunning()) {
      void vscode.window.showWarningMessage(t().serverNotRunning);
      return;
    }
    try {
      const pick = vscode.window.createQuickPick<IndexStatusItem>();
      pick.title = t().indexTitle;
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
  readonly excluded?: number;
  readonly failedFiles?: (string | { readonly uri?: string; readonly error?: string })[];
  readonly pendingFiles?: string[];
  readonly excludedFiles?: (string | { readonly uri?: string; readonly reason?: string })[];
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
    `pending:${status.pending ?? 0}`,
    `excluded:${status.excluded ?? 0}`
  ].join('  ');
}

function indexStatusItems(status: IndexStatusResponse): IndexStatusItem[] {
  const strings = t();
  const items: IndexStatusItem[] = [
    { label: `$(sync) ${strings.indexReindexAction}`, description: strings.indexReindexDetail, action: 'reindex' }
  ];
  const failedCount = status.failed ?? 0;
  if (failedCount > 0) {
    items.push({
      label: `$(debug-restart) ${strings.indexRetryFailed(failedCount)}`,
      description: strings.indexRetryFailedDetail,
      action: 'retryFailed'
    });
  }

  const failed = status.failedFiles ?? [];
  const pending = status.pendingFiles ?? [];
  if (failed.length === 0 && pending.length === 0 && (status.succeeded ?? 0) > 0) {
    items.push({ label: `$(check) ${strings.indexAllAnalyzed}`, detail: indexStatusSummary(status) });
  }
  if (failed.length > 0) {
    items.push({ label: strings.indexFailed(failed.length), kind: vscode.QuickPickItemKind.Separator });
    for (const entry of failed.slice(0, 200)) {
      const uri = typeof entry === 'string' ? entry : entry.uri;
      if (uri === undefined) {
        continue;
      }
      const error = typeof entry === 'string' ? undefined : entry.error;
      items.push({
        ...fileStatusItem(uri, 'failed'),
        detail: error === undefined || error === '' ? strings.indexAnalysisFailed : error,
        buttons: [retryFileButton, openFileButton]
      });
    }
    if (failed.length > 200) {
      items.push({ label: strings.indexMoreFailed(failed.length - 200), kind: vscode.QuickPickItemKind.Separator });
    }
  }
  if (pending.length > 0) {
    items.push({ label: strings.indexPending(pending.length), kind: vscode.QuickPickItemKind.Separator });
    for (const file of pending.slice(0, 200)) {
      items.push({
        ...fileStatusItem(file, 'pending'),
        detail: strings.indexPendingDetail,
        buttons: [openFileButton]
      });
    }
    if (pending.length > 200) {
      items.push({ label: strings.indexMorePending(pending.length - 200), kind: vscode.QuickPickItemKind.Separator });
    }
  }
  const excluded = status.excludedFiles ?? [];
  if (excluded.length > 0) {
    items.push({ label: strings.indexExcluded(excluded.length), kind: vscode.QuickPickItemKind.Separator });
    for (const entry of excluded.slice(0, 200)) {
      const uri = typeof entry === 'string' ? entry : entry.uri;
      if (uri === undefined) {
        continue;
      }

      const reason = typeof entry === 'string' ? undefined : entry.reason;
      items.push({
        ...fileStatusItem(uri, 'excluded'),
        detail: reason === 'data' ? strings.indexExcludedData :
          reason === 'pattern' ? strings.indexExcludedPattern :
          strings.indexExcludedDetail,
        buttons: [openFileButton]
      });
    }
    if (excluded.length > 200) {
      items.push({ label: strings.indexMoreExcluded(excluded.length - 200), kind: vscode.QuickPickItemKind.Separator });
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

/**
 * Serves the readonly builtin Lua library pages (`lunil-builtin:base.lua`,
 * `lunil-builtin:math.lua`, ...). VS Code's own definition opener (F12) calls
 * provideTextDocumentContent directly, bypassing our commands, so the provider must
 * be able to fetch each page from the server on demand.
 */
class BuiltinDocumentProvider implements vscode.TextDocumentContentProvider {
  private readonly content = new Map<string, string>();
  private readonly changed = new vscode.EventEmitter<vscode.Uri>();
  public readonly onDidChange = this.changed.event;

  public constructor(private readonly fetchSource: (name: string) => Thenable<string>) {}

  public provideTextDocumentContent(uri: vscode.Uri): string | Thenable<string> {
    const name = builtinDocumentName(uri);
    const cached = this.content.get(name);
    if (cached !== undefined && cached !== '') {
      return cached;
    }

    return this.fetchSource(name).then(text => {
      if (text !== '') {
        this.content.set(name, text);
      }
      return text;
    });
  }

  public update(name: string, content: string): void {
    this.content.set(name, content);
    this.changed.fire(vscode.Uri.parse(`lunil-builtin:${name}.lua`));
  }
}

/** The builtin page name for a `lunil-builtin:<name>.lua` URI. */
function builtinDocumentName(uri: vscode.Uri): string {
  const path = uri.path.replace(/\.lua$/i, '');
  return path === '' ? 'base' : path;
}

/** Keeps virtual Lunil documents tagged as Lua so highlighting and sync apply. */
function ensureVirtualDocumentLanguage(document: vscode.TextDocument): void {
  if ((document.uri.scheme === 'lunil-builtin' || document.uri.scheme === 'lunil-host') &&
      document.languageId !== 'lua') {
    void vscode.languages.setTextDocumentLanguage(document, 'lua');
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
    locale: resolveLocaleTag(),
    hostContractPath: configuration.get<string>('hostContractPath', ''),
    hostContractJson: configuration.get<string>('hostContractJson', ''),
    workspace: {
      library: configuration.get<string[]>('workspace.library', [])
    },
    require: {
      searchPaths: configuration.get<string[]>('require.searchPaths', [])
    },
    analysis: {
      exclude: configuration.get<string[]>('analysis.exclude', []),
      autoDetectDataFiles: configuration.get<boolean>('analysis.autoDetectDataFiles', true),
      classFactories: configuration.get<unknown[]>('analysis.classFactories', [])
    },
    server: {
      trace: configuration.get<string>('server.trace', 'off'),
      suppressedDiagnosticCodes: configuration.get<string[]>('server.suppressedDiagnosticCodes', [])
    }
  };
}

/** The effective locale tag: the `lunil.locale` setting, or the VS Code UI language. */
function resolveLocaleTag(): string {
  const configured = vscode.workspace.getConfiguration('lunil').get<string>('locale', 'auto');
  return configured === 'auto' ? vscode.env.language : configured;
}

interface ClientStrings {
  readonly menuIndexStatus: string;
  readonly menuReindex: string;
  readonly menuRestart: string;
  readonly menuHostContract: string;
  readonly menuClearCache: string;
  readonly menuOutput: string;
  readonly menuSettings: string;
  readonly statusStarting: string;
  readonly statusReady: string;
  readonly statusStopped: string;
  readonly statusIdle: string;
  readonly statusRestricted: string;
  readonly restartingIn: (seconds: number) => string;
  readonly statusRestartRequired: string;
  readonly serverStoppedRepeatedly: string;
  readonly restartButton: string;
  readonly serverNotRunning: string;
  readonly cacheCleared: string;
  readonly noCacheToClear: string;
  readonly reindexed: (modules: number) => string;
  readonly suppressed: (code: string) => string;
  readonly alreadySuppressed: (code: string) => string;
  readonly modules: (count: number) => string;
  readonly indexing: string;
  readonly indexTitle: string;
  readonly indexReindexAction: string;
  readonly indexReindexDetail: string;
  readonly indexRetryFailed: (count: number) => string;
  readonly indexRetryFailedDetail: string;
  readonly indexAllAnalyzed: string;
  readonly indexFailed: (count: number) => string;
  readonly indexPending: (count: number) => string;
  readonly indexPendingDetail: string;
  readonly indexAnalysisFailed: string;
  readonly indexMoreFailed: (count: number) => string;
  readonly indexMorePending: (count: number) => string;
  readonly indexPhaseLoading: string;
  readonly indexPhaseDeclarations: string;
  readonly indexPhaseDiscovery: string;
  readonly indexPhaseResolution: string;
  readonly indexPhaseAnalysis: string;
  readonly indexPhaseIndexing: string;
  readonly indexPhaseCacheMaintenance: string;
  readonly indexExcluded: (count: number) => string;
  readonly indexExcludedDetail: string;
  readonly indexExcludedData: string;
  readonly indexExcludedPattern: string;
  readonly indexMoreExcluded: (count: number) => string;
  readonly tooltipRetryFile: string;
  readonly tooltipOpenFile: string;
  readonly tooltipReindexAll: string;
  readonly tooltipRefresh: string;
  readonly searchTitle: string;
  readonly searchPlaceholder: string;
  readonly searchEmpty: string;
  readonly hierarchyTitle: string;
  readonly hierarchyBases: string;
  readonly hierarchyDerived: string;
  readonly hierarchyNoClass: string;
  readonly usagesTitle: string;
  readonly usagesNoReferences: string;
}

const clientStringsEn: ClientStrings = {
  menuIndexStatus: 'Show Index Status',
  menuReindex: 'Reindex Workspace',
  menuRestart: 'Restart Language Server',
  menuHostContract: 'Show Virtual Host Contract',
  menuClearCache: 'Clear Analysis Cache',
  menuOutput: 'Show Output',
  menuSettings: 'Open Settings',
  statusStarting: 'starting',
  statusReady: 'ready',
  statusStopped: 'stopped',
  statusIdle: 'idle',
  statusRestricted: 'disabled in Restricted Mode',
  restartingIn: seconds => `restarting in ${seconds}s`,
  statusRestartRequired: 'server stopped; restart required',
  serverStoppedRepeatedly: 'Lunil Language Server stopped repeatedly.',
  restartButton: 'Restart',
  serverNotRunning: 'Lunil Language Server is not running.',
  cacheCleared: 'Lunil: analysis cache cleared.',
  noCacheToClear: 'Lunil: no analysis cache to clear.',
  reindexed: modules => `Lunil: reindexed ${modules} modules.`,
  suppressed: code => `Lunil: ${code} suppressed.`,
  alreadySuppressed: code => `Lunil: ${code} is already suppressed.`,
  modules: count => `${count} module${count === 1 ? '' : 's'}`,
  indexing: 'indexing',
  indexTitle: 'Lunil Index Status',
  indexReindexAction: 'Reindex Workspace',
  indexReindexDetail: 'rebuild the index',
  indexRetryFailed: count => `Retry Failed (${count})`,
  indexRetryFailedDetail: 're-analyze failed documents',
  indexAllAnalyzed: 'All documents analyzed',
  indexFailed: count => `Failed (${count})`,
  indexPending: count => `Pending (${count})`,
  indexPendingDetail: 'Queued for analysis; retried automatically after edits or reindex',
  indexAnalysisFailed: 'Analysis failed for this document',
  indexMoreFailed: count => `... and ${count} more failed files`,
  indexMorePending: count => `... and ${count} more pending files`,
  indexPhaseLoading: 'reading files',
  indexPhaseDeclarations: 'scanning declarations',
  indexPhaseDiscovery: 'discovering modules',
  indexPhaseResolution: 'resolving modules',
  indexPhaseAnalysis: 'analyzing',
  indexPhaseIndexing: 'indexing',
  indexPhaseCacheMaintenance: 'maintaining caches',
  indexExcluded: count => `Excluded from analysis (${count})`,
  indexExcludedDetail: 'Excluded from indexing; open the file to analyze it anyway',
  indexExcludedData: 'Auto-detected generated data file (huge table literal, no code)',
  indexExcludedPattern: 'Matches a lunil.analysis.exclude pattern',
  indexMoreExcluded: count => `... and ${count} more excluded files`,
  tooltipRetryFile: 'Retry analyzing this document',
  tooltipOpenFile: 'Open document',
  tooltipReindexAll: 'Reindex workspace',
  tooltipRefresh: 'Refresh index status',
  searchTitle: 'Search Everywhere',
  searchPlaceholder: 'Search classes, functions, and fields across the workspace...',
  searchEmpty: 'No matching symbols',
  hierarchyTitle: 'Class Hierarchy',
  hierarchyBases: 'Base classes',
  hierarchyDerived: 'Derived classes',
  hierarchyNoClass: 'No class found at the cursor',
  usagesTitle: 'Usages',
  usagesNoReferences: 'No references found'
};

const clientStringsZh: ClientStrings = {
  menuIndexStatus: '显示索引状态',
  menuReindex: '重建工作区索引',
  menuRestart: '重启语言服务器',
  menuHostContract: '查看虚拟宿主契约',
  menuClearCache: '清除分析缓存',
  menuOutput: '显示输出',
  menuSettings: '打开设置',
  statusStarting: '启动中',
  statusReady: '就绪',
  statusStopped: '已停止',
  statusIdle: '空闲',
  statusRestricted: '受限模式下已禁用',
  restartingIn: seconds => `${seconds} 秒后重启`,
  statusRestartRequired: '服务器已停止，需要手动重启',
  serverStoppedRepeatedly: 'Lunil 语言服务器反复停止。',
  restartButton: '重启',
  serverNotRunning: 'Lunil 语言服务器未运行。',
  cacheCleared: 'Lunil: 分析缓存已清除。',
  noCacheToClear: 'Lunil: 没有可清除的分析缓存。',
  reindexed: modules => `Lunil: 已重建 ${modules} 个模块的索引。`,
  suppressed: code => `Lunil: 已抑制 ${code}。`,
  alreadySuppressed: code => `Lunil: ${code} 已处于抑制状态。`,
  modules: count => `${count} 个模块`,
  indexing: '索引中',
  indexTitle: 'Lunil 索引状态',
  indexReindexAction: '重建工作区索引',
  indexReindexDetail: '重建索引',
  indexRetryFailed: count => `重试失败项 (${count})`,
  indexRetryFailedDetail: '重新分析失败的文档',
  indexAllAnalyzed: '全部文档已分析完成',
  indexFailed: count => `失败 (${count})`,
  indexPending: count => `等待 (${count})`,
  indexPendingDetail: '已排队等待分析；编辑或重建索引后会自动重试',
  indexAnalysisFailed: '此文档分析失败',
  indexMoreFailed: count => `……另有 ${count} 个失败文件`,
  indexMorePending: count => `……另有 ${count} 个等待文件`,
  indexPhaseLoading: '读取文件',
  indexPhaseDeclarations: '扫描声明',
  indexPhaseDiscovery: '发现模块',
  indexPhaseResolution: '解析模块',
  indexPhaseAnalysis: '分析中',
  indexPhaseIndexing: '索引中',
  indexPhaseCacheMaintenance: '维护缓存',
  indexExcluded: count => `已排除 (${count})`,
  indexExcludedDetail: '已从索引中排除；打开文件仍可单独分析',
  indexExcludedData: '自动检测为生成数据文件（巨型表字面量、无代码）',
  indexExcludedPattern: '匹配 lunil.analysis.exclude 排除模式',
  indexMoreExcluded: count => `……另有 ${count} 个排除文件`,
  tooltipRetryFile: '重试分析此文档',
  tooltipOpenFile: '打开文档',
  tooltipReindexAll: '重建工作区索引',
  tooltipRefresh: '刷新索引状态',
  searchTitle: '全局搜索',
  searchPlaceholder: '搜索工作区的类、函数和字段...',
  searchEmpty: '没有匹配的符号',
  hierarchyTitle: '类层级',
  hierarchyBases: '基类',
  hierarchyDerived: '派生类',
  hierarchyNoClass: '光标处未找到类',
  usagesTitle: '引用',
  usagesNoReferences: '未找到引用'
};

/** Client strings for the effective locale (`lunil.locale`, or the UI language). */
function t(): ClientStrings {
  return resolveLocaleTag().toLowerCase().startsWith('zh') ? clientStringsZh : clientStringsEn;
}

function timestamp(): string {
  return new Date().toISOString();
}
