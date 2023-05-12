using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using GenerateWhatsnew.Jira;
using Microsoft.Data.SqlClient;
using MySql.Data.MySqlClient;

namespace ImportGitea;

internal class Importer
{
	public const string ComonentLabelPrefix = "comp/";
	public const string IssueTypeLabelPrefix = "kind/";
	public const string DefaultUserId = "1";

	private readonly long _repoId;
	private readonly MySqlConnection _sqlConnection;

	private readonly HashSet<string> _labels = new();
	private readonly Dictionary<string, FixVersion> _versions = new();
	private readonly List<CommentInfo> _comments = new();
	private readonly List<Issue> _issues = new();
	private readonly Dictionary<string, long> _userIds = new();
	private readonly Dictionary<string, IssueInfo> _issueInfo = new();

	private long _index = 0;

	/// <summary>
	/// Specifies that existing issues in repo must be removed.
	/// If set index will be taken from JIRA issue key.
	/// </summary>
	public bool RemoveExistingIssues { get; set; } = true;

	public Importer(long repoId, 
		MySqlConnection sqlConnection)
	{
		_repoId = repoId;
		_sqlConnection = sqlConnection;
	}

	public void Add(Issue issue)
	{
		if (issue.Fields != null)
		{
			CreateUser(issue.Fields.Assignee);
			CreateUser(issue.Fields.Creator);
			CreateUser(issue.Fields.Reporter);

			if (issue.Fields.Labels?.Length > 0)
			{
				foreach (var label in issue.Fields.Labels)
				{
					_labels.Add(label);
				}
			}

			if (issue.Fields.Components?.Length > 0)
			{
				foreach (var label in issue.Fields.Components.Select(x => x.Name))
				{
					string compLabel = ComonentLabelPrefix + label;

					if (!_labels.Contains(compLabel))
					{
						_labels.Add(compLabel);
					}
				}
			}

			if (!string.IsNullOrEmpty(issue.Fields.issuetype?.Name))
			{
				var typeLabel = IssueTypeLabelPrefix + issue.Fields.issuetype?.Name;

				_labels.Add(typeLabel);
			}

			if (issue.Fields.FixVersions?.Length > 0)
			{
				foreach (var fixVersion in issue.Fields.FixVersions)
				{
					_versions.TryAdd(fixVersion.Name, fixVersion);
				}
			}
		}

		_issues.Add(issue);
	}

	public void Add(IssueDetails details)
	{
		if (details.Fields.Comment?.Comments?.Length > 0)
		{
			foreach (var comment in details.Fields.Comment.Comments)
			{
				CreateUser(comment.Author);

				var commentInfo = new CommentInfo(details.Key,
					comment.Author.EmailAddress,
					comment.Body, 
					DateTimeOffset.Parse(comment.Created),
					DateTimeOffset.Parse(comment.Updated));

				_comments.Add(commentInfo);
			}
		}
	}

	internal void Import()
	{
		_index = GetMaxIndex() + 1;

		Console.WriteLine("Creating labels");
		GenerateLabels();

		Console.WriteLine("Creating versions");
		GenerateVersions();

		Console.WriteLine("Creating issues");
		GenerateIssues();

		Console.WriteLine("Setting labels");
		SetLabels();

		Console.WriteLine("Setting assignees");
		SetAssignees();

		Console.WriteLine("Setting dependencies");
		SetDependencies();

		Console.WriteLine("Setting comments");
		SetComments();

		Execute($"UPDATE issue_index SET max_index = {_issueInfo.Select(x => x.Value.GiteaIndex).Max()} where group_id = {_repoId}");
	}

	private long GetMaxIndex()
	{
		using var cmd = _sqlConnection.CreateCommand();
		cmd.CommandText = $@"SELECT MAX(""index"") FROM ISSUE WHERE repo_id={_repoId}";

		var result = cmd.ExecuteScalar();

		if (result is long lresult)
		{
			return lresult;
		}
		else
		{
			return 0;
		}
	}

	private void GenerateLabels()
	{
		Execute($"DELETE FROM `label` WHERE repo_id = {_repoId};");

		foreach (var label in _labels)
		{
			ExecuteInsert("label", "repo_id, name", $"{_repoId}, {WrapToQuotes(label)}");
		}
	}



	private void GenerateVersions()
	{
		Execute($"DELETE FROM milestone WHERE repo_id = {_repoId};");

		foreach (var version in _versions.Values)
		{
			DateTimeOffset? releaseDate = null;

			if (DateTimeOffset.TryParse(version.ReleaseDate, out var rd))
			{
				releaseDate = rd;
			}

			ExecuteInsert("milestone", "repo_id, name, is_closed, closed_date_unix, deadline_unix, content", 
				$"{_repoId}, {WrapToQuotes(version.Name)}, {(version.Released ? 1 : 0)}"
				+ $", {GetNullable(releaseDate?.ToUnixTimeSeconds())}, {releaseDate?.ToUnixTimeSeconds() ?? 253402264799}"
				+ $", {WrapToQuotes(ConvertContent(version.Description ?? string.Empty))}");
		}
	}

	private static string GetNullable<T>(T value)
		=> value?.ToString() ?? "NULL";

	private void SetDependencies()
	{
		foreach (var issue in _issues)
		{
			if (issue.Fields?.issuelinks?.Length > 0)
			{
				foreach (var link in issue.Fields.issuelinks)
				{
					if (link.InwardIssue == null)
					{
						continue;
					}

					var issueId = _issueInfo[issue.Key].GiteaId;
					var dependency = _issueInfo.GetValueOrDefault(link.InwardIssue.Key);

					if (dependency != null)
					{
						try
						{
							ExecuteInsert("issue_dependency", "user_id, issue_id, dependency_id", $"1, {issueId}, {dependency.GiteaId}");
						}
						catch (DbException exc)
						{
							Console.WriteLine("Duplicate entry skipped.");
						}
					}
				}
			}
		}
	}

	private void SetComments()
	{
		foreach (var comment in _comments)
		{
			ExecuteInsert("comment",
				"`type`, poster_id, issue_id, created_unix, updated_unix, content",
				$"0, {GetUserId(comment.Author) ?? DefaultUserId}, {_issueInfo[comment.Key].GiteaId}, {comment.Created.ToUnixTimeSeconds()}, {comment.Updated.ToUnixTimeSeconds()}, {WrapToQuotes(ConvertContent(comment.Body))}");
		}
	}

	private void SetLabels()
	{
		void AddLabel(long issueId, string label)
		{
			try
			{
				ExecuteInsert("issue_label", "label_id, issue_id",
					$"(SELECT id FROM label WHERE name={WrapToQuotes(label)} AND repo_id={_repoId} LIMIT 1), {issueId}");
			}
			catch (DbException exc)
			{
				Console.Error.WriteLine("Cannot add label." + Environment.NewLine + exc.ToString());
			}
		}

		foreach (var issue in _issues)
		{
			long issueId = _issueInfo[issue.Key].GiteaId;

			if (issue.Fields?.Labels != null)
			{
				foreach (var label in issue.Fields.Labels)
				{
					AddLabel(issueId, label);
				}
			}

			if (issue.Fields?.Components != null)
			{
				foreach (var label in issue.Fields.Components.Select(x => x.Name))
				{
					AddLabel(issueId, ComonentLabelPrefix + label);
				}
			}

			if (issue.Fields?.issuetype != null)
			{
				AddLabel(issueId, IssueTypeLabelPrefix + issue.Fields.issuetype.Name);
			}
		}
	}

	private void SetAssignees()
	{
		foreach (var issue in _issues)
		{
			if (issue.Fields?.Assignee == null)
			{
				continue;
			}

			string? assignee = GetUserId(issue.Fields.Assignee.EmailAddress);

			if (string.IsNullOrEmpty(assignee))
			{
				continue;
			}

			long issueId = _issueInfo[issue.Key].GiteaId;

			ExecuteInsert("issue_assignees", "assignee_id, issue_id", $"{assignee}, {issueId}");
		}
	}

	private void Execute(string command)
	{
		using var cmd = _sqlConnection.CreateCommand();
		cmd.CommandText = command;
		cmd.ExecuteNonQuery();
	}

	private long ExecuteInsert(string table, string fields, string values)
	{
		using var cmd = _sqlConnection.CreateCommand();
		cmd.CommandText = $"INSERT INTO `{table}` ({fields}) VALUES ({values}); SELECT LAST_INSERT_ID();";

		using (var reader = cmd.ExecuteReader())
		{
			if (reader.Read())
			{
				ulong insertedId = Convert.ToUInt64(reader[0]);
				long id = (long)insertedId;
				return id;
			}
		}

		throw new Exception("Failed to retrieve the last inserted ID.");
	}




	class IssueInfo
	{
		public long GiteaId { get; set; }
		public long GiteaIndex { get; set; }
	}
	
	private void GenerateIssues()
	{
		if (RemoveExistingIssues)
		{
			Execute($"DELETE FROM issue WHERE repo_id = {_repoId};");
		}


		foreach (var issue in _issues)
		{
			if (issue.Key == null)
			{
				continue;
			}

			int dashIndex = issue.Key.IndexOf("-");
			string jiraId = issue.Key.Substring(dashIndex + 1);

			// Calculate new gitea index by incrementing max value.
			string giteaIndex = _index++.ToString();
			// If issues must be cleared, then we can calculate giteaIndex from jira key.
			if (RemoveExistingIssues)
			{
				giteaIndex = jiraId;
			}
        
			var created = DateTimeOffset.Parse(issue.Fields.Created);
			var updated = DateTimeOffset.Parse(issue.Fields.Updated);

			var fixVersion = issue.Fields.FixVersions?.FirstOrDefault()?.Name ?? "<NONE>";
			
			long id = ExecuteInsert("issue",
					@"is_pull, repo_id, `index`, poster_id, name, content"
					+ ", is_closed, original_author, original_author_id, created_unix, updated_unix, closed_unix, milestone_id",

					$"0, {_repoId}, {giteaIndex}, {GetUserId(issue.Fields?.Reporter?.EmailAddress) ?? DefaultUserId}, {WrapToQuotes(GetIssueName(issue))}, {WrapToQuotes(GetIssueDescription(issue))}"
					+ $", {GetClosed(issue)}, '', 0, {created.ToUnixTimeSeconds()}, {updated.ToUnixTimeSeconds()}, {updated.ToUnixTimeSeconds()}"
					+ $", (SELECT id FROM milestone WHERE name={WrapToQuotes(fixVersion)} AND repo_id={_repoId})");
				

			_issueInfo[issue.Key] = new IssueInfo
			{
				GiteaIndex = long.Parse(giteaIndex),
				GiteaId = id
			};

		}
	}
	
	

private long ExecuteInsert(string table, string fields, string parameterNames, params MySqlParameter[] parameters)
{
    using var cmd = _sqlConnection.CreateCommand();
    cmd.CommandText = $"INSERT INTO `{table}` ({fields}) VALUES ({parameterNames}); SELECT LAST_INSERT_ID();";
    cmd.Parameters.AddRange(parameters);

    using (var reader = cmd.ExecuteReader())
    {
        if (reader.Read())
        {
            ulong insertedId = Convert.ToUInt64(reader[0]);
            long id = (long)insertedId;
            return id;
        }
    }

    throw new Exception("Failed to retrieve the last inserted ID.");
}



	private static string GetIssueDescription(Issue issue)
	{
		var content = new StringBuilder();

		if (!string.IsNullOrEmpty(issue.Fields?.Description))
		{
			content.Append(ConvertContent(issue.Fields.Description));
		}

		// Add custom fields

		if (!string.IsNullOrEmpty(issue.Fields?.ReproduceSteps))
		{
			content.AppendLine();
			content.AppendLine("## Reproduce steps");
			content.AppendLine();
			content.Append(ConvertContent(issue.Fields.ReproduceSteps));
		}

		if (!string.IsNullOrEmpty(issue.Fields?.Forum))
		{
			content.AppendLine();
			content.AppendLine("## Forum");
			content.AppendLine();
			content.AppendLine("- " + issue.Fields.Forum);
			content.AppendLine();
		}

		if (!string.IsNullOrEmpty(issue.Fields?.ReleaseComment))
		{
			content.AppendLine();
			content.AppendLine("## Comment");
			content.AppendLine();
			content.AppendLine(issue.Fields.ReleaseComment);
		}

		return content.ToString();
	}

	private static string ConvertContent(string content)
	{
		var result = Regex.Replace(content, @"\{noformat\}", "\r\n```\r\n");

		result = Regex.Replace(result, @"\{quote\}", "\r\n```\r\n");

		result = Regex.Replace(result, @"^h2\.\s+", "## ");

		result = Regex.Replace(result, @"\[(.+)\|(.+)\]", "[$1]($2)");

		return result;
	}

	private string GetIssueName(Issue issue)
	{
		string name = issue.Fields?.Summary ?? string.Empty;

		if (!RemoveExistingIssues)
		{
			// If we leave existing issues intact, add JIRA key to issue header.
			// Otherwise we will loose JIRA key, which could be used in commit messages.

			name = $"[{issue.Key}] {name}";

		}

		if (name.Length > 255)
		{
			name = name[0..255];
		}

		return name;
	}


	private static int GetClosed(Issue issue)
	{
		return (issue.Fields?.Resolution != null) ? 1 : 0;
	}
	
	private static string WrapToQuotes(string value)
	{
		value = value.Replace("'", "''");
		return $"'{value.Replace("\\", "\\\\")}'";
	}

	private string? GetUserId(string? email)
	{
		if (email == null || !_userIds.ContainsKey(email))
			return null;

		return _userIds.GetValueOrDefault(email).ToString();
	}

	private void CreateUser(UserInfo? user)
	{
		if (user == null || _userIds.ContainsKey(user.EmailAddress))
		{
			return;
		}

		// Find existing user

		using var cmd = _sqlConnection.CreateCommand();
		cmd.CommandText = $"SELECT id FROM `user` WHERE email = {WrapToQuotes(user.EmailAddress)}";
		bool hasExistingUser = false;

		using (var reader = cmd.ExecuteReader())
		{
			if (reader.Read())
			{
				long id = (long)reader["id"];
				_userIds[user.EmailAddress] = id;
				hasExistingUser = true;
			}
		}

		if (!hasExistingUser)
			using (var insertCmd = _sqlConnection.CreateCommand())
			{
				insertCmd.CommandText = $"INSERT INTO `user` (type, name, lower_name, email, passwd, avatar, avatar_email) " +
				                        $"VALUES (@type, @name, @lowerName, @email, @passwd, @avatar, @avatarEmail)";

				insertCmd.Parameters.AddWithValue("@type", 0);
				insertCmd.Parameters.AddWithValue("@name", user.Name);
				insertCmd.Parameters.AddWithValue("@lowerName", user.Name.ToLowerInvariant());
				insertCmd.Parameters.AddWithValue("@email", user.EmailAddress);
				insertCmd.Parameters.AddWithValue("@passwd", "giteaa");
				insertCmd.Parameters.AddWithValue("@avatar", "https://www.gravatar.com/avatar/f584ef0e58a6058a8dc2c9668df3146e?d=mm&s=32");
				insertCmd.Parameters.AddWithValue("@avatarEmail", user.EmailAddress);

				insertCmd.ExecuteNonQuery();

				long id = insertCmd.LastInsertedId;
				_userIds[user.EmailAddress] = id;
			}
	}
}

public record CommentInfo(
	string Key,
	string? Author,
	string Body,
	DateTimeOffset Created,
	DateTimeOffset Updated);

