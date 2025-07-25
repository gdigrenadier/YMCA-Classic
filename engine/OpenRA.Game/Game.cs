#region Copyright & License Information
/*
 * Copyright 2007-2020 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.Graphics;
using OpenRA.Network;
using OpenRA.Primitives;
using OpenRA.Server;
using OpenRA.Support;
using OpenRA.Widgets;

namespace OpenRA
{
	public static class Game
	{
		public const int NetTickScale = 3; // 120 ms net tick for 40 ms local tick
		public const int Timestep = 40;
		public const int TimestepJankThreshold = 250; // Don't catch up for delays larger than 250ms

		public static InstalledMods Mods { get; private set; }
		public static ExternalMods ExternalMods { get; private set; }

		public static ModData ModData;
		public static Settings Settings;
		public static CursorManager Cursor;
		public static bool HideCursor;
		static WorldRenderer worldRenderer;
		static string modLaunchWrapper;

		internal static OrderManager OrderManager;
		static Server.Server server;

		public static MersenneTwister CosmeticRandom = new MersenneTwister(); // not synced

		public static Renderer Renderer;
		public static Sound Sound;

		public static string EngineVersion { get; private set; }
		public static LocalPlayerProfile LocalPlayerProfile;

		static Task discoverNat;
		static bool takeScreenshot = false;
		static Benchmark benchmark = null;

		public static event Action OnShellmapLoaded = () => { };

		public static OrderManager JoinServer(ConnectionTarget endpoint, string password, bool recordReplay = true)
		{
			var connection = new NetworkConnection(endpoint);
			if (recordReplay)
				connection.StartRecording(() => { return TimestampedFilename(); });

			var om = new OrderManager(endpoint, password, connection);
			JoinInner(om);
			return om;
		}

		public static string TimestampedFilename(bool includemilliseconds = false, string extra = "")
		{
			var format = includemilliseconds ? "yyyy-MM-ddTHHmmssfffZ" : "yyyy-MM-ddTHHmmssZ";
			return ModData.Manifest.Id + extra + "-" + DateTime.UtcNow.ToString(format, CultureInfo.InvariantCulture);
		}

		static void JoinInner(OrderManager om)
		{
			OrderManager?.Dispose();
			OrderManager = om;
			lastConnectionState = ConnectionState.PreConnecting;
			ConnectionStateChanged(OrderManager);
		}

		public static void JoinReplay(string replayFile)
		{
			JoinInner(new OrderManager(new ConnectionTarget(), "", new ReplayConnection(replayFile)));
		}

		static void JoinLocal()
		{
			JoinInner(new OrderManager(new ConnectionTarget(), "", new EchoConnection()));
		}

		// More accurate replacement for Environment.TickCount
		static Stopwatch stopwatch = Stopwatch.StartNew();
		public static long RunTime { get { return stopwatch.ElapsedMilliseconds; } }

		public static int RenderFrame = 0;
		public static int NetFrameNumber { get { return OrderManager.NetFrameNumber; } }
		public static int LocalTick { get { return OrderManager.LocalFrameNumber; } }

		public static event Action<ConnectionTarget> OnRemoteDirectConnect = _ => { };
		public static event Action<OrderManager> ConnectionStateChanged = _ => { };
		static ConnectionState lastConnectionState = ConnectionState.PreConnecting;
		public static int LocalClientId { get { return OrderManager.Connection.LocalClientId; } }

		public static void RemoteDirectConnect(ConnectionTarget endpoint)
		{
			OnRemoteDirectConnect(endpoint);
		}

		// Hacky workaround for orderManager visibility
		public static Widget OpenWindow(World world, string widget)
		{
			return Ui.OpenWindow(widget, new WidgetArgs() { { "world", world }, { "orderManager", OrderManager }, { "worldRenderer", worldRenderer } });
		}

		// Who came up with the great idea of making these things
		// impossible for the things that want them to access them directly?
		public static Widget OpenWindow(string widget, WidgetArgs args)
		{
			return Ui.OpenWindow(widget, new WidgetArgs(args)
			{
				{ "world", worldRenderer.World },
				{ "orderManager", OrderManager },
				{ "worldRenderer", worldRenderer },
			});
		}

		// Load a widget with world, orderManager, worldRenderer args, without adding it to the widget tree
		public static Widget LoadWidget(World world, string id, Widget parent, WidgetArgs args)
		{
			return ModData.WidgetLoader.LoadWidget(new WidgetArgs(args)
			{
				{ "world", world },
				{ "orderManager", OrderManager },
				{ "worldRenderer", worldRenderer },
			}, parent, id);
		}

		public static event Action LobbyInfoChanged = () => { };

		internal static void SyncLobbyInfo()
		{
			LobbyInfoChanged();
		}

		public static event Action BeforeGameStart = () => { };
		internal static void StartGame(string mapUID, WorldType type)
		{
			// Dispose of the old world before creating a new one.
			worldRenderer?.Dispose();

			Cursor.SetCursor(null);
			BeforeGameStart();

			Map map;

			using (new PerfTimer("PrepareMap"))
				map = ModData.PrepareMap(mapUID);
			using (new PerfTimer("NewWorld"))
				OrderManager.World = new World(ModData, map, OrderManager, type);

			OrderManager.World.GameOver += FinishBenchmark;

			worldRenderer = new WorldRenderer(ModData, OrderManager.World);

			// Proactively collect memory during loading to reduce peak memory.
			GC.Collect();

			using (new PerfTimer("LoadComplete"))
				OrderManager.World.LoadComplete(worldRenderer);

			// Proactively collect memory during loading to reduce peak memory.
			GC.Collect();

			if (OrderManager.GameStarted)
				return;

			Ui.MouseFocusWidget = null;
			Ui.KeyboardFocusWidget = null;

			OrderManager.LocalFrameNumber = 0;
			OrderManager.LastTickTime = RunTime;
			OrderManager.StartGame();
			worldRenderer.RefreshPalette();
			Cursor.SetCursor("default");

			// Now loading is completed, now is the ideal time to run a GC and compact the LOH.
			// - All the temporary garbage created during loading can be collected.
			// - Live objects are likely to live for the length of the game or longer,
			//   thus promoting them into a higher generation is not an issue.
			// - We can remove any fragmentation in the LOH caused by temporary loading garbage.
			// - A loading screen is visible, so a delay won't matter to the user.
			//   Much better to clean up now then to drop frames during gameplay for GC pauses.
			GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
			GC.Collect();
		}

		public static void RestartGame()
		{
			var replay = OrderManager.Connection as ReplayConnection;
			var replayName = replay != null ? replay.Filename : null;
			var lobbyInfo = OrderManager.LobbyInfo;

			// Reseed the RNG so this isn't an exact repeat of the last game
			lobbyInfo.GlobalSettings.RandomSeed = CosmeticRandom.Next();

			var orders = new[]
			{
					Order.Command("sync_lobby {0}".F(lobbyInfo.Serialize())),
					Order.Command("startgame")
			};

			// Disconnect from the current game
			Disconnect();
			Ui.ResetAll();

			// Restart the game with the same replay/mission
			if (replay != null)
				JoinReplay(replayName);
			else
				CreateAndStartLocalServer(lobbyInfo.GlobalSettings.Map, orders);
		}

		public static void CreateAndStartLocalServer(string mapUID, IEnumerable<Order> setupOrders)
		{
			OrderManager om = null;

			Action lobbyReady = null;
			lobbyReady = () =>
			{
				LobbyInfoChanged -= lobbyReady;
				foreach (var o in setupOrders)
					om.IssueOrder(o);
			};

			LobbyInfoChanged += lobbyReady;

			om = JoinServer(CreateLocalServer(mapUID), "");
		}

		public static bool IsHost
		{
			get
			{
				var id = OrderManager.Connection.LocalClientId;
				var client = OrderManager.LobbyInfo.ClientWithIndex(id);
				return client != null && client.IsAdmin;
			}
		}

		static Modifiers modifiers;
		public static Modifiers GetModifierKeys() { return modifiers; }
		internal static void HandleModifierKeys(Modifiers mods) { modifiers = mods; }

		public static void InitializeSettings(Arguments args)
		{
			Settings = new Settings(Path.Combine(Platform.SupportDir, "settings.yaml"), args);
		}

		public static RunStatus InitializeAndRun(string[] args)
		{
			Initialize(new Arguments(args));

			// Proactively collect memory during loading to reduce peak memory.
			GC.Collect();
			return Run();
		}

		static void Initialize(Arguments args)
		{
			var engineDirArg = args.GetValue("Engine.EngineDir", null);
			if (!string.IsNullOrEmpty(engineDirArg))
				Platform.OverrideEngineDir(engineDirArg);

			var supportDirArg = args.GetValue("Engine.SupportDir", null);
			if (!string.IsNullOrEmpty(supportDirArg))
				Platform.OverrideSupportDir(supportDirArg);

			Console.WriteLine("Platform is {0}", Platform.CurrentPlatform);

			// Load the engine version as early as possible so it can be written to exception logs
			try
			{
				EngineVersion = File.ReadAllText(Path.Combine(Platform.EngineDir, "VERSION")).Trim();
			}
			catch { }

			if (string.IsNullOrEmpty(EngineVersion))
				EngineVersion = "Unknown";

			Console.WriteLine("Engine version is {0}", EngineVersion);

			// Special case handling of Game.Mod argument: if it matches a real filesystem path
			// then we use this to override the mod search path, and replace it with the mod id
			var modID = args.GetValue("Game.Mod", null);
			var explicitModPaths = new string[0];
			if (modID != null && (File.Exists(modID) || Directory.Exists(modID)))
			{
				explicitModPaths = new[] { modID };
				modID = Path.GetFileNameWithoutExtension(modID);
			}

			InitializeSettings(args);

			Log.AddChannel("perf", "perf.log");
			Log.AddChannel("debug", "debug.log");
			Log.AddChannel("server", "server.log", true);
			Log.AddChannel("sound", "sound.log");
			Log.AddChannel("graphics", "graphics.log");
			Log.AddChannel("geoip", "geoip.log");
			Log.AddChannel("nat", "nat.log");
			Log.AddChannel("client", "client.log");

			var platforms = new[] { Settings.Game.Platform, "Default", null };
			foreach (var p in platforms)
			{
				if (p == null)
					throw new InvalidOperationException("Failed to initialize platform-integration library. Check graphics.log for details.");

				Settings.Game.Platform = p;
				try
				{
					var rendererPath = Path.Combine(Platform.BinDir, "OpenRA.Platforms." + p + ".dll");
					var assembly = Assembly.LoadFile(rendererPath);

					var platformType = assembly.GetTypes().SingleOrDefault(t => typeof(IPlatform).IsAssignableFrom(t));
					if (platformType == null)
						throw new InvalidOperationException("Platform dll must include exactly one IPlatform implementation.");

					var platform = (IPlatform)platformType.GetConstructor(Type.EmptyTypes).Invoke(null);
					Renderer = new Renderer(platform, Settings.Graphics);
					Sound = new Sound(platform, Settings.Sound);

					break;
				}
				catch (Exception e)
				{
					Log.Write("graphics", "{0}", e);
					Console.WriteLine("Renderer initialization failed. Check graphics.log for details.");

					Renderer?.Dispose();

					Sound?.Dispose();
				}
			}

			if (Settings.Server.DiscoverNatDevices)
				discoverNat = UPnP.DiscoverNatDevices(Settings.Server.NatDiscoveryTimeout);

			var modSearchArg = args.GetValue("Engine.ModSearchPaths", null);
			var modSearchPaths = modSearchArg != null ?
				FieldLoader.GetValue<string[]>("Engine.ModsPath", modSearchArg) :
				new[] { Path.Combine(Platform.EngineDir, "mods") };

			Mods = new InstalledMods(modSearchPaths, explicitModPaths);
			Console.WriteLine("Internal mods:");
			foreach (var mod in Mods)
				Console.WriteLine("\t{0}: {1} ({2})", mod.Key, mod.Value.Metadata.Title, mod.Value.Metadata.Version);

			modLaunchWrapper = args.GetValue("Engine.LaunchWrapper", null);

			ExternalMods = new ExternalMods();

			if (modID != null && Mods.TryGetValue(modID, out _))
			{
				var launchPath = args.GetValue("Engine.LaunchPath", null);
				var launchArgs = new List<string>();

				// Sanitize input from platform-specific launchers
				// Process.Start requires paths to not be quoted, even if they contain spaces
				if (launchPath != null && launchPath.First() == '"' && launchPath.Last() == '"')
					launchPath = launchPath.Substring(1, launchPath.Length - 2);

				if (launchPath == null)
				{
					// When launching the assembly directly we must propagate the Engine.EngineDir argument if defined
					// Platform-specific launchers are expected to manage this internally.
					launchPath = Assembly.GetEntryAssembly().Location;
					if (!string.IsNullOrEmpty(engineDirArg))
						launchArgs.Add("Engine.EngineDir=\"" + engineDirArg + "\"");
				}

				ExternalMods.Register(Mods[modID], launchPath, launchArgs, ModRegistration.User);

				if (ExternalMods.TryGetValue(ExternalMod.MakeKey(Mods[modID]), out var activeMod))
					ExternalMods.ClearInvalidRegistrations(activeMod, ModRegistration.User);
			}

			Console.WriteLine("External mods:");
			foreach (var mod in ExternalMods)
				Console.WriteLine("\t{0}: {1} ({2})", mod.Key, mod.Value.Title, mod.Value.Version);

			InitializeMod(modID, args);
		}

		public static void InitializeMod(string mod, Arguments args)
		{
			// Clear static state if we have switched mods
			LobbyInfoChanged = () => { };
			ConnectionStateChanged = om => { };
			BeforeGameStart = () => { };
			OnRemoteDirectConnect = endpoint => { };
			delayedActions = new ActionQueue();

			Ui.ResetAll();

			worldRenderer?.Dispose();
			worldRenderer = null;
			server?.Shutdown();
			OrderManager?.Dispose();

			if (ModData != null)
			{
				ModData.ModFiles.UnmountAll();
				ModData.Dispose();
			}

			ModData = null;

			if (mod == null)
				throw new InvalidOperationException("Game.Mod argument missing.");

			if (!Mods.ContainsKey(mod))
				throw new InvalidOperationException("Unknown or invalid mod '{0}'.".F(mod));

			Console.WriteLine("Loading mod: {0}", mod);

			Sound.StopVideo();

			ModData = new ModData(Mods[mod], Mods, true);

			LocalPlayerProfile = new LocalPlayerProfile(Path.Combine(Platform.SupportDir, Settings.Game.AuthProfile), ModData.Manifest.Get<PlayerDatabase>());

			if (!ModData.LoadScreen.BeforeLoad())
				return;

			using (new PerfTimer("LoadMaps"))
				ModData.MapCache.LoadMaps();

			ModData.InitializeLoaders(ModData.DefaultFileSystem);
			Renderer.InitializeFonts(ModData);

			var grid = ModData.Manifest.Contains<MapGrid>() ? ModData.Manifest.Get<MapGrid>() : null;
			Renderer.InitializeDepthBuffer(grid);

			Cursor?.Dispose();

			Cursor = new CursorManager(ModData.CursorProvider);

			PerfHistory.Items["render"].HasNormalTick = false;
			PerfHistory.Items["batches"].HasNormalTick = false;
			PerfHistory.Items["render_world"].HasNormalTick = false;
			PerfHistory.Items["render_widgets"].HasNormalTick = false;
			PerfHistory.Items["render_flip"].HasNormalTick = false;
			PerfHistory.Items["terrain_lighting"].HasNormalTick = false;

			JoinLocal();

			try
			{
				discoverNat?.Wait();
			}
			catch (Exception e)
			{
				Console.WriteLine("NAT discovery failed: {0}", e.Message);
				Log.Write("nat", e.ToString());
			}

			ChromeMetrics.TryGet("ChatMessageColor", out chatMessageColor);
			ChromeMetrics.TryGet("SystemMessageColor", out systemMessageColor);

			ModData.LoadScreen.StartGame(args);
		}

		public static void LoadEditor(string mapUid)
		{
			StartGame(mapUid, WorldType.Editor);
		}

		public static void LoadShellMap()
		{
			var shellmap = ChooseShellmap();

			using (new PerfTimer("StartGame"))
			{
				StartGame(shellmap, WorldType.Shellmap);
				OnShellmapLoaded();
			}
		}

		static string ChooseShellmap()
		{
			var shellmaps = ModData.MapCache
				.Where(m => m.Status == MapStatus.Available && m.Visibility.HasFlag(MapVisibility.Shellmap))
				.Select(m => m.Uid);

			if (!shellmaps.Any())
				throw new InvalidDataException("No valid shellmaps available");

			return shellmaps.Random(CosmeticRandom);
		}

		public static void SwitchToExternalMod(ExternalMod mod, string[] launchArguments = null, Action onFailed = null)
		{
			try
			{
				var path = mod.LaunchPath;
				var args = launchArguments != null ? mod.LaunchArgs.Append(launchArguments) : mod.LaunchArgs;
				if (modLaunchWrapper != null)
				{
					path = modLaunchWrapper;
					args = new[] { mod.LaunchPath }.Concat(args);
				}

				var p = Process.Start(path, args.Select(a => "\"" + a + "\"").JoinWith(" "));
				if (p == null || p.HasExited)
					onFailed();
				else
				{
					p.Close();
					Exit();
				}
			}
			catch (Exception e)
			{
				Log.Write("debug", "Failed to switch to external mod.");
				Log.Write("debug", "Error was: " + e.Message);
				onFailed();
			}
		}

		static RunStatus state = RunStatus.Running;
		public static event Action OnQuit = () => { };

		// Note: These delayed actions should only be used by widgets or disposing objects
		// - things that depend on a particular world should be queuing them on the world actor.
		static volatile ActionQueue delayedActions = new ActionQueue();
		static Color systemMessageColor = Color.White;
		static Color chatMessageColor = Color.White;
		public static void RunAfterTick(Action a) { delayedActions.Add(a, RunTime); }
		public static void RunAfterDelay(int delayMilliseconds, Action a) { delayedActions.Add(a, RunTime + delayMilliseconds); }

		static void TakeScreenshotInner()
		{
			using (new PerfTimer("Renderer.SaveScreenshot"))
			{
				var mod = ModData.Manifest.Metadata;
				var directory = Path.Combine(Platform.SupportDir, "Screenshots", ModData.Manifest.Id, mod.Version);
				Directory.CreateDirectory(directory);

				var filename = TimestampedFilename(true);
				var path = Path.Combine(directory, string.Concat(filename, ".png"));
				Log.Write("debug", "Taking screenshot " + path);

				Renderer.SaveScreenshot(path);
				Debug("Saved screenshot " + filename);
			}
		}

		static void InnerLogicTick(OrderManager orderManager)
		{
			var tick = RunTime;

			var world = orderManager.World;

			var uiTickDelta = tick - Ui.LastTickTime;
			if (uiTickDelta >= Timestep)
			{
				// Explained below for the world tick calculation
				var integralTickTimestep = (uiTickDelta / Timestep) * Timestep;
				Ui.LastTickTime += integralTickTimestep >= TimestepJankThreshold ? integralTickTimestep : Timestep;

				Sync.RunUnsynced(Settings.Debug.SyncCheckUnsyncedCode, world, Ui.Tick);
				Cursor.Tick();
			}

			var worldTimestep = world == null ? Timestep : world.IsLoadingGameSave ? 1 : world.Timestep;
			var worldTickDelta = tick - orderManager.LastTickTime;
			if (worldTimestep != 0 && worldTickDelta >= worldTimestep)
			{
				using (new PerfSample("tick_time"))
				{
					// Tick the world to advance the world time to match real time:
					//    If dt < TickJankThreshold then we should try and catch up by repeatedly ticking
					//    If dt >= TickJankThreshold then we should accept the jank and progress at the normal rate
					// dt is rounded down to an integer tick count in order to preserve fractional tick components.
					var integralTickTimestep = (worldTickDelta / worldTimestep) * worldTimestep;
					orderManager.LastTickTime += integralTickTimestep >= TimestepJankThreshold ? integralTickTimestep : worldTimestep;

					Sound.Tick();
					Sync.RunUnsynced(Settings.Debug.SyncCheckUnsyncedCode, world, orderManager.TickImmediate);

					if (world == null)
						return;

					var isNetTick = LocalTick % NetTickScale == 0;

					if (!isNetTick || orderManager.IsReadyForNextFrame)
					{
						++orderManager.LocalFrameNumber;

						if (Settings.Debug.EnableDebugTickLogging)
							Log.Write("debug", "--Tick: {0} ({1})", LocalTick, isNetTick ? "net" : "local");

						if (isNetTick)
							orderManager.Tick();

						Sync.RunUnsynced(Settings.Debug.SyncCheckUnsyncedCode, world, () =>
						{
							world.OrderGenerator.Tick(world);
						});

						world.Tick();

						PerfHistory.Tick();
					}
					else if (orderManager.NetFrameNumber == 0)
						orderManager.LastTickTime = RunTime;

					// Wait until we have done our first world Tick before TickRendering
					if (orderManager.LocalFrameNumber > 0)
						Sync.RunUnsynced(Settings.Debug.SyncCheckUnsyncedCode, world, () => world.TickRender(worldRenderer));
				}

				benchmark?.Tick(LocalTick);
			}
		}

		static void LogicTick()
		{
			PerformDelayedActions();

			if (OrderManager.Connection.ConnectionState != lastConnectionState)
			{
				lastConnectionState = OrderManager.Connection.ConnectionState;
				ConnectionStateChanged(OrderManager);
			}

			InnerLogicTick(OrderManager);
			if (worldRenderer != null && OrderManager.World != worldRenderer.World)
				InnerLogicTick(worldRenderer.World.OrderManager);
		}

		public static void PerformDelayedActions()
		{
			delayedActions.PerformActions(RunTime);
		}

		public static void TakeScreenshot()
		{
			takeScreenshot = true;
		}

		static void RenderTick()
		{
			using (new PerfSample("render"))
			{
				++RenderFrame;

				// Prepare renderables (i.e. render voxels) before calling BeginFrame
				using (new PerfSample("render_prepare"))
				{
					Renderer.WorldModelRenderer.BeginFrame();

					// World rendering is disabled while the loading screen is displayed
					if (worldRenderer != null && !worldRenderer.World.IsLoadingGameSave)
					{
						worldRenderer.Viewport.Tick();
						worldRenderer.PrepareRenderables();
					}

					Ui.PrepareRenderables();
					Renderer.WorldModelRenderer.EndFrame();
				}

				// worldRenderer is null during the initial install/download screen
				// World rendering is disabled while the loading screen is displayed
				// Use worldRenderer.World instead of OrderManager.World to avoid a rendering mismatch while processing orders
				if (worldRenderer != null && !worldRenderer.World.IsLoadingGameSave)
				{
					Renderer.BeginWorld(worldRenderer.Viewport.Rectangle);
					Sound.SetListenerPosition(worldRenderer.Viewport.CenterPosition);
					using (new PerfSample("render_world"))
						worldRenderer.Draw();
				}

				using (new PerfSample("render_widgets"))
				{
					Renderer.BeginUI();

					if (worldRenderer != null && !worldRenderer.World.IsLoadingGameSave)
						worldRenderer.DrawAnnotations();

					Ui.Draw();

					if (ModData != null && ModData.CursorProvider != null)
					{
						if (HideCursor)
							Cursor.SetCursor(null);
						else
						{
							Cursor.SetCursor(Ui.Root.GetCursorOuter(Viewport.LastMousePos) ?? "default");
							Cursor.Render(Renderer);
						}
					}
				}

				using (new PerfSample("render_flip"))
					Renderer.EndFrame(new DefaultInputHandler(OrderManager.World));

				if (takeScreenshot)
				{
					takeScreenshot = false;
					TakeScreenshotInner();
				}
			}

			PerfHistory.Items["render"].Tick();
			PerfHistory.Items["batches"].Tick();
			PerfHistory.Items["render_world"].Tick();
			PerfHistory.Items["render_widgets"].Tick();
			PerfHistory.Items["render_flip"].Tick();
			PerfHistory.Items["terrain_lighting"].Tick();
		}

		static void Loop()
		{
			// The game loop mainly does two things: logic updates and
			// drawing on the screen.
			// ---
			// We ideally want the logic to run every 'Timestep' ms and
			// rendering to be done at 'MaxFramerate', so 1000 / MaxFramerate ms.
			// Any additional free time is used in 'Sleep' so we don't
			// consume more CPU/GPU resources than necessary.
			// ---
			// In case logic or rendering takes more time than the ideal
			// and we're getting behind, we can skip rendering some frames
			// but there's a fail-safe minimum FPS to make sure the screen
			// gets updated at least that often.
			// ---
			// TODO: Separate world/UI rendering
			// It would be nice to separate the world rendering from the UI rendering
			// so that we can update the UI more often than the world. This would
			// help make the game playable (mouse/controls) even in low world
			// framerates.
			// It's not possible at the moment because the render buffer is cleared
			// before rendering and we don't keep the last rendered world buffer.

			// When the logic has fallen behind by this much, skip the pending
			// updates and start fresh.
			// For example, if we want to update logic every 10 ms but each loop
			// temporarily takes 100 ms, the 'nextLogic' timestamp will be too low
			// and the current timestamp ('now') will have moved on. Even if the
			// update time returns to normal, it will take a long time to catch up
			// (if ever).
			// This also means that the 'logicInterval' cannot be longer than this
			// value.
			const int MaxLogicTicksBehind = 250;

			// Try to maintain at least this many FPS during replays, even if it slows down logic.
			// However, if the user has enabled a framerate limit that is even lower
			// than this, then that limit will be used.
			const int MinReplayFps = 10;

			// Timestamps for when the next logic and rendering should run
			var nextLogic = RunTime;
			var nextRender = RunTime;
			var forcedNextRender = RunTime;
			var renderBeforeNextTick = false;

			while (state == RunStatus.Running)
			{
				// Ideal time between logic updates. Timestep = 0 means the game is paused
				// but we still call LogicTick() because it handles pausing internally.
				var logicInterval = worldRenderer != null && worldRenderer.World.Timestep != 0 ? worldRenderer.World.Timestep : Timestep;

				// Ideal time between screen updates
				var maxFramerate = Settings.Graphics.CapFramerate ? Settings.Graphics.MaxFramerate.Clamp(1, 1000) : 1000;
				var renderInterval = 1000 / maxFramerate;

				// Tick as fast as possible while restoring game saves, capping rendering at 5 FPS
				if (OrderManager.World != null && OrderManager.World.IsLoadingGameSave)
				{
					logicInterval = 1;
					renderInterval = 200;
				}

				var now = RunTime;

				// If the logic has fallen behind too much, skip it and catch up
				if (now - nextLogic > MaxLogicTicksBehind)
					nextLogic = now;

				// When's the next update (logic or render)
				var nextUpdate = Math.Min(nextLogic, nextRender);
				if (now >= nextUpdate)
				{
					var forceRender = renderBeforeNextTick || now >= forcedNextRender;

					if (now >= nextLogic && !renderBeforeNextTick)
					{
						nextLogic += logicInterval;

						LogicTick();

						// Force at least one render per tick during regular gameplay
						if (OrderManager.World != null && !OrderManager.World.IsLoadingGameSave && !OrderManager.World.IsReplay)
							renderBeforeNextTick = true;
					}

					var haveSomeTimeUntilNextLogic = now < nextLogic;
					var isTimeToRender = now >= nextRender;

					if ((isTimeToRender && haveSomeTimeUntilNextLogic) || forceRender)
					{
						nextRender = now + renderInterval;

						// Pick the minimum allowed FPS (the lower between 'minReplayFPS'
						// and the user's max frame rate) and convert it to maximum time
						// allowed between screen updates.
						// We do this before rendering to include the time rendering takes
						// in this interval.
						var maxRenderInterval = Math.Max(1000 / MinReplayFps, renderInterval);
						forcedNextRender = now + maxRenderInterval;

						RenderTick();
						renderBeforeNextTick = false;
					}
				}
				else
					Thread.Sleep((int)(nextUpdate - now));
			}
		}

		static RunStatus Run()
		{
			if (Settings.Graphics.MaxFramerate < 1)
			{
				Settings.Graphics.MaxFramerate = new GraphicSettings().MaxFramerate;
				Settings.Graphics.CapFramerate = false;
			}

			try
			{
				Loop();
			}
			finally
			{
				// Ensure that the active replay is properly saved
				OrderManager?.Dispose();
			}

			worldRenderer?.Dispose();
			ModData.Dispose();
			ChromeProvider.Deinitialize();

			Sound.Dispose();
			Renderer.Dispose();

			OnQuit();

			return state;
		}

		public static void Exit()
		{
			state = RunStatus.Success;
		}

		public static void AddSystemLine(string text)
		{
			AddSystemLine("Battlefield Control", text);
		}

		public static void AddSystemLine(string name, string text)
		{
			OrderManager.AddChatLine(name, systemMessageColor, text, systemMessageColor);
		}

		public static void AddChatLine(string name, Color nameColor, string text)
		{
			OrderManager.AddChatLine(name, nameColor, text, chatMessageColor);
		}

		public static void Debug(string s, params object[] args)
		{
			AddSystemLine("Debug", string.Format(s, args));
		}

		public static void Disconnect()
		{
			OrderManager.World?.TraitDict.PrintReport();

			OrderManager.Dispose();
			CloseServer();
			JoinLocal();
		}

		public static void CloseServer()
		{
			server?.Shutdown();
		}

		public static T CreateObject<T>(string name)
		{
			return ModData.ObjectCreator.CreateObject<T>(name);
		}

		public static ConnectionTarget CreateServer(ServerSettings settings)
		{
			var endpoints = new List<IPEndPoint>
			{
				new IPEndPoint(IPAddress.IPv6Any, settings.ListenPort),
				new IPEndPoint(IPAddress.Any, settings.ListenPort)
			};
			server = new Server.Server(endpoints, settings, ModData, ServerType.Multiplayer);

			return server.GetEndpointForLocalConnection();
		}

		public static ConnectionTarget CreateLocalServer(string map)
		{
			var settings = new ServerSettings()
			{
				Name = "Skirmish Game",
				Map = map,
				AdvertiseOnline = false
			};

			// Always connect to local games using the same loopback connection
			// Exposing multiple endpoints introduces a race condition on the client's PlayerIndex (sometimes 0, sometimes 1)
			// This would break the Restart button, which relies on the PlayerIndex always being the same for local servers
			var endpoints = new List<IPEndPoint>
			{
				new IPEndPoint(IPAddress.Loopback, 0)
			};
			server = new Server.Server(endpoints, settings, ModData, ServerType.Local);

			return server.GetEndpointForLocalConnection();
		}

		public static bool IsCurrentWorld(World world)
		{
			return OrderManager != null && OrderManager.World == world && !world.Disposing;
		}

		public static bool SetClipboardText(string text)
		{
			return Renderer.Window.SetClipboardText(text);
		}

		public static void BenchmarkMode(string prefix)
		{
			benchmark = new Benchmark(prefix);
		}

		public static void LoadMap(string launchMap)
		{
			var orders = new List<Order>
			{
				Order.Command("option gamespeed {0}".F("default")),
				Order.Command("state {0}".F(Session.ClientState.Ready))
			};

			var path = Platform.ResolvePath(launchMap);
			var map = ModData.MapCache.SingleOrDefault(m => m.Uid == launchMap) ??
				ModData.MapCache.SingleOrDefault(m => m.Package.Name == path);

			if (map == null)
				throw new InvalidOperationException("Could not find map '{0}'.".F(launchMap));

			CreateAndStartLocalServer(map.Uid, orders);
		}

		public static void FinishBenchmark()
		{
			if (benchmark != null)
			{
				benchmark.Write();
				Exit();
			}
		}
	}
}
