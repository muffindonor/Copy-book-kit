# Releasing

Both packages, `CopybookKit` and `CopybookKit.Tool`, are published by `.github/workflows/release.yml` when a version tag is pushed.

## Once

Publishing uses NuGet Trusted Publishing, so no API key is stored anywhere.

1. On nuget.org, open your user menu > **Trusted Publishing** and add a policy: repository owner `muffindonor`, repository `Copy-book-kit`, workflow file `release.yml`, environment `nuget`, scope **Push new packages and package versions**, and the package pattern `CopybookKit*`.
2. In the GitHub repository, open **Settings > Secrets and variables > Actions > Variables** and add a variable named `NUGET_USER` holding your nuget.org profile name (not your email).

## Each release

1. Set `<Version>` in `src/Directory.Build.props`, and commit.
2. Tag the commit with the same version and push the tag:

   ```bash
   git tag v0.1.0
   ```

   ```bash
   git push origin v0.1.0
   ```

The workflow stops without publishing if the tag and the version differ or a test fails. Then delete the tag, fix the problem and tag again. A version that has reached NuGet can never be published again, so a fix after that needs a new version number.

## Download counts

`.github/workflows/downloads.yml` runs once a day and keeps an issue named **NuGet downloads** up to date with both packages' download counts. When a count goes up it comments on the issue and mentions the repository owner, so GitHub sends a notification. GitHub turns off scheduled workflows in a repository with no activity for 60 days, and emails a warning first; to keep it running, re-enable it under **Actions**.
