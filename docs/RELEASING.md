# Release and publishing

The package is published as the `Graphify.CSharp` .NET global tool. Releases
are driven by a published GitHub release: a release whose tag is `v0.1.0`
publishes package version `0.1.0` after the same build, test, vulnerability,
package-smoke, and determinism gates used by CI.

## One-time setup

1. Create or sign in to the NuGet.org account that should own
   `Graphify.CSharp`.
2. In NuGet.org, add a Trusted Publishing policy for the GitHub repository.
   Enter the repository owner and name, the workflow filename
   `publish-nuget.yml` (without `.github/workflows/`), and the GitHub Actions
   environment `nuget`. The policy must match the workflow that performs the
   publish.
3. In the repository settings, create a GitHub Actions environment named
   `nuget` and add an environment or repository variable/secret named
   `NUGET_USER` containing the NuGet profile name, not the account email. The
   workflow passes this value to `NuGet/login@v1`; it is required even when the
   GitHub and NuGet names happen to match. A protected environment with a
   required reviewer is recommended before the first public release.
4. Confirm that `origin` points at the repository that contains this workflow.

The workflow requests the `id-token: write` permission and uses
`NuGet/login@v1` immediately before publishing. NuGet exchanges the GitHub OIDC
token for a short-lived credential; no long-lived NuGet API key is stored in
GitHub. The temporary credential is still passed to `dotnet nuget push`, as
required by NuGet’s publishing protocol, and expires after the workflow.

## Release a version

The release workflow installs both the .NET 10 and .NET 11 SDKs because the
single package contains both tool assets. Run the normal checks locally, then
use GitHub’s New release page:

```text
dotnet test Graphify.CSharp.sln --configuration Release
```

Create a release with:

- a tag in the form `v0.1.0` (create the tag from the intended commit if it
  does not already exist);
- the matching release title, such as `v0.1.0`; and
- the release published immediately, or publish the draft when ready.

Publishing the release starts `Publish NuGet package`. It checks out the
release tag, validates the version, runs the build/test/vulnerability gates,
packs with that exact version, installs the local package into a temporary tool
path, runs the fixture smoke test, checks repeatability, obtains a short-lived
Trusted Publishing credential, and then pushes the package to NuGet.org. The
package artifact is retained on the workflow run for inspection.

NuGet package versions are immutable. If a publish needs to be retried, rerun
the same workflow only when the package contents are unchanged; otherwise use
a new version tag.

## Local package check without publishing

```text
dotnet pack src/Graphify.CSharp.Cli/Graphify.CSharp.Cli.csproj \
  --configuration Release \
  -p:Version=0.1.0 \
  -p:PackageVersion=0.1.0 \
  --output artifacts
```

The existing CI workflow also installs the generated package from the local
`artifacts` folder and runs it against the reference fixture.

Install a local package explicitly for either runtime asset:

```text
dotnet tool install --tool-path .tool-net10 --add-source artifacts \
  --framework net10.0 Graphify.CSharp --version 0.1.0
dotnet tool install --tool-path .tool-net11 --add-source artifacts \
  --framework net11.0 Graphify.CSharp --version 0.1.0
```
