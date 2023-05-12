using System.Net.Http.Json;
using System.Text;
using System.Web;

namespace GenerateWhatsnew.Jira;

internal class Loader
{
	private readonly HttpClient _client;

	public Loader(HttpClient client)
	{
		_client = client;
	}

	public async Task<List<Issue>> LoadIssues(string filterId, int limit = -1)
	{
		long start = 0;

		var issues = new List<Issue>();

		var filter = HttpUtility.UrlEncode(BuildFilter(filterId));

		while (true)
		{
			var response = await _client.GetAsync($"/rest/api/2/search?startAt={start}&jql={filter}");
			//Console.WriteLine(await response.Content.ReadAsStringAsync());
			var r = await response.Content.ReadFromJsonAsync<IssueResponse>();

			if (r == null)
			{
				break;
			}

			if (r.Issues != null)
			{
				issues.AddRange(r.Issues);

				Console.WriteLine($"Loaded {issues.Count} issues.");
			}

			long received = r.StartAt + r.MaxResults;

			if (received >= r.Total)
			{
				break;
			}

			start = received;

			if (limit > 0 && issues.Count >= limit)
			{
				break;
			}			
		}

		return issues;
	}

	public async Task<IssueDetails?> LoadDetails(string uri)
	{
		var response = await _client.GetAsync(uri);

		return await response.Content.ReadFromJsonAsync<IssueDetails>();
	}

	private static string BuildFilter(string filterId)
	{
		var sb = new StringBuilder();

		sb.AppendFormat("filter = {0}", filterId);

		return sb.ToString();
	}
}
