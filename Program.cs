﻿using System.Threading.Tasks;
using System;
using PercivalBot.ContinuousIntegration;
using PercivalBot.VersionControlSystems;
using System.Runtime.InteropServices;
using PercivalBot.ChatClients;
using PercivalBot.Config;
using PercivalBot.Core;
using PercivalBot.ChatClients.Interface;
using PercivalBot.ContinuousIntegration.Interface;
using PercivalBot.VersionControlSystems.Interface;

using PercivalBot.Enums;
using PercivalBot.Structs;
using PercivalBot.ChatClients.Discord;
using PercivalBot.ChatClients.Slack;
using System.Threading;

namespace PercivalBot
{
    public class Program
	{
		static ManualResetEventSlim waitforProcessShutdown = new();
		static ManualResetEventSlim waitForMainExit = new();

		#region Main
		// ========================================================================================
		// Main variables
		// ========================================================================================

		// TODO I can probably get rid of all this legacy crap. AppDoman.CurrentDomain.ProcessExit
		//#if Windows // Fun fact: VS's "publish" function won't fucking use these OS directives, thanks C#, so instead I'll just assume that I will only ever debug on windows...
#if DEBUG
		[DllImport("Kernel32")]
		private static extern bool SetConsoleCtrlHandler(CloseEventHandler handler, bool add);
		private delegate bool CloseEventHandler();
#endif

		// ========================================================================================
		// Main
		// ========================================================================================
		public static async Task Main(string[] args)
		{
			AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
			{
				bot?.runComplete.SetResult(true);
			};

			//#if Windows
#if DEBUG
			Console.CancelKeyPress += HandleCancelKeyPress;
			SetConsoleCtrlHandler(() => { return HandleClose(); }, true);
#endif
			Program program = new Program();

			await program.ProgramMain();

			Shutdown();
		}

//#if Windows
#if DEBUG
		static void HandleCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
		{
			Shutdown();
			e.Cancel = true;
		}
#endif

//#if Windows
#if DEBUG
		static bool HandleClose()
		{
			Shutdown();
			return false;
		}
#endif

		static void Shutdown()
		{
			Console.WriteLine("Program shutdown");
			bot?.Stop();
		}

#endregion
		// ========================================================================================
		// Settings 
		// ========================================================================================
		readonly string configSource = "config.xml";

		// ========================================================================================
		// State
		// ========================================================================================
		static Percival? bot;

		// ========================================================================================
		// API
		// ========================================================================================
		async Task ProgramMain()
		{
			ProgramConfig config = new ProgramConfig(configSource);

			// Spawn systems
			IEmbedBuilder embedBuilder = EmbedBuilderFactory.Build(config.chatClient, config.ci);
			IChatClient chatClient = CreateChatClient(config.chatClient, embedBuilder);
			IVersionControlSystem vcs = CreateVersionControlSystem(config.vcs);
			IContinuousIntegrationSystem cis = CreateContinuousIntegrationSystem(config.ci);

			bot = new(vcs, cis, chatClient, config.percivalConfig);
			await bot.Start();

			Console.WriteLine("\n" +
				"Bot running! To shut down: curl -H \"key:<yourkey>\" http://<botaddress>/shutdown" +
				"\n");

			await bot.runComplete.Task;
		}

		// --------------------------------------
		private IVersionControlSystem CreateVersionControlSystem(VersionControlConfig config)
		{
			switch (config.System)
			{
				case EVersionControlSystem.Perforce:
				{
					return new PerforceVCS(config);
				}
				case EVersionControlSystem.Git:
				{
					return new GitVCS(config);
				}
			}

			throw new Exception("Invalid VCS specified! Check config?");
		}

		// --------------------------------------
		private IContinuousIntegrationSystem CreateContinuousIntegrationSystem(ContinuousIntegrationConfig config)
		{
			switch (config.System)
			{
				case EContinuousIntegrationSoftware.Jenkins:
				{
					return new JenkinsCI(config);
				}
				case EContinuousIntegrationSoftware.TeamCity:
				{
					return new TeamCityCI(config);
				}
			}

			throw new Exception("Invalid CI system specified! Check config?");
		}

		// --------------------------------------
		private IChatClient CreateChatClient(ChatClientConfig chatClientConfig, IEmbedBuilder embedBuilder)
		{
			switch (chatClientConfig.System)
			{
				case EChatClient.Discord:
				{
					return new ChatClient_Discord(chatClientConfig, embedBuilder);
				}
				case EChatClient.Slack:
				{
					throw new NotImplementedException();
				}
			}

			throw new Exception("Invalid chat client specified! Check config?");
		}
	}
}

