# Release and publishing

The package is published as the `Graphify.CSharp` .NET global tool. Releases
are driven by a published GitHub release: a release whose tag is `v0.1.0`
publishes package version `0.1.0` after the same build, test, vulnerability,
package-smoke, and determinism gates used by CI.

## One-time setup

1. Create or sign in to the NuGet.org account that should own
   `Graphify.CSharp`.
2. In NuGet.org, create an API key with the `Push` scope, restricted to the
   `Graphify.CSharp` package, and give it an expiry date. Do not commit or paste
   the key into workflow files.
3. In the repository settings, create a GitHub Actions environment named
   `nuget` and add an environment secret named `NUGET_API_KEY`. If environment
   secrets are unavailable on the repository’s GitHub plan, add the same secret
   as a repository Actions secret instead. A protected environment with a
   required reviewer is recommended before the first public release.
4. Confirm that `origin` points at the repository that contains this workflow.

The workflow only grants the job read access to repository contents. The NuGet
secret is made available only to the publish job, after any environment
approval.

## Release a version

Run the normal checks locally, then use GitHub’s New release page:

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
path, runs the fixture smoke test, checks repeatability, and then pushes the
package to NuGet.org. The package artifact is retained on the workflow run for
inspection.

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
