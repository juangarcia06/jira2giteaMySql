# Jira to Gitea Migration Script

## Introduction
Written in C# and used to migrate issues from Jira to Gitea with MySQL. The script allows you to migrate:

- Issues
- Reporters
- Assignees
- Labels
- Milestones
- Dependencies

## Usage
1. **Configuration:**

- If you are using Docker I have left a docker-compose.yaml file in the repository.

- Edit launchSettings.json. Once your credentials are there and you saved the file run the script. I recommend JetBrains Rider. If you have any problem use it's debug mode.

- The script will delete existing issues in that repository and will migrate new ones. Create the repository before you migrate data or some issues won't work.


### Launch settings.json commandLineArgs:
--RepoId put your repository Id, if it's the first repository it is 1

--JiraServer http://yourserver.ip

--JiraFilter searchSomethingInJiraAndGetTheFilterFromTheUrl

--JiraUser jiraUser

--JiraPassword putYourJiraPasswordHere

--DbServer localhost (don't put a port!!!)

--DbName gitea

--DbUser gitea

--DbUserPass gitea





2. **Customization:** Open the `Loader.cs` file and update the following parameters according to your needs:
   - **Line 16:** Adjust the limit of data to fetch by modifying the value in the `limit` variable. If you wan't to fetch all put -1
   - **Line 18:** Edit the starting issue by updating the value of the `start` variable.

3. **Code Formatting:** The `WrapToQuotes` function is used for formatting the code to avoid breaking the SQL parameters. If you get any SQL error, try adding something to the function.

4. **Migration Details:**
   - **Dependencies:** Dependencies are migrated, but since Gitea does not have tags for dependencies, they are all merged.
   - **Attachments:** Attachments are not moved in this current version of the script. Ensure you handle them separately if required.
   - **Other Data:** All other relevant data from the issues will be copied during the migration.

5. **Execution:** Run the script using your preferred C# development environment or via the command line. Make sure you have proper connectivity to both the Jira and Gitea instances as well as the MySQL database.

## Configuration/Environment

Ensure that the necessary configurations for Jira, Gitea, and MySQL are set up correctly before running the script.

Note
Please note that this is a basic example and may require additional modifications or enhancements based on your specific use case and environment.

If using docker remember to expose the port of the database to be able to connect.