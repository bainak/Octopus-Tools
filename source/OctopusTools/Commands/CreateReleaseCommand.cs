﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using OctopusTools.Client;
using OctopusTools.Infrastructure;
using OctopusTools.Model;
using log4net;

namespace OctopusTools.Commands
{
    public class CreateReleaseCommand : ApiCommand
    {
        readonly IDeploymentWatcher deploymentWatcher;

        public CreateReleaseCommand(IOctopusSessionFactory session, ILog log, IDeploymentWatcher deploymentWatcher)
            : base(session, log)
        {
            this.deploymentWatcher = deploymentWatcher;

            DeployToEnvironmentNames = new List<string>();
            DeploymentStatusCheckSleepCycle = TimeSpan.FromSeconds(10);
            DeploymentTimeout = TimeSpan.FromMinutes(10);
            PackageVersionNumberOverrides = new Dictionary<string, string>();
        }

        public string ProjectName { get; set; }
        public IList<string> DeployToEnvironmentNames { get; set; }
        public string VersionNumber { get; set; }
        public string PackageVersionNumber { get; set; }
        public string ReleaseNotes { get; set; }
        public bool ForceVersion { get; set; }
        public bool Force { get; set; }
        public bool WaitForDeployment { get; set; }
        public TimeSpan DeploymentTimeout { get; set; }
        public TimeSpan DeploymentStatusCheckSleepCycle { get; set; }
        public IDictionary<string, string> PackageVersionNumberOverrides { get; set; }

        public bool Wait { get; set; }
        public TimeSpan? Delay { get; set; }
        
        public override OptionSet Options
        {
            get
            {
                var options = base.Options;
                options.Add("project=", "Name of the project", v => ProjectName = v);
                options.Add("deployto=", "[Optional] Environment to automatically deploy to, e.g., Production", v => DeployToEnvironmentNames.Add(v));
                options.Add("version=", "Version number to use for the new release.", v => VersionNumber = v);
                options.Add("packageversion=", "Version number of the package to use for this release.", v => PackageVersionNumber = v);
                options.Add("packageversionoverride=", "[Optional] Version number to use for a package in the release.", ParsePackageConstraint);
                options.Add("forceversion", "Take the version from the packageversion option and do not check for existence first.", v => ForceVersion = true);
                options.Add("force", "Whether to force redeployment of already installed packages (flag, default false).", v => Force = true);
                options.Add("releasenotes=", "Release Notes for the new release.", v => ReleaseNotes = v);
                options.Add("releasenotesfile=", "Path to a file that contains Release Notes for the new release.", ReadReleaseNotesFromFile);
                options.Add("waitfordeployment", "Whether to wait synchronously for deployment to finish.", v => WaitForDeployment = true );
                options.Add("deploymenttimeout=", "[Optional] Specifies maximum time (timespan format) that deployment can take (default 00:10:00)", v => DeploymentTimeout = TimeSpan.Parse(v));
                options.Add("deploymentchecksleepcycle=", "[Optional] Specifies how much time (timespan format) should elapse between deployment status checks (default 00:00:10)", v => DeploymentStatusCheckSleepCycle = TimeSpan.Parse(v));

                options.Add("wait", "[Optional] Cause the command to wait the delay time before executing (flag, default false).", v => Wait = true);
                options.Add("delay=", "[Optional] Specifies time (timespan format) to delay execution, otherwise, no delay.", v => Delay = TimeSpan.Parse(v));

                return options;
            }
        }

        private void ReadReleaseNotesFromFile(string value)
        {
            try
            {
                ReleaseNotes = File.ReadAllText(value);
            }
            catch (IOException ex)
            {
                throw new CommandException(ex.Message);
            }
        }

        public void ParsePackageConstraint(string value)
        {
            try
            {
                var packageIdAndVersion = PackageConstraintExtensions.GetPackageIdAndVersion(value);

                if (PackageVersionNumberOverrides.ContainsKey(packageIdAndVersion[0]))
                {
                    throw new ArgumentException(string.Format("More than one constraint was specified for package {0}", packageIdAndVersion[0]));
                }

                PackageVersionNumberOverrides.Add(packageIdAndVersion[0], packageIdAndVersion[1]);
            }
            catch (ArgumentException ex)
            {
                throw new CommandException(ex.Message);
            }
        }

        public string GetPackageVersionForStep(Step step)
        {
            if (PackageVersionNumberOverrides != null && PackageVersionNumberOverrides.ContainsKey(step.NuGetPackageId))
            {
                return PackageVersionNumberOverrides[step.NuGetPackageId];
            }
            if (!string.IsNullOrEmpty(PackageVersionNumber))
            {
                return PackageVersionNumber;
            }

            return null;
        }

        public override void Execute()
        {
            if (string.IsNullOrWhiteSpace(ProjectName)) throw new CommandException("Please specify a project name using the parameter: --project=XYZ");

            if (Delay.HasValue)
            {
                if (!Wait)
                {
                    Fork();
                    return;
                }
                Log.Debug(string.Format("Waiting {0} to create release ...", Delay.Value));
                Thread.Sleep(Delay.Value);
            }

            Log.Debug("Finding project: " + ProjectName);
            var project = Session.GetProject(ProjectName);

            Log.Debug("Finding environments...");
            var environments = Session.FindEnvironments(DeployToEnvironmentNames);

            Log.Debug("Finding steps for project...");
            var steps = Session.FindStepsForProject(project);

            Log.Debug("Getting package versions for each step...");
            var selected = new List<SelectedPackage>();
            foreach (var step in steps)
            {
                SelectedPackage version;
                var packageVersionConstraint = GetPackageVersionForStep(step);
                if (packageVersionConstraint != null)
                {
                    if (ForceVersion)
                    {
                        version = new SelectedPackage { StepId = step.Id, NuGetPackageVersion = packageVersionConstraint };
                    }
                    else
                    {
                        version = Session.GetPackageForStep(step, packageVersionConstraint);
                    }
                }
                else
                {
                    version = Session.GetLatestPackageForStep(step);
                }

                Log.DebugFormat("{0} - latest: {1}", step.Description, version.NuGetPackageVersion);
                selected.Add(version);
            }

            var versionNumber = VersionNumber;
            if (string.IsNullOrWhiteSpace(versionNumber))
            {
                versionNumber = selected.Select(p => SemanticVersion.Parse(p.NuGetPackageVersion)).OrderByDescending(v => v).First().ToString();
                Log.Warn("A --version parameter was not specified, so a version number was automatically selected based on the highest package version: " + versionNumber);
            }

            Log.Debug("Creating release: " + versionNumber);
            var release = Session.CreateRelease(project, selected, versionNumber, ReleaseNotes);
            Log.Info("Release created successfully!");

            if (environments != null)
            {
                var linksToDeploymentTasks = Session.GetDeployments(release, environments, Force, Log);

                if (WaitForDeployment)
                {
                    deploymentWatcher.WaitForDeploymentsToFinish(Session, linksToDeploymentTasks, DeploymentTimeout, DeploymentStatusCheckSleepCycle);
                }
            }

        }

        public void Fork()
        {
            string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1));
            args += " --wait";

            var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                UseShellExecute = true,                 
                FileName = Process.GetCurrentProcess().MainModule.FileName, 
                Arguments = args
            };

            var success = process.Start();
            
            if (success)
                Log.InfoFormat("Successfully scheduled create release command.");
            else
                Log.InfoFormat("Failed scheduled create release command.");
        }
    }
}
