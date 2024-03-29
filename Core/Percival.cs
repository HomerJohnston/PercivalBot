﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using PercivalBot.Config;
using WatsonWebserver;
using WatsonWebserver.Core;
using WatsonWebserver.Extensions.HostBuilderExtension;
using System.Linq;
using Perforce.P4;
using PercivalBot.ChatClients.Interface;
using PercivalBot.ContinuousIntegration.Interface;
using PercivalBot.VersionControlSystems.Interface;

using PercivalBot.Enums;
using PercivalBot.Structs;
using Microsoft.VisualBasic;
using Discord;


namespace PercivalBot.Core
{
	using PostBuildDelegate = Func<HttpContextBase, BuildStatusUpdateRequest, BuildRecord, CIBuildResponseConfig, Task>;

	public class Percival
    {

		#region Data Members
		// --------------------------------------
		public TaskCompletionSource<bool> runComplete = new();

        IVersionControlSystem versionControlSystem;

        IContinuousIntegrationSystem continuousIntegrationSystem;

        IChatClient chatClient;

        WebserverBase webserver;
        string? webserverKey;

        // --------------------------------------
        readonly List<CIBuildResponseConfig> buildJobs;
		readonly List<VCSCommitResponse> ignoreCommitResponses = new();
		readonly List<VCSCommitResponse> commitResponses = new();
        readonly List<WebhookConfig> webhooks;

        readonly Dictionary<BuildRecord, ulong> BuildRunningMessages = new();
        readonly Dictionary<CIBuildResponseConfig, List<ulong>> BuildCompletionMessages = new();
		#endregion

		#region Construction/Destruction
		// ---------------------------------------------------------------
		public Percival(IVersionControlSystem inVCS, IContinuousIntegrationSystem inCI, IChatClient inChatClient, PercivalConfig config)
        {
            // Set up major components
            versionControlSystem = inVCS;
            continuousIntegrationSystem = inCI;
            chatClient = inChatClient;

            // Set up other config data

            if (config.vcsCommitResponses?.Responses == null)
            {
                LogSync("Warning: no commit responses found in config file!");
                commitResponses = new List<VCSCommitResponse>();
            }
            else
            {
                foreach (VCSCommitResponse response in config.vcsCommitResponses.Responses)
                {
                    if (response.Ignore == false & response.StartBuild == null && response.PostWebhook == null)
                    {
                        LogSync($"Unconfigured commit response: {response.Name}");
                        continue;
                    }

                    if (response.Ignore && (response.StartBuild != null || response.PostWebhook != null))
                    {
                        LogSync($"Improperly configured commit response had both ignore and startBuild or postWebHook entered: {response.Name}");
                        continue;
                    }

                    if (response.Ignore)
                    {
                        LogSync($"Ignore response added, {response}");
                        ignoreCommitResponses.Add(response);
                    }
                    else
                    {
						LogSync($"Commit response added, {response}");
						commitResponses.Add(response);
					}
				}
            }

            buildJobs = config.ciBuildResponses?.Job ?? new();
            webhooks = config.namedWebhooks?.Webhook ?? new();

			// Set up HTTP server
			if (config.webserver == null)
			{
				throw new Exception("No webserver config!");
			}

            WebserverConfig webserverConfig = config.webserver;

            webserver = BuildWebServer(webserverConfig);
            webserverKey = webserverConfig.Key ?? string.Empty;

            // Check for errors
            if (versionControlSystem == null) throw new Exception("Invalid VersionControlSystem! Check config?");
            if (continuousIntegrationSystem == null) throw new Exception("Invalid ContinuousIntegrationSystem! Check config?");
            if (chatClient == null) throw new Exception("Invalid ChatClient! Check config?");
            if (webserver == null) throw new Exception("Invalid Webserver! Check config?");

            if (commitResponses.Count == 0) { LogSync("Error: Found no commit responses!"); }
            if (buildJobs.Count == 0) { LogSync("Error: Found no build jobs!"); }
            if (webhooks.Count == 0) { LogSync("Warning: Found no named webhooks."); }
        }

        // ---------------------------------------------------------------
        public async Task Start()
        {
            await chatClient.Start();
            webserver.Start();
        }

        // ---------------------------------------------------------------
        public void Stop()
        {
            chatClient.Stop();
            webserver.Stop();
        }

        // ---------------------------------------------------------------
        Webserver BuildWebServer(WebserverConfig serverConfig)
        {
            string address = "*";

            if (serverConfig.Address == null)
            {
                LogSync($"Webserver address is not configured. Defaulting to listen on any address.");
            }
            else
            {
                address = serverConfig.Address;
            }

            int port = 1199;

            if (serverConfig.Port == null)
            {
				LogSync($"Webserver port is not configured. Defaulting to {port}.");
            }
            else
            {
                port = (int)serverConfig.Port;
            }

            HostBuilder hostBuilder = new HostBuilder(address, port, false, DefaultRoute)
                .MapAuthenticationRoute(AuthenticateRequest)
                .MapParameteRoute(HttpMethod.POST, "/on-commit", OnCommit, true) // Handles triggers from source control system
                .MapParameteRoute(HttpMethod.POST, "/build-status-update", OnBuildStatusUpdate, true) // Handles status updates from continuous integration
                .MapStaticRoute(HttpMethod.GET, "/shutdown", Shutdown, true);

            return hostBuilder.Build();
        }
		#endregion

		#region WebAuth
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

            await LogAndReply(
                "Rejected incoming request, " + ((incomingKey != null) ? ("invalid key: " + incomingKey) : ("no key")),
				"Request denied. Required format: curl http://botaddress -H \"key:passphrase\"",
                HttpStatusCode.Forbidden, 
                context);
        }
		#endregion

		#region Commit Response
		// --------------------------------------
		async Task OnCommit(HttpContextBase context)
        {
            var queryParams = GetQueryParams(context);

            string change = queryParams["change"] ?? string.Empty;
            string client = queryParams["client"] ?? string.Empty;
            string user = queryParams["user"] ?? string.Empty;
            string branch = queryParams["branch"] ?? string.Empty; // branch is used for potential git compatibility only; p4 triggers %stream% is bugged and cannot send stream name

			Commit commit = new Commit(change, client, user, branch);
			
			// workaround for p4 trigger bug - no sending of stream name capability. query for it instead.
			if (!commit.HasBranch())
            {
                await Log("No branch supplied, trying to grab stream using VCS method...");
                commit.Branch = versionControlSystem.GetStream(change, client) ?? string.Empty;
            }

            if (!commit.IsValid(out string error))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;

                string msg = $"Commit POST was INVALID - {error}";

				await Log(msg);
                await context.Response.Send(msg);
                return;
            }

            await HandleValidCommit(context, commit);
        }

        // --------------------------------------
        async Task HandleValidCommit(HttpContextBase context, Commit commit)
		{
			await Log($"OnCommit: {commit}");

            // if any ignore specs match with this commit
            if (ignoreCommitResponses.Any(spec => (spec.Name != null) && commit.Branch.StartsWith(spec.Name)))
			{
                await LogAndReply($"Ignored commit trigger for change {commit.Change}; found matching commit ignore", HttpStatusCode.OK, context);
				return;
			}

			List<VCSCommitResponse> matchedCommits = commitResponses.FindAll(spec => (spec.Name != null) && commit.Branch.StartsWith(spec.Name));

            string commitDescription = versionControlSystem.GetCommitDescription(commit.Change) ?? "<No description>";

            if (matchedCommits.Count == 0)
            {
                await LogAndReply($"No matching action specs found! Ignoring this commit.", HttpStatusCode.OK, context);
                return;
            }

            bool containedCode;
            bool containedWwise;
            versionControlSystem.GetRequiredActionsBasedOnChanges(commit.Change, out containedCode, out containedWwise);

            await Log($"Change contained code: {containedCode}; Change contained wwise: {containedWwise}");

            // Simple work to avoid posting the same commit twice
            HashSet<string> commitPostedTo = new();

            foreach (VCSCommitResponse spec in matchedCommits)
            {
                if (spec.PostWebhook != null && !commitPostedTo.Contains(spec.PostWebhook))
                {
                    string? commitWebhook = spec.PostWebhook;

                    if (commitWebhook != null)
                    {
                        await PostCommitMessage(commit.Change, commit.User, commit.Branch, commit.Client, commitWebhook, commitDescription, containedCode || containedWwise);
                        commitPostedTo.Add(spec.PostWebhook);
                    }
                }

                if (containedCode || containedWwise)
                {
                    string? jobName = spec.StartBuild;

                    if (jobName != null)
                    {
                        await Log($"Attempting to start build: {jobName} at change: {commit.Change}");
                        bool result = await continuousIntegrationSystem.StartJob(jobName, commit.Change, containedCode, containedWwise);
                        await Log($"Build {jobName} start result: {result}");
                    }
                }
            }

            await LogAndReply($"OnCommit: change {commit.Change}, client {commit.Client}, user {commit.User}, stream/branch {commit.Branch}, buildCode {containedCode}, buildWwise {containedWwise}", HttpStatusCode.OK, context);
        }

        // --------------------------------------
        private async Task<ulong?> PostCommitMessage(string change, string user, string branch, string client, string commitWebhook, string description, bool doBuild)
        {
            WebhookConfig? webhook = webhooks.Find(x => x.Name == commitWebhook);

            if (webhook == null || webhook.ID == null)
            {
                return null;
            }

            CommitEmbedData commitEmbedData = new CommitEmbedData();
            commitEmbedData.change = change;
            commitEmbedData.user = user;
            commitEmbedData.branch = branch;
            commitEmbedData.client = client;
            commitEmbedData.description = description;

            return await chatClient.PostCommitMessage(commitEmbedData, webhook.ID);
        }
		#endregion

		#region Build Status Updates
		// --------------------------------------
		async Task OnBuildStatusUpdate(HttpContextBase context)
        {
            var queryParams = GetQueryParams(context);

            string changeID = queryParams["changeID"] ?? string.Empty;
            string jobName = queryParams["jobName"] ?? string.Empty;
            string buildNumber = queryParams["buildNumber"] ?? string.Empty;
            string buildID = queryParams["buildID"] ?? string.Empty;
            string buildStatusParam = queryParams["buildStatus"] ?? string.Empty;

            BuildStatusUpdateRequest buildStatusUpdate = new BuildStatusUpdateRequest(changeID, jobName, buildNumber, buildID, buildStatusParam);

            if (!buildStatusUpdate.IsValid(out string error))
            {
                await LogAndReply($"BuildStatusUpdate POST was INVALID - {error}", HttpStatusCode.BadRequest, context);
                return;
            }

			await HandleValidBuildStatusUpdate(context, buildStatusUpdate);
        }
        
        // --------------------------------------
        private async Task HandleValidBuildStatusUpdate(HttpContextBase context, BuildStatusUpdateRequest update)
        {
			await Log($"OnBuildStatusUpdate: {update}");

            List<CIBuildResponseConfig> matchedJobs = buildJobs.FindAll(spec => spec.Name == update.JobName);

			await Log($"TEST 1: {update}");

			if (matchedJobs.Count == 0)
            {
				await LogAndReply($"Config warning: found no build jobs named {update.JobName} in config entries to post status for!\n", HttpStatusCode.InternalServerError, context);
				return;
            }
            else if (matchedJobs.Count > 1)
            {
				await LogAndReply($"Config warning: found multiple build jobs named {update.JobName}, there must only be one - aborting!\n", HttpStatusCode.InternalServerError, context);
				return;
			}

			await Log($"TEST 2: {update}");

			CIBuildResponseConfig buildJob = matchedJobs.First();

			await Log($"TEST 3: {buildJob.Name}");

			if (buildJob.PostChannel == null)
            {
                await LogAndReply($"Config warning: {update.JobName} has no post channel set!", HttpStatusCode.InternalServerError, context);
                return;
            }

			await Log($"TEST 4: {buildJob.Name}");

			BuildRecord record = new BuildRecord(update.JobName, update.BuildNumber, update.BuildID);

			await Log($"TEST 5: {buildJob.Name}");

			PostBuildDelegate func = (update.Status == EBuildStatus.Running) ? PostBuildRunning : PostBuildCompletion;
            await func(context, update, record, buildJob);

            if (!context.Response.ResponseSent)
            {
                await LogAndReply("Unknown error! Failed to post build status.", HttpStatusCode.InternalServerError, context);
            }
        }

		// --------------------------------------
		private async Task PostBuildRunning(HttpContextBase context, BuildStatusUpdateRequest update, BuildRecord record, CIBuildResponseConfig buildJob)
		{
			await Log($"TEST 6: {buildJob.Name}");

			if (buildJob.PostChannel == null)
			{
                await LogAndReply($"Config warning: {buildJob.Name} has no post channel set!",  HttpStatusCode.BadRequest, context);
				return;
			}

			await Log($"TEST 7: {buildJob.Name}");

			if (BuildRunningMessages.ContainsKey(record))
			{
                await LogAndReply($"Received multiple start signals for {update.JobName}, build {update.BuildID}, ignoring!", HttpStatusCode.BadRequest, context);
				return;
			}

			await Log($"TEST 8: {buildJob.Name}");

			ulong? message = await PostBuildStatus(update, buildJob.PostChannel);

			await Log($"TEST 9: {buildJob.Name}");

			if (message == null)
			{
                await LogAndReply($"Unknown error, failed to post message!", HttpStatusCode.InternalServerError, context);
				return;
			}

			await Log($"TEST 10: {buildJob.Name}");

			BuildRunningMessages.Add(record, (ulong)message);

            await LogAndReply($"Success", HttpStatusCode.OK, context);
		}

		// --------------------------------------
		private async Task PostBuildCompletion(HttpContextBase context, BuildStatusUpdateRequest update, BuildRecord record, CIBuildResponseConfig buildJob)
		{
			ulong runningMessage;

			if (!BuildRunningMessages.TryGetValue(record, out runningMessage))
			{
                await LogAndReply($"Received a build status update for {update.JobName} build {update.BuildID} but there was no build in progress for this, ignoring!", HttpStatusCode.BadRequest, context);
				return;
			}

            if (buildJob.PostChannel == null)
			{
				await LogAndReply($"Config warning: {buildJob.Name} has no post channel set!", HttpStatusCode.InternalServerError, context);
				return;
			}

			await chatClient.DeleteMessage(runningMessage, buildJob.PostChannel);
			BuildRunningMessages.Remove(record);

			ulong? message = await PostBuildStatus(update, buildJob.PostChannel);

			if (message == null)
			{
                await LogAndReply($"Failed to post build status message, error unknown!", HttpStatusCode.BadRequest, context);
				return;
			}

            if (update.Status == EBuildStatus.Succeeded)
			{
				if (BuildCompletionMessages.TryGetValue(buildJob, out List<ulong>? existingMessages) && existingMessages != null)
				{
					foreach (ulong existingMessage in existingMessages)
					{
						await chatClient.DeleteMessage(existingMessage, buildJob.PostChannel);
					}

                    existingMessages.Clear();
				}
			}

			BuildCompletionMessages.TryAdd(buildJob, new List<ulong>());
            BuildCompletionMessages[buildJob].Add((ulong)message);

			await LogAndReply($"Success", HttpStatusCode.OK, context);
		}

		// --------------------------------------
		public async Task<ulong?> PostBuildStatus(BuildStatusUpdateRequest update, string channelName)
		{
			BuildStatusEmbedData embedData = new BuildStatusEmbedData();

            embedData.changeID = update.ChangeID;
            embedData.buildConfig = update.JobName;
            embedData.buildNumber = update.BuildNumber;
            embedData.buildID = update.BuildID;
            embedData.buildStatus = update.Status;

            ulong? message = await chatClient.PostBuildStatusEmbed(embedData, channelName);

            return message;
        }
		#endregion

		#region Default Route
		// --------------------------------------
		async Task DefaultRoute(HttpContextBase context)
        {
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            await context.Response.Send(
                "\n" +
                "Ping? Pong! This is a default response. Usage:\n" +
                "\n" +
                "curl http://botaddress:port/command -H \"key:passphrase\" -d \"param=value&param=value\"\n" +
                "-----OR-----\n" +
                "Invoke-RestMethod -Method 'POST' -Uri http://botaddress:port/command -Headers @{'key'='passphrase'} -Body @{'param'='value';'param'='value'}\n" +
                "\n" +
                "Valid commands:\n" +
                "    /on-commit            params: change=id, client=name, user=name, branch=name, build=trueOrFalse\n" +
                "    /build-status-update  params: jobName=...&buildID=...&buildStatus=running|succeeded|failed|unstable|aborted\n" +
                "    /shutdown             params: (none required)"
            );
		}

		// --------------------------------------
		async Task Shutdown(HttpContextBase context)
		{
			await LogAndReply("Shutting down!", HttpStatusCode.OK, context);
			runComplete.SetResult(true);
		}
		#endregion

		#region Utility Functions
		// --------------------------------------
		static System.Collections.Specialized.NameValueCollection GetQueryParams(HttpContextBase context)
        {
            Uri urlAsURI = new(context.Request.Url.Full);
            System.Collections.Specialized.NameValueCollection queryParams = HttpUtility.ParseQueryString(context.Request.DataAsString);// urlAsURI.Query);

            return queryParams;
        }

		private static async Task LogAndReply(string msg, HttpStatusCode code, HttpContextBase context)
		{
            await LogAndReply(msg, msg, code, context);
		}

		private static async Task LogAndReply(string log, string reply, HttpStatusCode code, HttpContextBase context)
		{
			await Log(log);

			context.Response.StatusCode = (int)code;
			await context.Response.Send(reply);
		}

		private static void LogSync(string message)
        {
            Console.WriteLine(message);
        }

#pragma warning disable 1998
        private static async Task Log(string message)
        {
            Console.WriteLine(message);
        }
#pragma warning restore 1998
		#endregion
	}
}
