using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Octokit;

namespace OctoFetch.Services
{
    public class CurlHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _curlPath;
        private readonly Action<string> _logger;

        public CurlHttpMessageHandler(Action<string> logger)
        {
            _logger = logger;
            _curlPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GitCore", "curl.exe");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!System.IO.File.Exists(_curlPath)) throw new FileNotFoundException("curl.exe not found.");

            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OctoFetch");
            if (!System.IO.Directory.Exists(tempDir)) System.IO.Directory.CreateDirectory(tempDir);

            string tempBodyFile = null;
            string tempHeaderFile = System.IO.Path.Combine(tempDir, Guid.NewGuid().ToString() + ".tmp");
            string tempOutFile = System.IO.Path.Combine(tempDir, Guid.NewGuid().ToString() + ".tmp");

            try
            {
                var args = new StringBuilder();
                args.Append("--retry 3 --retry-delay 2 --connect-timeout 20 -s -k ");
                args.Append($"-D \"{tempHeaderFile}\" ");
                args.Append($"-o \"{tempOutFile}\" ");
                args.Append($"-X {request.Method.Method} ");

                foreach (var header in request.Headers)
                    args.Append($"-H \"{header.Key}: {string.Join(", ", header.Value)}\" ");

                if (request.Content != null)
                {
                    foreach (var header in request.Content.Headers)
                        args.Append($"-H \"{header.Key}: {string.Join(", ", header.Value)}\" ");

                    byte[] bodyBytes = await request.Content.ReadAsByteArrayAsync();
                    if (bodyBytes.Length > 0)
                    {
                        tempBodyFile = System.IO.Path.Combine(tempDir, Guid.NewGuid().ToString() + ".tmp");
                        System.IO.File.WriteAllBytes(tempBodyFile, bodyBytes);
                        args.Append($"-d @\"{tempBodyFile}\" ");
                    }
                }

                args.Append($"\"{request.RequestUri.ToString()}\"");

                var psi = new ProcessStartInfo
                {
                    FileName = _curlPath,
                    WorkingDirectory = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GitCore"),
                    Arguments = args.ToString(),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process != null)
                    {
                        try { await process.WaitForExitAsync(cancellationToken); }
                        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); throw; }
                    }
                }

                var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
                if (System.IO.File.Exists(tempOutFile) && new System.IO.FileInfo(tempOutFile).Length > 0)
                    response.Content = new ByteArrayContent(System.IO.File.ReadAllBytes(tempOutFile));
                else
                    response.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                if (System.IO.File.Exists(tempHeaderFile))
                {
                    var headerLines = System.IO.File.ReadAllLines(tempHeaderFile);
                    if (headerLines.Length > 0)
                    {
                        var statusLine = headerLines[0].Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (statusLine.Length >= 2 && int.TryParse(statusLine[1], out int code))
                            response.StatusCode = (HttpStatusCode)code;

                        for (int i = 1; i < headerLines.Length; i++)
                        {
                            var line = headerLines[i];
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            var sep = line.IndexOf(':');
                            if (sep > 0)
                            {
                                string key = line.Substring(0, sep).Trim();
                                string val = line.Substring(sep + 1).Trim();
                                if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) response.Content.Headers.TryAddWithoutValidation(key, val);
                                else response.Headers.TryAddWithoutValidation(key, val);
                            }
                        }
                    }
                }

                if (!response.Content.Headers.Contains("Content-Type")) response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
                response.RequestMessage = request;
                return response;
            }
            finally
            {
                try { if (tempBodyFile != null && System.IO.File.Exists(tempBodyFile)) System.IO.File.Delete(tempBodyFile); } catch { }
                try { if (System.IO.File.Exists(tempHeaderFile)) System.IO.File.Delete(tempHeaderFile); } catch { }
                try { if (System.IO.File.Exists(tempOutFile)) System.IO.File.Delete(tempOutFile); } catch { }
            }
        }
    }

    public class CloudNode
    {
        public string Token { get; set; }
        public string RepoName { get; set; }

        [Newtonsoft.Json.JsonIgnore] public string Username { get; set; }
        [Newtonsoft.Json.JsonIgnore] public bool IsConnected { get; set; }
        [Newtonsoft.Json.JsonIgnore] public GitHubClient Client { get; set; }
        [Newtonsoft.Json.JsonIgnore] public string BadgeColor { get; set; } = "#E53935";
        [Newtonsoft.Json.JsonIgnore] public string VisibilityText { get; set; } = "Unknown";
        [Newtonsoft.Json.JsonIgnore] public bool IsPrivate { get; set; }

        public string DisplayTitle => $"{RepoName} ({Token.Substring(0, Math.Min(5, Token.Length))}...)";
    }

    public class RemoteFile
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Sha { get; set; }
        public string RawUrl { get; set; }
        public CloudNode OwnerNode { get; set; }
    }

    public class GitHubService
    {
        private readonly Action<string> _logger;
        public List<CloudNode> ActiveNodes { get; private set; } = new List<CloudNode>();
        public bool IsConnected => ActiveNodes.Any(n => n.IsConnected);

        private int _roundRobinIndex = 0;

        public GitHubService(Action<string> logger) { _logger = logger; }

        private CloudNode GetNextAvailableNode()
        {
            var connected = ActiveNodes.Where(n => n.IsConnected).ToList();
            if (connected.Count == 0) throw new Exception("No active GitHub accounts connected.");
            var node = connected[_roundRobinIndex % connected.Count];
            _roundRobinIndex++;
            return node;
        }

        public async Task<bool> InitializeNodeAsync(CloudNode node, Action<string> onSettingsLog = null)
        {
            node.BadgeColor = "#FFCA28";
            onSettingsLog?.Invoke($"⏳ Testing: [{node.RepoName}]...");

            int retries = 3;
            while (retries > 0)
            {
                try
                {
                    var connection = new Connection(new ProductHeaderValue("OctoFetch-Pro"), new Octokit.Internal.HttpClientAdapter(() => new CurlHttpMessageHandler(_logger)));
                    node.Client = new GitHubClient(connection) { Credentials = new Credentials(node.Token) };

                    var user = await node.Client.User.Current();
                    node.Username = user.Login;

                    Repository repo;
                    try
                    {
                        repo = await node.Client.Repository.Get(node.Username, node.RepoName);
                    }
                    catch (NotFoundException)
                    {
                        onSettingsLog?.Invoke($"⚙️ Creating repo '{node.RepoName}'...");
                        repo = await node.Client.Repository.Create(new NewRepository(node.RepoName) { Private = false, AutoInit = true });
                    }

                    node.IsPrivate = repo.Private;
                    node.VisibilityText = repo.Private ? "🔒 Private" : "🌐 Public";

                    await InjectWorkflowAsync(node);

                    node.IsConnected = true;
                    node.BadgeColor = "#00E676";
                    if (!ActiveNodes.Contains(node)) ActiveNodes.Add(node);

                    onSettingsLog?.Invoke($"✅ Connected: [{node.RepoName}] as {node.Username}");
                    return true;
                }
                catch (Exception ex)
                {
                    retries--;
                    if (retries == 0)
                    {
                        node.IsConnected = false;
                        node.BadgeColor = "#E53935";
                        node.VisibilityText = "Offline";
                        onSettingsLog?.Invoke($"❌ Failed [{node.RepoName}]: {ex.Message}");
                        return false;
                    }
                    onSettingsLog?.Invoke($"⚠️ Network glitch on '{node.RepoName}'. Retrying...");
                    await Task.Delay(2000);
                }
            }
            return false;
        }

        public async Task ToggleNodeVisibilityAsync(CloudNode node)
        {
            if (!node.IsConnected) throw new Exception("Node is not connected.");
            var update = new RepositoryUpdate { Name = node.RepoName, Private = !node.IsPrivate };
            var repo = await node.Client.Repository.Edit(node.Username, node.RepoName, update);
            node.IsPrivate = repo.Private;
            node.VisibilityText = repo.Private ? "🔒 Private" : "🌐 Public";
        }

        public async Task TriggerLeechAsync(string targetUrl, bool isSafe, bool isEncrypted, Action<string, string> onLinkFetched)
        {
            var node = GetNextAvailableNode();
            string rawName = Uri.UnescapeDataString(targetUrl.Split('?')[0].Split('/').Last());
            string nameNoExt = System.IO.Path.GetFileNameWithoutExtension(rawName);

            string folderName;
            if (isEncrypted)
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(nameNoExt + DateTime.Now.Ticks));
                    folderName = "ENC_" + BitConverter.ToString(hash).Replace("-", "").ToLower().Substring(0, 16);
                }
            }
            else folderName = isSafe ? Regex.Replace(nameNoExt, "[^a-zA-Z0-9]", "_") : nameNoExt;

            if (string.IsNullOrWhiteSpace(folderName)) folderName = "DL_" + DateTime.Now.Ticks;

            _logger($"🚀 [{node.RepoName}] Task started. Folder: {folderName}");

            var inputs = new Dictionary<string, object> { { "file_url", targetUrl }, { "folder_name", folderName }, { "safe_mode", isSafe.ToString().ToLower() } };

            try
            {
                await node.Client.Actions.Workflows.CreateDispatch(node.Username, node.RepoName, "smart-downloader.yml", new CreateWorkflowDispatch("main") { Inputs = inputs });
                _logger("⏳ Trigger sent. Waiting for Action to complete...");
                await MonitorAndFetchAsync(node, folderName, "smart-downloader.yml", onLinkFetched);
            }
            catch (Exception ex)
            {
                _logger($"❌ Action Trigger Error: {ex.Message}");
            }
        }

        private async Task MonitorAndFetchAsync(CloudNode node, string targetFolder, string workflowName, Action<string, string> onLinkFetched)
        {
            bool isFinished = false;
            int retryCount = 0;
            await Task.Delay(15000);

            while (!isFinished && retryCount < 90)
            {
                try
                {
                    var runs = await node.Client.Actions.Workflows.Runs.ListByWorkflow(node.Username, node.RepoName, workflowName, new WorkflowRunsRequest());
                    var latestRun = runs.WorkflowRuns.OrderByDescending(r => r.CreatedAt).FirstOrDefault();

                    if (latestRun != null)
                    {
                        if (latestRun.Status == WorkflowRunStatus.Completed)
                        {
                            if (latestRun.Conclusion == WorkflowRunConclusion.Success)
                            {
                                _logger("✅ Action completed successfully. Fetching links...");
                                await FetchLinksFromFolderAsync(node, targetFolder, onLinkFetched);
                            }
                            else
                            {
                                _logger("❌ GitHub Action failed (Timeout, Storage Limit, or Error).");
                            }
                            isFinished = true;
                        }
                        else if (latestRun.Status == WorkflowRunStatus.Queued)
                        {
                            _logger($"⏳ [{node.RepoName}] Queued: Waiting for GitHub to allocate a free server...");
                        }
                        else if (latestRun.Status == WorkflowRunStatus.InProgress)
                        {
                            _logger($"⚙️ [{node.RepoName}] In Progress: Server allocated, processing file...");
                        }
                        else
                        {
                            _logger($"🔄 [{node.RepoName}] Status: {latestRun.Status}...");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger($"⚠️ Network Error: {ex.Message}. Retrying monitor...");
                }

                if (!isFinished)
                {
                    await Task.Delay(10000);
                    retryCount++;
                }
            }

            if (!isFinished) _logger("❌ Monitor Timeout. The GitHub queue is too long. Check GitHub website directly.");
        }

        private async Task FetchLinksFromFolderAsync(CloudNode node, string folder, Action<string, string> onLinkFetched)
        {
            try
            {
                var contents = await node.Client.Repository.Content.GetAllContents(node.Username, node.RepoName, $"downloads/{folder}");
                if (contents == null) return;

                foreach (var item in contents.Where(c => c.Type == ContentType.File))
                {
                    string rawUrl = node.IsPrivate
                        ? $"https://github.com/{node.Username}/{node.RepoName}/raw/refs/heads/main/{item.Path}"
                        : $"https://raw.githubusercontent.com/{node.Username}/{node.RepoName}/main/{item.Path}";

                    onLinkFetched?.Invoke(item.Name, rawUrl);
                    _logger($"🔗 Fetched: {item.Name}");
                }
            }
            catch (Exception ex) { _logger($"❌ Fetch Error: {ex.Message}"); }
        }

        public async Task<List<RemoteFile>> GetAllCloudFilesAggregatedAsync()
        {
            var allFiles = new List<RemoteFile>();
            var tasks = new List<Task>();

            foreach (var node in ActiveNodes.Where(n => n.IsConnected))
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var contents = await node.Client.Repository.Content.GetAllContents(node.Username, node.RepoName, "downloads");
                        if (contents == null) return;

                        foreach (var item in contents.Where(c => c.Type == ContentType.Dir))
                        {
                            var subContents = await node.Client.Repository.Content.GetAllContents(node.Username, node.RepoName, item.Path);
                            if (subContents == null) continue;

                            foreach (var subItem in subContents)
                            {
                                lock (allFiles)
                                {
                                    string rawUrl = node.IsPrivate
                                        ? $"https://github.com/{node.Username}/{node.RepoName}/raw/refs/heads/main/{subItem.Path}"
                                        : $"https://raw.githubusercontent.com/{node.Username}/{node.RepoName}/main/{subItem.Path}";

                                    allFiles.Add(new RemoteFile
                                    {
                                        Name = subItem.Name,
                                        Path = subItem.Path,
                                        Sha = subItem.Sha,
                                        OwnerNode = node,
                                        RawUrl = rawUrl
                                    });
                                }
                            }
                        }
                    }
                    catch (NotFoundException) { }
                }));
            }

            await Task.WhenAll(tasks);
            return allFiles;
        }

        public async Task DeleteFilesAsync(List<RemoteFile> files)
        {
            var filesByNode = files.GroupBy(f => f.OwnerNode);
            var tasks = new List<Task>();

            foreach (var group in filesByNode)
            {
                var node = group.Key;
                foreach (var f in group)
                {
                    tasks.Add(node.Client.Repository.Content.DeleteFile(node.Username, node.RepoName, f.Path, new DeleteFileRequest($"Delete {f.Name}", f.Sha)));
                }
            }
            await Task.WhenAll(tasks);
        }

        private async Task InjectWorkflowAsync(CloudNode node)
        {
            string yamlContent = @"
name: 'Cloud Leecher Engine'
env:
  FORCE_JAVASCRIPT_ACTIONS_TO_NODE24: true
on:
  workflow_dispatch:
    inputs:
      file_url:
        required: true
        type: string
      folder_name:
        required: true
        type: string
      safe_mode:
        required: true
        type: string
permissions:
  contents: write
jobs:
  leech:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Process File
        run: |
          sudo apt-get update && sudo apt-get install -y zip
          URL=""${{ github.event.inputs.file_url }}""
          FOLDER=""${{ github.event.inputs.folder_name }}""
          SAFE_MODE=""${{ github.event.inputs.safe_mode }}""
          
          RAW_NAME=$(basename ""${URL%%\?*}"" | sed 's/%20/ /g')
          EXT=""${RAW_NAME##*.}""
          
          if [ ""$SAFE_MODE"" == ""true"" ]; then
            FINAL_FILE=""$FOLDER.$EXT""
          else
            NAME_NO_EXT=""${RAW_NAME%.*}""
            FINAL_FILE=""$NAME_NO_EXT.$EXT""
          fi
          
          curl -L --fail --retry 5 -A ""Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"" --progress-bar -o ""temp_download"" ""$URL""
          
          mkdir -p wrap_dir
          mv ""temp_download"" ""wrap_dir/$FINAL_FILE""
          cd wrap_dir
          zip -0 ""../$FOLDER.zip"" ""$FINAL_FILE""
          cd ..
          
          mkdir -p ""downloads/$FOLDER""
          split -b 90M -d -a 3 ""$FOLDER.zip"" ""downloads/$FOLDER/chunk_""
          
          a=1
          for i in downloads/$FOLDER/chunk_*; do
            new=$(printf ""downloads/$FOLDER/$FOLDER.zip.%03d"" ""$a"")
            mv -- ""$i"" ""$new""
            let a=a+1
          done
          
          git config user.name ""OctoFetchBot""; git config user.email ""bot@octofetch.local""
          git pull origin main || true
          git add downloads/
          git commit -m ""Add $FOLDER ZIP Archive"" || echo ""No changes""
          git push";

            try
            {
                var files = await node.Client.Repository.Content.GetAllContents(node.Username, node.RepoName, ".github/workflows");
                var file = files?.FirstOrDefault(f => f.Name == "smart-downloader.yml");
                if (file != null) return;
            }
            catch (NotFoundException) { }

            try { await node.Client.Repository.Content.CreateFile(node.Username, node.RepoName, ".github/workflows/smart-downloader.yml", new CreateFileRequest("Init Architecture", yamlContent)); } catch { }
        }
    }
}