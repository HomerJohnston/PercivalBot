﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web;
using GPGBot.ChatClients;
using GPGBot.ContinuousIntegration;
using GPGBot.VersionControlSystems;
using WatsonWebserver;
using WatsonWebserver.Core;
using WatsonWebserver.Extensions.HostBuilderExtension;

namespace GPGBot
{
	internal class GPGBot
	{
		IVersionControlSystem versionControlSystem;
		IContinuousIntegrationSystem continuousIntegrationSystem;
		IChatClient chatClient;

		WebserverBase webserver;
		string? webserverKey;

		Dictionary<ulong, object?> activeEmbeds = new();

		List<Config.ActionSpec> actionSpecs = new();
		readonly Dictionary<int, BuildRecord> buildRecords = new();

		public bool bShutdownRequested = false;

		public TaskCompletionSource<bool> runComplete = new();

		// ---------------------------------------------------------------
		public GPGBot(IVersionControlSystem inVCS, IContinuousIntegrationSystem inCI, IChatClient inChatClient, Config.Webserver webserverConfig, Config.Actions actions)
		{
			versionControlSystem = inVCS;
			continuousIntegrationSystem = inCI;
			chatClient = inChatClient;

			webserver = BuildWebServer(webserverConfig);
			webserverKey = webserverConfig.Key ?? string.Empty;

			if (versionControlSystem == null) throw new Exception("Invalid VersionControlSystem! Check config?");
			if (continuousIntegrationSystem == null) throw new Exception("Invalid ContinuousIntegrationSystem! Check config?");
			if (chatClient == null) throw new Exception("Invalid ChatClient! Check config?");
			if (webserver == null) throw new Exception("Invalid Webserver! Check config?");

			/*
			if (actions.Spec != null)
			{
				//actionSpecs = actions.Spec;
			}
			else
			{
				Console.WriteLine("Warning: no action specs found!");
			}
			*/
		}

		public void Run()
		{
			chatClient?.Start();
			webserver?.Start();
		}

		public void Stop()
		{
			chatClient?.Stop();
			webserver?.Stop();
		}

		Webserver BuildWebServer(Config.Webserver serverConfig)
		{
			if (serverConfig.Port == null)
			{
				throw new Exception("Invalid webserver port! Check config?");
			}

			HostBuilder hostBuilder = new HostBuilder("localhost", (int)serverConfig.Port, false, DefaultRoute)
				.MapAuthenticationRoute(AuthenticateRequest)
				.MapParameteRoute(HttpMethod.POST, "/on-commit", OnCommit, true) // Handles triggers from source control system
				.MapParameteRoute(HttpMethod.POST, "/build-status-update", OnBuildStatusUpdate, true) // Handles status updates from continuous integration
				.MapParameteRoute(HttpMethod.POST, "/test", Test, true)
				.MapStaticRoute(HttpMethod.GET, "/shutdown", Shutdown, true);

			return hostBuilder.Build();
		}

		/*
		public void AbortBuild()
		{
			throw new NotImplementedException();
		}

		public async Task void HandleCommit(string user, string commitID, string description, string spec)
		{
			CommitSpec? commitSpec = commitSpecs[spec];
			
			if (commitSpec == null)
			{
				return;
			}
		}

		public Task HandleCommit()
		{
			throw new NotImplementedException();
		}

		public void StartBuild()
		{
			throw new NotImplementedException();
		}

		public async Task HandleBuildStatusUpdate(HttpContextBase context)
		{
			Console.WriteLine("Received build state update!");

			await SendEmbed();

			await Task.CompletedTask;
		}

		private IUserMessage? maesssag = null;


		private async Task void SendEmbed()
		{
			
			Embed testEmbed = embedBuilder.BuildEmbed(-1, "title", "", Color.Gold, "desc", 123, "user", "job");

			ulong TESTCHANNELID = 191070133190524928;
			if (discordClient.GetChannel(TESTCHANNELID) is IMessageChannel messageChannel)
			{
				IUserMessage msg = await messageChannel.SendMessageAsync("test text", false, testEmbed);
				maesssag = msg;
			}
			
		}
		*/

		// --------------------------------------
		async Task AuthenticateRequest(HttpContextBase context)
		{
			if (webserverKey == null)
			{
				await Accept(context);
				return;
			}

			string? key = context.Request.Headers["key"];
			if (key != null && key == webserverKey)
			{
				await Accept(context);
				return;
			}

			await Reject(context);
		}

		static async Task Accept(HttpContextBase context)
		{
			await Task.CompletedTask;
		}

		static async Task Reject(HttpContextBase context)
		{
			string? incomingKey = context.Request.Headers["key"];

			Console.WriteLine("Rejected incoming request" + ((incomingKey != null) ? (", invalid key: " + (incomingKey)) : ", no key"));
			
			context.Response.StatusCode = 401;
			await context.Response.Send("Request denied.");
		}

		// Master goals:
		// - We want to post commits to the project's main development stream
		// - We want to launch build processes if the commit contains code files
		// - We want to ignore commits that are made by CI processes

		// Perforce process:
		// Is the committed stream of interest to us? (e.g. Megacity-Mainline)
		// If yes, is the command type of interest to us? (e.g. change-commit)

		// --------------------------------------
		async Task OnCommit(HttpContextBase context)
		{
			Console.WriteLine("OnCommit start");

			if (versionControlSystem == null)
			{
				throw new Exception();
			}

			var queryParams = GetQueryParams(context);

			string change = queryParams["change"] ?? string.Empty;
			string client = queryParams["client"] ?? string.Empty;
			string root = queryParams["root"] ?? string.Empty;
			string user = queryParams["user"] ?? string.Empty;
			string address = queryParams["address"] ?? string.Empty;
			string branch = queryParams["branch"] ?? string.Empty; // branch is used for potential git compatibility only; p4 triggers %stream% is bugged and cannot send stream name
			string type = queryParams["type"] ?? string.Empty;

			string stream = string.Empty;

			// workaround for p4 trigger bug no stream name capability
			if (branch == string.Empty)
			{
				stream = versionControlSystem?.GetStream(change, client) ?? string.Empty;
			}

			Console.WriteLine("OnCommit: change {0}, client {1}, root {2}, user {3}, address {4}, branch {5}, type {6}", change, client, root, user, address, branch, type);

			// TODO: error handling.. config errors throw annoying HTTP error "it's not you it's me" error messages

			foreach (Config.ActionSpec spec in actionSpecs)
			{
				if (spec.Stream == stream || spec.Branch == branch)
				{
					CommitEmbedData commitEmbedData = new CommitEmbedData();
					commitEmbedData.change = change;
					commitEmbedData.user = user;
					commitEmbedData.stream = branch;
					commitEmbedData.client = client;

					try
					{
						commitEmbedData.description = versionControlSystem.GetCommitDescription(change);
					}
					catch
					{
						throw new Exception("Failed to get commit description!");
					}

					switch (type)
					{
						case "code":
						{
							string? build = spec.BuildConfigName;

							if (build != null)
							{
								await continuousIntegrationSystem.StartBuild(build);
							}

							await chatClient.PostCommitMessage(commitEmbedData);

							break;
						}
						case "content":
						{

							Console.WriteLine("Posting commit message");
							await chatClient.PostCommitMessage(commitEmbedData);
							break;
						}
						default:
						{
							Console.WriteLine("Wtfbbq");
							break;
						}
					}

				}
			}

			Console.WriteLine("Wow, end of teh function!!!");

			context.Response.StatusCode = 200;
			await context.Response.Send(string.Format("OnCommit: change {0}, client {1}, root {2}, user {3}, address {4}, stream {5}, type {6}", change, client, root, user, address, branch, type));
		}

		// --------------------------------------
		async Task OnBuildStatusUpdate(HttpContextBase context)
		{
			if (chatClient == null)
			{
				throw new Exception("Chat client was null!");
			}

			Console.WriteLine("OnBuildStatusUpdate(" + context.Request.Query.Querystring + ")");

			var queryParams = GetQueryParams(context);

			BuildStatusEmbedData data = new BuildStatusEmbedData();

			if (!Enum.TryParse<EBuildStatus>(queryParams["buildstat"], true, out data.buildStatus))
			{
				throw new Exception("Failed to parse a valid build status!");
			}

			data.text = "TestText";
			data.buildConfig = "TestBuildConfig";

			ulong msgID = await chatClient.PostBuildStatusEmbed(data);

			if (msgID == 0)
			{
				await context.Response.Send("Remote failed to run OnBuildStatusUpdate");
			}
            else
            {
				activeEmbeds.Add(msgID, null);
				await context.Response.Send("Remote ran OnBuildStatusUpdate");
			}
		}

		// --------------------------------------
		public async Task Test(HttpContextBase context)
		{
			Console.WriteLine("Test(" + context.ToString() + ")");

			if (chatClient == null)
			{
				return;
			}

			foreach (var embed in activeEmbeds)
			{
				await chatClient.DeleteMessage(embed.Key);
			}

			await context.Response.Send("Remote ran Test");
		}

		public async Task Shutdown(HttpContextBase context)
		{
			await context.Response.Send("Bot: shutting down!");

			runComplete.SetResult(true);
		}

		// --------------------------------------
		async Task DefaultRoute(HttpContextBase context) =>
		  await context.Response.Send("Pong");

		// --------------------------------------
		static System.Collections.Specialized.NameValueCollection GetQueryParams(HttpContextBase context)
		{
			Uri urlAsURI = new(context.Request.Url.Full);
			System.Collections.Specialized.NameValueCollection queryParams = HttpUtility.ParseQueryString(context.Request.DataAsString);// urlAsURI.Query);
			
			return queryParams;
		}
	}
}