# Releasing AinbFormat

The package and workflow are prepared locally. Neither the GitHub repository
nor a NuGet publishing policy is created by these files.

1. Review the source, supported-feature limits, credits and MIT license.
2. Create the intended public repository, `KB0MB/AinbFormat`, and push the
   approved source only. Check that no fixtures, ROMFS files or local artifacts
   are staged.
3. Create a GitHub environment named `nuget`, preferably requiring your approval.
   Set repository variable `NUGET_USER` to the actual NuGet profile name.
4. Create a NuGet Trusted Publishing policy. Suggested policy name:
   `AinbFormat-GitHub`. Repository owner: `KB0MB`; repository: `AinbFormat`;
   workflow filename: `publish.yml`; environment: `nuget`. Limit its package
   glob to `AinbFormat`, with permission to push new packages and versions.
5. Confirm the package ID is available, then run the source-only tests and the
   private integration suite. Update the version in the project file for each
   release; NuGet versions cannot be overwritten.
6. Publish a GitHub prerelease tagged `v0.1.0-alpha.1`. The tag must agree with
   the project version. The release workflow tests, packs and publishes only
   after its environment approval.
7. Confirm the package is visible on NuGet, restore TkSharp from NuGet.org, then
   remove TkSharp's temporary experimental-package publishing guard in a reviewed
   change. Keep AINB merging opt-in until full TKMM profile tests are complete.

Ordinary source pushes run CI but do not publish. Updating a repository alone
does not update a NuGet package. No long-lived API key is required by this setup.

This workflow follows [NuGet's Trusted Publishing documentation](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
and [NuGet/login](https://github.com/NuGet/login).
