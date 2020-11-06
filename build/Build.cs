using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet.Versioning;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Nuke.Common.Utilities.Collections;
using YamlDotNet.Serialization;

namespace NukeBuildSample
{
    [UnsetVisualStudioEnvironmentVariables]
    class Build : NukeBuild
    {
        public static int Main() => Execute<Build>(x => x.BuildPack);

        AbsolutePath OutputDir => RootDirectory / "build-output";
        AbsolutePath AppOutputDir => OutputDir / "app";
        AbsolutePath WorkingDir => RootDirectory / "repos";

        string Powershell => ToolPathResolver.GetPathExecutable("powershell");
        public List<DockerImageDetails> DockerImages { get; set; }

        [Parameter(Name = "forlinux")]
        string ForLinux = "true";

        [Parameter(Name = "dotnetruntimelinux")]
        string DotnetRuntimeLinux = "aspnetcore-runtime-3.1.9-linux-x64.tar.gz";

        [Parameter(Name = "dockerversion")]
        string DockerVersion;

        [Parameter(Name = "buildversion")]
        string BuildVersion;

        public BuildInformation BuildInformation => 
            JsonConvert.DeserializeObject<BuildInformation>(File.OpenText(RootDirectory / "build/buildinformation.json").ReadToEnd());

        Target SourceSetup => _ => _
        .Executes(() =>
        {
            Directory.CreateDirectory(WorkingDir);
            Directory.CreateDirectory(OutputDir);
            Directory.CreateDirectory(AppOutputDir);

            foreach (var project in BuildInformation.Projects)
            {
                if (FileSystemTasks.DirectoryExists(WorkingDir / project.Name))
                {
                    GitTasks.Git("pull", WorkingDir / project.Name);
                }
                else
                {
                    GitTasks.Git("clone -b " + project.Branch + " " + project.Repo + " " + project.Name, WorkingDir);
                }
            }
        });

        Target UpdateVersion => _ => _
        .DependsOn(SourceSetup)
        .Requires(() => BuildVersion)
        .Executes(() =>
        {
            ////Update version
            foreach (var file in BuildInformation.Versioning.Files)
            {
                var versionedFile = WorkingDir / file;

                if (FileSystemTasks.FileExists(versionedFile))
                {
                    var xmlDoc = XDocument.Load(versionedFile);
                    xmlDoc.Descendants().Where(w => w.Value == "latest").FirstOrDefault().Value = BuildVersion;
                    xmlDoc.Save(versionedFile);
                }
            }
        });

        Target PublishProjects => _ => _
        .DependsOn(UpdateVersion)
        .Executes(() =>
        {
            ////Start publishing of all modules
            foreach (var project in BuildInformation.Projects.OrderBy(o => o.BuildOrder))
            {
                var projectDir = WorkingDir / project.Name;
                var buildDir = projectDir / "build";

                ////BeforeBuild Actions
                if (project.BeforeBuild != null)
                {
                    Logger.Info("Before Build Actions " + project.Name);
                }

                ////Build Actions
                Logger.Info("Publishing Project " + project.Name);
                ProcessTasks.StartProcess(Powershell, buildDir + "/" + project.BuildCmd, buildDir).AssertWaitForExit();

                ////AfterBuild Actions
                if (project.AfterBuild != null)
                {
                    Logger.Info("After Build Actions " + project.Name);
                    foreach (var action in project.AfterBuild.FileCopyActions)
                    {
                        var source = WorkingDir / project.Name + "/" + action.SourceDir;
                        var destination = action.OutputDirType == DirType.Output ? AppOutputDir / action.DestDir : WorkingDir / action.DestDir;

                        if (FileSystemTasks.FileExists((AbsolutePath)(source)))
                        {
                            FileSystemTasks.CopyFileToDirectory(
                                source,
                                destination,
                                FileExistsPolicy.Overwrite,
                                true);
                        }
                        else
                        {
                            FileSystemTasks.CopyDirectoryRecursively(
                                source,
                                destination,
                                DirectoryExistsPolicy.Merge,
                                FileExistsPolicy.Overwrite);
                        }
                    }
                }
            }
        });

        Target BuildPack => _ => _
        .DependsOn(PublishProjects)
        .Requires(() => ForLinux)
        .Executes(() =>
        {
            ////Get Dotnet runtime
            Logger.Info("Download dotnet runtime");
            if (Convert.ToBoolean(ForLinux))
            {
                var dotnetRuntimeLinux = OutputDir / DotnetRuntimeLinux;

                Logger.Info("Started downloading dotnet runtime for linux");
                HttpTasks.HttpDownloadFile("https://dotnetcli.azureedge.net/dotnet/aspnetcore/Runtime/3.1.9/aspnetcore-runtime-3.1.9-linux-x64.tar.gz", dotnetRuntimeLinux);

                Logger.Info("Extracting dotnet runtime for linux");
                CompressionTasks.UncompressTarGZip(dotnetRuntimeLinux, OutputDir / "dotnet");
            }
            else
            {
                ProcessTasks.StartProcess(Powershell, "-NoProfile -ExecutionPolicy unrestricted -Command \"[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; &([scriptblock]::Create((Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1'))) -NoPath -Runtime aspnetcore -InstallDir dotnet \" ", OutputDir).AssertWaitForExit();
            }

            ////Get service files
            Logger.Info("Pack service files");
            var serviceOutputDir = OutputDir / "services";
            FileSystemTasks.DeleteDirectory(serviceOutputDir);

            Directory.CreateDirectory(serviceOutputDir);

            Directory.GetFiles(WorkingDir, "*.service", SearchOption.AllDirectories)
            .ToList()
            .ForEach(file =>
            FileSystemTasks.CopyFileToDirectory(
                file,
                serviceOutputDir,
                FileExistsPolicy.Overwrite,
                true));

            ////Get App
            Logger.Info("Pack apps");
            foreach (var project in BuildInformation.Projects)
            {
                var projectDir = WorkingDir / project.Name;

                foreach (var projectOutput in project.BuildOutputs)
                {
                    var projectOutputDir = projectOutput.OutputDirType == DirType.Output ?
                                            AppOutputDir / projectOutput.DestDir : WorkingDir / projectOutput.DestDir;

                    FileSystemTasks.CopyDirectoryRecursively(
                        projectDir + "/" + projectOutput.SourceDir,
                        projectOutputDir,
                        DirectoryExistsPolicy.Merge,
                        FileExistsPolicy.Overwrite);
                }
            }

            ////Delete runtime after extraction
            if (Convert.ToBoolean(ForLinux))
            {
                FileSystemTasks.DeleteFile(OutputDir / DotnetRuntimeLinux);
            }

            ////Compress package
            Logger.Info("Zip the app");
            FileSystemTasks.DeleteFile(OutputDir / "app.zip");
            CompressionTasks.CompressZip(OutputDir, OutputDir / "app.zip", null, CompressionLevel.Optimal);
        });

        Target BuildDockerImage => _ => _
        .DependsOn(SourceSetup)
        .Executes(() =>
        {
            Logger.Info("Getting docker image details from docker-compose.yaml files.");
            foreach (var project in BuildInformation.Projects.OrderBy(o => o.BuildOrder))
            {
                var projectDir = WorkingDir / project.Name;
                var buildDir = projectDir / "src";

                var reader = new StringReader(File.ReadAllText(buildDir + "/docker-compose.yml"));
                var deserializer = new DeserializerBuilder().Build();
                var yamlObject = deserializer.Deserialize(reader);
                var serializer = new SerializerBuilder()
                    .JsonCompatible()
                    .Build();

                var yamlJson = serializer.Serialize(yamlObject);
                JObject yamlJObj = JObject.Parse(yamlJson);
                var first = yamlJObj["services"].First;
                DockerImages = new List<DockerImageDetails>();
                while (first != null)
                {
                    var imageName = first.First["image"].ToString().Replace("${DOCKER_REGISTRY-}", BuildInformation.DockerRepoDetails.Repo + "/");
                    var dockerFilepath = first.First["build"].SelectTokens("dockerfile").FirstOrDefault().ToString();
                    var buildContext = first.First["build"].SelectTokens("context").FirstOrDefault().ToString();

                    DockerImages.Add(new DockerImageDetails()
                    {
                        ImageTag = imageName + ":" + DockerVersion,
                        DockerFilePath = buildDir / dockerFilepath,
                        BuildContext = buildDir + buildContext.Replace(".", "")
                    });
                    first = first.Next;
                }
            }

            Logger.Info("Creating docker images");
            foreach (var image in DockerImages)
            {
                Logger.Info("Image: " + image.ImageTag);
                DockerTasks.DockerBuild(x => x
                            .SetPath(image.BuildContext)
                            .SetFile(image.DockerFilePath)
                            .SetTag(image.ImageTag)
                            );
            }
        });

        Target PushDockerImage => _ => _
        .DependsOn(BuildDockerImage)
        .Requires(() => DockerVersion)
        .Executes(() =>
        {
            Logger.Info("Login on docker repository: " + BuildInformation.DockerRepoDetails.Repo);
            DockerTasks.DockerLogin(x => x
                            .SetServer(BuildInformation.DockerRepoDetails.Repo)
                            .SetUsername(BuildInformation.DockerRepoDetails.Username)
                            .SetPassword(BuildInformation.DockerRepoDetails.Password)
                            );

            Logger.Info("Pushing docker images to docker repository: " + BuildInformation.DockerRepoDetails.Repo);
            foreach (var image in DockerImages)
            {
                Logger.Info("Image: " + image.ImageTag);
                var imageName = image.ImageTag.Replace(BuildInformation.DockerRepoDetails.Repo + "/", "");
                DockerTasks.DockerPush(x => x
                            .SetName(image.ImageTag)
                            );
            }
        });
    }
}