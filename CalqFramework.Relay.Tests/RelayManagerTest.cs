using CalqFramework.Relay.ArgoCD;
using CalqFramework.Relay.Cloud;
using CalqFramework.Relay.Cloud.Azure;
using CalqFramework.Relay.Cloud.Custom;
using CalqFramework.Relay.Cloud.Gcp;
using CalqFramework.Relay.Docker;
using CalqFramework.Relay.Kustomize;
using CalqFramework.Relay.Platform;

namespace CalqFramework.Relay.Tests;

public class RelayManagerTest {
    [Fact]
    public void ConfigLoader_ParsesJson() {
        string configPath = Path.Combine(Path.GetTempPath(), $"relay-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(
                new PlatformConfig {
                    Name = "test-platform",
                    Environments = new() {
                        ["dev"] = new EnvironmentConfig {
                            Clusters = new() {
                                ["aks-dev"] = new ClusterConfig {
                                    Provider = "azure",
                                    Name = "aks-dev"
                                }
                            },
                            Registry = new RegistryConfig {
                                Provider = "azure",
                                Name = "acrdev"
                            }
                        },
                        ["prod"] = new EnvironmentConfig {
                            Clusters = new() {
                                ["gke-prod-east"] = new ClusterConfig {
                                    Provider = "gcp",
                                    Name = "gke-prod-east",
                                    Project = "my-project",
                                    Region = "us-central1"
                                },
                                ["gke-prod-west"] = new ClusterConfig {
                                    Provider = "gcp",
                                    Name = "gke-prod-west",
                                    Project = "my-project",
                                    Region = "us-west1"
                                }
                            },
                            Registry = new RegistryConfig {
                                Provider = "gcp",
                                Name = "my-repo",
                                Project = "my-project",
                                Region = "us"
                            }
                        }
                    },
                    Services = new() {
                        ["web"] = new ServiceConfig {
                            Path = "kubernetes/web",
                            BlueGreen = true
                        },
                        ["api"] = new ServiceConfig {
                            Path = "kubernetes/api",
                            BlueGreen = false
                        }
                    }
                }));

        PlatformConfig cfg = ConfigLoader.Load(configPath);

        Assert.Equal("test-platform", cfg.Name);
        Assert.Equal(2, cfg.Environments.Count);
        Assert.Equal(2, cfg.Services.Count);
        Assert.True(cfg.Services["web"].BlueGreen);
        Assert.False(cfg.Services["api"].BlueGreen);
        Assert.Equal(
            "gcp",
            cfg.Environments["prod"]
                .Clusters["gke-prod-east"].Provider);
        Assert.Equal(2, cfg.Environments["prod"].Clusters.Count);
        File.Delete(configPath);
    }

    [Fact]
    public void CloudFactory_CreatesAzureImplementations() {
        IClusterAuthenticator auth = CloudFactory.CreateAuthenticator("azure");
        Assert.IsType<AksAuthenticator>(auth);
        IContainerRegistry registry = CloudFactory.CreateRegistry(
            new RegistryConfig {
                Provider = "azure",
                Name = "myacr"
            });
        Assert.IsType<AcrRegistry>(registry);
    }

    [Fact]
    public void CloudFactory_CreatesGcpImplementations() {
        IClusterAuthenticator auth = CloudFactory.CreateAuthenticator("gcp");
        Assert.IsType<GkeAuthenticator>(auth);
        IContainerRegistry registry = CloudFactory.CreateRegistry(
            new RegistryConfig {
                Provider = "gcp",
                Name = "myrepo",
                Project = "proj",
                Region = "us"
            });
        Assert.IsType<GarRegistry>(registry);
    }

    [Fact]
    public void CloudFactory_CreatesCustomImplementations() {
        IClusterAuthenticator auth = CloudFactory.CreateAuthenticator("aws");
        Assert.IsType<CustomAuthenticator>(auth);
        IContainerRegistry registry = CloudFactory.CreateRegistry(
            new RegistryConfig {
                Provider = "ecr",
                Name = "myrepo",
                LoginServer = "123456789.dkr.ecr.us-east-1.amazonaws.com"
            });
        Assert.IsType<CustomRegistry>(registry);
        Assert.Equal("123456789.dkr.ecr.us-east-1.amazonaws.com", registry.LoginServer);
    }

    [Fact]
    public void ApplicationGenerator_GeneratesValidYaml() {
        string yaml = ApplicationGenerator.Generate("web", "https://github.com/org/repo.git", "kubernetes/web", "main", "https://kubernetes.default.svc", "web");
        Assert.Contains("name: web", yaml);
        Assert.Contains("path: kubernetes/web", yaml);
        Assert.Contains("targetRevision: main", yaml);
        Assert.Contains("namespace: web", yaml);
        Assert.Contains("CreateNamespace=true", yaml);
    }

    [Fact]
    public void KustomizeScaffolder_CreatesBlueGreenStructure() {
        string tmpDir = Path.Combine(Path.GetTempPath(), $"relay-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try {
            KustomizeScaffolder.Scaffold(tmpDir, "myservice", true);
            Assert.True(File.Exists(Path.Combine(tmpDir, "myservice", "base", "kustomization.yaml")));
            Assert.True(File.Exists(Path.Combine(tmpDir, "myservice", "base", "service.yaml")));
            Assert.True(File.Exists(Path.Combine(tmpDir, "myservice", "blue", "kustomization.yaml")));
            Assert.True(File.Exists(Path.Combine(tmpDir, "myservice", "blue", "deployment.yaml")));
            Assert.True(File.Exists(Path.Combine(tmpDir, "myservice", "green", "kustomization.yaml")));
            Assert.True(File.Exists(Path.Combine(tmpDir, "myservice", "green", "deployment.yaml")));
            Assert.False(File.Exists(Path.Combine(tmpDir, "myservice", "base", "deployment.yaml")));
        } finally {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void KustomizeScaffolder_FlatStructureWithoutBlueGreen() {
        string tmpDir = Path.Combine(Path.GetTempPath(), $"relay-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try {
            KustomizeScaffolder.Scaffold(tmpDir, "api", false);
            Assert.True(File.Exists(Path.Combine(tmpDir, "api", "deployment.yaml")));
            Assert.True(File.Exists(Path.Combine(tmpDir, "api", "service.yaml")));
            Assert.True(File.Exists(Path.Combine(tmpDir, "api", "kustomization.yaml")));
            Assert.False(Directory.Exists(Path.Combine(tmpDir, "api", "base")));
            Assert.False(Directory.Exists(Path.Combine(tmpDir, "api", "blue")));
            Assert.False(Directory.Exists(Path.Combine(tmpDir, "api", "green")));
        } finally {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void DockerfileGenerator_DetectsWebSdk() {
        string tmpDir = Path.Combine(Path.GetTempPath(), $"relay-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try {
            string csproj = Path.Combine(tmpDir, "MyApp.csproj");
            File.WriteAllText(
                csproj,
                """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            string dockerfile = DockerfileGenerator.Generate(csproj);

            Assert.Contains("aspnet:9.0", dockerfile);
            Assert.Contains("sdk:9.0", dockerfile);
            Assert.Contains("MyApp.dll", dockerfile);
            Assert.Contains("dotnet publish", dockerfile);
        } finally {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void PodRecyclerGenerator_GeneratesValidManifest() {
        string yaml = PodRecyclerGenerator.Generate();
        Assert.Contains("kind: CronJob", yaml);
        Assert.Contains("name: pod-recycler", yaml);
        Assert.Contains("calq-relay-system", yaml);
        Assert.Contains("bitnami/kubectl:latest", yaml);
        Assert.Contains("pod-deletion-cost", yaml);
    }

    [Fact]
    public void CanaryEnforcerGenerator_GeneratesValidManifest() {
        string yaml = CanaryEnforcerGenerator.Generate();
        Assert.Contains("kind: CronJob", yaml);
        Assert.Contains("name: canary-enforcer", yaml);
        Assert.Contains("calq-relay-system", yaml);
        Assert.Contains("bitnami/kubectl:latest", yaml);
        Assert.Contains("canary-weight", yaml);
        Assert.Contains("active-slot", yaml);
        Assert.Contains("deployments/scale", yaml);
    }

    [Fact]
    public void CanaryEnforcerGenerator_RespectsCustomImage() {
        string yaml = CanaryEnforcerGenerator.Generate(kubectlImage: "myregistry.io/kubectl:1.30");
        Assert.Contains("myregistry.io/kubectl:1.30", yaml);
        Assert.DoesNotContain("bitnami/kubectl:latest", yaml);
    }

    [Fact]
    public void DockerfileGenerator_FallsBackToRuntime() {
        string tmpDir = Path.Combine(Path.GetTempPath(), $"relay-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try {
            string csproj = Path.Combine(tmpDir, "Worker.csproj");
            File.WriteAllText(
                csproj,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """);

            string dockerfile = DockerfileGenerator.Generate(csproj);

            Assert.Contains("runtime:10.0", dockerfile);
            Assert.Contains("sdk:10.0", dockerfile);
            Assert.Contains("Worker.dll", dockerfile);
        } finally {
            Directory.Delete(tmpDir, true);
        }
    }
}
