using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SpaceEngineers.Game;
using Torch.API;
using Torch.API.Managers;
using Torch.API.ModAPI;
using Torch.Commands;
using Torch.Managers;
using Torch.Utils;
using VRage.Collections;
using VRage.FileSystem;
using VRage.Game.ObjectBuilder;
using VRage.ObjectBuilders;
using VRage.Plugins;
using VRage.Scripting;
using VRage.Utils;

namespace Torch
{
    /// <summary>
    /// Base class for code shared between the Torch client and server.
    /// </summary>
    public abstract class TorchBase : ViewModel, ITorchBase, IPlugin
    {
        static TorchBase()
        {
            // We can safely never detach this since we don't reload assemblies.
            new ReflectedManager().Attach();
        }

        /// <summary>
        /// Hack because *keen*.
        /// Use only if necessary, prefer dependency injection.
        /// </summary>
        public static ITorchBase Instance { get; private set; }
        /// <inheritdoc />
        public ITorchConfig Config { get; protected set; }
        /// <inheritdoc />
        public Version TorchVersion { get; protected set; }
        /// <inheritdoc />
        public Version GameVersion { get; private set; }
        /// <inheritdoc />
        public string[] RunArgs { get; set; }
        /// <inheritdoc />
        public IPluginManager Plugins { get; protected set; }
        /// <inheritdoc />
        public IMultiplayerManager Multiplayer { get; protected set; }
        /// <inheritdoc />
        public EntityManager Entities { get; protected set; }
        /// <inheritdoc />
        public INetworkManager Network { get; protected set; }
        /// <inheritdoc />
        public CommandManager Commands { get; protected set; }
        /// <inheritdoc />
        public event Action SessionLoading;
        /// <inheritdoc />
        public event Action SessionLoaded;
        /// <inheritdoc />
        public event Action SessionUnloading;
        /// <inheritdoc />
        public event Action SessionUnloaded;

        /// <summary>
        /// Common log for the Torch instance.
        /// </summary>
        protected static Logger Log { get; } = LogManager.GetLogger("Torch");

        /// <inheritdoc/>
        public IDependencyManager Managers { get; }

        private bool _init;

        /// <summary>
        /// 
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if a TorchBase instance already exists.</exception>
        protected TorchBase()
        {
            if (Instance != null)
                throw new InvalidOperationException("A TorchBase instance already exists.");

            Instance = this;

            TorchVersion = Assembly.GetExecutingAssembly().GetName().Version; 
            RunArgs = new string[0];

            Managers = new DependencyManager();

            Plugins = new PluginManager(this);
            Multiplayer = new MultiplayerManager(this);
            Entities = new EntityManager(this);
            Network = new NetworkManager(this);
            Commands = new CommandManager(this);

            Managers.AddManager(new FilesystemManager(this));
            Managers.AddManager(new UpdateManager(this));
            Managers.AddManager(Network);
            Managers.AddManager(Commands);
            Managers.AddManager(Plugins);
            Managers.AddManager(Multiplayer);
            Managers.AddManager(Entities);
            Managers.AddManager(new ChatManager(this));


            TorchAPI.Instance = this;
        }

        /// <inheritdoc />
        public T GetManager<T>() where T : class, IManager
        {
            return Managers.GetManager<T>();
        }

        /// <inheritdoc />
        public bool AddManager<T>(T manager) where T : class, IManager
        {
            return Managers.AddManager(manager);
        }

        public bool IsOnGameThread()
        {
            return Thread.CurrentThread.ManagedThreadId == MySandboxGame.Static.UpdateThread.ManagedThreadId;
        }

        public Task SaveGameAsync(Action<SaveGameStatus> callback)
        {
            Log.Info("Saving game");

            if (!MySandboxGame.IsGameReady)
            {
                callback?.Invoke(SaveGameStatus.GameNotReady);
            }
            else if(MyAsyncSaving.InProgress)
            {
                callback?.Invoke(SaveGameStatus.SaveInProgress);
            }
            else
            {
                var e = new AutoResetEvent(false);
                MyAsyncSaving.Start(() => e.Set());

                return Task.Run(() =>
                {
                    callback?.Invoke(e.WaitOne(5000) ? SaveGameStatus.Success : SaveGameStatus.TimedOut);
                    e.Dispose();
                });
            }

            return Task.CompletedTask;
        }

        #region Game Actions

        /// <summary>
        /// Invokes an action on the game thread.
        /// </summary>
        /// <param name="action"></param>
        public void Invoke(Action action)
        {
            MySandboxGame.Static.Invoke(action);
        }

        /// <summary>
        /// Invokes an action on the game thread asynchronously.
        /// </summary>
        /// <param name="action"></param>
        public Task InvokeAsync(Action action)
        {
            if (Thread.CurrentThread == MySandboxGame.Static.UpdateThread)
            {
                Debug.Assert(false, $"{nameof(InvokeAsync)} should not be called on the game thread.");
                action?.Invoke();
                return Task.CompletedTask;
            }

            return Task.Run(() => InvokeBlocking(action));
        }

        /// <summary>
        /// Invokes an action on the game thread and blocks until it is completed.
        /// </summary>
        /// <param name="action"></param>
        public void InvokeBlocking(Action action)
        {
            if (action == null)
                return;

            if (Thread.CurrentThread == MySandboxGame.Static.UpdateThread)
            {
                Debug.Assert(false, $"{nameof(InvokeBlocking)} should not be called on the game thread.");
                action.Invoke();
                return;
            }

            var e = new AutoResetEvent(false);

            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    action.Invoke();
                }
                finally
                {
                    e.Set();
                }
            });

            if (!e.WaitOne(60000))
                throw new TimeoutException("The game action timed out.");
        }

        #endregion

        /// <inheritdoc />
        public virtual void Init()
        {
            Debug.Assert(!_init, "Torch instance is already initialized.");
            SpaceEngineersGame.SetupBasicGameInfo();
            SpaceEngineersGame.SetupPerGameSettings();

            TorchVersion = Assembly.GetEntryAssembly().GetName().Version;
            GameVersion = new Version(new MyVersion(MyPerGameSettings.BasicGameInfo.GameVersion.Value).FormattedText.ToString().Replace("_", "."));
            var verInfo = $"{Config.InstanceName} - Torch {TorchVersion}, SE {GameVersion}";
            try { Console.Title = verInfo; }
            catch { 
                ///Running as service 
            }

#if DEBUG
            Log.Info("DEBUG");
#else
            Log.Info("RELEASE");
#endif
            Log.Info(verInfo);
            Log.Info($"Executing assembly: {Assembly.GetEntryAssembly().FullName}");
            Log.Info($"Executing directory: {AppDomain.CurrentDomain.BaseDirectory}");

            MySession.OnLoading += OnSessionLoading;
            MySession.AfterLoading += OnSessionLoaded;
            MySession.OnUnloading += OnSessionUnloading;
            MySession.OnUnloaded += OnSessionUnloaded;
            RegisterVRagePlugin();
            Managers.Attach();
            _init = true;
        }

        private void OnSessionLoading()
        {
            Log.Debug("Session loading");
            SessionLoading?.Invoke();
        }

        private void OnSessionLoaded()
        {
            Log.Debug("Session loaded");
            SessionLoaded?.Invoke();
        }

        private void OnSessionUnloading()
        {
            Log.Debug("Session unloading");
            SessionUnloading?.Invoke();
        }

        private void OnSessionUnloaded()
        {
            Log.Debug("Session unloaded");
            SessionUnloaded?.Invoke();
        }

        /// <summary>
        /// Hook into the VRage plugin system for updates.
        /// </summary>
        private void RegisterVRagePlugin()
        {
            var fieldName = "m_plugins";
            var pluginList = typeof(MyPlugins).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as List<IPlugin>;
            if (pluginList == null)
                throw new TypeLoadException($"{fieldName} field not found in {nameof(MyPlugins)}");

            pluginList.Add(this);
        }

        /// <inheritdoc/>
        public virtual Task Save(long callerId)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc/> 
        public virtual void Start()
        {
            
        }

        /// <inheritdoc />
        public virtual void Stop()
        {
            
        }

        /// <inheritdoc />
        public virtual void Restart()
        {
            
        }

        /// <inheritdoc />
        public virtual void Dispose()
        {
            Managers.Detach();
        }

        /// <inheritdoc />
        public virtual void Init(object gameInstance)
        {

        }

        /// <inheritdoc />
        public virtual void Update()
        {
            Plugins.UpdatePlugins();
        }
    }
}
