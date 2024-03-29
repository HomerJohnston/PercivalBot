﻿using Discord;
using Perforce.P4;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PercivalBot.Core
{
	public class Commit
	{
		public string Change { get; }
		public string Client { get; }
		public string User { get; }
		public string Branch { get; set; }

		public Commit(string change, string client, string user, string branch)
		{
			Change = change;
			Client = client;
			User = user;
			Branch = branch;
		}

		// --------------------------------------
		public bool IsValid(out string error)
		{
			bool valid = true;
			error = string.Empty;

			if (Change == string.Empty)
			{
				valid = false;
				error = string.Join(", ", error, "Change unset");
			}

			if (Client == string.Empty)
			{
				valid = false;
				error = string.Join(", ", error, "Client unset");
			}

			if (User == string.Empty)
			{
				valid = false;
				error = string.Join(", ", error += "User unset");
			}

			if (Branch == string.Empty)
			{
				valid = false;
				error = string.Join(", ", error += "Branch unset");
			}

			return valid;
		}

		// --------------------------------------
		public bool HasBranch()
		{
			return Branch != string.Empty;
		}

		// --------------------------------------
		public override string ToString()
		{
			return ($"Change: {NoneOr(Change)}, Client: {NoneOr(Client)}, User: {NoneOr(User)}, Branch {NoneOr(Branch)}");
		}

		// --------------------------------------
		private string NoneOr(string s)
		{
			return s == string.Empty ? "NONE" : s;
		}

	}
}
