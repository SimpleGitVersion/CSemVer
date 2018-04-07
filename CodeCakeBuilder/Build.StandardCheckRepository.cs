using Cake.Common.Build;
using Cake.Common.Diagnostics;
using Cake.Common.Solution;
using CK.Text;
using SimpleGitVersion;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace CodeCake
{
    public partial class Build
    {
        /// <summary>
        /// Base class that defines a NuGet feed.
        /// </summary>
        abstract class NuGetRemoteFeed
        {
            /// <summary>
            /// Gets the push API key name.
            /// This is the environment variable name. 
            /// </summary>
            public string APIKeyName { get; protected set; }

            /// <summary>
            /// Gets the push url.
            /// </summary>
            public string PushUrl { get; protected set; }

            /// <summary>
            /// Gets the push symbol url.
            /// Can be null: pushing the symbol is skipped.
            /// </summary>
            public string PushSymbolUrl { get; protected set; }

            /// <summary>
            /// Gets a mutable list of SolutionProject for which packages should be created and 
            /// pushed to this feed.
            /// </summary>
            public List<SolutionProject> PackagesToPush { get; } = new List<SolutionProject>();

            /// <summary>
            /// Checks whether a given package exists in this feed.
            /// </summary>
            /// <param name="client">The <see cref="HttpClient"/> to use.</param>
            /// <param name="packageId">The package name.</param>
            /// <param name="version">The package version.</param>
            /// <returns>True if the package exists, false otherwise.</returns>
            public abstract Task<bool> CheckPackageAsync( HttpClient client, string packageId, string version );
        }

        class MyGetPublicFeed : NuGetRemoteFeed
        {
            readonly string _feedName;

            public MyGetPublicFeed( string feedName, string apiKeyName )
            {
                _feedName = feedName;
                APIKeyName = APIKeyName;
                PushUrl = $"https://www.myget.org/F/{feedName}/api/v2/package";
                PushSymbolUrl = $"https://www.myget.org/F/{feedName}/symbols/api/v2/package";
            }

            public override async Task<bool> CheckPackageAsync( HttpClient client, string packageId, string version )
            {
                // My first idea was to challenge the Manual Download url with a Head, unfortunately myget
                // returns a 501 not implemented. I use the html page for the package.
                var page = $"https://www.myget.org/feed/{_feedName}/package/nuget/{packageId}/{version}";
                using( var m = new HttpRequestMessage( HttpMethod.Head, new Uri( page ) ) )
                using( var r = await client.SendAsync( m ) )
                {
                    return r.StatusCode == System.Net.HttpStatusCode.OK;
                }
            }
        }


        /// <summary>
        /// Exposes global state information for the build script.
        /// </summary>
        class CheckRepositoryInfo
        {
            /// <summary>
            /// Gets or sets the build configuration: either "Debug" or "Release".
            /// Defaults to "Debug".
            /// </summary>
            public string BuildConfiguration { get; set; } = "Debug";

            /// <summary>
            /// Gets or sets the version of the packages.
            /// </summary>
            public string Version { get; set; }

            /// <summary>
            /// Gets or sets the local feed path to which <see cref="LocalFeedPackagesToCopy"/> should be copied.
            /// Can be null if no local feed exists or if no push to local feed should be done.
            /// </summary>
            public string LocalFeedPath { get; set; }

            /// <summary>
            /// Gets a mutable list of SolutionProject for which packages should be created and copied
            /// to the <see cref="LocalFeedPath"/>.
            /// </summary>
            public List<SolutionProject> LocalFeedPackagesToCopy { get; } = new List<SolutionProject>();

            /// <summary>
            /// Gets or sets the remote feed to which packages should be pushed.
            /// </summary>
            public NuGetRemoteFeed RemoteFeed { get; set; }

            /// <summary>
            /// Gets the union of <see cref="LocalFeedPackagesToCopy"/> and <see cref="RemoteFeed"/>'s
            /// <see cref="NuGetRemoteFeed.PackagesToPush"/> without duplicates.
            /// </summary>
            public IEnumerable<SolutionProject> ActualPackagesToPublish => LocalFeedPackagesToCopy.Concat( RemoteFeed?.PackagesToPush ?? Enumerable.Empty<SolutionProject>() ).Distinct();

            /// <summary>
            /// Gets whether it is useless to continue. By default if <see cref="NoPackagesToProduce"/> is true, this is true,
            /// but if <see cref="IgnoreNoPackagesToProduce"/> is set, then we should continue.
            /// </summary>
            public bool ShouldStop => NoPackagesToProduce && !IgnoreNoPackagesToProduce;

            /// <summary>
            /// Gets or sets whether <see cref="NoPackagesToProduce"/> should be ignored.
            /// Defaults to false: by default if there is no packages to produce <see cref="ShouldStop"/> is true.
            /// </summary>
            public bool IgnoreNoPackagesToProduce { get; set; }

            /// <summary>
            /// Gets whether there is at least one package to produce and push.
            /// </summary>
            public bool NoPackagesToProduce => (LocalFeedPath == null || LocalFeedPackagesToCopy.Count == 0)
                                               &&
                                               (RemoteFeed == null || RemoteFeed.PackagesToPush.Count == 0);
        }

        /// <summary>
        /// Creates a new <see cref="CheckRepositoryInfo"/>.
        /// </summary>
        /// <param name="projectsToPublish">The projects to publish.</param>
        /// <param name="gitInfo">The git info.</param>
        /// <returns>A new info object.</returns>
        CheckRepositoryInfo StandardCheckRepository( IEnumerable<SolutionProject> projectsToPublish, SimpleRepositoryInfo gitInfo )
        {
            // Local function that displays information for packages already in a feed or not.
            void DispalyFeedPackageResult( string feedId, IReadOnlyList<SolutionProject> missingPackages, int totalCount )
            {
                var missingCount = missingPackages.Count;
                var existCount = totalCount - missingCount;

                if( missingCount == 0 )
                {
                    Cake.Information( $"All {existCount} packages are already in '{feedId}'." );
                }
                else if( existCount == 0 )
                {
                    Cake.Information( $"All {missingCount} packages must be pushed to '{feedId}'." );
                }
                else
                {
                    Cake.Information( $"{missingCount} packages are missing on '{feedId}': {missingPackages.Select( p => p.Name ).Concatenate()}." );
                    Cake.Information( $"{existCount} packages are already pushed on '{feedId}': {projectsToPublish.Except( missingPackages ).Select( p => p.Name ).Concatenate()}." );
                }
            }

            var result = new CheckRepositoryInfo { Version = gitInfo.SafeNuGetVersion };

            // We build in Debug for any prerelease except "rc": the last prerelease step is in "Release".
            result.BuildConfiguration = gitInfo.IsValidRelease
                                        && (gitInfo.PreReleaseName.Length == 0 || gitInfo.PreReleaseName == "rc")
                                        ? "Release"
                                        : "Debug";

            if( !gitInfo.IsValid )
            {
                if( Cake.InteractiveMode() != InteractiveMode.NoInteraction
                    && Cake.ReadInteractiveOption( "PublishDirtyRepo", "Repository is not ready to be published. Proceed anyway?", 'Y', 'N' ) == 'Y' )
                {
                    Cake.Warning( "GitInfo is not valid, but you choose to continue..." );
                }
                else
                {
                    // On Appveyor, we let the build run: this gracefully handles Pull Requests.
                    if( Cake.AppVeyor().IsRunningOnAppVeyor )
                    {
                        result.IgnoreNoPackagesToProduce = true;
                    }
                    else Cake.TerminateWithError( "Repository is not ready to be published." );
                }
                // When the gitInfo is not valid, we do not ty to push any packages, even if the build continues
                // (either because the user choose to continue or if we are on the CI server).
                // We don't need to worry about feeds here.
            }
            else
            {
                // gitInfo is valid: it is either ci or a release build. 
                // Blank releases must not be pushed on any remote and are compied to LocalFeed/Blank
                // local feed it it exists.
                bool isBlankCIRelease = gitInfo.Info.FinalSemVersion.Prerelease?.Contains( "ci-blank." ) ?? false;
                var localFeed = Cake.FindDirectoryAbove( "LocalFeed" );
                if( localFeed != null && isBlankCIRelease )
                {
                    localFeed = System.IO.Path.Combine( localFeed, "Blank" );
                    if( !System.IO.Directory.Exists( localFeed ) ) localFeed = null;
                }
                result.LocalFeedPath = localFeed;

                // Creating the right NuGetRemoteFeed according to the release level.
                if( !isBlankCIRelease )
                {
                    if( gitInfo.IsValidRelease )
                    {
                        if( gitInfo.PreReleaseName == ""
                            || gitInfo.PreReleaseName == "prerelease"
                            || gitInfo.PreReleaseName == "rc" )
                        {
                            result.RemoteFeed = new MyGetPublicFeed( "invenietis-release", "MYGET_RELEASE_API_KEY" );
                        }
                        else
                        {
                            // An alpha, beta, delta, epsilon, gamma, kappa goes to invenietis-preview.
                            result.RemoteFeed = new MyGetPublicFeed( "invenietis-preview", "MYGET_PREVIEW_API_KEY" );
                        }
                    }
                    else
                    {
                        Debug.Assert( gitInfo.IsValidCIBuild );
                        result.RemoteFeed = new MyGetPublicFeed( "invenietis-ci", "MYGET_CI_API_KEY" );
                    }
                }
            }

            // Now that Local/RemoteFeed are selected, we can check the packages that already exist
            // in those feeds.
            if( result.RemoteFeed != null )
            {
                using( var client = new HttpClient() )
                {
                    var requests = projectsToPublish
                                    .Select( p => new
                                    {
                                        Project = p,
                                        ExistsAsync = result.RemoteFeed.CheckPackageAsync( client, p.Name, gitInfo.SafeNuGetVersion )
                                    } )
                                    .ToList();
                    System.Threading.Tasks.Task.WaitAll( requests.Select( r => r.ExistsAsync ).ToArray() );
                    var notOk = requests.Where( r => !r.ExistsAsync.Result ).Select( r => r.Project );
                    result.RemoteFeed.PackagesToPush.AddRange( notOk );
                    DispalyFeedPackageResult( result.RemoteFeed.PushUrl, result.RemoteFeed.PackagesToPush, requests.Count );
                }
            }
            if( result.LocalFeedPath != null )
            {
                var lookup = projectsToPublish
                                .Select( p => new
                                {
                                    Project = p,
                                    Path = System.IO.Path.Combine( result.LocalFeedPath, $"{p.Name}.{gitInfo.SafeNuGetVersion}.nupkg" )
                                } )
                                .Select( x => new
                                {
                                    x.Project,
                                    Exists = System.IO.File.Exists( x.Path )
                                } )
                                .ToList();
                var notOk = lookup.Where( r => !r.Exists ).Select( r => r.Project );
                result.LocalFeedPackagesToCopy.AddRange( notOk );
                DispalyFeedPackageResult( result.LocalFeedPath, result.LocalFeedPackagesToCopy, lookup.Count );
            }
            Cake.Information( $"Should actually publish {result.ActualPackagesToPublish.Count()} out of {projectsToPublish.Count()} projects with version={gitInfo.SafeNuGetVersion} and configuration={result.BuildConfiguration}: {result.ActualPackagesToPublish.Select( p => p.Name ).Concatenate()}" );

            var appVeyor = Cake.AppVeyor();
            if( appVeyor.IsRunningOnAppVeyor )
            {
                if( result.ShouldStop )
                {
                    appVeyor.UpdateBuildVersion( $"Already-done-{appVeyor.Environment.Build.Id}" );
                }
                else
                {
                    try
                    {
                        appVeyor.UpdateBuildVersion( gitInfo.SafeNuGetVersion );
                    }
                    catch
                    {
                        appVeyor.UpdateBuildVersion( $"{gitInfo.SafeNuGetVersion} - {appVeyor.Environment.Build.Id}" );
                    }
                }
            }
            
            return result;
        }

    }
}
