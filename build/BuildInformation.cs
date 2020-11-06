using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NuGet.Packaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace NukeBuildSample
{
    public class BuildInformation
    {
        public GitCredentials GitCredentials { get; set; }
        public List<Projects> Projects { get; set; }
        public DockerRepoDetails DockerRepoDetails { get; set; }
        public Versioning Versioning { get; set; }
    }

    public class GitCredentials
    {
        public string Username { get; set; }
        public SecureString Password { get; set; } 
    }

    public class DockerRepoDetails
    {
        public string Repo { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public class Projects
    {
        public string Name { get; set; }
        public string Repo { get; set; }
        public string Branch { get; set; }
        public int BuildOrder { get; set; }
        public string BuildCmd { get; set; }
        public List<BuildOutputs> BuildOutputs { get; set; }
        public AfterBuild AfterBuild { get; set; }
        public BeforeBuild BeforeBuild { get; set; }
    }

    public class Versioning
    {
        public List<string> Files { get; set; }
    }

    public class AfterBuild
    {
        public List<FileCopyActions> FileCopyActions { get; set; }
    }

    public class BeforeBuild
    {
        public List<FileCopyActions> FileCopyActions { get; set; }
    }

    public class FileCopyActions
    {
        public string SourceDir { get; set; }
        public string DestDir { get; set; }
        public DirType OutputDirType { get; set; }
    }

    public class BuildOutputs
    {
        public string Name { get; set; }
        public string DestDir { get; set; }
        public string SourceDir { get; set; }
        public DirType OutputDirType { get; set; }
    }

    public class DockerImageDetails
    {
        public string ImageTag { get; set; }
        public string DockerFilePath { get; set; }
        public string BuildContext { get; set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum DirType
    {
        Output = 0,
        Working
    }
}
