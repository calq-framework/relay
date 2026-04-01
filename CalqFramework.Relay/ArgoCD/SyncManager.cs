namespace CalqFramework.Relay.ArgoCD;

/// <summary>
///     Triggers and monitors ArgoCD sync operations with failure detection
///     and diagnostic output on deployment failures.
/// </summary>
public static class SyncManager {
    public static void Sync(string appName, bool prune = true, int timeoutSeconds = 300) {
        string pruneFlag = prune ? "--prune" : "";
        RUN($"argocd app sync {appName} {pruneFlag} --port-forward --port-forward-namespace argocd --timeout {timeoutSeconds}");
    }

    /// <summary>
    ///     Waits for an ArgoCD Application to reach Healthy status.
    ///     On failure, detects degraded deployments and prints pod logs
    ///     for crash-looping or failed containers.
    /// </summary>
    public static void WaitHealthy(string appName, int timeoutSeconds = 300) {
        try {
            RUN($"argocd app wait {appName} --health --timeout {timeoutSeconds} --port-forward --port-forward-namespace argocd");
        } catch {
            Console.Error.WriteLine($"Health check failed for {appName}. Diagnosing...");
            DiagnoseFailure(appName);
            throw;
        }
    }

    public static string GetStatus(string appName) =>
        CMD($"argocd app get {appName} --port-forward --port-forward-namespace argocd -o json")
            .Trim();

    public static void Refresh(string appName) => RUN($"argocd app get {appName} --refresh --port-forward --port-forward-namespace argocd");

    /// <summary>
    ///     Configures ArgoCD to ignore specific JSON paths during diff comparison.
    ///     Used to prevent ArgoCD from reverting imperative changes like switchover.
    /// </summary>
    public static void IgnoreDiff(string appName, string jsonPointer) => RUN($"argocd app set {appName} --port-forward --port-forward-namespace argocd --ignore-diff 'jsonPointers=[\"{jsonPointer}\"]'");

    /// <summary>
    ///     Sets the container image override on an ArgoCD Application via Kustomize parameter.
    ///     This avoids committing image tags to Git  EArgoCD stores the override in its Application spec.
    /// </summary>
    public static void SetImage(string appName, string imageName, string imageUrl) => RUN($"argocd app set {appName} --kustomize-image {imageName}={imageUrl} --port-forward --port-forward-namespace argocd");

    /// <summary>
    ///     Reads the current image from the live deployment in the cluster via kubectl.
    /// </summary>
    public static string GetImage(string containerName, string ns) {
        try {
            return CMD($"kubectl get deployment -l app={containerName} -n {ns} -o jsonpath='{{.items[0].spec.template.spec.containers[0].image}}'")
                .Trim()
                .Trim('\'');
        } catch {
            return "";
        }
    }

    /// <summary>
    ///     Diagnoses a failed deployment by finding unhealthy pods and printing
    ///     their logs and events.
    /// </summary>
    private static void DiagnoseFailure(string appName) {
        try {
            // Get the app's target namespace
            string status = CMD($"argocd app get {appName} --port-forward --port-forward-namespace argocd -o json");

            // Find pods that aren't Running
            string ns = "";
            foreach (string line in status.Split('\n')) {
                if (line.Contains("\"namespace\"") && line.Contains(':')) {
                    ns = line.Split(':')
                        .Last()
                        .Trim()
                        .Trim('"', ',', ' ');
                    break;
                }
            }

            if (string.IsNullOrEmpty(ns)) {
                return;
            }

            Console.Error.WriteLine($"--- Checking pods in {ns} ---");

            // Find non-running pods
            try {
                string pods = CMD($"kubectl get pods -n {ns} --field-selector=status.phase!=Running,status.phase!=Succeeded -o wide");
                if (!string.IsNullOrWhiteSpace(pods)) {
                    Console.Error.WriteLine("Non-running pods:");
                    Console.Error.WriteLine(pods);
                }
            } catch {
            }

            // Get events for the namespace (shows scheduling failures, pull errors, etc.)
            try {
                string events = CMD($"kubectl get events -n {ns} --sort-by=.lastTimestamp --field-selector type=Warning");
                if (!string.IsNullOrWhiteSpace(events)) {
                    Console.Error.WriteLine("Warning events:");
                    Console.Error.WriteLine(events);
                }
            } catch {
            }

            // Find crash-looping pods and print their logs
            try {
                string crashPods = CMD($"kubectl get pods -n {ns} -o jsonpath='{{range .items[*]}}{{range .status.containerStatuses[*]}}{{if .state.waiting}}{{$.metadata.name}} {{end}}{{end}}{{end}}'");
                foreach (string pod in crashPods.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                    Console.Error.WriteLine($"--- Logs for {pod} ---");
                    try {
                        string logs = CMD($"kubectl logs {pod} -n {ns} --tail=50 --previous");
                        Console.Error.WriteLine(logs);
                    } catch {
                        try {
                            string logs = CMD($"kubectl logs {pod} -n {ns} --tail=50");
                            Console.Error.WriteLine(logs);
                        } catch {
                            Console.Error.WriteLine("(no logs available)");
                        }
                    }
                }
            } catch {
            }
        } catch {
            Console.Error.WriteLine("Could not diagnose failure.");
        }
    }
}
