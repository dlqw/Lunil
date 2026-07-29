using System;
using System.Collections.Generic;
using Lunil.Compiler;
using Lunil.Hosting;
using Lunil.StandardLibrary;
using UnityEngine;

namespace Lunil.Unity
{
    /// <summary>Unity Update/FixedUpdate owner for one engine-neutral Lunil game-loop host.</summary>
    [DisallowMultipleComponent]
    public sealed class LuaUnityGameLoop : MonoBehaviour
    {
        [SerializeField] private LuaScriptAsset _entryScript;
        [SerializeField] private LuaScriptAsset[] _modules = new LuaScriptAsset[0];
        [SerializeField] private bool _startOnEnable = true;
        [SerializeField] private int _maximumDispatchedCallbacks = 1024;

        private LuaUnityDispatcher _dispatcher;
        private LuaGameLoopHost _gameLoop;
        private LuaGameLoopOperation _entryOperation;
        private bool _paused;

        public LuaScriptAsset EntryScript
        {
            get { return _entryScript; }
            set { if (IsInitialized) throw new InvalidOperationException("Shutdown the host before replacing its entry script."); _entryScript = value; }
        }

        public LuaScriptAsset[] Modules
        {
            get { return _modules; }
            set { if (IsInitialized) throw new InvalidOperationException("Shutdown the host before replacing its modules."); _modules = value ?? new LuaScriptAsset[0]; }
        }

        public bool StartOnEnable
        {
            get { return _startOnEnable; }
            set { _startOnEnable = value; }
        }

        public bool IsInitialized
        {
            get { return _gameLoop != null; }
        }

        public LuaGameLoopHost GameLoop
        {
            get
            {
                if (_gameLoop == null) throw new InvalidOperationException("The Unity game-loop host is not initialized.");
                return _gameLoop;
            }
        }

        public LuaGameLoopOperation EntryOperation
        {
            get { return _entryOperation; }
        }

        public event Action<LuaGameLoopTickResult> TickCompleted;
        public event Action<Exception> HostFailed;

        /// <summary>Applies host capabilities and generated bindings before initialization.</summary>
        public Func<LuaGameLoopHostOptions, LuaGameLoopHostOptions> ConfigureHostOptions { get; set; }

        private void OnEnable()
        {
            if (Application.isPlaying && _startOnEnable) Initialize();
        }

        public void Initialize()
        {
            if (_gameLoop != null) return;
            if (_maximumDispatchedCallbacks <= 0)
                throw new InvalidOperationException("The dispatcher callback limit must be positive.");

            var assets = new List<LuaScriptAsset>();
            if (_entryScript != null) assets.Add(_entryScript);
            if (_modules != null) assets.AddRange(_modules);
            var resolver = new LuaUnityAssetResolver(assets);
            _dispatcher = new LuaUnityDispatcher();
            var console = new LuaUnityConsole();
            var hostOptions = LuaHostOptions.Default with
            {
                ExecutionBackend = LuaHostExecutionBackend.Interpreter,
                ModuleResolver = resolver,
                StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Trusted) with
                {
                    FileSystem = resolver
                }
            };
            var gameLoopOptions = new LuaGameLoopHostOptions
            {
                HostOptions = hostOptions,
                Dispatcher = _dispatcher,
                TimeProvider = new LuaUnityTimeProvider(),
                Console = console,
                ModuleResolver = resolver,
                AssetResolver = resolver,
                PersistentStore = new LuaUnityPersistentStore()
            };
            if (ConfigureHostOptions != null)
            {
                gameLoopOptions = ConfigureHostOptions(gameLoopOptions);
                if (gameLoopOptions == null)
                    throw new InvalidOperationException("The Unity host options callback returned null.");
            }
            _gameLoop = new LuaGameLoopHost(gameLoopOptions);
            LuaUnityRuntimeRegistry.Register(this);

            if (_entryScript != null)
            {
                var source = LuaSourceDocument.FromBytes(_entryScript.Bytes.Span, _entryScript.AssetId);
                var compilation = _gameLoop.Host.Compile(source);
                if (!compilation.Succeeded)
                {
                    Shutdown();
                    throw new InvalidOperationException("The Unity Lua entry script did not compile: " +
                        string.Join("; ", compilation.Diagnostics));
                }
                _entryOperation = _gameLoop.Start(compilation);
            }
        }

        private void Update()
        {
            TickUpdate();
        }

        private void FixedUpdate()
        {
            TickFixed();
        }

        public LuaGameLoopTickResult TickUpdate()
        {
            return Tick(false);
        }

        public LuaGameLoopTickResult TickFixed()
        {
            return Tick(true);
        }

        private LuaGameLoopTickResult Tick(bool fixedTick)
        {
            if (_gameLoop == null) Initialize();
            if (_paused) return null;
            try
            {
                _dispatcher.Drain(_maximumDispatchedCallbacks);
                var result = fixedTick ? _gameLoop.TickFixed() : _gameLoop.Tick();
                var handler = TickCompleted;
                if (handler != null) handler(result);
                return result;
            }
            catch (Exception exception)
            {
                var handler = HostFailed;
                if (handler != null) handler(exception);
                else Debug.LogException(exception, this);
                throw;
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            _paused = pauseStatus;
        }

        private void OnDisable()
        {
            Shutdown();
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        public void Shutdown()
        {
            var gameLoop = _gameLoop;
            _gameLoop = null;
            _entryOperation = null;
            LuaUnityRuntimeRegistry.Unregister(this);
            if (_dispatcher != null)
            {
                _dispatcher.Close();
                _dispatcher = null;
            }
            if (gameLoop != null) gameLoop.Dispose();
        }
    }
}
