using CalqFramework.Relay.Cloud;

namespace CalqFramework.Relay.GitHub;

/// <summary>
///     Generates GitHub Actions workflow files for Calq Relay operations.
/// </summary>
public static class WorkflowScaffolder {
    // Constants avoid C# interpolation conflicts with GitHub Actions ${{ }} expressions
    private const string SECRETS = "${{ secrets.GITHUB_TOKEN }}";
    private const string AZURE_CREDS = "${{ secrets.AZURE_CREDENTIALS }}";
    private const string GCP_CREDS = "${{ secrets.GCP_CREDENTIALS }}";
    private const string PR_NUM = "${{ github.event.number }}";
    private const string ALL_SECRETS = "${{ toJson(secrets) }}";
    private const string RUN_ID = "${{ github.run_id }}";
    private const string INPUT_SVC = "${{ inputs.service }}";
    private const string INPUT_SRC = "${{ inputs.source }}";
    private const string INPUT_TGT = "${{ inputs.target }}";
    private const string INPUT_ENV = "${{ inputs.environment }}";
    private const string INPUT_CMD = "${{ inputs.command }}";

    /// <summary>
    ///     Scaffolds all workflows. Only creates files that don't already exist.
    /// </summary>
    public static void Scaffold(string workflowsDir, PlatformConfig cfg, string clusterProvider) {
        Directory.CreateDirectory(workflowsDir);

        string login = GetLoginStep(clusterProvider, cfg);
        string deps = GetDepsStep(clusterProvider);
        bool hasBG = cfg.Services.Values.Any(s => s.BlueGreen);
        bool hasNonBG = cfg.Services.Values.Any(s => !s.BlueGreen);
        string choices = string.Join(", ", cfg.Services.Keys);

        WriteIfMissing(Path.Combine(workflowsDir, "deploy.yaml"), Deploy(login, deps, cfg));
        WriteIfMissing(Path.Combine(workflowsDir, "pr-environment.yaml"), PrEnvironment(login, deps));
        if (hasNonBG) {
            WriteIfMissing(Path.Combine(workflowsDir, "promote.yaml"), Promote(login, deps, choices));
        }

        if (hasBG) {
            WriteIfMissing(Path.Combine(workflowsDir, "stage.yaml"), Stage(login, deps, choices));
        }

        if (hasBG) {
            WriteIfMissing(Path.Combine(workflowsDir, "switchover.yaml"), Switchover(login, deps, choices));
        }

        WriteIfMissing(Path.Combine(workflowsDir, "relay.yaml"), Generic(login, deps));
    }

    private static string GetLoginStep(string provider, PlatformConfig cfg) {
        // Check for custom workflow login from any cluster config
        foreach (EnvironmentConfig env in cfg.Environments.Values)
            foreach (ClusterConfig c in env.Clusters.Values) {
                if (c.WorkflowLogin != null && !string.IsNullOrEmpty(c.WorkflowLogin.Action)) {
                    string step = $"      - uses: {c.WorkflowLogin.Action}\n";
                    if (c.WorkflowLogin.With.Count > 0) {
                        step += "        with:\n";
                        foreach (KeyValuePair<string, string> kvp in c.WorkflowLogin.With) {
                            step += $"          {kvp.Key}: {kvp.Value}\n";
                        }
                    }

                    return step.TrimEnd('\n');
                }
            }

        return provider.ToLowerInvariant() switch {
            "azure" or "aks" => "      - uses: azure/login@v1\n" + "        with:\n" + $"          creds: {AZURE_CREDS}",
            "gcp" or "gke" => "      - uses: google-github-actions/auth@v2\n" + "        with:\n" + $"          credentials_json: {GCP_CREDS}",
            _ => "      # Add cloud authentication step for your provider"
        };
    }

    private static string GetDepsStep(string provider) {
        string gkePlugin = provider.ToLowerInvariant() is "gcp" or "gke" ? "\n          gcloud components install gke-gcloud-auth-plugin --quiet" : "";
        return "      - name: Install dependencies\n" + "        run: |\n" + "          curl -sSL -o /usr/local/bin/argocd https://github.com/argoproj/argo-cd/releases/latest/download/argocd-linux-amd64\n" + "          chmod +x /usr/local/bin/argocd" +
               gkePlugin;
    }

    private static string Deploy(string login, string deps, PlatformConfig cfg) {
        string steps = string.Join(
            "\n",
            cfg.Services.Select(kvp => "      - uses: calq-framework/relay@latest\n" + "        with:\n" + $"          command: deploy --service {kvp.Key} --environment dev\n" + "        env:\n" + $"          GH_TOKEN: {SECRETS}"));

        string secrets = string.Join(
            "\n",
            cfg.Services.Select(kvp =>
                $"      - name: Sync secrets for {kvp.Key}\n" + "        run: |\n" +
                $"          ARGS=$(echo '{ALL_SECRETS}' | jq -r 'to_entries[] | select(.key | startswith(\"K8S_\")) | \"--from-literal=\\(.key | ltrimstr(\"K8S_\"))=\\(.value)\"' | tr '\\n' ' ')\n" +
                $"          [ -n \"$ARGS\" ] && kubectl create secret generic {kvp.Key}-secrets -n ${{{{ env.NAMESPACE }}}} $ARGS --dry-run=client -o yaml | kubectl apply -f -\n"));

        return "name: Deploy to DEV\n" + "on:\n" + "  push:\n" + "    branches: [main]\n" + "jobs:\n" + "  deploy:\n" + "    runs-on: ubuntu-latest\n" + "    permissions:\n" + "      contents: write\n" + "    steps:\n" +
               "      - uses: actions/checkout@v6\n" + "      - uses: actions/setup-dotnet@v4\n" + login + "\n" + deps + "\n" + steps + "\n" + secrets + "\n";
    }

    private static string PrEnvironment(string login, string deps) =>
        "name: PR Environment\n" + "on:\n" + "  pull_request:\n" + "    types: [opened, synchronize, reopened, closed]\n" + "    paths-ignore:\n" + "      - '.relay/**'\n" + "      - '.github/**'\n" + "      - 'k8s/**'\n" + "      - '*.md'\n" +
        "jobs:\n" + "  deploy:\n" + "    if: github.event.action != 'closed'\n" + "    runs-on: ubuntu-latest\n" + "    steps:\n" + "      - uses: actions/checkout@v6\n" + "      - uses: actions/setup-dotnet@v4\n" + login + "\n" + deps + "\n" +
        "      - uses: calq-framework/relay@latest\n" + "        with:\n" + $"          command: environment clone pr-{PR_NUM} --base-environment dev\n" + "  cleanup:\n" + "    if: github.event.action == 'closed'\n" + "    runs-on: ubuntu-latest\n" +
        "    steps:\n" + "      - uses: actions/checkout@v6\n" + "      - uses: actions/setup-dotnet@v4\n" + login + "\n" + deps + "\n" + "      - uses: calq-framework/relay@latest\n" + "        with:\n" +
        $"          command: environment remove pr-{PR_NUM} --base-environment dev\n";

    private static string Promote(string login, string deps, string choices) =>
        "name: Promote to PROD\n" + "on:\n" + "  workflow_dispatch:\n" + "    inputs:\n" + "      service:\n" + "        required: true\n" + "        type: choice\n" + "        options: [" + choices + "]\n" + "      source:\n" +
        "        required: true\n" + "        default: dev\n" + "      target:\n" + "        required: true\n" + "        default: prod\n" + "concurrency:\n" + "  group: relay-promote\n" + "  cancel-in-progress: false\n" + "jobs:\n" + "  promote:\n" +
        "    runs-on: ubuntu-latest\n" + "    permissions:\n" + "      contents: write\n" + "    steps:\n" + "      - uses: actions/checkout@v6\n" + "      - uses: actions/setup-dotnet@v4\n" + login + "\n" + deps + "\n" +
        "      - uses: calq-framework/relay@latest\n" + "        with:\n" + $"          command: promote --service {INPUT_SVC} --source {INPUT_SRC} --target {INPUT_TGT}\n" + "        env:\n" + $"          GH_TOKEN: {SECRETS}\n";

    private static string Stage(string login, string deps, string choices) =>
        "name: Stage to PROD\n" + "on:\n" + "  workflow_dispatch:\n" + "    inputs:\n" + "      service:\n" + "        required: true\n" + "        type: choice\n" + "        options: [" + choices + "]\n" + "      source:\n" + "        required: true\n" +
        "        default: dev\n" + "      target:\n" + "        required: true\n" + "        default: prod\n" + "concurrency:\n" + "  group: relay-stage\n" + "  cancel-in-progress: false\n" + "jobs:\n" + "  stage:\n" + "    runs-on: ubuntu-latest\n" +
        "    permissions:\n" + "      contents: write\n" + "    steps:\n" + "      - uses: actions/checkout@v6\n" + "      - uses: actions/setup-dotnet@v4\n" + login + "\n" + deps + "\n" + "      - uses: calq-framework/relay@latest\n" +
        "        with:\n" + $"          command: stage --service {INPUT_SVC} --source {INPUT_SRC} --target {INPUT_TGT}\n" + "        env:\n" + $"          GH_TOKEN: {SECRETS}\n";

    private static string Switchover(string login, string deps, string choices) =>
        "name: Switchover\n" + "on:\n" + "  workflow_dispatch:\n" + "    inputs:\n" + "      service:\n" + "        required: true\n" + "        type: choice\n" + "        options: [" + choices + "]\n" + "      environment:\n" +
        "        required: true\n" + "        default: prod\n" + "concurrency:\n" + "  group: relay-switchover\n" + "  cancel-in-progress: false\n" + "jobs:\n" + "  switchover:\n" + "    runs-on: ubuntu-latest\n" + "    permissions:\n" +
        "      contents: write\n" + "    steps:\n" + "      - uses: actions/checkout@v6\n" + "      - uses: actions/setup-dotnet@v4\n" + login + "\n" + deps + "\n" + "      - uses: calq-framework/relay@latest\n" + "        with:\n" +
        $"          command: switchover --service {INPUT_SVC} --environment {INPUT_ENV}\n" + "        env:\n" + $"          GH_TOKEN: {SECRETS}\n";

    private static string Generic(string login, string deps) =>
        "name: Calq Relay\n" + "on:\n" + "  workflow_dispatch:\n" + "    inputs:\n" + "      command:\n" + "        required: true\n" + "        description: 'calq-relay command (e.g., restart --service web --environment prod)'\n" + "jobs:\n" +
        "  run:\n" + "    runs-on: ubuntu-latest\n" + "    permissions:\n" + "      contents: write\n" + "      pull-requests: write\n" + "    steps:\n" + "      - uses: actions/checkout@v6\n" + "      - uses: actions/setup-dotnet@v4\n" + login + "\n" +
        deps + "\n" + "      - uses: calq-framework/relay@latest\n" + "        with:\n" + $"          command: {INPUT_CMD}\n" + "      - name: Commit changes if any\n" + "        run: |\n" + "          git diff --quiet && exit 0\n" +
        $"          git checkout -b relay/{RUN_ID}\n" + "          git add -A\n" + $"          git commit -m \"calq-relay: {INPUT_CMD}\"\n" + $"          git push -u origin relay/{RUN_ID}\n" +
        $"          gh pr create --title \"calq-relay: {INPUT_CMD}\" --body \"\"\n" + "        env:\n" + $"          GH_TOKEN: {SECRETS}\n";

    private static void WriteIfMissing(string path, string content) {
        if (!File.Exists(path)) {
            File.WriteAllText(path, content);
            Console.Error.WriteLine($"Generated {path}");
        }
    }
}
